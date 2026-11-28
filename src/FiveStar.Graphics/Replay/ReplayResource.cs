// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace FiveStar.Graphics.Replay;

/// <summary>
/// One captured resource on the replay device: the resource the program reads and writes, an upload buffer with its
/// bytes before the dispatch (copied in again before every run) and, for one with a reference output, a readback buffer.
/// The resource is in video memory, or, for a range of game memory when game memory is imported, in system memory as
/// in the game (<see cref="ImportedBuffer"/>).
/// </summary>
internal sealed unsafe class ReplayResource : IDisposable
{
    private readonly ID3D12Resource _upload;
    private readonly ID3D12Resource? _readback;
    private readonly ImportedBuffer? _imported;
    private readonly ResourceStates _home;
    private ResourceStates _state = ResourceStates.Common;

    public ReplayResource(ID3D12Device device, CaptureFolder folder, CapturedResource captured, bool importGameMemory)
    {
        Captured = captured;

        if (importGameMemory && captured.IsBuffer && captured.ConsoleAddress is not null)
        {
            _imported = new ImportedBuffer(device, captured.Width);
            Resource = _imported.Resource;
        }
        else
        {
            Resource = device.CreateCommittedResource(new HeapProperties(HeapType.Default), HeapFlags.None, Description(captured), ResourceStates.Common);
        }

        _upload = device.CreateCommittedResource(new HeapProperties(HeapType.Upload), HeapFlags.None, ResourceDescription.Buffer(captured.Bytes), ResourceStates.GenericRead);
        Fill(folder);
        _home = Home(captured);

        if (captured.Output is not null)
        {
            _readback = device.CreateCommittedResource(new HeapProperties(HeapType.Readback), HeapFlags.None, ResourceDescription.Buffer(captured.Bytes), ResourceStates.CopyDest);
        }
    }

    public CapturedResource Captured { get; }

    public ID3D12Resource Resource { get; }

    public bool HasOutput => _readback is not null;

    /// <summary>Records the copy of the bytes before the dispatch into the resource, leaving it in its home state.</summary>
    public void Restore(ID3D12GraphicsCommandList list)
    {
        Transition(list, ResourceStates.CopyDest);
        Copy(list, Resource, _upload, toBuffer: false);
        Transition(list, _home);
    }

    /// <summary>Records the copy of the resource into the readback buffer (only for one with an output).</summary>
    public void Read(ID3D12GraphicsCommandList list)
    {
        if (_readback is null)
        {
            return;
        }

        Transition(list, ResourceStates.CopySource);
        Copy(list, _readback, Resource, toBuffer: true);
    }

    /// <summary>The bytes copied by <see cref="Read"/>, in the capture's layout, once the list ran.</summary>
    public byte[] Output()
    {
        void* data;
        _readback!.Map(0, null, &data).CheckError();

        try
        {
            return new ReadOnlySpan<byte>(data, checked((int)Captured.Bytes)).ToArray();
        }
        finally
        {
            _readback.Unmap(0);
        }
    }

    public void Dispose()
    {
        _readback?.Dispose();
        _upload.Dispose();

        if (_imported is not null)
        {
            _imported.Dispose();

            return;
        }

        Resource.Dispose();
    }

    private void Transition(ID3D12GraphicsCommandList list, ResourceStates state)
    {
        if (_state != state)
        {
            list.ResourceBarrier(ResourceBarrier.BarrierTransition(Resource, _state, state));
            _state = state;
        }
    }

    /// <summary>A whole buffer, or each subresource of a texture by its footprint, between the resource and a buffer.</summary>
    private void Copy(ID3D12GraphicsCommandList list, ID3D12Resource destination, ID3D12Resource source, bool toBuffer)
    {
        if (Captured.IsBuffer)
        {
            list.CopyBufferRegion(destination, 0, source, 0, Captured.Bytes);

            return;
        }

        var footprints = Captured.Footprints ?? [];

        for (var subresource = 0; subresource < footprints.Count; subresource++)
        {
            var placed = Placed(footprints[subresource]);
            var texture = new TextureCopyLocation(Resource, (uint)subresource);

            if (toBuffer)
            {
                list.CopyTextureRegion(new TextureCopyLocation(destination, placed), 0, 0, 0, texture, null);
            }
            else
            {
                list.CopyTextureRegion(texture, 0, 0, 0, new TextureCopyLocation(source, placed), null);
            }
        }
    }

    /// <summary>The upload buffer's bytes: zeros, the input blob, the fills, the patches.</summary>
    private void Fill(CaptureFolder folder)
    {
        void* data;
        _upload.Map(0, null, &data).CheckError();

        try
        {
            var bytes = new Span<byte>(data, checked((int)Captured.Bytes));
            bytes.Clear();

            if (Captured.Input is not null)
            {
                var input = folder.ReadFile(Captured.Input);
                input.AsSpan(0, Math.Min(bytes.Length, input.Length)).CopyTo(bytes);
            }

            foreach (var fill in Captured.Fills ?? [])
            {
                for (uint index = 0; index < fill.Count; index++)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(bytes[checked((int)(fill.Offset + ((ulong)index * fill.Stride)))..], fill.Value + (index * fill.Step));
                }
            }

            foreach (var patch in Captured.Patches ?? [])
            {
                folder.ReadFile(patch.Blob).CopyTo(bytes[checked((int)patch.Offset)..]);
            }
        }
        finally
        {
            _upload.Unmap(0);
        }
    }

    private static PlacedSubresourceFootPrint Placed(CapturedFootprint footprint)
    {
        return new PlacedSubresourceFootPrint
        {
            Offset = footprint.Offset,
            Footprint = new SubresourceFootPrint((Format)footprint.Format, footprint.Width, footprint.Height, footprint.Depth, footprint.RowPitch),
        };
    }

    private static ResourceDescription Description(CapturedResource captured)
    {
        if (captured.IsBuffer)
        {
            return ResourceDescription.Buffer(captured.Width, ResourceFlags.AllowUnorderedAccess);
        }

        return new ResourceDescription(
            (ResourceDimension)captured.Dimension, 0, captured.Width, captured.Height, captured.DepthOrArraySize, captured.MipLevels,
            (Format)captured.Format, 1, 0, TextureLayout.Unknown, (ResourceFlags)captured.Flags);
    }

    /// <summary>The state the program sees it in: unordered access when it can be written, else a shader resource.</summary>
    private static ResourceStates Home(CapturedResource captured)
    {
        if (captured.IsBuffer || (((ResourceFlags)captured.Flags) & ResourceFlags.AllowUnorderedAccess) != 0)
        {
            return ResourceStates.UnorderedAccess;
        }

        return ResourceStates.NonPixelShaderResource;
    }
}
