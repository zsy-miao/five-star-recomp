// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Shims.Xg;

namespace FiveStar.Loader.Shims.D3D12X.Resources;

/// <summary>
/// Buffer SRVs and UAVs (device slots 18 and 19) of a buffer at a console address. A slot-133 buffer whose pages lie in
/// two page sections is one desktop buffer per run (<see cref="XboxPlacedResources"/>), and the game's object is the
/// first; a view over the whole buffer does not fit it and removes the device. One in memory the game committed itself
/// shares that memory's buffer, so its first element moves by its address. The view goes to the run that holds its
/// first element, moved to that run's offset and cut at its end (elements past a cut read 0). A view that fits its
/// buffer passes unchanged. A view whose start is no whole number of elements into the shared buffer (a 28-byte
/// structured buffer at a 64 KiB-aligned address) cannot be moved as it is: it becomes a raw view from the same byte,
/// which is how translated programs read every V# anyway. Before, it stayed unmoved, so it began at the shared buffer's
/// start, the start of the game's 1.1 GiB coherent heap where the game keeps its pool objects:
/// GpuParticleUpdate~CS_InitFrameCompute's 28-byte records (0x7FFFFFFF three times, 0x80000001 three times, 0) were
/// written over them from offset 0, and the story load crashed in the pool allocator.
/// </summary>
internal static unsafe class BufferViewRanges
{
    /// <summary>D3D12_SRV_DIMENSION_BUFFER / D3D12_UAV_DIMENSION_BUFFER and the RAW flag (d3d12.h).</summary>
    private const int BufferDimension = 1;
    private const int RawFlag = 1;
    private const uint RawElementBytes = sizeof(uint);

    /// <summary>D3D12_RAW_UAV_SRV_BYTE_ALIGNMENT: a raw view's first byte is a multiple of 16.</summary>
    private const uint RawAlignment = 16;

    /// <summary>D3D12_BUFFER_SRV fields: FirstElement, NumElements, StructureByteStride, Flags (byte offsets).</summary>
    private const int SrvFirst = 16;
    private const int SrvCount = 24;
    private const int SrvStride = 28;

    /// <summary>D3D12_BUFFER_UAV fields: FirstElement, NumElements, StructureByteStride.</summary>
    private const int UavFirst = 8;
    private const int UavCount = 16;
    private const int UavStride = 20;

    private const int Flags = 32;

    /// <summary>D3D12_BUFFER_UAV.CounterOffsetInBytes and DXGI_FORMAT_R32_TYPELESS.</summary>
    private const int UavCounterOffset = 24;
    private const int RawFormat = 39;
    private const int ViewSize = 40;
    private const int MisplacedLogged = 64;

    private static long _misplaced;

    /// <summary>
    /// The desktop resource and desc (<paramref name="view"/>, possibly <paramref name="copy"/>) for a buffer view on
    /// <paramref name="wrapper"/>, whose desktop buffer is <paramref name="desktop"/>.
    /// </summary>
    [HotPath]
    public static nint Fit(nint wrapper, nint desktop, void* description, byte* copy, bool unordered, out void* view)
    {
        view = description;
        var resource = ShimContext.Runtime.D3D12X.Objects.AsResource(wrapper);

        if (description == null || desktop == 0 || resource == null || resource->GameAddress == 0 || ((int*)description)[1] != BufferDimension)
        {
            return desktop;
        }

        if (unordered)
        {
            return Fitted(resource->GameAddress, desktop, (byte*)description, copy, UavFirst, UavCount, UavStride, ref view);
        }

        return Fitted(resource->GameAddress, desktop, (byte*)description, copy, SrvFirst, SrvCount, SrvStride, ref view);
    }

    [HotPath]
    private static nint Fitted(ulong game, nint desktop, byte* description, byte* copy, int firstAt, int countAt, int strideAt, ref void* view)
    {
        uint element = ElementBytes(description, strideAt);
        ulong start = game + (*(ulong*)(description + firstAt) * element);
        uint count = *(uint*)(description + countAt);

        if (element == 0 || !ShimContext.Runtime.D3D12X.GameMemory.TryFind(start, out var range) || range.Resource == 0)
        {
            return desktop;
        }

        ulong end = range.GameAddress + range.Size;

        if (range.Resource == desktop && range.GameAddress == game && start + ((ulong)count * element) <= end)
        {
            return desktop;
        }

        if ((start - range.GameAddress) % element != 0)
        {
            ulong bytes = (ulong)count * element;

            if (bytes > end - start)
            {
                bytes = end - start;
            }

            if (!AsRaw(description, copy, start - range.GameAddress, bytes, firstAt == UavFirst))
            {
                return 0;
            }

            view = copy;

            return range.Resource;
        }

        ulong fits = (end - start) / element;
        Buffer.MemoryCopy(description, copy, ViewSize, ViewSize);
        *(ulong*)(copy + firstAt) = (start - range.GameAddress) / element;

        if (fits < count)
        {
            count = (uint)fits;
        }

        *(uint*)(copy + countAt) = count;
        view = copy;

        return range.Resource;
    }

    /// <summary>
    /// <c>--scan-line-views</c>: a buffer UAV as the desktop gets it (<paramref name="view"/> on <paramref name="desktop"/>)
    /// against the game's (<paramref name="description"/> on <paramref name="wrapper"/>): when the desktop buffer is the one
    /// over the memory the game's resource lies in, the view must start at the same console address. One that does not
    /// is logged (the first <see cref="MisplacedLogged"/>), with whether its record holds the console base.
    /// </summary>
    public static void Check(nint wrapper, nint desktop, void* description, void* view, bool based)
    {
        var resource = ShimContext.Runtime.D3D12X.Objects.AsResource(wrapper);

        if (description == null || view == null || desktop == 0 || resource == null || resource->GameAddress == 0 || ((int*)description)[1] != BufferDimension || ((int*)view)[1] != BufferDimension)
        {
            return;
        }

        if (!ShimContext.Runtime.D3D12X.GameMemory.TryFind(resource->GameAddress, out var range) || range.Resource != desktop)
        {
            return;
        }

        ulong wanted = resource->GameAddress + (*(ulong*)((byte*)description + UavFirst) * ElementBytes((byte*)description, UavStride));
        ulong got = range.GameAddress + (*(ulong*)((byte*)view + UavFirst) * ElementBytes((byte*)view, UavStride));

        if (wanted == got || Interlocked.Increment(ref _misplaced) > MisplacedLogged)
        {
            return;
        }

        string record = based ? "holds the console base" : "keeps the heap index";
        ShimContext.Runtime.Log.Warn(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"[line views] a buffer UAV of the resource at 0x{resource->GameAddress:x} (first element {*(ulong*)((byte*)description + UavFirst)}, {*(uint*)((byte*)description + UavCount)} elements of {ElementBytes((byte*)description, UavStride)} bytes, format {*(int*)description}) starts at 0x{got:x} on the desktop, not at 0x{wanted:x}; its record {record}"));
    }

    /// <summary>
    /// Writes into <paramref name="copy"/> the view as a raw one over <paramref name="bytes"/> from byte
    /// <paramref name="offset"/>: R32_TYPELESS, the RAW flag, no stride, no counter. False for a start off 16 bytes
    /// (D3D12_RAW_UAV_SRV_BYTE_ALIGNMENT): such a view is invalid and a driver may move it, so its writes would land in
    /// the wrong place; there is none.
    /// </summary>
    [HotPath]
    internal static bool AsRaw(byte* description, byte* copy, ulong offset, ulong bytes, bool unordered)
    {
        if (offset % RawAlignment != 0)
        {
            return false;
        }

        int firstAt = unordered ? UavFirst : SrvFirst;
        int countAt = unordered ? UavCount : SrvCount;
        int strideAt = unordered ? UavStride : SrvStride;
        Buffer.MemoryCopy(description, copy, ViewSize, ViewSize);
        *(int*)copy = RawFormat;
        *(ulong*)(copy + firstAt) = offset / RawElementBytes;

        *(uint*)(copy + countAt) = (uint)(bytes / RawElementBytes);
        *(uint*)(copy + strideAt) = 0;
        *(int*)(copy + Flags) |= RawFlag;

        if (unordered)
        {
            *(ulong*)(copy + UavCounterOffset) = 0;
        }

        return true;
    }

    /// <summary>Bytes per element of a buffer SRV or (<paramref name="unordered"/>) UAV desc, as <see cref="ElementBytes(byte*, int)"/>.</summary>
    [HotPath]
    internal static uint ElementBytes(byte* description, bool unordered)
    {
        if (unordered)
        {
            return ElementBytes(description, UavStride);
        }

        return ElementBytes(description, SrvStride);
    }

    /// <summary>Bytes per element: 4 for raw views, the stride of structured ones, the format's size for typed ones; 0 when unknown.</summary>
    [HotPath]
    private static uint ElementBytes(byte* description, int strideAt)
    {
        if ((*(int*)(description + Flags) & RawFlag) != 0)
        {
            return RawElementBytes;
        }

        uint stride = *(uint*)(description + strideAt);

        if (stride != 0)
        {
            return stride;
        }

        int format = *(int*)description;

        if (format == ViewFormats.Unknown)
        {
            return 0;
        }

        return (uint)XgFormats.Find((uint)format).BytesPerElement;
    }
}
