// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text;
using FiveStar.Loader.Diagnostics;

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>
/// <c>--watch-4g-lines</c>: two world loads faulted on game records overwritten just past a 4 GiB line of the game's
/// GPU-writable heap (0x252_0000_2840, 0x157_0000_0030) with consecutive values, as a GPU store whose address lost its
/// low dword would write them. The first <see cref="WatchedBytes"/> after every 4 GiB line inside each GPU-writable
/// game allocation, and of each one that starts on a line, are snapshot when found and compared every <see cref="PollMilliseconds"/> ms; a change is logged
/// with its dword runs, old and new bytes (flagging a run of consecutive values), and every queue's executions retired
/// since the last check (<see cref="QueueProgress.DescribeSince"/>, their last pipelines). A change with no execution
/// retired since is the CPU's (the game keeps objects there): it is counted, and logged only when it holds an index run.
/// </summary>
internal static unsafe class FourGiBLineWatch
{
    /// <summary>Consecutive values that make a change look like an index array.</summary>
    public const int IndexRun = 4;

    /// <summary>Bytes watched after each line: one page of the address map.</summary>
    private const int WatchedBytes = 64 * Kibibyte;
    private const int Kibibyte = 1024;

    private const int PollMilliseconds = 10;
    private const int ScanMilliseconds = 2000;
    private const int DetailedReports = 32;
    private const int IndexReports = 192;
    private const int ShownRuns = 4;
    private const int ShownBytes = 64;

    private const int ShownPipelines = 16;

    /// <summary>The game's graphics heap (1.0.2.1), whose start holds its static quad buffers.</summary>
    private const ulong GraphicsHeapBytes = 0x89700000;

    private static readonly Dictionary<ulong, byte[]> Snapshots = [];

    /// <summary>Where <c>--break-line-writes</c> breaks in the graphics heap: its first index, an offset inside the id runs, the composite's quad.</summary>
    private static readonly ulong[] BreakOffsets = [0x0, 0xbf8, 0x1be0];
    private static readonly Dictionary<ulong, ulong> Sizes = [];

    private static readonly HashSet<ulong> Unreadable = [];
    private static readonly Dictionary<nint, int> WindowPipelines = [];
    private static readonly uint[] MarkersNow = new uint[GpuBreadcrumbs.Slots];
    private static readonly uint[] MarkersAfter = new uint[GpuBreadcrumbs.Slots];
    private static readonly uint[] MarkersBefore = new uint[GpuBreadcrumbs.Slots];
    private static bool _markersTaken;
    private static int _windows;
    private static LoaderLog? _log;
    private static ulong[] _since = [];
    private static long _gpuChanges;
    private static long _cpuChanges;
    private static long _reports;
    private static long _indexReports;

    /// <summary>Lines watched and changes seen, for the exit summary.</summary>
    public static string Summary => $"[4g lines] {Snapshots.Count} lines watched, {Interlocked.Read(ref _gpuChanges)} changes with GPU work retired, {Interlocked.Read(ref _cpuChanges)} without";

    /// <summary><c>--break-line-writes</c>: hardware write breakpoints at the start of the game's graphics heap.</summary>
    public static bool BreakWrites { get; set; }

    /// <summary>Whether the watch runs.</summary>
    public static bool Started { get; private set; }

    public static void Start(LoaderLog log)
    {
        _log = log;
        Started = true;
        var thread = new Thread(Watch) { IsBackground = true, Name = "fivestar 4g lines" };
        thread.Start();
        log.Info($"[4g lines] watching the first {WatchedBytes / Kibibyte} KiB after each 4 GiB line of GPU-writable game memory every {PollMilliseconds} ms");
    }

    private static void Watch()
    {
        // Not long.MinValue: now - scanned would overflow and no scan would ever be due (w304 to w306 watched nothing).
        long scanned = Environment.TickCount64 - ScanMilliseconds;

        while (true)
        {
            long now = Environment.TickCount64;

            if (now - scanned >= ScanMilliseconds)
            {
                Scan();
                WriteBreakpoints.Refresh();
                scanned = now;
            }

            Check();
            WriteBreakpoints.Drain(_log!);
            Thread.Sleep(PollMilliseconds);
        }
    }

    /// <summary>Takes a snapshot of each line of the GPU-writable allocations not watched yet.</summary>
    private static void Scan()
    {
        foreach (var (start, bytes) in ShimContext.Runtime.XMemPhysical.Graphics.Writable())
        {
            var lines = LineChanges.Lines((ulong)start, bytes);

            // An allocation whose address the system chose starts on a line itself (AlignedReservations), and the game
            // keeps its static geometry there: the full-screen quad at + 0x1be0 was overwritten with an index run.
            if (bytes != 0 && (ulong)start % LineChanges.Line == 0)
            {
                lines.Insert(0, (ulong)start);
                Sizes[(ulong)start] = bytes;
                Break((ulong)start, bytes);
            }

            foreach (ulong line in lines)
            {
                int watched = (int)Math.Min(WatchedBytes, (ulong)start + bytes - line);

                var snapshot = Snapshots.ContainsKey(line) ? null : Read(line, watched);

                if (snapshot is not null)
                {
                    Snapshots[line] = snapshot;
                    _log?.Info($"[4g lines] watching 0x{line:x}, {watched} bytes into the allocation at 0x{start:x}");
                }
                else if (!Snapshots.ContainsKey(line) && Unreadable.Add(line))
                {
                    _log?.Info($"[4g lines] 0x{line:x} of the allocation at 0x{start:x} cannot be read yet");
                }
            }
        }
    }

    private static void Check()
    {
        var completed = QueueProgress.Completed();
        bool retired = Retired(completed);

        // Before the memory is read: a write seen in this check or the next one came after these markers.
        GpuBreadcrumbs.Snapshot(MarkersNow);

        foreach (var (line, before) in Snapshots.ToArray())
        {
            var after = Read(line, before.Length);

            if (after is null)
            {
                Snapshots.Remove(line);

                continue;
            }

            var runs = LineChanges.Changed(before, after);

            if (runs.Count != 0)
            {
                Changed(line, before, after, runs, retired);
                Snapshots[line] = after;
            }
        }

        _since = completed;
        MarkersNow.CopyTo(MarkersBefore, 0);
        _markersTaken = true;
    }

    private static void Changed(ulong line, byte[] before, byte[] after, List<(int Offset, int Bytes)> runs, bool retired)
    {
        bool index = runs.Exists(run => LineChanges.ConsecutiveValues(after.AsSpan(run.Offset, run.Bytes)).Count >= IndexRun);

        if (retired)
        {
            Interlocked.Increment(ref _gpuChanges);
        }
        else
        {
            Interlocked.Increment(ref _cpuChanges);
        }

        long count = Interlocked.Increment(ref _reports);

        if (index)
        {
            Window(line);
        }

        if (!index && (!retired || (count > DetailedReports && (count & (count - 1)) != 0)))
        {
            return;
        }

        // A writer that runs every frame: its first reports, then every power of two.
        long runs64 = index ? Interlocked.Increment(ref _indexReports) : 0;

        if (runs64 > IndexReports && (runs64 & (runs64 - 1)) != 0)
        {
            return;
        }

        Report(line, before, after, runs, count, index, retired);
    }

    private static void Report(ulong line, byte[] before, byte[] after, List<(int Offset, int Bytes)> runs, long count, bool index, bool retired)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"[4g lines] #{count}: 0x{line:x} changed in {runs.Count} runs");

        if (index)
        {
            WriteBreakpoints.Summarize(text);
            Windows(text, line);
        }

        foreach (var run in runs.Take(ShownRuns))
        {
            text.Append("\n      ").Append(LineChanges.Describe(before, after, run, ShownBytes));
        }

        if (retired)
        {
            text.Append("\n      executions retired since the last check:");
            QueueProgress.DescribeSince(text, _since);
        }
        else
        {
            text.Append("\n      no execution retired since the last check: the CPU wrote it, or GPU work that still runs");
        }

        _log?.Warn(text.ToString());
    }

    /// <summary>
    /// <c>--break-line-writes</c>: when the game's 2.2 GiB graphics heap is first seen, every thread gets hardware write
    /// breakpoints at <see cref="BreakOffsets"/> of it (VirtualProtect fails on the heap, w317): a CPU writer of the id
    /// runs is logged with its instruction, and a GPU writer raises nothing.
    /// </summary>
    private static void Break(ulong start, ulong bytes)
    {
        if (!BreakWrites || bytes != GraphicsHeapBytes || WriteBreakpoints.Active || _log is null)
        {
            return;
        }

        var addresses = new ulong[BreakOffsets.Length];

        for (int i = 0; i < addresses.Length; i++)
        {
            addresses[i] = start + BreakOffsets[i];
        }

        WriteBreakpoints.Arm(addresses, _log);
    }

    /// <summary>
    /// With <c>--d3d12-breadcrumbs</c>: counts, for an index run in the graphics heap, each pipeline and command with an
    /// operation between the markers taken before the last check read the memory and now, after this check read it:
    /// the GPU writer's operation is among them every time, the others come and go.
    /// </summary>
    private static void Window(ulong line)
    {
        if (!GpuBreadcrumbs.Active || !_markersTaken || Sizes.GetValueOrDefault(line) != GraphicsHeapBytes)
        {
            return;
        }

        var pipelines = new HashSet<nint>();
        GpuBreadcrumbs.Snapshot(MarkersAfter);
        GpuBreadcrumbs.CollectWindow(MarkersBefore, MarkersAfter, pipelines);
        _windows++;

        foreach (nint pipeline in pipelines)
        {
            WindowPipelines[pipeline] = WindowPipelines.GetValueOrDefault(pipeline) + 1;
        }
    }

    private static void Windows(StringBuilder text, ulong line)
    {
        if (_windows == 0 || Sizes.GetValueOrDefault(line) != GraphicsHeapBytes)
        {
            return;
        }

        text.Append(CultureInfo.InvariantCulture, $"\n      GPU operations in the {_windows} windows of such changes, by pipeline ({WindowPipelines.Count} in all):");

        foreach (var (pipeline, windows) in WindowPipelines.OrderByDescending(pair => pair.Value).Take(ShownPipelines))
        {
            text.Append(CultureInfo.InvariantCulture, $"\n        in {windows}: {QueueProgress.PipelineName(pipeline)}");
        }
    }

    /// <summary>Whether a queue finished work since the last check.</summary>
    private static bool Retired(ulong[] completed)
    {
        for (int q = 0; q < completed.Length; q++)
        {
            ulong seen = 0;

            if (q < _since.Length)
            {
                seen = _since[q];
            }

            if (completed[q] > seen)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The bytes at <paramref name="line"/> through the kernel; null when the game freed them.</summary>
    private static byte[]? Read(ulong line, int bytes)
    {
        var data = new byte[bytes];
        nuint done = 0;

        fixed (byte* to = data)
        {
            if (Native.Kernel32.ReadProcessMemory(Native.Kernel32.GetCurrentProcess(), (nint)line, to, (nuint)bytes, &done) != 0 && done == (nuint)bytes)
            {
                return data;
            }

            // The kernel refuses the copy for some of the game's graphics memory (the heap at the start of w305's
            // 0x26000000000 was never watched): read it here once both ends are committed and readable.
            if (bytes == 0 || !GameHangWatch.IsReadable(line) || !GameHangWatch.IsReadable(line + (ulong)bytes - 1))
            {
                return null;
            }

            Buffer.MemoryCopy((void*)line, to, bytes, bytes);
        }

        return data;
    }
}
