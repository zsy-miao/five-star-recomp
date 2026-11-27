// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Globalization;
using System.Text;
using FiveStar.Loader.Diagnostics;
using FiveStar.Loader.Native;
using FiveStar.Loader.Shims.D3D12X.DebugLayer;
using FiveStar.Loader.Shims.D3D12X.Pipelines;

namespace FiveStar.Loader.Shims.D3D12X.Present;

/// <summary>
/// Queue slot 32 (PresentX, *inferred*) on a flip-model swap chain on the game's window, its buffers the size of the
/// window's client area. The first plane is copied into the back buffer when the sizes match, else scaled into it with
/// its aspect ratio (<see cref="PresentScaler"/>; same typeless family; otherwise, and with <see cref="ShowTestPattern"/>
/// on, the test pattern is drawn instead) on the presenting queue, then shown with vsync. Created lazily at the first
/// present, which also fits an oversized window to its monitor (<see cref="GameWindowFit"/>). The plane is taken to be a
/// render target at present time: the state the debug layer tracks for the game's plane then.
/// </summary>
internal sealed unsafe class Presenter : IDisposable
{
    /// <summary>Presents between two interval lines in the log.</summary>
    private const long ReportEvery = 600;

    /// <summary>The first presents are logged one by one, with their time since the first.</summary>
    private const long EarlyPresents = 8;

    /// <summary>Presents between two searches for the game's window while there is none.</summary>
    private const long WindowRetry = 60;

    private const double MillisecondsPerSecond = 1000.0;

    /// <summary>The parameter block is 0x20 bytes (cleared at 0x142482364; only the dword at +0x18 is set).</summary>
    private const int ParameterDwords = 8;

    /// <summary>Back-buffer resizes logged; dragging a window edge makes one per present.</summary>
    private const long LoggedResizes = 16;

    private readonly XboxObjects _objects;
    private readonly OnceLog _warnings;
    private readonly LoaderLog _log;
    private readonly PresentTimeline _timeline;
    private nint _window;
    private nint _queue;
    private DxgiSwapChain? _swapChain;
    private PresentRecorder? _recorder;
    private PresentScaler? _scaler;
    private long _resizes;
    private long _presents;
    private long _skipped;
    private long _nextAttempt;
    private long _first;
    private long _reportStart;
    private bool _described;
    private bool _windowGone;
    private ResizeRetry _resize;

    public Presenter(XboxObjects objects, OnceLog warnings, LoaderLog log)
    {
        _objects = objects;
        _warnings = warnings;
        _log = log;
        _timeline = new PresentTimeline(log);
    }

    /// <summary>Draw the test pattern instead of the game's plane (<c>--present-test</c>).</summary>
    public bool ShowTestPattern { get; set; }

    /// <summary>Reads back the plane at chosen presents (<c>--capture-dir</c>); null when off.</summary>
    public FrameCapture? Capture { get; set; }

    /// <summary>PIX GPU captures of chosen presents (<c>--pix-capture</c>); null when off.</summary>
    public PixGpuCapture? Pix { get; set; }

    /// <summary>Signatures of presents for an input script's screens; null without a script.</summary>
    public ScreenSampler? Screens { get; set; }

    /// <summary>Reads back the render targets of traced draws (<c>--d3d12-trace-draws</c>); null when off.</summary>
    public DrawTrace? Trace { get; set; }

    /// <summary>Frames shown so far.</summary>
    public long Presents => Volatile.Read(ref _presents);

    /// <summary>The window presents go to; found among this process's windows unless set first.</summary>
    public nint Window
    {
        get => _window;
        set => _window = value;
    }

    /// <summary>(plane count, planes, parameters); count 0 (shutdown, and before the game rebuilds its targets) shows nothing.</summary>
    [HotPath]
    public int Present(nint queue, uint count, PresentPlane* planes, uint* parameters)
    {
        if (count == 0 || planes == null)
        {
            return HResult.Ok;
        }

        if (!_described)
        {
            Describe(count, planes, parameters);
        }

        var source = PlaneReader.Read(_objects, planes, ShowTestPattern);

        if (!EnsureOutput(queue, source))
        {
            return HResult.Ok;
        }

        if (PresentNative.IsIconic(_window) != 0 || WindowGone())
        {
            _skipped++;

            return HResult.Ok;
        }

        Show(source);

        return HResult.Ok;
    }

    /// <summary>
    /// Whether the window was destroyed (closed while the game still presents): nothing is shown from then on. A
    /// present to it fails with DXGI_ERROR_INVALID_CALL and a resize with E_ACCESSDENIED (issue #64).
    /// </summary>
    [HotPath]
    private bool WindowGone()
    {
        if (!_windowGone && PresentNative.IsWindow(_window) == 0)
        {
            _windowGone = true;
            NoteWindowGone();
        }

        return _windowGone;
    }

    private void NoteWindowGone()
    {
        _log.Info($"  [present] window 0x{_window:x} is gone (closed): frames are not shown from now on");
    }

    /// <summary>Logs the presents so far and their mean interval.</summary>
    public void LogSummary()
    {
        long presents = Presents;

        if (presents > 0)
        {
            _log.Info($"  [present] {presents} presents, {_skipped} skipped while minimized, {MeanInterval(_first, presents):F2} ms mean interval");
        }
    }

    public void Dispose()
    {
        if (_recorder is not null && _queue != 0)
        {
            _recorder.WaitIdle(_queue);
        }

        LogSummary();
        _swapChain?.Dispose();
        _scaler?.Dispose();
        _recorder?.Dispose();
    }

    [HotPath]
    private void Show(PlaneSource source)
    {
        var swapChain = _swapChain!;
        var recorder = _recorder!;

        if (!Sized(swapChain, source))
        {
            _skipped++;

            return;
        }

        if (source.ColorSpace != swapChain.ColorSpace)
        {
            ChangeColorSpace(source.ColorSpace);
        }

        _timeline.Enter();
        swapChain.WaitForFrame();
        _timeline.Mark(PresentTimeline.LatencyWait);

        uint index = swapChain.CurrentIndex();
        nint back = swapChain.Buffers[(int)index];
        recorder.Begin(index);
        _timeline.Mark(PresentTimeline.RetireWait);

        bool sameSize = swapChain.Width == source.Width && swapChain.Height == source.Height;

        if (source.Copyable && sameSize)
        {
            CopyPlane(recorder, back, source);
        }
        else if (!source.Copyable || !Scale(recorder, index, back, source))
        {
            recorder.Transition(back, TransitionBarrier.PresentState, TransitionBarrier.RenderTargetState);
            TestPattern.Record(recorder.List, recorder.View((int)index), swapChain.Width, swapChain.Height, _presents);
            recorder.Transition(back, TransitionBarrier.RenderTargetState, TransitionBarrier.PresentState);
        }

        recorder.Submit(_queue, index);
        _timeline.Mark(PresentTimeline.Submit);
        int hr = swapChain.Present(FramePacer.SyncInterval);
        _timeline.Mark(PresentTimeline.Present);
        Count(hr);
        _timeline.Exit(_presents);

        if (Capture is not null)
        {
            Inspect(source);
        }

        if (Screens is not null && Screens.Due && source.Copyable)
        {
            SampleScreen(source);
        }
    }

    /// <summary>
    /// The back buffers at the size and format the frame wants, resized when they differ; false when the frame cannot
    /// be shown: a resize failed now (retried later, frames going to the old buffers meanwhile) or no buffer is held.
    /// </summary>
    private bool Sized(DxgiSwapChain swapChain, PlaneSource source)
    {
        var (width, height) = TargetSize(source);
        bool resize = width != swapChain.Width || height != swapChain.Height || source.Format != swapChain.Format;

        if (resize && _resize.MayTry(_skipped + _presents) && !Resize(width, height, source.Format))
        {
            return false;
        }

        return swapChain.Usable;
    }

    /// <summary>The plane's source rect copied into a back buffer of its size.</summary>
    [HotPath]
    private static void CopyPlane(PresentRecorder recorder, nint back, PlaneSource source)
    {
        var box = source.Box;
        recorder.Transition(back, TransitionBarrier.PresentState, TransitionBarrier.CopyDestState);
        recorder.Transition(source.Resource, source.State, TransitionBarrier.CopySourceState);
        recorder.Copy(back, source.Resource, &box);
        recorder.Transition(source.Resource, TransitionBarrier.CopySourceState, source.State);
        recorder.Transition(back, TransitionBarrier.CopyDestState, TransitionBarrier.PresentState);
    }

    /// <summary>A screen signature after a present, once the presenting queue is idle.</summary>
    private void SampleScreen(PlaneSource source)
    {
        _recorder!.WaitIdle(_queue);
        Screens!.Sample(_objects.DesktopDevice, source.Resource, source.State);
    }

    /// <summary>Debug readbacks after a present, once the presenting queue (all the game's work) is idle.</summary>
    private void Inspect(PlaneSource source)
    {
        bool plane = Capture!.Wants(_presents);
        bool targets = Trace is not null && Trace.ReportDue(_presents);

        if (!plane && !targets)
        {
            return;
        }

        _recorder!.WaitIdle(_queue);

        if (plane)
        {
            Capture.Save(_objects.DesktopDevice, source.Resource, source.State, $"present-{_presents}");
        }

        if (targets)
        {
            Trace!.Report();
        }

        if (plane)
        {
            DebugLayer.MeshProbes.Report(_presents, _log);
            Binding.WindowGuard.Report(_presents, _log);
            Binding.WindowGuard.ReportCaps(_presents, _log);
        }
    }

    [HotPath]
    private void Count(int hr)
    {
        if (HResult.Failed(hr))
        {
            Failed(hr);

            return;
        }

        long now = Stopwatch.GetTimestamp();

        if (_presents == 0)
        {
            _first = now;
            _reportStart = now;
        }

        Volatile.Write(ref _presents, _presents + 1);
        Pipelines.StandInUse.Frame = _presents;
        Pipelines.PipelineEviction.AfterPresent(_presents);
        Pipelines.ShaderTableGuard.AfterPresent(_presents);
        Pix?.AfterPresent(_presents);

        if (_presents <= EarlyPresents)
        {
            ReportEarly(now);
        }

        if (_presents % ReportEvery == 0)
        {
            Report(now);
        }
    }

    /// <summary>The swap chain and recorder at the first present that finds a window; false until then.</summary>
    [HotPath]
    private bool EnsureOutput(nint queue, PlaneSource source)
    {
        if (_swapChain is not null)
        {
            if (queue != _queue)
            {
                _warnings.Warn("present-queue", "[present] a present came on another queue than the first; copies stay on the first");
            }

            return true;
        }

        if (_skipped + _presents < _nextAttempt)
        {
            _skipped++;

            return false;
        }

        return Start(queue, source);
    }

    private bool Start(nint queue, PlaneSource source)
    {
        _nextAttempt = _skipped + _presents + WindowRetry;

        if (_window == 0)
        {
            _window = GameWindowFinder.Find();
        }

        if (_window == 0)
        {
            _warnings.Warn("present-window", "[present] no window of this process to present to yet; frames are dropped until one shows");

            return false;
        }

        var recorder = new PresentRecorder(_objects.DesktopDevice);
        int hr = recorder.Create();
        FitWindow(source);
        _scaler ??= CreateScaler();
        var (width, height) = TargetSize(source);

        if (!HResult.Failed(hr))
        {
            hr = DxgiSwapChain.Create(queue, _window, width, height, source.Format, out _swapChain);
        }

        if (HResult.Failed(hr) || _swapChain is null)
        {
            recorder.Dispose();
            _warnings.Warn("present-create", $"[present] swap chain on window 0x{_window:x} failed: {HResult.Format(hr)}");

            return false;
        }

        _queue = queue;
        _recorder = recorder;
        recorder.SetTargets(_swapChain.Buffers);
        string content = ShowTestPattern ? "test pattern" : "game plane";
        _log.Info($"  [present] flip-model swap chain on window 0x{_window:x}: {width}x{height} (plane {source.Width}x{source.Height}), format {source.Format}, {DxgiSwapChain.BufferCount} buffers, {content}");

        return true;
    }

    /// <summary>The back-buffer size: the window's client area, or the plane's without one or without the scaler.</summary>
    [HotPath]
    private (uint Width, uint Height) TargetSize(PlaneSource source)
    {
        PlaneRect client;

        if (_scaler is null)
        {
            return (source.Width, source.Height);
        }

        if (PresentNative.GetClientRect(_window, &client) == 0 || client.Right <= client.Left || client.Bottom <= client.Top)
        {
            // No client area to read (the window is going, or has none for a moment): the size stays as it is.
            if (_swapChain is not null)
            {
                return (_swapChain.Width, _swapChain.Height);
            }

            return (source.Width, source.Height);
        }

        return ((uint)(client.Right - client.Left), (uint)(client.Bottom - client.Top));
    }

    /// <summary>Scales the plane into a back buffer of another size; false when no pipeline takes the format (the scaler is dropped).</summary>
    [HotPath]
    private bool Scale(PresentRecorder recorder, uint index, nint back, PlaneSource source)
    {
        var swapChain = _swapChain!;

        if (_scaler is not null && _scaler.Record(recorder.List, index, back, recorder.View((int)index), swapChain.Width, swapChain.Height, swapChain.Format, source))
        {
            return true;
        }

        if (_scaler is not null)
        {
            DropScaler(swapChain.Format);
        }

        return false;
    }

    private void DropScaler(int format)
    {
        _warnings.Warn("present-scale", $"[present] no scaling pipeline for format {format}; back buffers take the plane's size");
        _scaler = null;
    }

    private PresentScaler? CreateScaler()
    {
        var scaler = new PresentScaler(_objects.DesktopDevice);

        if (scaler.Create(out string failure))
        {
            return scaler;
        }

        scaler.Dispose();
        _warnings.Warn("present-scaler", $"[present] no scaler ({failure}); back buffers take the plane's size");

        return null;
    }

    private void FitWindow(PlaneSource source)
    {
        var placed = GameWindowFit.Fit(_window, source.Width, source.Height);

        if (placed.HasValue)
        {
            var (x, y, width, height) = placed.Value;
            _log.Info($"  [present] window larger than its monitor's work area: now {width}x{height} at ({x}, {y})");
        }
    }

    /// <summary>New back buffers; false when ResizeBuffers failed: the old ones are the targets again (when they could be taken) and it is retried later.</summary>
    private bool Resize(uint width, uint height, int format)
    {
        _recorder!.WaitIdle(_queue);
        int hr = _swapChain!.Resize(width, height, format);

        if (HResult.Failed(hr))
        {
            if (_swapChain.Usable)
            {
                _recorder.SetTargets(_swapChain.Buffers);
            }

            if (_resize.Failed(_skipped + _presents))
            {
                _log.Warn($"[present] ResizeBuffers({width}x{height}, format {format}) failed: {HResult.Format(hr)}; this frame is not shown, the back buffers stay {_swapChain.Width}x{_swapChain.Height} (held again: {_swapChain.Usable}) and the resize is tried again in {ResizeRetry.RetryFrames} frames");
            }

            return false;
        }

        _resize.Succeeded();
        _recorder.SetTargets(_swapChain.Buffers);
        _resizes++;

        if (_resizes <= LoggedResizes)
        {
            _log.Info($"  [present] back buffers now {width}x{height}, format {format}");
        }

        return true;
    }

    private void ChangeColorSpace(uint colorSpace)
    {
        if (_swapChain!.SetColorSpace(colorSpace))
        {
            _log.Info($"  [present] swap-chain color space {colorSpace}");

            return;
        }

        _warnings.Warn($"present-colorspace-{colorSpace}", $"[present] the display path does not take color space {colorSpace}; it stays {_swapChain.ColorSpace}");
    }

    private void Describe(uint count, PresentPlane* planes, uint* parameters)
    {
        _described = true;

        for (uint i = 0; i < count; i++)
        {
            var plane = planes + i;
            var rect = plane->SourceRect;
            string source = rect == null ? "none" : $"({rect->Left},{rect->Top})-({rect->Right},{rect->Bottom})";
            nint resource = plane->Resources == null ? 0 : plane->Resources[0];
            _log.Info($"  [present] plane {i}: token {plane->Token}, {plane->ResourceCount} resource(s) 0x{resource:x}, source {source}, dword@0x28 {plane->ColorSpace}");
        }

        if (parameters == null)
        {
            return;
        }

        var text = new StringBuilder("  [present] parameters:");

        for (int i = 0; i < ParameterDwords; i++)
        {
            text.Append(CultureInfo.InvariantCulture, $" {parameters[i]:x8}");
        }

        _log.Info(text.ToString());
    }

    private void Report(long now)
    {
        _log.Info($"  [present] {_presents} presents, {MeanInterval(_reportStart, ReportEvery):F2} ms mean interval over the last {ReportEvery}");
        _reportStart = now;
    }

    private void ReportEarly(long now)
    {
        double since = (now - _first) * MillisecondsPerSecond / Stopwatch.Frequency;
        _log.Info($"  [present] present {_presents} at {since:F1} ms");
    }

    private void Failed(int hr)
    {
        if (DeviceLoss.IsRemoval(hr))
        {
            DeviceLoss.Noticed($"a present failed with {HResult.Format(hr)}");
        }

        _warnings.Warn($"present-{hr:x8}", $"[present] IDXGISwapChain::Present failed: {HResult.Format(hr)}");
    }

    private static double MeanInterval(long start, long frames)
    {
        return (Stopwatch.GetTimestamp() - start) * MillisecondsPerSecond / Stopwatch.Frequency / frames;
    }
}
