// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using FiveStar.Loader.Native;

namespace FiveStar.Loader.Shims.D3D12X.Binding;

/// <summary>
/// A small compute program that writes one value over a texture UAV of the shader-visible view heap, by heap index: a
/// float4 for a float, unorm or snorm view, a uint for a uint view (an R9G9B9E5 colour packed, as the game's
/// R32_UINT views of such textures hold it). 2D, 2D-array and 3D views. The value is the caller's constant, or a share
/// of a level the same program measured before from another view (<see cref="Measure"/>: the mean of the game's cached
/// screen radiance, for the screen-probe light's stand-in; the estimate is VIctory's idea, see <see cref="RtStandIns"/>).
/// Recording it replaces the list's compute root signature and pipeline: the caller sets its own again.
/// </summary>
internal static unsafe class UavFill
{
    private const string Source = """
        #define RS "RootConstants(num32BitConstants=11, b0), UAV(u0, space=0), " \
            "DescriptorTable(UAV(u0, space=1, numDescriptors=unbounded)), DescriptorTable(UAV(u0, space=2, numDescriptors=unbounded)), " \
            "DescriptorTable(UAV(u0, space=3, numDescriptors=unbounded)), DescriptorTable(UAV(u0, space=4, numDescriptors=unbounded)), " \
            "DescriptorTable(UAV(u0, space=5, numDescriptors=unbounded)), DescriptorTable(UAV(u0, space=6, numDescriptors=unbounded))"
        cbuffer Fill : register(b0) { uint index; uint kind; uint width; uint height; uint value; float red; float green; float blue; float share; float least; float tint; };
        RWStructuredBuffer<float4> level : register(u0, space0);
        RWTexture2D<float4> floats2[] : register(u0, space1);
        RWTexture2D<uint> uints2[] : register(u0, space2);
        RWTexture2DArray<float4> floatArrays[] : register(u0, space3);
        RWTexture2DArray<uint> uintArrays[] : register(u0, space4);
        RWTexture3D<float4> floats3[] : register(u0, space5);
        RWTexture3D<uint> uints3[] : register(u0, space6);
        // DXGI's R9G9B9E5_SHAREDEXP bits of a colour, as FillValue.SharedExponent.
        uint Pack(float3 colour) {
            colour = clamp(colour, 0.0f, 65408.0f);
            float largest = max(colour.r, max(colour.g, colour.b));
            int exponent = clamp((int)floor(log2(max(largest, 1e-30f))) + 16, 0, 31);
            float scale = exp2((float)(exponent - 24));
            if (round(largest / scale) > 511.0f && exponent < 31) { exponent++; scale *= 2.0f; }
            uint3 mantissa = (uint3)clamp(round(colour / scale), 0.0f, 511.0f);
            uint packed = mantissa.r | (mantissa.g << 9) | (mantissa.b << 18) | ((uint)exponent << 27);
            return largest > 0.0f ? packed : 0u;
        }
        // The mean of a 32x18 grid of the float view at index, each sample bounded by red, into level[0].
        void Measure() {
            float3 sum = 0.0f;
            for (uint y = 0u; y < 18u; y++) {
                for (uint x = 0u; x < 32u; x++) {
                    uint2 at = uint2((x * 2u + 1u) * width / 64u, (y * 2u + 1u) * height / 36u);
                    sum += min(max(floats2[index][at].rgb, 0.0f), red);
                }
            }
            level[0] = float4(sum / 576.0f, 1.0f);
        }
        [RootSignature(RS)]
        [numthreads(8, 8, 1)]
        void main(uint3 id : SV_DispatchThreadID) {
            if (kind == 6u) {
                if (id.x == 0u && id.y == 0u && id.z == 0u) Measure();
                return;
            }
            if (id.x >= width || id.y >= height) return;
            float4 colour = float4(red, green, blue, 1.0f);
            uint packed = value;
            if (share > 0.0f) {
                float3 mean = level[0].rgb;
                colour.rgb = max(lerp(dot(mean, float3(0.2126f, 0.7152f, 0.0722f)), mean, tint) * share, least);
                packed = Pack(colour.rgb);
            }
            if (kind == 0u) floats2[index][id.xy] = colour;
            else if (kind == 1u) uints2[index][id.xy] = packed;
            else if (kind == 2u) floatArrays[index][id] = colour;
            else if (kind == 3u) uintArrays[index][id] = packed;
            else if (kind == 4u) floats3[index][id] = colour;
            else uints3[index][id] = packed;
        }
        """;

    /// <summary>Root parameters: the constants, the measured level's buffer, then one table per kind.</summary>
    private const uint ConstantsParameter = 0;
    private const uint LevelParameter = 1;
    private const uint FirstTable = 2;
    private const uint Tables = 6;
    private const uint Constants = 11;
    private const uint GroupSide = 8;

    /// <summary>The program's kind that measures a float view instead of writing one.</summary>
    private const uint MeasureKind = 6;

    /// <summary>The measured level's buffer: one float4, in a buffer of the smallest size the desktop makes.</summary>
    private const ulong LevelBytes = 256;

    /// <summary>
    /// ID3D12Device::CheckFeatureSupport with D3D12_FEATURE_FORMAT_SUPPORT, whose data is the format and its two
    /// support masks; D3D12_FORMAT_SUPPORT2_UAV_TYPED_LOAD in the second (d3d12.h).
    /// </summary>
    private const int CheckFeatureSupportSlot = 13;
    private const int FormatSupportFeature = 3;
    private const int FormatSupportDwords = 3;
    private const int Support2Dword = 2;
    private const uint TypedLoad = 0x40;

    /// <summary>ID3D12GraphicsCommandList::SetComputeRootUnorderedAccessView.</summary>
    private const int SetRootUavSlot = 41;

    /// <summary>The program's first kind of 2D-array views and of 3D views (float, then uint).</summary>
    private const uint ArrayKinds = 2;
    private const uint VolumeKinds = 4;

    /// <summary>D3DCOMPILE_ENABLE_UNBOUNDED_DESCRIPTOR_TABLES: the program indexes whole heap tables.</summary>
    private const uint UnboundedTables = 1u << 20;

    /// <summary>ID3D12Device: CreateComputePipelineState and CreateRootSignature (d3d12.h).</summary>
    private const int CreateComputePipelineSlot = 11;
    private const int CreateRootSignatureSlot = 16;

    /// <summary>ID3D12GraphicsCommandList: Dispatch, SetPipelineState, SetComputeRootSignature, SetComputeRootDescriptorTable, SetComputeRoot32BitConstants.</summary>
    private const int DispatchSlot = 14;
    private const int SetPipelineSlot = 25;
    private const int SetRootSignatureSlot = 29;
    private const int SetTableSlot = 31;
    private const int SetConstantsSlot = 35;

    /// <summary>The DXGI *_UINT formats (a uint view); *_SINT and typeless views are not written.</summary>
    private static readonly HashSet<int> UintFormats = [3, 7, 12, 17, 25, 30, 36, 42, 50, 57, 62];
    private static readonly HashSet<int> SintFormats = [4, 8, 14, 18, 32, 38, 43, 52, 59, 64];

    private static readonly object Gate = new();
    private static readonly Guid RootSignatureIid = new("c54a6b66-72df-4ee8-8be5-a946a1429214");
    private static readonly Guid PipelineIid = new("765a30f3-f624-4c6f-a828-ace948622445");
    private static readonly ConcurrentDictionary<int, bool> TypedLoads = new();
    private static nint _root;
    private static nint _pipeline;
    private static ulong _levelAddress;
    private static bool _measured;
    private static bool _failed;

    /// <summary>Records the fill of the UAV at <paramref name="index"/> of the heap whose GPU start is <paramref name="heapStart"/>; false when the view is not written.</summary>
    public static bool Record(nint device, nint list, ulong heapStart, uint index, UavView view, FillValue fill)
    {
        uint kind = Kind(view);

        if (kind == uint.MaxValue || !Ready(device))
        {
            return false;
        }

        uint depth = 1;

        if (view.Dimension != UavViews.Texture2D)
        {
            depth = view.Slices;
        }

        Bind(list, heapStart, index, kind, view, fill);
        ((delegate* unmanaged<nint, uint, uint, uint, void>)ComObject.Slot(list, DispatchSlot))(list, (view.Width + GroupSide - 1) / GroupSide, (view.Height + GroupSide - 1) / GroupSide, depth);

        return true;
    }

    /// <summary>Whether a level was measured (<see cref="Measure"/>): until then a fill by its share would write the least.</summary>
    public static bool Measured => Volatile.Read(ref _measured);

    /// <summary>
    /// Records the measurement of the 2D float UAV at <paramref name="index"/>: the mean of a 32x18 grid of its texels,
    /// each bounded by <paramref name="limit"/>, kept on the GPU for the fills that write a share of it
    /// (<see cref="FillValue.Share"/>). False when the view is not a 2D float one the adapter can load from.
    /// </summary>
    public static bool Measure(nint device, nint list, ulong heapStart, uint index, UavView view, float limit)
    {
        if (Kind(view) != 0 || !Ready(device) || !Loads(device, view.Format))
        {
            return false;
        }

        Bind(list, heapStart, index, MeasureKind, view, new FillValue(limit, 0, 0, 0));
        ((delegate* unmanaged<nint, uint, uint, uint, void>)ComObject.Slot(list, DispatchSlot))(list, 1, 1, 1);
        Volatile.Write(ref _measured, true);

        return true;
    }

    /// <summary>Sets the program, its tables over the heap at <paramref name="heapStart"/>, the level's buffer and its constants.</summary>
    private static void Bind(nint list, ulong heapStart, uint index, uint kind, UavView view, FillValue fill)
    {
        ((delegate* unmanaged<nint, nint, void>)ComObject.Slot(list, SetRootSignatureSlot))(list, _root);
        ((delegate* unmanaged<nint, nint, void>)ComObject.Slot(list, SetPipelineSlot))(list, _pipeline);
        ((delegate* unmanaged<nint, uint, ulong, void>)ComObject.Slot(list, SetRootUavSlot))(list, LevelParameter, _levelAddress);

        for (uint table = FirstTable; table < FirstTable + Tables; table++)
        {
            ((delegate* unmanaged<nint, uint, ulong, void>)ComObject.Slot(list, SetTableSlot))(list, table, heapStart);
        }

        // The program's cbuffer: index, kind, width, height, the uint value, the colour, then the share, the least and the tint.
        uint* constants = stackalloc uint[]
        {
            index, kind, view.Width, view.Height, fill.Packed,
            BitConverter.SingleToUInt32Bits(fill.Red), BitConverter.SingleToUInt32Bits(fill.Green), BitConverter.SingleToUInt32Bits(fill.Blue),
            BitConverter.SingleToUInt32Bits(fill.Share), BitConverter.SingleToUInt32Bits(fill.Least), BitConverter.SingleToUInt32Bits(fill.Tint),
        };
        ((delegate* unmanaged<nint, uint, uint, uint*, uint, void>)ComObject.Slot(list, SetConstantsSlot))(list, ConstantsParameter, Constants, constants, 0);
    }

    /// <summary>Whether the adapter loads from a UAV of <paramref name="format"/> (asked once per format).</summary>
    private static bool Loads(nint device, int format)
    {
        return TypedLoads.GetOrAdd(format, static (asked, adapter) =>
        {
            uint* support = stackalloc uint[FormatSupportDwords];
            support[0] = (uint)asked;
            int hr = ((delegate* unmanaged<nint, int, void*, uint, int>)ComObject.Slot(adapter, CheckFeatureSupportSlot))(adapter, FormatSupportFeature, support, FormatSupportDwords * sizeof(uint));

            return !HResult.Failed(hr) && (support[Support2Dword] & TypedLoad) != 0;
        }, device);
    }

    /// <summary>Whether a view format is an integer one (*_UINT or *_SINT).</summary>
    public static bool Integer(int format)
    {
        return UintFormats.Contains(format) || SintFormats.Contains(format);
    }

    /// <summary>The program's kind of view: 0/1 2D float/uint, 2/3 array, 4/5 3D; uint.MaxValue for a view not written.</summary>
    public static uint Kind(UavView view)
    {
        if (SintFormats.Contains(view.Format) || view.Format == 0)
        {
            return uint.MaxValue;
        }

        uint uint32 = 0;

        if (UintFormats.Contains(view.Format))
        {
            uint32 = 1;
        }

        switch (view.Dimension)
        {
            case UavViews.Texture2D:
                return uint32;
            case UavViews.Texture2DArray:
                return ArrayKinds + uint32;
            case UavViews.Texture3D:
                return VolumeKinds + uint32;
            default:
                return uint.MaxValue;
        }
    }

    private static bool Ready(nint device)
    {
        if (_pipeline != 0)
        {
            return true;
        }

        lock (Gate)
        {
            if (_pipeline == 0 && !_failed)
            {
                _failed = !Make(device);
            }

            return _pipeline != 0;
        }
    }

    private static bool Make(nint device)
    {
        nint code = Compile();

        if (device == 0 || code == 0)
        {
            return false;
        }

        byte* bytes = ((delegate* unmanaged<nint, byte*>)ComObject.Slot(code, IndirectClampNative.BlobPointerSlot))(code);
        nuint size = ((delegate* unmanaged<nint, nuint>)ComObject.Slot(code, IndirectClampNative.BlobSizeSlot))(code);
        Guid rootIid = RootSignatureIid;
        nint root = 0;
        int hr = ((delegate* unmanaged<nint, uint, byte*, nuint, Guid*, nint*, int>)ComObject.Slot(device, CreateRootSignatureSlot))(device, 0, bytes, size, &rootIid, &root);

        if (!HResult.Failed(hr))
        {
            nint* description = stackalloc nint[] { root, (nint)bytes, (nint)size, 0, 0, 0, 0 };
            Guid pipelineIid = PipelineIid;
            nint pipeline = 0;
            hr = ((delegate* unmanaged<nint, nint*, Guid*, nint*, int>)ComObject.Slot(device, CreateComputePipelineSlot))(device, description, &pipelineIid, &pipeline);

            _levelAddress = MakeLevel(device);
            _pipeline = pipeline;
            QueueProgress.Name(pipeline, "loader:uav-fill");
        }

        ComObject.Release(code);
        _root = root;

        if (HResult.Failed(hr))
        {
            ShimContext.Log?.Warn($"[d3d12_xs] stand-in UAV fill: no program ({HResult.Format(hr)}); held passes leave their outputs as they are");
        }

        return !HResult.Failed(hr);
    }

    /// <summary>
    /// The buffer the measured level is kept in, made with the program and never released: the measurement writes it and
    /// every fill's root signature names it, in any list, so it stays in UNORDERED_ACCESS. Its GPU address.
    /// </summary>
    private static ulong MakeLevel(nint device)
    {
        Marshal.AddRef(device);
        using var desktop = new Vortice.Direct3D12.ID3D12Device(device);
        var description = Vortice.Direct3D12.ResourceDescription.Buffer(LevelBytes, Vortice.Direct3D12.ResourceFlags.AllowUnorderedAccess);
        var buffer = desktop.CreateCommittedResource(Vortice.Direct3D12.HeapType.Default, description, Vortice.Direct3D12.ResourceStates.UnorderedAccess);
        ulong address = buffer.GPUVirtualAddress;

        // One reference stays: the buffer lives as long as the process.
        Marshal.AddRef(buffer.NativePointer);
        buffer.Dispose();

        return address;
    }

    /// <summary>The program compiled by d3dcompiler_47 (cs_5_1, its root signature inside); 0 when that fails.</summary>
    private static nint Compile()
    {
        byte[] source = Encoding.ASCII.GetBytes(Source);
        byte[] entry = Encoding.ASCII.GetBytes("main\0");
        byte[] target = Encoding.ASCII.GetBytes("cs_5_1\0");
        byte[] name = Encoding.ASCII.GetBytes("uav_fill\0");

        nint code = 0;
        nint errors = 0;
        int hr;

        fixed (byte* text = source, main = entry, profile = target, file = name)
        {
            hr = IndirectClampNative.D3DCompile(text, (nuint)source.Length, file, 0, 0, main, profile, UnboundedTables, 0, &code, &errors);
        }

        if (errors != 0)
        {
            byte* message = ((delegate* unmanaged<nint, byte*>)ComObject.Slot(errors, IndirectClampNative.BlobPointerSlot))(errors);
            nuint length = ((delegate* unmanaged<nint, nuint>)ComObject.Slot(errors, IndirectClampNative.BlobSizeSlot))(errors);
            ShimContext.Log?.Warn($"[d3d12_xs] stand-in UAV fill: {Encoding.ASCII.GetString(message, (int)length).Trim()}");
            ComObject.Release(errors);
        }

        if (HResult.Failed(hr))
        {
            return 0;
        }

        return code;
    }
}
