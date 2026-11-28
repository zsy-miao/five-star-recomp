// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using FiveStar.Loader.Native;
using FiveStar.Shaders.Translation;

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>
/// <c>--capture-dispatch &lt;text&gt;[@n],...</c>: the n-th Dispatch or compute ExecuteIndirect (1 by default) of a
/// compute pipeline whose name holds the text is captured into <c>--capture-dispatch-dir</c>, for
/// <c>fivestar shader replay</c> (<see cref="FiveStar.Graphics.Replay.DispatchCapture"/>). While the game records it, its
/// reach is found (<see cref="CapturePlanner"/>) and every resource it reaches is copied into a readback buffer right
/// before it and each it can write again right after it, in the game's list (<see cref="CaptureCopies"/>); once that
/// list's submission finished, the folder is written (<see cref="CaptureOutput"/>). Off by default; debug only.
/// </summary>
internal static unsafe class DispatchCaptures
{
    private const char OccurrenceSeparator = '@';

    /// <summary><c>text@pN[:k]</c>: the k-th dispatch (1 by default) from present N on.</summary>
    private const char PresentMark = 'p';
    private const char PresentOccurrenceSeparator = ':';
    private const char ListSeparator = ',';
    private const int PollMilliseconds = 200;
    private const double GiveUpSeconds = 120;

    /// <summary>Argument records of an ExecuteIndirect kept at most.</summary>
    private const uint MaxIndirectRecords = 64;

    /// <summary>ID3D12Fence::GetCompletedValue (d3d12.h).</summary>
    private const int CompletedSlot = 8;

    private const ulong BytesPerMebibyte = 1 << 20;

    private static readonly ConcurrentDictionary<string, byte[]> Programs = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<int, int> Seen = new();
    private static readonly ConcurrentDictionary<nint, DispatchCaptureJob> Open = new();
    private static readonly ConcurrentDictionary<nint, long> Resets = new();
    private static readonly List<DispatchCaptureJob> Pending = [];
    private static readonly List<DispatchCaptureJob> Written = [];
    private static readonly object Gate = new();
    private static int _polling;
    private static int _captures;
    private static int _failed;
    private static IReadOnlyList<CaptureSpec> _specs = [];

    /// <summary>The entries; empty when off.</summary>
    public static IReadOnlyList<CaptureSpec> Specs
    {
        get => _specs;
        set
        {
            _specs = value;
            Volatile.Write(ref _failed, 0);
        }
    }

    /// <summary>Where the captures go, one folder each.</summary>
    public static string Folder { get; set; } = "captures";

    /// <summary>The ahead-of-time translation (--translated-dir), whose HLSL a capture keeps when it has the program's.</summary>
    public static string? TranslatedFolder { get; set; }

    /// <summary>Whether captures are on: entries given and none failed (<see cref="Fail"/>).</summary>
    public static bool Enabled => Specs.Count > 0 && Volatile.Read(ref _failed) == 0;

    /// <summary>Entries of a comma-separated list: <c>text</c> or <c>text@n</c>.</summary>
    public static IReadOnlyList<CaptureSpec> Parse(string list)
    {
        return [.. list.Split(ListSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(ParseEntry)];
    }

    private static CaptureSpec ParseEntry(string text)
    {
        int at = text.LastIndexOf(OccurrenceSeparator);

        if (at < 0)
        {
            return new CaptureSpec(text, 1, 0);
        }

        var count = text.AsSpan(at + 1);
        long fromPresent = 0;

        if (count.Length > 0 && count[0] == PresentMark)
        {
            int colon = count.IndexOf(PresentOccurrenceSeparator);
            var present = colon < 0 ? count[1..] : count[1..colon];
            fromPresent = long.Parse(present, NumberStyles.None, CultureInfo.InvariantCulture);
            count = colon < 0 ? "1".AsSpan() : count[(colon + 1)..];
        }

        int occurrence = int.Parse(count, NumberStyles.None, CultureInfo.InvariantCulture);

        if (occurrence < 1)
        {
            throw new FormatException($"--capture-dispatch {text}: occurrences count from 1");
        }

        return new CaptureSpec(text[..at], occurrence, fromPresent);
    }

    /// <summary>A compute program was given to a pipeline: its DXIL is kept when an entry names it.</summary>
    public static void NoteProgram(string name, ReadOnlySpan<byte> dxil)
    {
        if (Specs.Any(spec => name.Contains(spec.Text, StringComparison.OrdinalIgnoreCase)))
        {
            Programs[name] = dxil.ToArray();
        }
    }

    /// <summary>The DXIL kept of <paramref name="name"/>; null when none was.</summary>
    public static byte[]? Dxil(string name)
    {
        return Programs.GetValueOrDefault(name);
    }

    /// <summary>A list's Reset: the readback buffers of its earlier recordings can go once written.</summary>
    public static void Reset(nint list)
    {
        Resets.AddOrUpdate(list, 1, static (_, count) => count + 1);
    }

    /// <summary>Before a Dispatch of <paramref name="list"/>: captured when it is an entry's occurrence.</summary>
    public static void Before(XboxObject* list, uint x, uint y, uint z)
    {
        try
        {
            var plan = Match(list);

            if (plan is not null)
            {
                plan.Direct = new FiveStar.Graphics.Replay.CapturedDispatch(x, y, z);
                Start(list, plan);
            }
        }
        catch (Exception exception)
        {
            Fail(list, exception);
        }
    }

    /// <summary>Before a compute ExecuteIndirect (wrapped argument and count buffers): captured with its records.</summary>
    public static void BeforeIndirect(XboxObject* list, nint arguments, ulong argumentOffset, uint stride, uint maxCount, nint count, ulong countOffset)
    {
        try
        {
            var plan = Match(list);

            if (plan is null)
            {
                return;
            }

            nint records = BufferCopies.Desktop(arguments, ref argumentOffset);
            nint counted = count == 0 ? 0 : BufferCopies.Desktop(count, ref countOffset);
            plan.Indirect = new IndirectArguments(records, argumentOffset, stride, Math.Min(maxCount, MaxIndirectRecords), counted, countOffset);
            Start(list, plan);
        }
        catch (Exception exception)
        {
            Fail(list, exception);
        }
    }

    /// <summary>After the work: a capture opened before it copies its outputs.</summary>
    public static void After(XboxObject* list)
    {
        try
        {
            Finish(list);
        }
        catch (Exception exception)
        {
            Fail(list, exception);
        }
    }

    /// <summary>
    /// A capture threw: logged once and every capture is turned off, so the game's Dispatch and ExecuteIndirect go on
    /// as without <c>--capture-dispatch</c> (an exception there made the guard drop every later dispatch, w161).
    /// </summary>
    private static void Fail(XboxObject* list, Exception exception)
    {
        Open.TryRemove((nint)list, out _);

        if (Interlocked.Exchange(ref _failed, 1) == 0)
        {
            ShimContext.Log?.Warn($"[capture] capture failed, --capture-dispatch is off from now on: {exception.GetType().Name}: {exception.Message}\n{exception.StackTrace}");
        }
    }

    private static void Finish(XboxObject* list)
    {
        if (!Open.TryRemove((nint)list, out var job))
        {
            return;
        }

        CaptureCopies.Record(list, job.Plan, job.Readback, after: true);

        lock (Gate)
        {
            Pending.Add(job);
        }

        if (Interlocked.Exchange(ref _polling, 1) == 0)
        {
            new Thread(Poll) { IsBackground = true, Name = "fivestar dispatch capture" }.Start();
        }
    }

    /// <summary>The plan of the bound compute work when it is the occurrence an entry asks for; null otherwise.</summary>
    private static CapturePlan? Match(XboxObject* list)
    {
        nint pipeline = ListActivity.Of(list)->Pipeline;

        if (pipeline == 0)
        {
            return null;
        }

        string name = QueueProgress.PipelineName(pipeline);

        for (int index = 0; index < Specs.Count; index++)
        {
            var spec = Specs[index];

            bool counted = name.Contains(spec.Text, StringComparison.OrdinalIgnoreCase) && Pipelines.StandInUse.Frame >= spec.FromPresent;

            if (counted && Seen.AddOrUpdate(index, 1, static (_, count) => count + 1) == spec.Occurrence)
            {
                ShimContext.Log?.Info($"[capture] {name}: dispatch {spec.Occurrence} from present {spec.FromPresent}, at present {Pipelines.StandInUse.Frame}");

                return CapturePlanner.Plan(list, name);
            }
        }

        return null;
    }

    /// <summary>Maps the plan's memory, lays out and makes its readback buffer and records the copies before the work.</summary>
    private static void Start(XboxObject* list, CapturePlan plan)
    {
        var d3d12x = ShimContext.Runtime.D3D12X;
        CaptureMemory.Map(plan, d3d12x.MemoryViews, FirstPair(plan));
        CaptureCopies.Layout(plan);
        var readback = CaptureCopies.Readback(plan);
        CaptureCopies.Record(list, plan, readback, after: false);

        string folder = Path.Combine(Folder, FolderName(plan.Pipeline, Interlocked.Increment(ref _captures)));
        var job = new DispatchCaptureJob((nint)list, plan, readback, folder, ListSubmission.Of((nint)list)->Value, Resets.GetValueOrDefault((nint)list), Stopwatch.GetTimestamp());
        Open[(nint)list] = job;
        ShimContext.Runtime.Log.Info($"  [capture] {plan.Pipeline}: {plan.Sources.Count} resources, {plan.Views.Count} views, {plan.ReadbackBytes / BytesPerMebibyte} MiB read back; to {folder}");
    }

    /// <summary>The first replay heap index of the memory ranges' view pairs: past the reserved slots and every captured view, even.</summary>
    private static uint FirstPair(CapturePlan plan)
    {
        uint first = Math.Max(NullTextureSlots.End, plan.Views.Select(view => view.Index + 1).DefaultIfEmpty(0u).Max());

        return (first + CaptureMemory.PairSize - 1) & ~(CaptureMemory.PairSize - 1);
    }

    private static string FolderName(string pipeline, int sequence)
    {
        var name = pipeline.ToCharArray();

        for (int index = 0; index < name.Length; index++)
        {
            if (!char.IsLetterOrDigit(name[index]) && name[index] != '_')
            {
                name[index] = '-';
            }
        }

        return string.Create(CultureInfo.InvariantCulture, $"{new string(name)}-{sequence}");
    }

    private static void Poll()
    {
        while (true)
        {
            Thread.Sleep(PollMilliseconds);
            List<DispatchCaptureJob> done;

            lock (Gate)
            {
                done = [.. Pending.Where(Finished)];
                Pending.RemoveAll(done.Contains);
                Release();
            }

            foreach (var job in done)
            {
                Write(job);
            }
        }
    }

    /// <summary>Whether the list was submitted after the capture and that submission finished; one waiting too long is given up.</summary>
    private static bool Finished(DispatchCaptureJob job)
    {
        var submission = ListSubmission.Of(job.List);

        if (Stopwatch.GetElapsedTime(job.At).TotalSeconds > GiveUpSeconds)
        {
            return true;
        }

        if (submission->Fence == 0 || submission->Value <= job.Submitted)
        {
            return false;
        }

        return ((delegate* unmanaged<nint, ulong>)ComObject.Slot(submission->Fence, CompletedSlot))(submission->Fence) >= submission->Value;
    }

    private static void Write(DispatchCaptureJob job)
    {
        var log = ShimContext.Runtime.Log;

        try
        {
            void* data;
            job.Readback.Map(0, null, &data).CheckError();

            try
            {
                var capture = CaptureOutput.Write(job.Plan, job.Folder, new ReadOnlySpan<byte>(data, checked((int)job.Plan.ReadbackBytes)));
                log.Info($"  [capture] {job.Plan.Pipeline} written to {job.Folder}: {capture.Dispatches.Count} dispatches, {capture.Resources.Count} resources, {capture.Notes.Count} notes");
            }
            finally
            {
                job.Readback.Unmap(0);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or SharpGen.Runtime.SharpGenException)
        {
            log.Warn($"  [capture] {job.Plan.Pipeline}: not written ({exception.Message})");
        }

        lock (Gate)
        {
            Written.Add(job);
        }
    }

    /// <summary>Readback buffers of written captures whose list was reset since (no execution can write them any more).</summary>
    private static void Release()
    {
        for (int i = Written.Count - 1; i >= 0; i--)
        {
            var job = Written[i];

            if (Resets.GetValueOrDefault(job.List) == job.Resets)
            {
                continue;
            }

            job.Readback.Dispose();
            Written.RemoveAt(i);
        }
    }
}
