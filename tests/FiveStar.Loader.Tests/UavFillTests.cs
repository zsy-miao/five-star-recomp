// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Graphics;
using FiveStar.Graphics.Rendering;
using FiveStar.Loader.Shims.D3D12X.Binding;
using FiveStar.Loader.Tests.Support;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>
/// The stand-in fill of held ray-tracing passes (RtStandIns) on WARP: one constant over a unorm shadow mask, a float
/// 2D-array and an R32_UINT view of R9G9B9E5 light, each found by heap index with the view UavViews keeps.
/// </summary>
public sealed unsafe class UavFillTests
{
    private const int Size = 8;

    [WarpFact]
    public void A_held_pass_outputs_read_the_stand_in_value_whatever_their_format()
    {
        using var gpu = GpuDevice.CreateWarp(debugLayer: false);
        var device = gpu.Device;
        using var heap = device.CreateDescriptorHeap(new Vortice.Direct3D12.DescriptorHeapDescription(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 3, DescriptorHeapFlags.ShaderVisible));
        using var context = new CommandContext(device);
        using var arena = new ResourceArena(device);

        var increment = device.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
        (Format Format, ushort Slices, FillValue Fill)[] targets =
        [
            (Format.R8_UNorm, 1, new FillValue(1, 1, 1, uint.MaxValue)),
            (Format.R16G16B16A16_Float, 2, new FillValue(0, 0, 0, 0)),
            (Format.R32_UInt, 1, FillValue.Shared(RtStandIns.AmbientLevel)),
        ];
        var textures = new List<ID3D12Resource>();

        uint* words = stackalloc uint[6];
        var list = context.List;
        list.SetDescriptorHeaps(1, new[] { heap });

        for (uint index = 0; index < targets.Length; index++)
        {
            var (format, slices, fill) = targets[index];
            var description = Vortice.Direct3D12.ResourceDescription.Texture2D(format, Size, Size, slices, 1, flags: ResourceFlags.AllowUnorderedAccess);
            var texture = device.CreateCommittedResource(HeapType.Default, description, ResourceStates.UnorderedAccess);
            textures.Add(texture);

            var dimension = slices > 1 ? UnorderedAccessViewDimension.Texture2DArray : UnorderedAccessViewDimension.Texture2D;
            var view = new UnorderedAccessViewDescription { Format = format, ViewDimension = dimension };
            view.Texture2DArray.ArraySize = slices;
            device.CreateUnorderedAccessView(texture, null, view, heap.GetCPUDescriptorHandleForHeapStart() + (int)(index * increment));

            (words[0], words[1], words[4]) = ((uint)format, (uint)dimension, slices);
            var known = UavViews.Of(Resource(description), words);
            Assert.NotNull(known);
            Assert.True(UavFill.Record(device.NativePointer, list.NativePointer, heap.GetGPUDescriptorHandleForHeapStart().Ptr, index, known.Value, fill));
        }

        context.ExecuteAndWait();

        Assert.Equal(0xFFu, Read(device, context, arena, textures[0], 1)[0] & 0xFFu);
        Assert.All(Read(device, context, arena, textures[1], 2 * 4), half => Assert.Equal(0u, half & 0xFFFFu));
        Assert.Equal(FillValue.SharedExponent(0.6f, 0.6f, 0.6f), Read(device, context, arena, textures[2], 4)[0]);
    }

    /// <summary>
    /// The scene-scaled stand-in: a light image filled (2, 1, 0.5) is measured, and a fill by half its level writes
    /// (1, 0.5, 0.25) into an R32_UINT view, packed on the GPU as the CPU packs R9G9B9E5.
    /// </summary>
    [WarpFact]
    public void A_fill_by_share_writes_that_share_of_the_measured_light_image()
    {
        using var gpu = GpuDevice.CreateWarp(debugLayer: false);
        var device = gpu.Device;
        using var heap = device.CreateDescriptorHeap(new Vortice.Direct3D12.DescriptorHeapDescription(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 2, DescriptorHeapFlags.ShaderVisible));
        using var context = new CommandContext(device);
        using var arena = new ResourceArena(device);

        var increment = device.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
        Format[] formats = [Format.R16G16B16A16_Float, Format.R32_UInt];
        var textures = new List<ID3D12Resource>();
        var views = new List<UavView>();
        uint* words = stackalloc uint[6];

        for (uint index = 0; index < formats.Length; index++)
        {
            var description = Vortice.Direct3D12.ResourceDescription.Texture2D(formats[index], Size, Size, 1, 1, flags: ResourceFlags.AllowUnorderedAccess);
            textures.Add(device.CreateCommittedResource(HeapType.Default, description, ResourceStates.UnorderedAccess));
            var view = new UnorderedAccessViewDescription { Format = formats[index], ViewDimension = UnorderedAccessViewDimension.Texture2D };
            device.CreateUnorderedAccessView(textures[(int)index], null, view, heap.GetCPUDescriptorHandleForHeapStart() + (int)(index * increment));
            (words[0], words[1], words[4]) = ((uint)formats[index], (uint)UnorderedAccessViewDimension.Texture2D, 1);
            views.Add(UavViews.Of(Resource(description), words)!.Value);
        }

        ulong start = heap.GetGPUDescriptorHandleForHeapStart().Ptr;
        var steps = new Func<nint, bool>[]
        {
            list => UavFill.Record(device.NativePointer, list, start, 0, views[0], new FillValue(2, 1, 0.5f, 0)),
            list => UavFill.Measure(device.NativePointer, list, start, 0, views[0], limit: 16),
            list => UavFill.Record(device.NativePointer, list, start, 1, views[1], new FillValue(0, 0, 0, 0, Share: 0.5f, Least: 0.01f)),
        };

        // Each step in its own submission: the next reads what the one before wrote.
        foreach (var step in steps)
        {
            context.List.SetDescriptorHeaps(1, new[] { heap });
            Assert.True(step(context.List.NativePointer));
            context.ExecuteAndWait();
        }

        Assert.True(UavFill.Measured);
        Assert.Equal(FillValue.SharedExponent(1, 0.5f, 0.25f), Read(device, context, arena, textures[1], 4)[0]);

        // Without tint the same share is a grey of the level's luminance.
        context.List.SetDescriptorHeaps(1, new[] { heap });
        Assert.True(UavFill.Record(device.NativePointer, context.List.NativePointer, start, 1, views[1], new FillValue(0, 0, 0, 0, Share: 0.5f, Least: 0.01f, Tint: 0)));
        context.ExecuteAndWait();

        float grey = ((0.2126f * 2) + 0.7152f + (0.0722f * 0.5f)) * 0.5f;
        Assert.Equal(FillValue.SharedExponent(grey, grey, grey), Read(device, context, arena, textures[1], 4)[0]);
    }

    [Fact]
    public void The_screen_probe_grey_packs_as_r9g9b9e5()
    {
        // 0.6 = 307 / 512: exponent 15 (bias 15, mantissa scale 2^-9), the same mantissa in each channel.
        Assert.Equal(307u | (307u << 9) | (307u << 18) | (15u << 27), FillValue.SharedExponent(0.6f, 0.6f, 0.6f));
    }

    [Fact]
    public void The_sample_line_says_when_no_held_pass_was_filled_and_each_rule_names_its_passes()
    {
        Assert.Equal("ray-tracing stand-ins: none filled", RtStandIns.Summary);
        Assert.Equal(0, RtStandIns.RuleOf("sgd1_0_t32_shadowmask~CS_TraceShadowRays_0_Wrapped"));
        Assert.Equal(-1, RtStandIns.RuleOf("sgd1_0_t32_PS_DeferredTextured_0_Wrapped"));
    }

    [Fact]
    public void Integer_views_are_never_filled_and_the_probe_grey_only_reaches_float_colour()
    {
        // R32_UINT (packed probe fields), R8G8B8A8_UNORM (encoded normals), R16G16B16A16_FLOAT and R8_UNORM.
        Assert.False(RtStandIns.Takes(View(42, 0), colourOnly: false));
        Assert.False(RtStandIns.Takes(View(28, 0), colourOnly: true));
        Assert.True(RtStandIns.Takes(View(10, 0), colourOnly: true));
        Assert.True(RtStandIns.Takes(View(61, 0), colourOnly: false));
    }

    [Fact]
    public void An_r32_uint_view_takes_the_probe_grey_only_when_its_texture_is_read_as_shared_exponent_light()
    {
        const nint Light = 0x5150;
        const nint Fields = 0x5160;
        UavViews.NoteSharedExponent(Light);

        Assert.True(RtStandIns.Takes(View(42, Light), colourOnly: true));
        Assert.False(RtStandIns.Takes(View(42, Light), colourOnly: false));
        Assert.False(RtStandIns.Takes(View(42, Fields), colourOnly: true));
    }

    private static UavView View(int format, nint resource)
    {
        return new UavView(format, UavViews.Texture2D, Size, Size, 1, resource);
    }

    private static Shims.D3D12X.ResourceDescription Resource(Vortice.Direct3D12.ResourceDescription description)
    {
        return new Shims.D3D12X.ResourceDescription
        {
            Dimension = (int)description.Dimension, Width = description.Width, Height = description.Height,
            DepthOrArraySize = description.DepthOrArraySize, MipLevels = description.MipLevels, Format = (int)description.Format,
        };
    }

    /// <summary>The first row of the texture's first slice: <paramref name="bytesPerTexel"/> bytes per texel, as dwords.</summary>
    private static uint[] Read(ID3D12Device device, CommandContext context, ResourceArena arena, ID3D12Resource texture, int bytesPerTexel)
    {
        const int Pitch = 256;

        var readback = arena.ReadbackBuffer(Pitch * Size);
        var footprint = new PlacedSubresourceFootPrint { Footprint = new SubresourceFootPrint(texture.Description.Format, Size, Size, 1, Pitch) };
        context.List.ResourceBarrierTransition(texture, ResourceStates.UnorderedAccess, ResourceStates.CopySource);
        context.List.CopyTextureRegion(new TextureCopyLocation(readback, footprint), 0, 0, 0, new TextureCopyLocation(texture, 0));
        context.List.ResourceBarrierTransition(texture, ResourceStates.CopySource, ResourceStates.UnorderedAccess);
        context.ExecuteAndWait();

        void* mapped;
        readback.Map(0, null, &mapped).CheckError();
        var data = (uint*)mapped;
        var row = new uint[Math.Max(1, Size * bytesPerTexel / 4)];
        new Span<uint>(data, row.Length).CopyTo(row);
        readback.Unmap(0);

        return row;
    }
}
