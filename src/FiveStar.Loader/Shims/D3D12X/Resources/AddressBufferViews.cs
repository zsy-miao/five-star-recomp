// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Native;
using FiveStar.Loader.Shims.D3D12X.Binding;
using FiveStar.Loader.Shims.Xg;

namespace FiveStar.Loader.Shims.D3D12X.Resources;

/// <summary>
/// Device slot 70 with a buffer desc: a buffer SRV (raw, structured or typed) at a console GPU address. The address
/// lies in a desktop buffer over game memory (<see cref="XboxGameMemoryBuffers.TryFind"/>); the view's first element
/// moves by the address's offset in that buffer. A view past the buffer's end is cut to the elements it holds, and one
/// off its element size whose first byte is on 16 bytes is read raw (<see cref="Fit"/>). An address outside known memory,
/// a view off its element size and off 16 bytes, one starting past the end, or a raw view whose first byte is off the 16
/// bytes D3D12 requires (D3D12_RAW_UAV_SRV_BYTE_ALIGNMENT) gets no view (the caller writes a null one).
/// </summary>
internal sealed unsafe class AddressBufferViews
{
    /// <summary>ID3D12Device::CreateShaderResourceView (d3d12.h).</summary>
    private const int DesktopShaderResourceViewSlot = 18;

    /// <summary>Bytes of a raw view's element (R32_TYPELESS).</summary>
    private const uint RawElementBytes = sizeof(uint);

    /// <summary>D3D12_RAW_UAV_SRV_BYTE_ALIGNMENT.</summary>
    private const uint RawAlignment = 16;

    private readonly XboxGameMemoryBuffers _memory;

    public AddressBufferViews(XboxGameMemoryBuffers memory)
    {
        _memory = memory;
    }

    /// <summary>
    /// Writes the buffer view into <paramref name="handle"/>, raw when its <paramref name="record"/> keeps the heap index
    /// (<see cref="Binding.RawBufferViews"/>). A view the desktop buffer cannot hold as described is made to fit where that
    /// reads the same bytes (<see cref="Fit"/>); otherwise nothing is written (the caller writes a null view).
    /// </summary>
    [HotPath]
    public BufferViewOutcome Create(nint device, AddressViewDescription* view, nint handle, Binding.BufferRecord record)
    {
        BufferViewDescription desktop;
        Buffer.MemoryCopy(view, &desktop, sizeof(BufferViewDescription), AddressViewDescription.DesktopViewSize);
        uint element = ElementBytes(desktop);

        if (desktop.Dimension != BufferViewDescription.BufferDimension || element == 0)
        {
            return BufferViewOutcome.NotBuffer;
        }

        if (!_memory.TryFind(view->Address, out var range) || range.Resource == 0)
        {
            return BufferViewOutcome.Uncovered;
        }

        var outcome = Fit(ref desktop, element, view->Address - range.GameAddress, range.Size);

        if (outcome is not (BufferViewOutcome.Created or BufferViewOutcome.Clamped or BufferViewOutcome.Raw))
        {
            return outcome;
        }

        var create = (delegate* unmanaged<nint, nint, BufferViewDescription*, nint, void>)ComObject.Slot(device, DesktopShaderResourceViewSlot);
        nint target = Binding.VramTwins.Substitute(range.Resource);
        BufferViewDescription raw;
        var written = (BufferViewDescription*)Binding.RawBufferViews.View(&desktop, (byte*)&raw, false, record, ref target);
        create(device, target, written, handle);

        return outcome;
    }

    /// <summary>
    /// The game's buffer view, <paramref name="offset"/> bytes into a desktop buffer of <paramref name="size"/> bytes,
    /// described for it: its first element moved by the offset; a view off its element size whose first byte is on 16
    /// bytes as a raw view over the same bytes (the elements' bytes do not move, only how they are counted); a view past
    /// the buffer's end cut to the elements it holds (the rest reads 0, as past a console view's range). Anything else
    /// does not fit: off its element size and off 16 bytes, starting past the end, or raw off 16 bytes.
    /// </summary>
    [HotPath]
    internal static BufferViewOutcome Fit(ref BufferViewDescription desktop, uint element, ulong offset, ulong size)
    {
        var outcome = BufferViewOutcome.Created;
        ulong first = offset + (desktop.SrvFirstElement * element);
        ulong bytes = (ulong)desktop.SrvElements * element;

        if (offset % element != 0)
        {
            if (first % RawAlignment != 0)
            {
                return BufferViewOutcome.Misaligned;
            }

            desktop = BufferViewDescription.RawShaderResource((uint)(bytes / RawElementBytes), first / RawElementBytes);
            (element, outcome) = (RawElementBytes, BufferViewOutcome.Raw);
        }
        else
        {
            desktop.SrvFirstElement = first / element;
        }

        if (first >= size)
        {
            return BufferViewOutcome.PastEnd;
        }

        if (first + bytes > size)
        {
            desktop.SrvElements = (uint)((size - first) / element);
            outcome = desktop.SrvElements == 0 ? BufferViewOutcome.PastEnd : BufferViewOutcome.Clamped;
        }

        if ((desktop.SrvFlags & BufferViewDescription.RawFlag) != 0 && first % RawAlignment != 0)
        {
            return BufferViewOutcome.RawMisaligned;
        }

        return outcome;
    }

    /// <summary>
    /// A raw SRV of <paramref name="size"/> bytes at console address <paramref name="address"/>, written for a
    /// constant-buffer view in one of the game's title-memory heaps: translated programs read every V#, a CBV's too, as
    /// a ByteAddressBuffer. False when the address has no desktop buffer, the range leaves it or it starts off 16 bytes.
    /// </summary>
    [HotPath]
    public bool CreateRaw(nint device, ulong address, uint size, nint handle)
    {
        if (!_memory.TryFind(address, out var range) || range.Resource == 0)
        {
            return false;
        }

        ulong offset = address - range.GameAddress;
        ulong bytes = range.Size - offset;

        if (size < bytes)
        {
            bytes = size;
        }

        if (offset % RawAlignment != 0 || bytes < RawElementBytes)
        {
            return false;
        }

        var desktop = BufferViewDescription.RawShaderResource((uint)(bytes / RawElementBytes));
        desktop.SrvFirstElement = offset / RawElementBytes;
        var create = (delegate* unmanaged<nint, nint, BufferViewDescription*, nint, void>)ComObject.Slot(device, DesktopShaderResourceViewSlot);
        create(device, Binding.VramTwins.Substitute(range.Resource), &desktop, handle);

        return true;
    }

    /// <summary>Bytes per element: 4 for raw views, the stride of structured ones, the format's size for typed ones; 0 when unknown.</summary>
    [HotPath]
    private static uint ElementBytes(in BufferViewDescription view)
    {
        if ((view.SrvFlags & BufferViewDescription.RawFlag) != 0)
        {
            return RawElementBytes;
        }

        if (view.SrvStride != 0)
        {
            return view.SrvStride;
        }

        if (view.Format == ViewFormats.Unknown)
        {
            return 0;
        }

        return (uint)XgFormats.Find((uint)view.Format).BytesPerElement;
    }
}
