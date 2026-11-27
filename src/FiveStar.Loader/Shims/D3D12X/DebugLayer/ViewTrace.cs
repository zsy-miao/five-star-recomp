// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using FiveStar.Loader.Shims.D3D12X.Binding;
using Vortice.Direct3D12;

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>
/// <c>--trace-views &lt;pipeline texts&gt;</c>: before a draw, dispatch or ExecuteIndirect of a matching pipeline (the first
/// <see cref="TracesPerWindow"/> per pipeline every <see cref="WindowSeconds"/> s) each record of its bound tables is
/// logged with what it resolves to: a buffer record's console range, or the desktop view <see cref="ViewLiveness"/>
/// noted at its handle (shape, resource, size, format, alive or released, or a null view), with the record's first
/// dwords (the console descriptor). CPU only: nothing is added to the game's lists, unlike <see cref="NanScan"/>.
/// For the exposure histogram reading black (w170). Off by default.
/// </summary>
internal static unsafe class ViewTrace
{
    private const int TracesPerWindow = 2;
    private const double WindowSeconds = 30;

    /// <summary>Record dwords shown: the console descriptor's first 8 (a 32-byte T#, or a V# in its first 4).</summary>
    private const int RecordDwords = 8;

    private const int RecordsDword = 2;
    private const int StrideDword = 1;
    private const int StrideShift = 16;
    private const uint StrideMask = 0x3FFF;

    /// <summary>Buffers up to this size show their floats: 15 records of 16 bytes (the histogram's constants) fit.</summary>
    private const ulong SmallBufferBytes = 256;

    private const uint HighMask = 0xFFFF;
    private const int HighShift = 32;

    private static readonly ConcurrentDictionary<nint, bool> Matching = new();
    private static readonly ConcurrentDictionary<nint, (long Start, int Count)> Windows = new();

    /// <summary>Texts (comma-separated) one of which the traced pipelines' names hold; null when off.</summary>
    public static string? Filter { get; set; }

    public static bool Enabled => Filter is not null;

    /// <summary>Before work of <paramref name="list"/>: its tables' records logged when its pipeline matches and its window has room.</summary>
    public static void Before(XboxObject* list, bool compute)
    {
        nint pipeline = ListActivity.Of(list)->Pipeline;

        if (pipeline == 0 || !Matching.GetOrAdd(pipeline, static key => NanScan.Matches(QueueProgress.PipelineName(key), Filter!)) || !Admit(pipeline))
        {
            return;
        }

        var bindings = CommandListBindings.Of(list);
        var layout = compute ? bindings->ComputeLayout : bindings->GraphicsLayout;
        var views = ShimContext.Runtime.D3D12X.DescriptorHeaps.Views;

        if (layout == null || views is null)
        {
            return;
        }

        uint* data = compute ? bindings->Compute : bindings->Graphics;
        uint first = compute ? UserData.ComputeBase : UserData.GraphicsBase;
        var text = new StringBuilder();

        for (uint parameter = 0; parameter < (uint)layout->Count; parameter++)
        {
            uint sgpr = first + layout->Offsets[parameter];

            if (sgpr < CommandListBindings.UserDataDwords && layout->Kind(parameter) == RootParameterKind.Table)
            {
                Table(text, views, parameter, data[sgpr], layout->Extents[parameter]);
            }
        }

        if (!compute)
        {
            MaterialViewTrace.Append(text, views, data);
        }

        string work = compute ? "dispatch" : "draw";
        ShimContext.Runtime.Log.Info($"  [view trace] {QueueProgress.PipelineName(pipeline)} ({work}, present {ShimContext.Runtime.D3D12X.Presenter.Presents}):{text}");
    }

    /// <summary>The first <see cref="TracesPerWindow"/> of a pipeline in each window.</summary>
    private static bool Admit(nint pipeline)
    {
        long now = Stopwatch.GetTimestamp();
        var window = Windows.GetOrAdd(pipeline, (now, 0));

        if (Stopwatch.GetElapsedTime(window.Start, now).TotalSeconds > WindowSeconds)
        {
            window = (now, 0);
        }

        if (window.Count >= TracesPerWindow)
        {
            return false;
        }

        Windows[pipeline] = (window.Start, window.Count + 1);

        return true;
    }

    private static void Table(StringBuilder text, ShaderVisibleHeap views, uint parameter, uint offset, uint extent)
    {
        if (offset >= DescriptorWindow.NullTableBase || offset % DescriptorWindow.ViewRecordBytes != 0)
        {
            text.Append(CultureInfo.InvariantCulture, $"\n      table {parameter}: window 0x{offset:x}, outside the view records");

            return;
        }

        uint index = (offset - DescriptorWindow.ViewBase) / DescriptorWindow.ViewRecordBytes;

        for (uint i = 0; i < extent && index + i < views.Count; i++)
        {
            text.Append(CultureInfo.InvariantCulture, $"\n      table {parameter} entry {i} (record {index + i}): {Resolve(views, index + i)}; dwords {Dwords(views, index + i)}{Seen(views, index + i, offset + (i * DescriptorWindow.ViewRecordBytes))}");
        }
    }

    /// <summary>
    /// What a program sees of a buffer record: the window's copy of its first four dwords when they differ from the
    /// heap's record, and for a console base the desktop buffer and byte offset the address map reads it from
    /// (<see cref="GameMemoryViews.Locate"/>) beside the game-memory buffer that covers the address.
    /// </summary>
    private static string Seen(ShaderVisibleHeap views, uint index, uint windowOffset)
    {
        var text = new StringBuilder();
        uint* window = (uint*)(ShimContext.Runtime.D3D12X.Bindings.Window.Data + windowOffset);

        if (ShimContext.Runtime.D3D12X.Bindings.Window.Data != null && (window[0] != views.RecordDword(index, 0) || window[1] != views.RecordDword(index, 1) || window[2] != views.RecordDword(index, 2) || window[3] != views.RecordDword(index, 3)))
        {
            text.Append(CultureInfo.InvariantCulture, $"; THE WINDOW HOLDS {window[0]:x8} {window[1]:x8} {window[2]:x8} {window[3]:x8}");
        }

        uint high = views.RecordDword(index, StrideDword) & HighMask;

        if (high == 0 || views.RecordDword(index, RecordsDword) == 0)
        {
            return text.ToString();
        }

        ulong address = views.RecordDword(index, 0) | ((ulong)high << HighShift);
        var d3d12 = ShimContext.Runtime.D3D12X;
        var (view, field) = d3d12.MemoryViews.Entry(address);
        string mapped = "no view";

        if (d3d12.MemoryViews.Locate(address, out nint buffer, out ulong offset))
        {
            mapped = string.Create(CultureInfo.InvariantCulture, $"buffer 0x{buffer:x} + 0x{offset:x}");
        }

        string covered = "no game-memory buffer";

        if (d3d12.GameMemory.TryFind(address, out var range))
        {
            covered = string.Create(CultureInfo.InvariantCulture, $"buffer 0x{range.Resource:x} + 0x{address - range.GameAddress:x} (the range at 0x{range.GameAddress:x}, 0x{range.Size:x} bytes)");
        }

        text.Append(CultureInfo.InvariantCulture, $"; the map: view {view} field 0x{field:x} -> {mapped}; covered by {covered}");

        return text.ToString();
    }

    /// <summary>What a record resolves to: a buffer record's console range, or the noted desktop view.</summary>
    internal static string Resolve(ShaderVisibleHeap views, uint index)
    {
        uint high = views.RecordDword(index, StrideDword) & HighMask;

        if (high != 0 && views.RecordDword(index, RecordsDword) != 0)
        {
            ulong address = views.RecordDword(index, 0) | ((ulong)high << HighShift);

            return string.Create(CultureInfo.InvariantCulture, $"buffer at 0x{address:x}, {views.RecordDword(index, RecordsDword)} records{Floats(views, index, address)}");
        }

        var noted = ViewLiveness.Peek(views.Source(index));

        if (noted is null)
        {
            return "no view noted at its handle";
        }

        var (view, live) = noted.Value;

        if (view.Shape == ViewShape.Null || view.Resource == 0)
        {
            return "null view (reads 0)";
        }

        if (!live)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{view.Shape} of released resource 0x{view.Resource:x}");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{view.Shape} of 0x{view.Resource:x} {Describe(view.Resource)}");
    }

    /// <summary>
    /// ", floats a, b, ..." of a small buffer (at most <see cref="SmallBufferBytes"/>: constants such as the histogram's
    /// exposure scale and EV range) as game memory holds it now, read through the kernel; empty for a larger one.
    /// </summary>
    private static string Floats(ShaderVisibleHeap views, uint index, ulong address)
    {
        ulong records = views.RecordDword(index, RecordsDword);
        ulong stride = (views.RecordDword(index, StrideDword) >> StrideShift) & StrideMask;
        ulong bytes = records * Math.Max(stride, 1);

        if (bytes > SmallBufferBytes)
        {
            return string.Empty;
        }

        var data = new byte[(int)bytes];
        nuint done = 0;

        fixed (byte* to = data)
        {
            _ = Native.Kernel32.ReadProcessMemory(Native.Kernel32.GetCurrentProcess(), (nint)address, to, (nuint)data.Length, &done);
        }

        return $", floats {ExposureTrace.Floats(data.AsSpan(0, (int)done))}";
    }

    /// <summary>A live desktop resource's size, mips and format (GetDesc only).</summary>
    private static string Describe(nint resource)
    {
        Marshal.AddRef(resource);
        using var texture = new ID3D12Resource(resource);
        var description = texture.Description;

        return string.Create(CultureInfo.InvariantCulture, $"{description.Width}x{description.Height}x{description.DepthOrArraySize} mips {description.MipLevels} format {(int)description.Format}");
    }

    private static string Dwords(ShaderVisibleHeap views, uint index)
    {
        var dwords = new string[RecordDwords];

        for (int dword = 0; dword < RecordDwords; dword++)
        {
            dwords[dword] = views.RecordDword(index, dword).ToString("x8", CultureInfo.InvariantCulture);
        }

        return string.Join(' ', dwords);
    }
}
