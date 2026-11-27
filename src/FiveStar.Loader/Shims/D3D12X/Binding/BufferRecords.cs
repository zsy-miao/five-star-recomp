// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Shims.D3D12X.Resources;
using FiveStar.Loader.Shims.Xg;

namespace FiveStar.Loader.Shims.D3D12X.Binding;

/// <summary>
/// The V# fields a buffer view's record carries in dwords 1 to 3 (<see cref="BufferRecord"/>; docs/d3d12x-shim.md,
/// Descriptor window), as AMD's driver lays out such descriptors: a constant buffer as float4 rows (stride 16, the
/// Scaleform batch programs read row batch * 4 + k), a structured view by its structure, a typed view by its format's
/// element and format, a raw view in bytes. Counts come from the game's desc, before the view is fitted to the
/// desktop buffer. Dword 3 carries the OOB_SELECT (bits 28-29) of the view's kind as the console's D3D12 writes it:
/// raw 3 (offset + soffset against bytes), structured 0 (index against records, offset within the structure), typed and
/// constant buffers 1 (index only); translated programs check accesses by it (docs/shader-translation.md#resources).
/// </summary>
internal static unsafe class BufferRecords
{
    /// <summary>Bytes of a constant-buffer row (a float4).</summary>
    public const uint ConstantBufferRow = 16;

    /// <summary>OOB_SELECT in dword 3: raw, structured with the offset checked, index only.</summary>
    private const uint RawBounds = 3u << 28;
    private const uint StructuredBounds = 0;
    private const uint IndexBounds = 1u << 28;

    /// <summary>Bytes of an element of a raw view.</summary>
    private const uint RawElement = 4;

    private const int ReasonCount = 4;

    private static readonly string[] Reasons =
    [
        "--buffer-records-heap-index",
        "no console address (a resource the desktop placed or committed)",
        "neither the address map nor the address table has its pages",
        "a UAV whose base another desktop resource covers",
    ];

    /// <summary>Kept records logged per reason.</summary>
    private const int KeptLogged = 8;

    /// <summary>Records written without V# fields, per kind (<see cref="NoteFieldless"/>).</summary>
    public const int FieldlessView = 0;
    public const int FieldlessConstantBuffer = 1;
    public const int FieldlessCopy = 2;

    private static readonly int[] FieldlessCounts = new int[FieldlessCopy + 1];

    /// <summary>Records that kept the heap index, per reason.</summary>
    private static readonly int[] KeptCounts = new int[ReasonCount];

    /// <summary>
    /// <c>--buffer-records-heap-index</c>: buffer records keep the desktop heap index in dword 0 instead of the console
    /// base address (<see cref="BufferRecord.At"/>), as before.
    /// </summary>
    public static bool HeapIndexOnly { get; set; }

    /// <summary>
    /// A view's record with its console base (<see cref="BufferRecord.At"/>) where reads, and for a UAV writes, through
    /// the address map meet the bytes the desktop resource holds:
    /// <list type="bullet">
    /// <item>a read-only view (SRV, CBV, slot 70) whose pages the address map gives a view now, as programs read
    /// through it (gpu_page: XMem pages mapped by the game have one without any placed buffer), or the address table
    /// covers: the GPU only reads, and the map gives it the bytes the game wrote;</item>
    /// <item>a UAV whose base the address table covers with the view's own desktop resource, so its writes land in the
    /// memory the command processor reads (indirect arguments, copies, index buffers).</item>
    /// </list>
    /// Any other keeps the heap index; the first <see cref="KeptLogged"/> of each reason are logged. With every record
    /// based (3ac3312), boots hung or crashed at 120-130 s: UAV writes went through the map to memory the desktop
    /// resources did not show (a placed buffer is its first page run, as its pages were when it was placed).
    /// </summary>
    [HotPath]
    public static BufferRecord Based(BufferRecord record, ulong address, ulong firstElement, nint desktop, ViewKind kind)
    {
        if (!record.IsBuffer)
        {
            return record;
        }

        if (HeapIndexOnly)
        {
            return Kept(record, kind, KeptReason.Flag, address, desktop);
        }

        if (address == 0)
        {
            return Kept(record, kind, KeptReason.NoAddress, address, desktop);
        }

        var based = record.At(address, firstElement);
        var d3d12 = ShimContext.Runtime.D3D12X;
        bool covered = d3d12.PlacedResources.Addresses.TryFind(based.Base, out var range);

        if (kind != ViewKind.UnorderedAccess)
        {
            if (covered || d3d12.MemoryViews.Maps(based.Base, Bytes(record)))
            {
                return based;
            }

            return Kept(record, kind, KeptReason.NotCovered, based.Base, desktop);
        }

        if (!covered)
        {
            return Kept(record, kind, KeptReason.NotCovered, based.Base, desktop);
        }

        if (range.Resource != desktop)
        {
            return Kept(record, kind, KeptReason.OtherResource, based.Base, desktop);
        }

        return based;
    }

    /// <summary>Bytes a view spans: its records of the stride, or bytes for a raw view.</summary>
    private static ulong Bytes(BufferRecord record)
    {
        if (record.Stride == 0)
        {
            return record.Records;
        }

        return (ulong)record.Records * record.Stride;
    }

    /// <summary>The record as it is; the first <see cref="KeptLogged"/> of each reason are logged.</summary>
    private static BufferRecord Kept(BufferRecord record, ViewKind kind, KeptReason reason, ulong address, nint desktop)
    {
        int count = Interlocked.Increment(ref KeptCounts[(int)reason]);

        if (count > KeptLogged)
        {
            return record;
        }

        var d3d12 = ShimContext.Runtime.D3D12X;
        var resource = d3d12.Objects.AsResource(desktop);
        string where = string.Empty;

        if (resource != null && resource->TitleMemory != 0)
        {
            where = ", a slot-127 committed resource";
        }

        if (address != 0)
        {
            string memory = ", not XMem";

            if (ShimContext.Runtime.XMemPhysical.IsReserved((nint)address))
            {
                memory = ", XMem";
            }

            where += memory;
        }

        d3d12.Warnings.Warn(
            $"record-kept-{reason}-{count}",
            $"[d3d12_xs] {kind} buffer record keeps the heap index: {Reasons[(int)reason]} (console address 0x{address:x}, {record.Records} records of stride {record.Stride}, format 0x{record.Format:x}{where})");

        return record;
    }

    /// <summary>
    /// A record written without V# fields in the game's heaps although it may stand for a buffer: a buffer view whose
    /// desc gives none (format unknown, no structure, not raw), a desktop CBV (the bytes behind its address had no
    /// desktop buffer), or descriptors copied in from a desktop heap. A program reading such a record as a V# finds
    /// num_records 0. The first <see cref="KeptLogged"/> of each kind are logged; false once they are.
    /// </summary>
    public static bool Noting(int kind)
    {
        return Volatile.Read(ref FieldlessCounts[kind]) < KeptLogged;
    }

    /// <summary>Logs one record without V# fields of <paramref name="kind"/> (callers test <see cref="Noting"/> first).</summary>
    public static void NoteFieldless(int kind, string what)
    {
        int count = Interlocked.Increment(ref FieldlessCounts[kind]);

        if (count <= KeptLogged)
        {
            ShimContext.Runtime.D3D12X.Warnings.Warn($"record-fieldless-{kind}-{count}", $"[d3d12_xs] a record without V# fields: {what}");
        }
    }

    /// <summary>The console GPU address of a resource the game placed (slot 133); 0 for one the desktop placed.</summary>
    [HotPath]
    public static ulong GameAddress(nint resource)
    {
        var wrapper = ShimContext.Runtime.D3D12X.Objects.AsResource(resource);

        if (wrapper == null)
        {
            return 0;
        }

        return wrapper->GameAddress;
    }

    /// <summary>The first element of a D3D12_SHADER_RESOURCE_VIEW_DESC or (<paramref name="unordered"/>) UAV desc of a buffer; 0 otherwise.</summary>
    [HotPath]
    public static ulong FirstElement(void* description, bool unordered)
    {
        var view = (BufferViewDescription*)description;

        if (view == null || view->Dimension != BufferViewDescription.BufferDimension)
        {
            return 0;
        }

        if (unordered)
        {
            return view->UavFirstElement;
        }

        return view->SrvFirstElement;
    }

    /// <summary>The record of a constant buffer of <paramref name="size"/> bytes.</summary>
    [HotPath]
    public static BufferRecord ConstantBuffer(uint size)
    {
        return new BufferRecord(ConstantBufferRow, (size + ConstantBufferRow - 1) / ConstantBufferRow, BufferFormatCodes.Float4 | IndexBounds);
    }

    /// <summary>The record of a D3D12_SHADER_RESOURCE_VIEW_DESC; none for a texture view or no desc.</summary>
    [HotPath]
    public static BufferRecord ShaderResource(void* description)
    {
        var view = (BufferViewDescription*)description;

        if (view == null || view->Dimension != BufferViewDescription.BufferDimension)
        {
            return BufferRecord.None;
        }

        return Buffer(view->Format, view->SrvStride, view->SrvElements, view->SrvFlags);
    }

    /// <summary>The record of a D3D12_UNORDERED_ACCESS_VIEW_DESC; none for a texture view or no desc.</summary>
    [HotPath]
    public static BufferRecord UnorderedAccess(void* description)
    {
        var view = (BufferViewDescription*)description;

        if (view == null || view->Dimension != BufferViewDescription.BufferDimension)
        {
            return BufferRecord.None;
        }

        return Buffer(view->Format, view->UavStride, view->UavElements, view->UavFlags);
    }

    [HotPath]
    private static BufferRecord Buffer(int format, uint structure, uint elements, int flags)
    {
        if ((flags & BufferViewDescription.RawFlag) != 0)
        {
            return new BufferRecord(0, elements * RawElement, BufferFormatCodes.Dword | RawBounds);
        }

        if (structure != 0)
        {
            return new BufferRecord(structure, elements, BufferFormatCodes.Dword | StructuredBounds);
        }

        if (format == ViewFormats.Unknown)
        {
            return new BufferRecord(0, 0, BufferFormatCodes.None);
        }

        return new BufferRecord((uint)XgFormats.Find((uint)format).BytesPerElement, elements, BufferFormatCodes.Of(format) | IndexBounds);
    }
}
