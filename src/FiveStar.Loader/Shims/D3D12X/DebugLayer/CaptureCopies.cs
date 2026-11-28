// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using Vortice.Direct3D12;

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>
/// The GPU copies of a capture, recorded in the game's list around the dispatch (as <see cref="DrawProbes"/> and
/// <see cref="NanScan"/> read textures back): after a UAV barrier, every source into one readback buffer before the
/// dispatch, and each output again after it; an ExecuteIndirect's argument records and count before it. A texture is
/// copied from its state on that list and put back; buffers are copied in the state they are in (no transition).
/// </summary>
internal static unsafe class CaptureCopies
{
    /// <summary>D3D12_TEXTURE_DATA_PLACEMENT_ALIGNMENT: where each copy starts in the readback buffer.</summary>
    private const ulong Alignment = 512;
    private const ulong CountBytes = sizeof(uint);

    /// <summary>D3D12_RESOURCE_STATE_RENDER_TARGET, UNORDERED_ACCESS, DEPTH_WRITE, STREAM_OUT, COPY_DEST and RESOLVE_DEST.</summary>
    private const int WriteStates = 0x4 | 0x8 | 0x10 | 0x100 | 0x400 | 0x1000;

    /// <summary>Places every copy in the readback buffer and sets its size.</summary>
    public static void Layout(CapturePlan plan)
    {
        ulong at = 0;

        foreach (var source in plan.Sources)
        {
            source.Before = at;
            at = Align(at + source.Shape.Bytes);

            if (source.Output)
            {
                source.After = at;
                at = Align(at + source.Shape.Bytes);
            }
        }

        if (plan.Indirect is not null)
        {
            plan.Indirect.At = at;
            at = Align(at + plan.Indirect.Bytes + CountBytes);
        }

        plan.ReadbackBytes = Math.Max(at, Alignment);
    }

    /// <summary>The readback buffer of a laid-out plan.</summary>
    public static ID3D12Resource Readback(CapturePlan plan)
    {
        nint device = ShimContext.Runtime.D3D12X.Objects.DesktopDevice;
        Marshal.AddRef(device);
        using var desktop = new ID3D12Device(device);

        return desktop.CreateCommittedResource(new HeapProperties(HeapType.Readback), HeapFlags.None, Vortice.Direct3D12.ResourceDescription.Buffer(plan.ReadbackBytes), ResourceStates.CopyDest);
    }

    /// <summary>Records the copies before the dispatch, or with <paramref name="after"/> those of its outputs after it.</summary>
    public static void Record(XboxObject* list, CapturePlan plan, ID3D12Resource readback, bool after)
    {
        Marshal.AddRef(list->Inner);
        using var commands = new ID3D12GraphicsCommandList(list->Inner);
        commands.ResourceBarrier(ResourceBarrier.BarrierUnorderedAccessView(null!));

        foreach (var source in plan.Sources)
        {
            if (after && !source.Output)
            {
                continue;
            }

            ulong at = after ? source.After : source.Before;

            if (source.Texture == 0)
            {
                CopyBuffer(commands, plan, source, readback, at, after);
            }
            else
            {
                CopyTexture(commands, list, plan, source, readback, at, after);
            }
        }

        if (!after && plan.Indirect is not null)
        {
            CopyArguments(commands, plan.Indirect, readback);
        }
    }

    /// <summary>
    /// Each piece, ending at its buffer's end: a page the address map places near the end of a desktop buffer can reach
    /// past it, and a copy out of a buffer's range makes the desktop Close fail (the game's list never runs; w441 and
    /// w442 read every resource back as zeros).
    /// </summary>
    private static void CopyBuffer(ID3D12GraphicsCommandList commands, CapturePlan plan, CaptureSource source, ID3D12Resource readback, ulong at, bool after)
    {
        foreach (var piece in source.Pieces)
        {
            using var buffer = Wrap(piece.Buffer);
            ulong bytes = InRange(piece.SourceOffset, piece.Bytes, buffer.Description.Width);

            if (bytes != piece.Bytes && !after)
            {
                plan.Notes.Add($"{source.Shape.Label}: 0x{piece.Bytes - bytes:x} bytes at 0x{piece.TargetOffset + bytes:x} past the end of buffer 0x{piece.Buffer:x}, kept as zeros");
            }

            if (bytes != 0)
            {
                commands.CopyBufferRegion(readback, at + piece.TargetOffset, buffer, piece.SourceOffset, bytes);
            }
        }
    }

    /// <summary>How many of <paramref name="bytes"/> from <paramref name="offset"/> lie inside a buffer of <paramref name="width"/> bytes.</summary>
    internal static ulong InRange(ulong offset, ulong bytes, ulong width)
    {
        if (offset >= width)
        {
            return 0;
        }

        return Math.Min(bytes, width - offset);
    }

    /// <summary>
    /// Every subresource from the texture's state on the list, which it goes back to. The state is the one the desktop
    /// texture is in: without the write states its flags do not allow, as the shim writes the game's barriers
    /// (<see cref="States.ResourceStates.Allowed"/>). A texture whose state or COPY_SOURCE the list's type cannot name (a
    /// shader-resource texture on a compute list) is not copied: such a barrier makes the desktop Close fail, and the
    /// game's whole list then never runs (w376 and w440 read every resource back as zeros).
    /// </summary>
    private static void CopyTexture(ID3D12GraphicsCommandList commands, XboxObject* list, CapturePlan plan, CaptureSource source, ID3D12Resource readback, ulong at, bool after)
    {
        var states = ShimContext.Runtime.D3D12X.ListStates.Find(list);
        using var texture = Wrap(source.Texture);
        int current = States.ResourceStates.Allowed(states?.Current(source.Texture, source.Home) ?? source.Home, (int)texture.Description.Flags);
        var resting = (ResourceStates)current;
        int legal = states?.LegalStates ?? ~0;

        if (!after)
        {
            plan.Notes.Add($"texture 0x{source.Texture:x}: state 0x{current:x} on the list, flags 0x{(int)texture.Description.Flags:x}");
        }

        if (((current | (int)ResourceStates.CopySource) & ~legal) != 0 || !Transitionable(current))
        {
            Refuse(plan, source, current, after);

            return;
        }

        var footprints = source.Shape.Footprints ?? [];

        if (resting != ResourceStates.CopySource)
        {
            commands.ResourceBarrierTransition(texture, resting, ResourceStates.CopySource);
        }

        for (var subresource = 0; subresource < footprints.Count; subresource++)
        {
            var footprint = footprints[subresource];
            var placed = new PlacedSubresourceFootPrint
            {
                Offset = at + footprint.Offset,
                Footprint = new SubresourceFootPrint((Vortice.DXGI.Format)footprint.Format, footprint.Width, footprint.Height, footprint.Depth, footprint.RowPitch),
            };
            commands.CopyTextureRegion(new TextureCopyLocation(readback, placed), 0, 0, 0, new TextureCopyLocation(texture, (uint)subresource), null);
        }

        if (resting != ResourceStates.CopySource)
        {
            commands.ResourceBarrierTransition(texture, ResourceStates.CopySource, resting);
        }
    }

    /// <summary>
    /// Whether <paramref name="state"/> may begin a transition: a write state (render target, UAV, depth write, copy or
    /// resolve destination, stream output) stands alone; combined with any other bit the desktop Close fails.
    /// </summary>
    internal static bool Transitionable(int state)
    {
        int writes = state & WriteStates;

        return writes == 0 || state == writes && (writes & (writes - 1)) == 0;
    }

    /// <summary>A texture left out: noted once (at the copy before the work), its bytes stay zero.</summary>
    private static void Refuse(CapturePlan plan, CaptureSource source, int state, bool after)
    {
        if (!after)
        {
            plan.Notes.Add($"texture 0x{source.Texture:x} not read: its state 0x{state:x} or COPY_SOURCE is not one this list's type may name");
        }
    }

    private static void CopyArguments(ID3D12GraphicsCommandList commands, IndirectArguments arguments, ID3D12Resource readback)
    {
        using (var records = Wrap(arguments.Buffer))
        {
            ulong bytes = InRange(arguments.Offset, arguments.Bytes, records.Description.Width);

            if (bytes != 0)
            {
                commands.CopyBufferRegion(readback, arguments.At, records, arguments.Offset, bytes);
            }
        }

        if (arguments.CountBuffer != 0)
        {
            using var count = Wrap(arguments.CountBuffer);
            commands.CopyBufferRegion(readback, arguments.At + arguments.Bytes, count, arguments.CountOffset, CountBytes);
        }
    }

    private static ID3D12Resource Wrap(nint resource)
    {
        Marshal.AddRef(resource);

        return new ID3D12Resource(resource);
    }

    private static ulong Align(ulong value)
    {
        return (value + Alignment - 1) & ~(Alignment - 1);
    }
}
