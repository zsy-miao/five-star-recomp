// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using FiveStar.Loader.Diagnostics;
using FiveStar.Loader.Native;
using FiveStar.Loader.Shims.D3D12X.DebugLayer;

namespace FiveStar.Loader.Shims.D3D12X;

/// <summary>
/// Command-list slot 15, CopyBufferRegion (desktop slot 15). A buffer at a console address (slot 133) may be a run of
/// pages or share the desktop buffer over the game's own memory with other resources (<see cref="XboxPlacedResources"/>),
/// so its offsets are console ones: each side becomes the desktop buffer holding its console address, with the offset
/// moved there (ExecuteIndirect's argument and count buffers too). Any other resource passes unwrapped.
/// </summary>
internal static unsafe class BufferCopies
{
    public const int CopyBufferRegionSlot = 15;

    private const int DesktopCopyBufferRegion = 15;

    [HotPath]
    [UnmanagedCallersOnly]
    public static void CopyBufferRegion(XboxObject* self, nint destination, ulong destinationOffset, nint source, ulong sourceOffset, ulong bytes)
    {
        try
        {
            ListSegments.Of(self)->Commands++;
            IndirectGather.Wrote(self, IndirectGatherMiss.AfterCopy);
            ulong gameOffset = destinationOffset;
            nint desktopDestination = Desktop(destination, ref destinationOffset);
            nint desktopSource = Desktop(source, ref sourceOffset);

            if (LineViewScan.Enabled)
            {
                NoteLineWrite(destination, desktopDestination, gameOffset, destinationOffset, bytes);
            }

            GpuBreadcrumbs.Before(self, RecordedCommand.CopyBuffer);

            if (NullAddressCheck.DropsCopy(self, desktopDestination, destinationOffset, desktopSource, sourceOffset, bytes))
            {
                return;
            }

            nint inner = self->Inner;
            var copy = (delegate* unmanaged<nint, nint, ulong, nint, ulong, ulong, void>)ComObject.Slot(inner, DesktopCopyBufferRegion);
            copy(inner, desktopDestination, destinationOffset, desktopSource, sourceOffset, bytes);
        }
        catch (Exception exception)
        {
            UnmanagedGuard.Caught(exception, "BufferCopies.CopyBufferRegion");
        }
    }

    /// <summary>
    /// <c>--scan-line-views</c>: a copy whose destination, as the desktop gets it, lies in the first 64 KiB of the buffer
    /// over a game allocation (they start on 4 GiB lines) is reported with the game's own destination.
    /// </summary>
    private static void NoteLineWrite(nint wrapper, nint desktop, ulong gameOffset, ulong desktopOffset, ulong bytes)
    {
        var d3d12x = ShimContext.Runtime.D3D12X;
        var resource = d3d12x.Objects.AsResource(wrapper);
        ulong game = resource == null ? 0 : resource->GameAddress;

        if (game == 0 || !d3d12x.GameMemory.TryFind(game, out var range) || range.Resource != desktop)
        {
            return;
        }

        LineViewScan.Writes(range.GameAddress + desktopOffset, bytes, "CopyBufferRegion (the game's destination 0x" + (game + gameOffset).ToString("x", System.Globalization.CultureInfo.InvariantCulture) + ")", 0);
    }

    /// <summary>The desktop buffer holding <paramref name="wrapper"/>'s byte at <paramref name="offset"/>, the offset moved to it.</summary>
    [HotPath]
    internal static nint Desktop(nint wrapper, ref ulong offset)
    {
        var d3d12x = ShimContext.Runtime.D3D12X;
        var resource = d3d12x.Objects.AsResource(wrapper);

        if (resource == null || resource->GameAddress == 0 || resource->Dimension != ResourceDescription.BufferDimension)
        {
            return d3d12x.Objects.Unwrap(wrapper);
        }

        ulong address = resource->GameAddress + offset;

        if (!d3d12x.GameMemory.TryFind(address, out var range) || range.Resource == 0)
        {
            return d3d12x.Objects.Unwrap(wrapper);
        }

        offset = address - range.GameAddress;

        return range.Resource;
    }
}
