// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text;
using FiveStar.Loader.Native;

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>
/// <c>--hunt-line-writer</c> (with <c>--watch-4g-lines</c> and <c>--d3d12-breadcrumbs</c>): names the GPU operation
/// that writes the id runs over the start of the game's graphics heap. The GPU runs a frame's lists in a burst too
/// short for the watch's 10 ms polls to tell its operations apart (w319: 16 pipelines in every window). Once the watch
/// sees the first id run, every game command list runs alone, one at a time over all queues, and the CPU spins until
/// its fence while it reads the list's breadcrumb marker and <see cref="WatchedBytes"/> of the heap at
/// <see cref="WatchedOffset"/>, which it keeps filled with <see cref="Sentinel"/>: a dword that is no longer the
/// sentinel was written by the operation the marker stood at (the marker read before the last clean look to the one
/// read after the dirty look). The first hits are logged with the operation, its tables' window offsets and their
/// first records, then the hits are counted per pipeline. The watched range is the game's static quad index buffer,
/// which the id runs overwrite anyway.
/// </summary>
internal static unsafe class LineWriterHunt
{
    /// <summary>Inside the id runs of every boot looked at (w307 to w319), in the game's quad index buffer.</summary>
    private const ulong WatchedOffset = 0x800;
    private const int WatchedBytes = 0x800;
    private const uint Sentinel = 0x5EA15EA1;

    private const int GetCompletedValueSlot = 8;
    private const long TimeoutMilliseconds = 500;
    private const int FullReports = 24;
    private const int ShownOperations = 6;
    private const int ShownRecords = 8;
    private const int ShownPipelines = 12;
    private const int RecordDwords = 4;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, int> Exact = [];
    private static readonly Dictionary<string, int> Ranged = [];
    private static ulong _heap;
    private static int _hits;
    private static int _outside;
    private static int _timeouts;

    /// <summary><c>--hunt-line-writer</c>.</summary>
    public static bool Enabled { get; set; }

    /// <summary>Whether lists run alone now: set once the watch saw an id run at the heap's start.</summary>
    public static bool Armed => Volatile.Read(ref _heap) != 0;

    /// <summary>The 4 GiB line watch saw an id run at the start of the graphics heap at <paramref name="heap"/>.</summary>
    public static void Arm(ulong heap)
    {
        if (!Enabled || !GpuBreadcrumbs.Active || Armed)
        {
            return;
        }

        lock (Gate)
        {
            Fill(heap);
            Volatile.Write(ref _heap, heap);
        }

        ShimContext.Runtime.Log.Warn($"[line hunt] armed: every command list runs alone from now on, 0x{heap + WatchedOffset:x} to 0x{heap + WatchedOffset + WatchedBytes:x} holds 0x{Sentinel:x}");
    }

    /// <summary>Before a list runs alone: takes the turn; a write since the last list ended was not a game list's.</summary>
    [HotPath]
    public static void Enter()
    {
        Monitor.Enter(Gate);
        int dirty = Dirty(_heap);

        if (dirty >= 0)
        {
            int count = Interlocked.Increment(ref _outside);

            if (count <= FullReports || (count & (count - 1)) == 0)
            {
                ShimContext.Runtime.Log.Warn($"[line hunt] outside #{count}: {Changed(dirty)} between two game lists (the loader's own GPU work, or a list that had not finished within {TimeoutMilliseconds} ms)");
            }

            Fill(_heap);
        }
    }

    /// <summary>After <paramref name="list"/> was submitted alone to <paramref name="queue"/>: spins until it is done, then gives the turn back.</summary>
    [HotPath]
    public static void Watch(XboxQueueObject* queue, nint list, bool wrapped)
    {
        try
        {
            uint serial = wrapped ? ListActivity.Of((XboxObject*)list)->Serial : 0;
            nint fence = queue->Fence;
            ulong value = Volatile.Read(ref queue->Signaled);

            if (fence == 0)
            {
                return;
            }

            long start = Environment.TickCount64;
            int previous = GpuBreadcrumbs.Position(serial);

            while (true)
            {
                bool done = Completed(fence) >= value;
                int position = GpuBreadcrumbs.Position(serial);
                int dirty = Dirty(_heap);

                if (dirty >= 0)
                {
                    Hit(queue, list, serial, previous, GpuBreadcrumbs.Position(serial), dirty);
                    Fill(_heap);
                }

                previous = position;

                if (done)
                {
                    return;
                }

                if (Environment.TickCount64 - start > TimeoutMilliseconds)
                {
                    Timeout(queue, list);

                    return;
                }
            }
        }
        finally
        {
            Monitor.Exit(Gate);
        }
    }

    private static void Timeout(XboxQueueObject* queue, nint list)
    {
        int count = Interlocked.Increment(ref _timeouts);

        if (count <= ShownOperations || (count & (count - 1)) == 0)
        {
            ShimContext.Runtime.Log.Warn($"[line hunt] timeout #{count}: list 0x{list:x} on queue #{queue->Number} had not finished after {TimeoutMilliseconds} ms (it waits for another queue); the next list runs beside it");
        }
    }

    private static void Hit(XboxQueueObject* queue, nint list, uint serial, int from, int to, int dirty)
    {
        int count = Interlocked.Increment(ref _hits);
        var names = new List<string>();

        for (int index = from; index <= to && names.Count < ShownOperations; index++)
        {
            names.Add(GpuBreadcrumbs.OperationText(serial, index, QueueProgress.PipelineName) ?? "an operation not kept");
        }

        var tally = from == to ? Exact : Ranged;

        foreach (string name in names)
        {
            string pipeline = Pipeline(name);
            tally[pipeline] = tally.GetValueOrDefault(pipeline) + 1;
        }

        if (count > FullReports && (count & (count - 1)) != 0)
        {
            return;
        }

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"[line hunt] #{count} (present {ShimContext.Runtime.D3D12X.Presenter.Presents}): {Changed(dirty)} while queue #{queue->Number} ran list 0x{list:x} alone, recording {serial}, operations {from} to {to}:");

        for (int i = 0; i < names.Count; i++)
        {
            text.Append(CultureInfo.InvariantCulture, $"\n      {from + i}: {names[i]}");
        }

        if (from == to)
        {
            Records(text, serial, from);
        }

        Tally(text, "one operation in the window", Exact);
        Tally(text, "several operations in the window", Ranged);
        ShimContext.Runtime.Log.Warn(text.ToString());
    }

    /// <summary>The pipeline of an operation's text (<see cref="GpuBreadcrumbs.OperationText"/>), or the text of a command.</summary>
    private static string Pipeline(string operation)
    {
        const string Mark = "pipeline ";
        int at = operation.IndexOf(Mark, StringComparison.Ordinal);

        if (at < 0)
        {
            return operation;
        }

        int end = operation.IndexOf(';', at);

        return end < 0 ? operation[(at + Mark.Length)..] : operation[(at + Mark.Length)..end];
    }

    /// <summary>The first records of the operation's tables as the window holds them now: the V#s its stores go through.</summary>
    private static void Records(StringBuilder text, uint serial, int index)
    {
        uint* tables = stackalloc uint[MarkedOperation.MaxTables];
        int count = GpuBreadcrumbs.TableOffsets(serial, index, tables);
        var window = ShimContext.Runtime.D3D12X.Bindings.Window;
        byte* data = window.Data;

        for (int t = 0; t < count && data != null; t++)
        {
            text.Append(CultureInfo.InvariantCulture, $"\n      table at window 0x{tables[t]:x}:");

            for (uint r = 0; r < ShownRecords; r++)
            {
                ulong offset = tables[t] + (r * Binding.DescriptorWindow.ViewRecordBytes);

                if (offset + Binding.DescriptorWindow.ViewRecordBytes > PoolStoreTrace.TraceBase)
                {
                    break;
                }

                uint* record = (uint*)(data + offset);
                text.Append(CultureInfo.InvariantCulture, $" [{r}]");

                for (int d = 0; d < RecordDwords; d++)
                {
                    text.Append(CultureInfo.InvariantCulture, $" {record[d]:x8}");
                }
            }
        }
    }

    private static void Tally(StringBuilder text, string title, Dictionary<string, int> tally)
    {
        if (tally.Count == 0)
        {
            return;
        }

        text.Append(CultureInfo.InvariantCulture, $"\n      hits with {title}, by pipeline:");

        foreach (var (pipeline, hits) in tally.OrderByDescending(pair => pair.Value).Take(ShownPipelines))
        {
            text.Append(CultureInfo.InvariantCulture, $"\n        {hits}: {pipeline}");
        }
    }

    private static string Changed(int dirty)
    {
        uint* watched = (uint*)(_heap + WatchedOffset);
        int dwords = 0;

        for (int i = 0; i < WatchedBytes / sizeof(uint); i++)
        {
            if (watched[i] != Sentinel)
            {
                dwords++;
            }
        }

        return string.Create(CultureInfo.InvariantCulture, $"{dwords} dwords written from heap + 0x{WatchedOffset + ((ulong)dirty * sizeof(uint)):x} (0x{watched[dirty]:x}, 0x{watched[Math.Min(dirty + 1, (WatchedBytes / sizeof(uint)) - 1)]:x})");
    }

    /// <summary>The first watched dword that is not the sentinel; -1 when all are.</summary>
    [HotPath]
    private static int Dirty(ulong heap)
    {
        return new ReadOnlySpan<uint>((void*)(heap + WatchedOffset), WatchedBytes / sizeof(uint)).IndexOfAnyExcept(Sentinel);
    }

    private static void Fill(ulong heap)
    {
        new Span<uint>((void*)(heap + WatchedOffset), WatchedBytes / sizeof(uint)).Fill(Sentinel);
    }

    private static ulong Completed(nint fence)
    {
        return ((delegate* unmanaged<nint, ulong>)ComObject.Slot(fence, GetCompletedValueSlot))(fence);
    }
}
