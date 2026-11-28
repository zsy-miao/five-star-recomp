// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Native;

namespace FiveStar.Loader.Shims.D3D12X.Binding;

/// <summary>The desktop device, resource and descriptor-heap methods the binding objects call (d3d12.h vtable order).</summary>
internal static unsafe class DesktopCalls
{
    private const int CreateShaderResourceViewSlot = 18;
    private const int CreateUnorderedAccessViewSlot = 19;
    private const int CreateSamplerSlot = 22;
    public const int CreateCommittedResourceSlot = 27;
    private const int CreatePlacedResourceSlot = 29;
    private const int MapSlot = 8;
    private const int GpuAddressSlot = 11;
    private const int HeapCpuStartSlot = 9;
    private const int HeapGpuStartSlot = 10;

    /// <summary>D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS and D3D12_TEXTURE_LAYOUT_ROW_MAJOR.</summary>
    public const int UnorderedAccessFlag = 4;
    private const int RowMajor = 1;

    private static readonly Guid ResourceIid = new("696442be-a72e-4059-bc79-5b5c98040fad");

    /// <summary>A committed buffer; 0 when the device refuses it.</summary>
    public static nint CreateBuffer(nint device, HeapProperties properties, ulong size, int flags, int state)
    {
        var description = new ResourceDescription { Dimension = ResourceDescription.BufferDimension, Width = size, Height = 1, DepthOrArraySize = 1, MipLevels = 1, SampleCount = 1, Layout = RowMajor, Flags = flags };
        Guid iid = ResourceIid;
        nint buffer = 0;
        var create = (delegate* unmanaged<nint, HeapProperties*, int, ResourceDescription*, int, void*, Guid*, nint*, int>)ComObject.Slot(device, CreateCommittedResourceSlot);
        int hr = create(device, &properties, 0, &description, state, null, &iid, &buffer);

        if (HResult.Failed(hr))
        {
            return 0;
        }

        return buffer;
    }

    /// <summary>A buffer placed at byte <paramref name="offset"/> of <paramref name="heap"/>; 0 when the device refuses it.</summary>
    public static nint CreatePlacedBuffer(nint device, nint heap, ulong offset, ulong size, int flags, int state)
    {
        var description = new ResourceDescription { Dimension = ResourceDescription.BufferDimension, Width = size, Height = 1, DepthOrArraySize = 1, MipLevels = 1, SampleCount = 1, Layout = RowMajor, Flags = flags };
        Guid iid = ResourceIid;
        nint buffer = 0;
        var create = (delegate* unmanaged<nint, nint, ulong, ResourceDescription*, int, void*, Guid*, nint*, int>)ComObject.Slot(device, CreatePlacedResourceSlot);
        int hr = create(device, heap, offset, &description, state, null, &iid, &buffer);

        if (HResult.Failed(hr))
        {
            return 0;
        }

        return buffer;
    }

    /// <summary>Maps subresource 0 for writing (no read range); null on failure.</summary>
    public static byte* Map(nint resource)
    {
        void* data = null;
        long* noRead = stackalloc long[] { 0, 0 };
        int hr = ((delegate* unmanaged<nint, uint, long*, void**, int>)ComObject.Slot(resource, MapSlot))(resource, 0, noRead, &data);

        if (HResult.Failed(hr))
        {
            return null;
        }

        return (byte*)data;
    }

    public static ulong GpuAddress(nint resource)
    {
        return ((delegate* unmanaged<nint, ulong>)ComObject.Slot(resource, GpuAddressSlot))(resource);
    }

    public static void ShaderResourceView(nint device, nint resource, BufferViewDescription* view, nint handle)
    {
        ((delegate* unmanaged<nint, nint, BufferViewDescription*, nint, void>)ComObject.Slot(device, CreateShaderResourceViewSlot))(device, VramTwins.Substitute(resource), view, handle);
    }

    public static void UnorderedAccessView(nint device, nint resource, BufferViewDescription* view, nint handle)
    {
        ((delegate* unmanaged<nint, nint, nint, BufferViewDescription*, nint, void>)ComObject.Slot(device, CreateUnorderedAccessViewSlot))(device, resource, 0, view, handle);
    }

    public static void Sampler(nint device, uint* description, nint handle)
    {
        ((delegate* unmanaged<nint, uint*, nint, void>)ComObject.Slot(device, CreateSamplerSlot))(device, description, handle);
    }

    public static nint HeapCpuStart(nint heap)
    {
        nint handle = 0;
        _ = ((delegate* unmanaged<nint, nint*, nint*>)ComObject.Slot(heap, HeapCpuStartSlot))(heap, &handle);

        return handle;
    }

    public static ulong HeapGpuStart(nint heap)
    {
        ulong handle = 0;
        _ = ((delegate* unmanaged<nint, ulong*, ulong*>)ComObject.Slot(heap, HeapGpuStartSlot))(heap, &handle);

        return handle;
    }
}
