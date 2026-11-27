// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Globalization;
using FiveStar.Loader.Shims.D3D12X.Binding;

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>
/// <c>--scan-line-views</c>: which pipelines hold a buffer V# that starts just past a 4 GiB line. The game's graphics
/// heap starts on such a line with its static quad index and vertex buffers, and a compute output list written there
/// every frame destroyed them (the frozen PROLOGUE card). Before a draw or dispatch (each pipeline at most once every
/// <see cref="PipelineMilliseconds"/> ms) the records its programs reach in each table (<see cref="TableReaches"/>, at
/// least <see cref="MinRecords"/>) are read from the window, and each buffer record whose base lies in the first
/// <see cref="LineBytes"/> after a line, or that keeps the heap index of a view made there, is logged: the first
/// <see cref="PerPipeline"/> per pipeline. CPU only; the window is write-combined, hence the rate limit.
/// </summary>
internal static unsafe class LineViewScan
{
    private const uint LineBytes = 0x10000;
    private const uint MinRecords = 64;
    private const uint MaxRecords = 1024;
    private const int PerPipeline = 6;
    private const long PipelineMilliseconds = 5000;
    private const uint HighMask = 0xFFFF;
    private const int StrideShift = 16;
    private const uint StrideMask = 0x3FFF;
    private const int RecordsDword = 2;
    private const int FormatDword = 3;
    private const uint FormatMask = 0xFFF;
    private const uint FormatTail = 0xFAC;

    private static readonly ConcurrentDictionary<nint, long> Scanned = new();
    private static readonly ConcurrentDictionary<nint, int> Logged = new();
    private static readonly ConcurrentDictionary<uint, ulong> KeptAtLine = new();

    [ThreadStatic]
    private static ulong _pending;

    /// <summary>Whether the scan runs.</summary>
    public static bool Enabled { get; set; }

    /// <summary>The console address of the view a kept heap index names; 0 when it is none made just past a line.</summary>
    public static ulong StartOf(uint desktop)
    {
        return KeptAtLine.GetValueOrDefault(desktop);
    }

    /// <summary>A buffer record at console address <paramref name="address"/> is about to be written with its heap index kept.</summary>
    public static void Keeping(ulong address)
    {
        _pending = 0;

        if (Enabled && address != 0 && (uint)address < LineBytes)
        {
            _pending = address;
        }
    }

    /// <summary>The record of desktop index <paramref name="desktop"/> is written; <paramref name="kept"/> when it keeps the heap index.</summary>
    public static void Wrote(uint desktop, bool kept)
    {
        ulong address = _pending;
        _pending = 0;

        if (!Enabled)
        {
            return;
        }

        if (kept && address != 0)
        {
            if (KeptAtLine.TryAdd(desktop, address))
            {
                ShimContext.Runtime.Log.Info(string.Create(CultureInfo.InvariantCulture, $"  [line views] heap index {desktop} is kept by a buffer record at 0x{address:x}"));
                PoolStoreTrace.AddView(desktop);
            }

            return;
        }

        KeptAtLine.TryRemove(desktop, out _);
    }

    /// <summary>Before work of <paramref name="pipeline"/> with the user data <paramref name="data"/>.</summary>
    public static void Before(nint pipeline, uint* data, bool compute)
    {
        long now = Environment.TickCount64;

        if (pipeline == 0 || (Scanned.TryGetValue(pipeline, out long last) && now - last < PipelineMilliseconds) || Logged.GetValueOrDefault(pipeline) >= PerPipeline)
        {
            return;
        }

        Scanned[pipeline] = now;
        byte* window = ShimContext.Runtime.D3D12X.Bindings.Window.Data;

        if (window == null)
        {
            return;
        }

        foreach (var entry in TableReaches.AllOf(pipeline))
        {
            if (entry.Sgpr >= CommandListBindings.UserDataDwords)
            {
                continue;
            }

            uint offset = data[entry.Sgpr];

            if (offset >= DescriptorWindow.NullTableBase || offset % DescriptorWindow.ViewRecordBytes != 0)
            {
                continue;
            }

            uint records = Math.Clamp(entry.Records, MinRecords, MaxRecords);

            for (uint i = 0; i < records && offset + ((i + 1) * DescriptorWindow.ViewRecordBytes) <= DescriptorWindow.NullTableBase; i++)
            {
                Record(pipeline, compute, entry, i, (uint*)(window + offset + (i * DescriptorWindow.ViewRecordBytes)));
            }
        }
    }

    private static void Record(nint pipeline, bool compute, TableReachEntry entry, uint index, uint* record)
    {
        uint low = record[0];
        uint high = record[1] & HighMask;

        // A V#'s format dword ends in 0xfac in every buffer record the shim writes; other table bytes are no V#.
        if (record[RecordsDword] == 0 || (record[FormatDword] & FormatMask) != FormatTail || index >= entry.Records)
        {
            return;
        }

        string what;

        if (high != 0 && low < LineBytes)
        {
            what = string.Create(CultureInfo.InvariantCulture, $"a buffer at 0x{((ulong)high << 32) | low:x}");
        }
        else if (high == 0 && KeptAtLine.TryGetValue(low, out ulong address))
        {
            what = string.Create(CultureInfo.InvariantCulture, $"heap index {low}, the view at 0x{address:x}");
        }
        else
        {
            return;
        }

        if (Logged.AddOrUpdate(pipeline, 1, static (_, seen) => seen + 1) > PerPipeline)
        {
            return;
        }

        string work = compute ? "dispatch" : "draw";
        uint stride = (record[1] >> StrideShift) & StrideMask;
        ShimContext.Runtime.Log.Info(string.Create(CultureInfo.InvariantCulture, $"  [line views] {QueueProgress.PipelineName(pipeline)} ({work}, present {ShimContext.Runtime.D3D12X.Presenter.Presents}): s{entry.Sgpr} record {index} (its programs reach {entry.Records}) is {what}: {record[RecordsDword]} records of stride {stride}, format 0x{record[FormatDword]:x}"));
    }
}
