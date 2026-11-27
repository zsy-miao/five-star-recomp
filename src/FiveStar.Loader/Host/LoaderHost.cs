// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Formats.Platform;
using FiveStar.Loader.Calls;
using FiveStar.Loader.Cpu;
using FiveStar.Loader.Diagnostics;
using FiveStar.Loader.Loading;
using FiveStar.Loader.Mods;
using FiveStar.Loader.Native;
using FiveStar.Loader.Shims;
using FiveStar.Loader.Shims.D3D12X;
using FiveStar.Loader.Shims.D3D12X.DebugLayer;
using FiveStar.Loader.Shims.D3D12X.Present;
using FiveStar.Loader.Shims.Input;

namespace FiveStar.Loader.Host;

/// <summary>Runs one fivestar-loader invocation: load, report, call exports, unload.</summary>
internal sealed class LoaderHost
{
    public const int Success = 0;
    public const int Failure = 1;
    public const int UsageError = 2;

    private readonly HostOptions _options;
    private readonly LoaderRuntime _runtime;
    private readonly LoaderLog _log;
    private readonly int _processId;

    public LoaderHost(HostOptions options, LoaderRuntime runtime, LoaderLog log, int processId)
    {
        _options = options;
        _runtime = runtime;
        _log = log;
        _processId = processId;
    }

    public int Run()
    {
        if (!DisableShims())
        {
            return UsageError;
        }

        string manual = string.Join(", ", _runtime.Loader.ManualModules.Order(StringComparer.Ordinal));
        string listed = manual.Length == 0 ? "(none)" : manual;
        _log.Info($"fivestar-loader {RunIdentity.Line()}");
        _log.Info($"fivestar-loader pid {_processId}; manual modules: {listed}; unresolved={_options.Unresolved}");

        LoadAbiTable();

        if (_options.RunMain)
        {
            // Before loading: TLS callbacks and DllMain already run mapped code.
            _runtime.Trace.Enable(_options.TraceSystemCalls);
            _runtime.Trace.TracedModules.UnionWith(_options.TracedModules);
            _runtime.Faults.Install();
        }

        using var timers = Timers();
        CheckCpu();

        if (!ConfigureMods())
        {
            return UsageError;
        }

        var (targets, rc) = LoadTargets();
        LoadReport.Print(_runtime, targets, _options.ListExports);
        ReportMainImage();

        rc = CallExports(rc);
        rc = RunMain(rc);

        if (rc == ExecutableRunner.TimedOut)
        {
            EndTimedOutRun();
        }

        Finish();
        _log.Info($"done, exit code {rc}");

        return rc;
    }

    /// <summary>The shims the options turn off; false when one named does not exist.</summary>
    private bool DisableShims()
    {
        // The wait-object shims cost a managed call on every wait; only the hang watch reads what they record.
        if (!_options.WatchHang)
        {
            foreach (string shim in WaitObjectShims.Names)
            {
                _runtime.Shims.Disable(shim);
            }
        }

        foreach (string spec in _options.DisabledShims)
        {
            if (!_runtime.Shims.Disable(spec))
            {
                _log.Error($"no such shim {spec}");

                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Logs which AMD-only instructions the host lacks. With --cpu intel, also checks their emulation through the
    /// exception handler (installed for it when --run did not).
    /// </summary>
    private void CheckCpu()
    {
        var features = CpuFeatures.None;

        if (!_options.AssumeIntelCpu)
        {
            features = CpuFeatures.Detect();
        }

        _runtime.Cpu.ReportHost(features, _options.AssumeIntelCpu);

        if (!_options.AssumeIntelCpu)
        {
            return;
        }

        _runtime.Faults.Install();
        var result = new AmdInstructionSelfTest(_runtime.CodeHeap, _runtime.Cpu).Run();

        foreach (string failure in result.Failures)
        {
            _log.Error($"cpu self-test: {failure}");
        }

        _log.Info($"cpu self-test: {result.Cases - result.Failures.Count} of {result.Cases} cases agree with the CPU and the reference");
    }

    /// <summary>The input script, its learned screens and the present signatures its screen blocks wait for.</summary>
    private void UseInputScript(string path, FrameCapture? capture)
    {
        string screensFile = _options.ScreensFile ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FiveStar", "screens.txt");
        var screens = new ScreenStore(screensFile);
        var sampler = new ScreenSampler(signature => _runtime.Input.Seen(signature), _log);
        _runtime.D3D12X.Presenter.Screens = sampler;

        bool scriptOnly = !_options.AllowRealInput;
        var script = _runtime.Input.UseScript(path, System.Diagnostics.Stopwatch.StartNew(), () => capture?.RequestNext(), sampler.Request, screens, scriptOnly);
        _log.Info($"  [input] {script.Presses.Count} scripted presses, {script.Marks.Count} captures and learns, {script.Screens.Count - 1} screens from {path} (followed while running); {screens.Count} learned screens in {screensFile}");

        foreach (var clash in screens.Alike(script.Blocks))
        {
            _log.Warn(clash.Describe(screensFile));
        }

        if (scriptOnly)
        {
            _log.Info("  [input] script input only: the real keyboard, mouse buttons and cursor are left out (F9 hands them back, --allow-real-input keeps them)");
        }
    }

    private void LoadAbiTable()
    {
        if (_options.GdkAbiTable is not null)
        {
            _runtime.Gdk.Runtime.LoadAbiTable(_options.GdkAbiTable);
        }
        else
        {
            LoadInstalledAbi();
        }

        if (_options.InputConfig is not null)
        {
            _runtime.Input.Configure(InputConfigFile.Load(_options.InputConfig));
            _log.Info($"  [input] mapping from {_options.InputConfig}");
        }

        var capture = EnableCapture();

        if (_options.InputScript is not null)
        {
            UseInputScript(_options.InputScript, capture);
        }

        EnableDebugging();
        EnablePixCapture();

        if (_options.D3D12TraceQueues)
        {
            QueueTrace.Enable(_runtime.D3D12X.Objects, _log);
        }

        foreach (string skips in _options.D3D12Skips)
        {
            foreach (var skipped in SkippedSlot.ParseList(skips))
            {
                _runtime.D3D12X.Objects.Skip(skipped);
            }
        }

        _runtime.D3D12X.Presenter.ShowTestPattern = _options.PresentTest;
        _runtime.BvhBuilder.Enabled = _options.RayTracing;
        _runtime.BvhBuilder.GpuInventory = _options.RayTracingGpuInventory;
        _runtime.XDsp.Convolve = _options.XDspConvolution;
        _runtime.Storage.BcPack.FailMissing = _options.FailMissingBcPack;
        _runtime.Storage.BcPack.LoadTables(_options.BcPackTables);

        if (_options.FidelityFxLibrary is not null)
        {
            _runtime.FidelityFx.Load(_options.FidelityFxLibrary);
        }
    }

    /// <summary>--pix-capture: WinPixGpuCapturer.dll loaded now, before the game creates its device.</summary>
    private void EnablePixCapture()
    {
        if (_options.PixCapturePresents.Count == 0)
        {
            return;
        }

        string directory = _options.PixCaptureDirectory ?? Path.GetFullPath("pix-captures");
        var pix = new PixGpuCapture(_options.PixCapturePresents, directory, _log);
        pix.Load();
        _runtime.D3D12X.Presenter.Pix = pix;
    }

    /// <summary>--aftermath: the DLL loaded and GPU crash dumps enabled now, before the game creates its device.</summary>
    private void EnableAftermath()
    {
        if (_options.AftermathDirectory is null)
        {
            return;
        }

        if (_options.D3D12Debug)
        {
            _log.Warn("[aftermath] the D3D12 debug layer (--d3d12-debug) keeps Aftermath from initializing the device: no markers or shader debug info");
        }

        var features = new AftermathFeatures(_options.AftermathMarkers, _options.AftermathResources, _options.AftermathShaderInfo);
        var dumps = AftermathCrashDumps.Start(_options.AftermathDirectory, _options.AftermathLibrary, features, AppContext.BaseDirectory, Environment.GetEnvironmentVariable(AftermathLibrary.SdkVariable), _log);

        if (dumps is not null)
        {
            _runtime.D3D12X.Aftermath = dumps;
            DeviceLoss.BeforeEnd = dumps.Report;
        }
    }

    /// <summary>--d3d12-debug (debug layer and DRED) or --d3d12-dred (DRED alone), and --aftermath.</summary>
    private void EnableDebugging()
    {
        EnableAftermath();

        if (_options.D3D12Debug)
        {
            _runtime.D3D12X.Debugging.Enable(_options.D3D12GpuValidation);
        }
        else if (_options.D3D12Dred)
        {
            _runtime.D3D12X.Debugging.EnableDredOnly();
        }
    }

    /// <summary>Without <c>--gdk-abi</c>, the thunk ABI of the installed PC GDK's xgameruntime.lib.</summary>
    private void LoadInstalledAbi()
    {
        string? library = GdkLibraryLocator.FindInstalled(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));

        if (library is null)
        {
            _log.Warn("no --gdk-abi table and no installed PC GDK: every GDK thunk call returns REGDB_E_CLASSNOTREG");

            return;
        }

        try
        {
            _runtime.Gdk.Runtime.LoadAbiLibrary(GdkThunkLibrary.Read(File.ReadAllBytes(library)), library);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _log.Warn($"GDK library {library}: {e.Message}");
        }
    }

    /// <summary>Reads the patch files and adds the mod folders; false after a reported error.</summary>
    private bool ConfigureMods()
    {
        try
        {
            var patches = PatchFileReader.ReadPaths(_options.Patches);

            var output = _options.OutputSize;

            if (output is not null)
            {
                patches.AddRange(OutputSizePatches.For(output.Value.Width, output.Value.Height));
            }

            _runtime.Patcher.Add(patches);

            if (patches.Count > 0)
            {
                _log.Info($"patches: {patches.Count} from {string.Join(", ", _options.Patches)}");
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _log.Error($"patches: {e.Message}");

            return false;
        }

        foreach (string folder in _options.ModFolders)
        {
            if (!Directory.Exists(folder))
            {
                _log.Error($"mod folder {folder} does not exist");

                return false;
            }

            _runtime.Overlay.AddFolder(folder);
            _log.Info($"mod folder {_runtime.Overlay.Folders.Count}: {folder}");
        }

        return true;
    }

    private (List<LoadedImage> Targets, int Rc) LoadTargets()
    {
        var targets = new List<LoadedImage>();
        int rc = Success;

        foreach (string path in _options.Loads)
        {
            try
            {
                targets.Add(_runtime.Loader.Load(path, true));
            }
            catch (Exception e)
            {
                _log.Error($"load {path} failed: {e.GetType().Name}: {e.Message}");
                rc = Failure;
            }
        }

        return (targets, rc);
    }

    private void ReportMainImage()
    {
        var main = _runtime.Loader.MainImage;

        if (main is null)
        {
            return;
        }

        _log.Info($"== process identity: main image {main.Name} @ 0x{main.Base:x} ({main.Path})");

        if (_options.PatchPeb)
        {
            _runtime.Identity.PatchPeb(main.Base);
        }
    }

    private int CallExports(int rc)
    {
        var caller = new ExportCaller(_runtime, _log);
        int result = rc;

        foreach (string request in _options.Calls)
        {
            var outcomes = caller.Run(request);

            if (outcomes is null || outcomes.Any(o => o.Result is null))
            {
                result = Failure;
            }
        }

        return result;
    }

    /// <summary>--sample-threads (with --profile-threads), --profile-managed and --cpu-sample.</summary>
    private void StartSampling()
    {
        var interval = _options.SampleThreads;
        var profileStart = _options.ProfileManagedStart;
        var cpuInterval = _options.CpuSampleInterval;

        if (cpuInterval.HasValue)
        {
            new CpuSampler(_runtime.Images, _runtime.Trace, _log).Start(cpuInterval.Value, _options.CpuSampleReport);
        }

        if (interval.HasValue)
        {
            new ThreadSampler(_runtime.Images, _runtime.Trace, SampledCounters, _options.ProfileThreads, _log).Start(interval.Value);
        }

        if (profileStart.HasValue)
        {
            ManagedProfile.Start(_runtime.Images, ManagedProfile.TracePath(_options.LogFile), profileStart.Value, _options.ProfileManagedLength, _log);
        }
    }

    private int RunMain(int rc)
    {
        var main = _runtime.Loader.MainImage;

        if (!_options.RunMain || rc != Success)
        {
            return rc;
        }

        if (main is null || main.Pe.IsDll)
        {
            _log.Error("--run needs an executable as the main image");

            return Failure;
        }

        StartSampling();
        StartMemoryWatch();
        ManagedExceptions.Watch(_log);

        EnableGpuMarkers();
        Shims.D3D12X.Binding.CommandListBindingMethods.SkipComputeChains = _options.SkipComputeChains;
        Shims.D3D12X.Binding.CommandListBindingMethods.EmptyIndirectAfterSkip = _options.EmptyIndirectAfterSkip;
        ApplyTraceOptions();

        ApplyBindingOptions();

        Shims.D3D12X.RetireWaits.Enable(_options.RetireWaitsAllQueues, _log);
        Shims.D3D12X.ListSplits.Enabled = _options.SplitAtWaits;
        Diagnostics.UnmanagedGuard.LogTo(_log.Error);

        StartPageWatches();

        if (_options.SectionHeapBudget != 0)
        {
            _runtime.D3D12X.SectionHeaps.Cap = _options.SectionHeapBudget;
        }

        Shims.D3D12X.SystemMemoryReserve.Bytes = _options.ReservedSystemMemory;

        if (_options.WatchTransient)
        {
            TransientPoolWatch.Start(_log);
        }

        if (_options.WatchHang)
        {
            GameHangWatch.Start(_runtime.Images, SampledCounters, _runtime.D3D12X.Fences.DescribeWait, _log);
        }

        if (_options.WatchMovies)
        {
            GameMovieWatch.Start(_log);
        }

        StartAudioCapture();
        _runtime.D3D12X.WaitForProgramIndex();

        return new ExecutableRunner(_runtime.Faults, _log).Run(main, _options.RunTimeout);
    }

    /// <summary>The memory map (<c>--memory-map</c>) and the memory limits (<c>--min-free-memory</c>, <c>--max-commit</c>).</summary>
    /// <summary><c>--d3d12-breadcrumbs</c> and <c>--gpu-timings</c>, for the device the game creates next; where a device removal is logged.</summary>
    private void EnableGpuMarkers()
    {
        GpuBreadcrumbs.Enable(_options.D3D12Breadcrumbs);
        Shims.D3D12X.DebugLayer.GpuTimings.Enable(_options.GpuTimings);
        Shims.D3D12X.Present.FrameLog.Every = _options.FrameLog;

        if (_options.SustainedGc)
        {
            // Background gen 2 only; the ConserveMemory compactions (blocking) wait for low memory. Measured with --frame-log.
            System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
        }

        DeviceLoss.Log = _log;
        KernelGraphicsCalls.Images = _runtime.Images;

        if (_options.D3dkmtWatch)
        {
            DriverShaderTables.Name = Shims.D3D12X.QueueProgress.PipelineName;
            DriverShaderTables.More = Shims.D3D12X.Pipelines.PipelineEviction.Describe;
            DriverShaderTables.Start(() => _runtime.D3D12X.Objects.DesktopDevice, _log);
        }

        DeviceLoss.Armed = true;
        ThreadSnapshot.Stuck = id => _log.Warn($"[sample] stack capture stuck on thread {id}: no answer in {ThreadSnapshot.TimeoutMilliseconds} ms; skipped, its worker abandoned");
    }

    /// <summary><c>--watch-pool-pages</c> and <c>--watch-4g-lines</c>.</summary>
    private void StartPageWatches()
    {
        if (_options.WatchPoolPages)
        {
            Shims.D3D12X.DebugLayer.PoolPageWatch.Start(_log);
        }

        if (_options.WatchFourGiBLines)
        {
            Shims.D3D12X.DebugLayer.FourGiBLineWatch.Start(_log);
        }
    }

    /// <summary>The present readback and the draw trace, when either is asked for; null otherwise.</summary>
    private FrameCapture? EnableCapture()
    {
        if (_options.CaptureDirectory is null && !_options.D3D12TraceDraws)
        {
            return null;
        }

        var capture = new FrameCapture(_options.CaptureDirectory, _options.CapturePresents, _log);

        // --dump-frame: the present is captured too, so the census and the trace window cover the dumped frame.
        if (_options.DumpFrame > 0)
        {
            capture.Add(_options.DumpFrame);
        }

        _runtime.D3D12X.EnableCapture(capture, _options.D3D12TraceDraws, _options.D3D12TracePipeline);

        return capture;
    }

    /// <summary>The draw trace's options: dumps, probes, records, the watched target and the world trigger.</summary>
    private void ApplyTraceOptions()
    {
        Shims.D3D12X.DebugLayer.DrawTrace.DumpTargets = _options.D3D12DumpTargets;
        Shims.D3D12X.DebugLayer.DrawTrace.ProbeTraced = _options.D3D12ProbeTraced;
        Shims.D3D12X.DebugLayer.TraceRecords.Offsets = _options.D3D12TraceRecords;

        Shims.D3D12X.DebugLayer.DrawTrace.WatchShape = _options.D3D12WatchTarget;
        Shims.D3D12X.DebugLayer.DrawTrace.WatchLimit = _options.D3D12WatchLimit;
        Shims.D3D12X.DebugLayer.DrawTrace.WatchRendered = _options.D3D12WatchRendered;
        Shims.D3D12X.DebugLayer.DrawTrace.CaptureWhenWorld = _options.CaptureWhenWorld;
        Shims.D3D12X.DebugLayer.DrawTrace.LogDraws = _options.LogDraws;

        Shims.D3D12X.DebugLayer.FrameStageDump.Present = _options.DumpFrame;
        Shims.D3D12X.DebugLayer.FrameStageDump.WorldOffset = _options.DumpFrameWorldOffset;
        Shims.D3D12X.DebugLayer.FrameStageDump.AfterSeconds = _options.DumpFrameSeconds;

        Shims.D3D12X.DebugLayer.FrameStageDump.Max = _options.DumpFrameMax;
        Shims.D3D12X.DebugLayer.FrameStageDump.Filter = _options.DumpFrameFilter;
        Shims.D3D12X.DebugLayer.FrameStageDump.MinimumWidth = _options.DumpFrameMinimumWidth;
        Shims.D3D12X.DebugLayer.FrameStageDump.Directory = _options.DumpFrameDirectory ?? Path.GetFullPath($"frame-{_options.DumpFrame}");
    }

    /// <summary>Pipeline and binding options the D3D12.X shim reads as static settings.</summary>
    private void ApplyBindingOptions()
    {
        Shims.D3D12X.Pipelines.PipelineFactory.CullNoneFor = _options.ForceCullNone;
        Shims.D3D12X.Pipelines.DepthFuncOverride.Rules = _options.DepthFuncOverrides;
        Shims.D3D12X.Pipelines.ClassDepthBounds.Enabled = !_options.NoClassDepthBounds;

        Shims.D3D12X.Pipelines.SkippedPipelines.Texts = _options.SkipPipelines;
        Shims.D3D12X.Pipelines.SkippedPipelines.All = _options.NoDraws;
        Shims.D3D12X.Binding.DescriptorWindow.MinTableRecords = _options.MirrorTableRecords;
        Shims.D3D12X.Binding.TableReaches.Enabled = _options.MirrorTableReach;
        Shims.D3D12X.DebugLayer.LineViewScan.Enabled = _options.ScanLineViews;
        Shims.D3D12X.DebugLayer.FourGiBLineWatch.BreakWrites = _options.BreakLineWrites;
        Shims.D3D12X.DebugLayer.LineWriterHunt.Enabled = _options.HuntLineWriter;
    }

    private void StartMemoryWatch()
    {
        var map = new MemoryMap(_runtime.Images, _runtime.XMem, _runtime.XMemPhysical, _log);

        if (_options.MemoryMap.HasValue)
        {
            map.Start(_options.MemoryMap.Value);
        }

        if (_options.MinimumFreeMemory != 0 || _options.MaximumCommit != 0)
        {
            new MemoryLimit(_options.MinimumFreeMemory, _options.MaximumCommit, map, _log).Start();
        }
    }

    /// <summary>With --run, per-monitor DPI awareness and the console's millisecond timers for the whole process, unless <c>--desktop-timers</c>.</summary>
    private ConsoleTimers? Timers()
    {
        if (!_options.RunMain)
        {
            return null;
        }

        DpiAwareness.Take(_log);

        if (_options.DesktopTimers)
        {
            return ConsoleTimers.Desktop(_log);
        }

        return ConsoleTimers.Take(_log);
    }

    /// <summary><c>--audio-capture</c>: starts the recorder, a child copy of the loader that records this process tree.</summary>
    private void StartAudioCapture()
    {
        string? path = _options.AudioCapture;
        string? loader = Environment.ProcessPath;

        if (path is null || loader is null)
        {
            return;
        }

        var start = new System.Diagnostics.ProcessStartInfo(loader) { UseShellExecute = false };
        start.ArgumentList.Add("--record-audio");
        start.ArgumentList.Add(_processId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add(path);
        using var recorder = System.Diagnostics.Process.Start(start);
        _log.Info($"  [audio-capture] recorder pid {recorder?.Id} writes {path}");
    }

    /// <summary>The commit by category (<see cref="CommitAccounting"/>), after the write-combined walk of the same sample.</summary>
    private string Commit(long pages, long helpers)
    {
        var d3d12 = _runtime.D3D12X;
        var figures = new CommitAccounting.Figures(
            Shims.D3D12X.Pipelines.CompileBudget.ProcessPrivateBytes,
            WriteCombinedMemory.CachedBytes,
            d3d12.Textures.Placed.Bytes,
            GC.GetGCMemoryInfo().TotalCommittedBytes,
            d3d12.GameMemory.CoveredBytes,
            Shims.D3D12X.Pipelines.CompileBudget.ExpectedBytes,
            pages,
            (long)d3d12.SectionHeaps.Opened,
            helpers,
            d3d12.Textures.Placed.UsedBytes,
            d3d12.Textures.Registry.UnmappedBytes,
            d3d12.Textures.Registry.KeptBytes,
            d3d12.Textures.Uploader.StagingBytes);

        return CommitAccounting.Format(figures);
    }

    /// <summary>The loader counters each thread sample logs.</summary>
    private string SampledCounters()
    {
        Shims.D3D12X.Binding.WindowGuard.ReportCaps(_runtime.D3D12X.Presenter.Presents, _log);

        string pipelines = _runtime.D3D12X.Pipelines?.Summary ?? "pipeline slots off";

        var prebuild = Shims.D3D12X.Pipelines.JournalPrebuild.Current;

        if (prebuild is not null)
        {
            pipelines += "; " + prebuild.Summary;
        }

        long pages = _runtime.XMemPhysical.LivePages * (long)XMemPhysicalMemory.PageSize;
        string memory = ProcessMemory.Summary(_runtime.XMem.LiveBytes, pages);
        ulong? gpuSystem = null;

        var budget = _runtime.D3D12X.SectionHeaps.Budget;

        if (budget is not null && budget.TryReadSystem(out _, out var usage))
        {
            gpuSystem = usage;
        }

        long helpers = Shims.D3D12X.Pipelines.CompileBudget.HelperPrivateBytes() + (Shims.D3D12X.Pipelines.PipelineHost.Current?.PrivateBytes ?? 0);
        memory += "; " + ProcessMemory.Machine(pages, helpers, gpuSystem);

        if (budget is not null)
        {
            memory += "; " + budget.Sample();
        }

        string audio = $"audio: {_runtime.Audio.Summary}; XDsp {_runtime.XDsp.Submitted} commands";

        return $"DirectStorage {_runtime.Storage.Summary()}; {pipelines}; {_runtime.D3D12X.Presenter.Presents} presents, {_runtime.D3D12X.FrameThrottle.Summary}; {Shims.D3D12X.ListSubmissions.Summary}; {_runtime.D3D12X.Residency?.Summary ?? "section windows resident"}; {_runtime.Input.Summary}; {audio}; {memory}; {MemorySnapshot.Summary()}; {WriteCombinedMemory.Summary()}; {Commit(pages, helpers)}; {CommandObjectCounts.Summary}; {Shims.D3D12X.Binding.RawBufferViews.Summary}; {Shims.D3D12X.Binding.RtStandIns.Summary}; {Shims.D3D12X.Resources.MapReadback.Summary}{Shims.D3D12X.IndirectGather.Summary}{Shims.D3D12X.IndirectBatch.Summary}{Shims.D3D12X.Present.GpuKeepAwake.Summary}{GpuTimes()}{ViewChecks()}";
    }

    /// <summary>"; gpu over N frames: ..." with <c>--gpu-timings</c> (<see cref="Shims.D3D12X.DebugLayer.GpuTimings"/>), else empty.</summary>
    private static string ViewChecks()
    {
        if (!Shims.D3D12X.DebugLayer.ViewLiveness.Enabled)
        {
            return string.Empty;
        }

        return "; " + Shims.D3D12X.DebugLayer.ViewLiveness.Summary;
    }

    private string GpuTimes()
    {
        if (!Shims.D3D12X.DebugLayer.GpuTimings.Active)
        {
            return string.Empty;
        }

        return "; " + Shims.D3D12X.DebugLayer.GpuTimings.Report(_runtime.D3D12X.Presenter.Presents);
    }

    /// <summary>
    /// After <c>--run-timeout</c> the game's threads still run and use the shims (the render thread presents on the
    /// swap chain), so disposing the runtime under them crashes. The process ends here instead, without cleanup.
    /// </summary>
    private void EndTimedOutRun()
    {
        _runtime.D3D12X.Presenter.LogSummary();
        _runtime.Cpu.LogSummary();
        SavePipelineCache();
        _log.Info($"done, exit code {ExecutableRunner.TimedOut} (the game's threads are still running: the process ends without cleanup)");
        Console.Out.Flush();
        _ = Kernel32.TerminateProcess(Kernel32.GetCurrentProcess(), (uint)ExecutableRunner.TimedOut);
    }

    /// <summary>Writes the pipelines built so far to the disk cache, as the process ends without cleanup.</summary>
    private void SavePipelineCache()
    {
        var cache = _runtime.D3D12X.Pipelines?.Cache;

        if (cache is null)
        {
            return;
        }

        cache.Save();
        _log.Info($"  [d3d12_xs] {cache.Summary}");
    }

    private void Finish()
    {
        _runtime.Cpu.LogSummary();
        var stubs = _runtime.Stubs;

        if (stubs.CallCount > 0)
        {
            _log.Warn($"{stubs.CallCount} call(s) hit unresolved-import stubs");
        }

        if (stubs.NotImplementedCallCount > 0)
        {
            _log.Info($"{stubs.NotImplementedCallCount} call(s) hit not-implemented GDK stubs");
        }

        _runtime.Identity.RestorePeb();

        if (_options.Unload)
        {
            _runtime.Loader.UnloadAll();
        }
    }
}
