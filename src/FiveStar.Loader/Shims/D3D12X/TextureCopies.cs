// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using FiveStar.Loader.Diagnostics;
using FiveStar.Loader.Native;
using FiveStar.Loader.Shims.D3D12X.DebugLayer;
using FiveStar.Loader.Shims.D3D12X.Present;
using FiveStar.Loader.Shims.D3D12X.States;

namespace FiveStar.Loader.Shims.D3D12X;

/// <summary>
/// The console's copy methods with a trailing flags argument (the flags are dropped): list slot 62
/// CopyMemoryToMemoryX(destination, source, bytes, flags) on console GPU addresses, 85 CopyTextureRegionX and 86
/// CopyResourceX (the game's texture copy, 0x1404F4210: texture to texture, and buffer to texture through its upload
/// ring). Names from victory-gta6's list table (its slots 60, 83, 84 in an older layout, two below ours). A texture the
/// shim created moves from the state it is in on the list to COPY_DEST or COPY_SOURCE and back; a buffer at a console
/// address becomes the desktop buffer holding it (<see cref="BufferCopies.Desktop"/>).
/// </summary>
internal static unsafe class TextureCopies
{
    public const int CopyMemorySlot = 62;
    public const int CopyTextureRegionSlot = 85;
    public const int CopyResourceSlot = 86;

    /// <summary>ID3D12GraphicsCommandList CopyBufferRegion, CopyTextureRegion, CopyResource (d3d12.h).</summary>
    private const int DesktopCopyBufferRegion = 15;
    private const int DesktopCopyTextureRegion = 16;
    private const int DesktopCopyResource = 17;

    private static XboxDeviceFactory D3D12X => ShimContext.Runtime.D3D12X;

    public static void Bind(XboxObjects objects)
    {
        delegate* unmanaged<XboxObject*, ulong, ulong, ulong, uint, void> memory = &CopyMemory;
        delegate* unmanaged<XboxObject*, TextureCopyLocation*, uint, uint, uint, TextureCopyLocation*, CopyBox*, uint, void> region = &CopyTextureRegion;
        delegate* unmanaged<XboxObject*, nint, nint, uint, void> resource = &CopyResource;
        objects.SetHandler(XboxInterfaceKind.CommandList, CopyMemorySlot, (nint)memory);
        objects.SetHandler(XboxInterfaceKind.CommandList, CopyTextureRegionSlot, (nint)region);
        objects.SetHandler(XboxInterfaceKind.CommandList, CopyResourceSlot, (nint)resource);
    }

    [HotPath]
    [UnmanagedCallersOnly]
    public static void CopyMemory(XboxObject* self, ulong destination, ulong source, ulong bytes, uint flags)
    {
        try
        {
            ListSegments.Of(self)->Commands++;
            IndirectGather.WroteMemory(self, IndirectGatherMiss.AfterCopy, destination, bytes);
            var memory = D3D12X.GameMemory;

            if (bytes == 0 || !memory.TryFind(destination, out var to) || !memory.TryFind(source, out var from) || to.Resource == 0 || from.Resource == 0)
            {
                WarnOutside(destination, source, bytes);

                return;
            }

            if (LineViewScan.Enabled)
            {
                LineViewScan.Writes(destination, bytes, "CopyMemoryToMemoryX", source);
            }

            GpuBreadcrumbs.Before(self, RecordedCommand.CopyBuffer);
            nint inner = self->Inner;
            var copy = (delegate* unmanaged<nint, nint, ulong, nint, ulong, ulong, void>)ComObject.Slot(inner, DesktopCopyBufferRegion);
            copy(inner, to.Resource, destination - to.GameAddress, from.Resource, source - from.GameAddress, bytes);
        }
        catch (Exception exception)
        {
            UnmanagedGuard.Caught(exception, "TextureCopies.CopyMemory");
        }
    }

    [HotPath]
    [UnmanagedCallersOnly]
    public static void CopyTextureRegion(XboxObject* self, TextureCopyLocation* destination, uint x, uint y, uint z, TextureCopyLocation* source, CopyBox* box, uint flags)
    {
        try
        {
            ListSegments.Of(self)->Commands++;
            IndirectGather.Wrote(self, IndirectGatherMiss.AfterCopy);

            if (destination == null || source == null)
            {
                return;
            }

            var to = *destination;
            var from = *source;
            var states = D3D12X.ListStates.For(self);
            nint inner = self->Inner;
            var target = Side(states, ref to, ResourceStates.CopyDest);
            var origin = Side(states, ref from, ResourceStates.CopySource);

            if (target.Refused || origin.Refused)
            {
                WarnRefused();

                return;
            }

            GpuBreadcrumbs.Before(self, RecordedCommand.CopyBuffer);
            Enter(inner, target);
            Enter(inner, origin);

            ((delegate* unmanaged<nint, TextureCopyLocation*, uint, uint, uint, TextureCopyLocation*, CopyBox*, void>)ComObject.Slot(inner, DesktopCopyTextureRegion))(inner, &to, x, y, z, &from, box);
            Leave(inner, origin);
            Leave(inner, target);
        }
        catch (Exception exception)
        {
            UnmanagedGuard.Caught(exception, "TextureCopies.CopyTextureRegion");
        }
    }

    [HotPath]
    [UnmanagedCallersOnly]
    public static void CopyResource(XboxObject* self, nint destination, nint source, uint flags)
    {
        try
        {
            ListSegments.Of(self)->Commands++;
            IndirectGather.Wrote(self, IndirectGatherMiss.AfterCopy);

            var states = D3D12X.ListStates.For(self);
            var to = new TextureCopyLocation { Resource = destination };
            var from = new TextureCopyLocation { Resource = source };
            var target = Side(states, ref to, ResourceStates.CopyDest);
            var origin = Side(states, ref from, ResourceStates.CopySource);

            if (target.Refused || origin.Refused)
            {
                WarnRefused();

                return;
            }

            nint inner = self->Inner;
            GpuBreadcrumbs.Before(self, RecordedCommand.CopyBuffer);
            Enter(inner, target);
            Enter(inner, origin);

            ((delegate* unmanaged<nint, nint, nint, void>)ComObject.Slot(inner, DesktopCopyResource))(inner, to.Resource, from.Resource);
            Leave(inner, origin);
            Leave(inner, target);
        }
        catch (Exception exception)
        {
            UnmanagedGuard.Caught(exception, "TextureCopies.CopyResource");
        }
    }

    /// <summary>
    /// Unwraps one side of a copy in place and returns the transition it needs: none for a buffer or a resource the shim
    /// did not create; refused when the list's type cannot name the texture's current state.
    /// </summary>
    [HotPath]
    private static CopySide Side(ListResourceStates states, ref TextureCopyLocation location, int copyState)
    {
        var objects = D3D12X.Objects;
        var wrapper = objects.AsResource(location.Resource);

        if (wrapper != null && wrapper->Dimension == ResourceStates.BufferDimension)
        {
            ulong offset = location.Type == TextureCopyLocation.FootprintType ? location.FootprintOffset : 0;
            location.Resource = BufferCopies.Desktop(location.Resource, ref offset);

            if (location.Type == TextureCopyLocation.FootprintType)
            {
                location.FootprintOffset = offset;
            }

            return default;
        }

        location.Resource = objects.Unwrap(location.Resource);

        if (wrapper == null || wrapper->Dimension == ResourceStates.UnknownDimension)
        {
            return default;
        }

        if (copyState == ResourceStates.CopyDest && wrapper->GameAddress != 0)
        {
            D3D12X.Textures.MarkCopiedInto(wrapper->GameAddress, location.Resource);
        }

        int current = states.Current(location.Resource, wrapper->HomeState);

        if (current == copyState)
        {
            return default;
        }

        bool legal = ((current | copyState) & ~states.LegalStates) == 0;

        return new CopySide(location.Resource, current, copyState, !legal);
    }

    [HotPath]
    private static void Enter(nint list, CopySide side)
    {
        if (side.Resource != 0)
        {
            BarrierWriter.Transition(list, side.Resource, side.State, side.CopyState);
        }
    }

    [HotPath]
    private static void Leave(nint list, CopySide side)
    {
        if (side.Resource != 0)
        {
            BarrierWriter.Transition(list, side.Resource, side.CopyState, side.State);
        }
    }

    private static void WarnOutside(ulong destination, ulong source, ulong bytes)
    {
        D3D12X.Warnings.Warn("copy-memory", $"[d3d12_xs] CopyMemoryToMemoryX(0x{destination:x}, 0x{source:x}, 0x{bytes:x}): an address outside game memory; not copied");
    }

    private static void WarnRefused()
    {
        D3D12X.Warnings.Warn("copy-list-type", "[d3d12_xs] a texture copy on a compute or copy list needs a state its list type cannot name; not copied");
    }

    /// <summary>The desktop texture of one side, the state it is in on the list and the copy state it moves to.</summary>
    private readonly record struct CopySide(nint Resource, int State, int CopyState, bool Refused);
}
