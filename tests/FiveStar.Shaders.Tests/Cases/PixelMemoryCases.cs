// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Shaders.Tests.Reference;
using FiveStar.Shaders.Tests.Warp;
using Vortice.DXGI;

namespace FiveStar.Shaders.Tests.Cases;

/// <summary>
/// Pixel-shader cases for scalar / buffer / global / formatted loads, images and samplers, and a float export. Bindless:
/// V# / T# dword0 is the resource heap index, S# dword0 the sampler heap index, 64-bit pointers address the GPU-memory
/// window at heap slot 0.
/// </summary>
public static class PixelMemoryCases
{
    private const string FormatProgram = """
        v_cvt_u32_f32_e32 v10, v2
        v_and_b32_e32 v10, 3, v10
        buffer_load_format_xyzw v[20:23], v10, s[0:3], 0 idxen
        s_waitcnt vmcnt(0)
        """;

    private static readonly uint[] BufferWords = [.. Enumerable.Range(0, 64).Select(index => 0xA0000000 + (0x11 * (uint)index))];

    public static IEnumerable<PixelCase> All()
    {
        return
        [
            ScalarBufferLoad(),
            BufferLoad(),
            GlobalPointers(),
            .. Formats(),
            ImageLoadSample(),
            LiteralSamplers(),
            LiteralSamplerAcrossBlocks(),
            OffsetsAndTexelCoordinates(),
            ImageLoadInteger(),
            ImageSampleLinear(),
            ImageGatherAtLevel(),
            FloatExport(),
            CompressedExport(),
            CompressedUintExport(),
            SharedExponentImplicitSample(),
            SharedExponentGather(),
            SharedExponentArraySample(),
            PatchedFloatExport(),
            PatchedRedExport(),
        ];
    }

    private static uint F(double value) => FloatBits.Of(value);

    private static Bindings Raw(int heapIndex, uint[] words, params uint[] user)
    {
        return new Bindings { UserSgprs = user, Buffers = [new BufferResource(heapIndex, words, Writable: false)] };
    }

    // Bindless: V# dword0 (s0) is the descriptor-heap index.
    private static PixelCase ScalarBufferLoad()
    {
        const string Program = """
            s_buffer_load_dword s20, s[0:3], 0x0
            s_buffer_load_dwordx2 s[22:23], s[0:3], 0x8
            s_buffer_load_dwordx4 s[24:27], s[0:3], 0x20
            s_mov_b32 s28, 0x44
            s_buffer_load_dword s29, s[0:3], s28
            s_waitcnt lgkmcnt(0)
            v_mov_b32 v20, s20
            v_mov_b32 v21, s22
            v_mov_b32 v22, s23
            v_mov_b32 v23, s24
            v_mov_b32 v24, s25
            v_mov_b32 v25, s26
            v_mov_b32 v26, s27
            v_mov_b32 v27, s29
            """;
        Expected[] expected = [BufferWords[0], BufferWords[2], BufferWords[3], BufferWords[8], BufferWords[9], BufferWords[10], BufferWords[11], BufferWords[17]];

        return new PixelCase("s_buffer_load", Program, 8, _ => expected) { Bindings = Raw(1, BufferWords, 1, 0, 0x100, 0) };
    }

    // offen / idxen (stride = V# dword1 bits 16-29) / soffset / instruction offset.
    private static PixelCase BufferLoad()
    {
        const string Program = """
            v_cvt_u32_f32_e32 v10, v2
            v_lshlrev_b32_e32 v11, 2, v10
            buffer_load_dword v20, v11, s[0:3], 0 offen
            buffer_load_dwordx2 v[21:22], v11, s[0:3], 0 offen offset:16
            buffer_load_dword v23, v10, s[0:3], 0 idxen
            buffer_load_dword v24, v10, s[0:3], s4 idxen offset:4
            s_waitcnt vmcnt(0)
            """;

        return new PixelCase(
            "buffer_load_dword",
            Program,
            5,
            pixel => [BufferWords[pixel.X], BufferWords[pixel.X + 4], BufferWords[pixel.X + 5], BufferWords[2 * pixel.X], BufferWords[(2 * pixel.X) + 3]])
        {
            Bindings = Raw(1, BufferWords, 1, 8 << 16, 0x100, 1u << 28, 8),
        };
    }

    // 64-bit pointers read the GPU-memory window (heap slot 0): address = pointer low dword + offset.
    private static PixelCase GlobalPointers()
    {
        const string Program = """
            s_load_dword s20, s[0:1], 0x8
            s_load_dwordx4 s[24:27], s[0:1], 0x0
            s_waitcnt lgkmcnt(0)
            v_cvt_u32_f32_e32 v12, v2
            v_lshl_add_u32 v10, v12, 2, 0x80
            v_mov_b32 v11, 0
            global_load_dword v21, v[10:11], off
            v_lshlrev_b32_e32 v13, 2, v12
            global_load_dword v22, v13, s[2:3] offset:4
            s_waitcnt vmcnt(0)
            v_mov_b32 v20, s20
            v_mov_b32 v23, s24
            v_mov_b32 v24, s27
            """;

        return new PixelCase(
            "smem_global_ptr",
            Program,
            5,
            pixel => [BufferWords[18], BufferWords[32 + pixel.X], BufferWords[(0x20 + (4 * pixel.X) + 4) / 4], BufferWords[16], BufferWords[19]])
        {
            Bindings = Raw(0, BufferWords, 0x40, 0, 0x20, 0),
        };
    }

    // V# dword3 as the hardware holds it: the gfx10 unified format (RDNA 2 ISA numbering) and the destination swizzle;
    // missing components read (0, 0, 0, 1).
    private static PixelCase[] Formats()
    {
        var h = (double value) => FloatBits.Half(value);
        uint[] tenBits = [0xC0300401, 0x3FF003FF, 0x40100403, 0xFFFFFFFF];
        uint[] halves = [FloatBits.Pack(h(1.5), h(-2.0)), FloatBits.Pack(h(65504.0), 0x0001), FloatBits.Pack(0x7C00, h(0.1)), FloatBits.Pack(h(-0.0), h(3.0))];
        uint[] smallFloats = [0x801C03C0, 0xF7C00FC0, 0x12345678, 0];
        uint[] bytes = [0xFF804000, 0x01020304, 0x7F7F7F7F, 0x00FFFF00];
        uint[] wide = [0x3F800000, 0xBF000000, 0x12345678, 0x80000001];

        return
        [
            FormatCase("8_8_8_8_unorm", 56, 10, BufferFormats.Unorm, 4, bytes, 1),
            FormatCase("16_16_16_16_float", 71, 12, BufferFormats.FloatingPoint, 8, [.. halves, .. halves], 0),
            FormatCase("32_32_uint", 62, 11, BufferFormats.UnsignedInt, 8, [0xDEADBEEF, 0x12345678, 1, 2, 3, 4, 5, 6], 0),
            FormatCase("8_8_snorm", 15, 3, BufferFormats.Snorm, 4, [0x7F80, 0x8100, 0x0040, 0xFFC0], 1),
            FormatCase("16_16_sint", 28, 5, BufferFormats.SignedInt, 4, [0x8000FFFF, 0x7FFF0001, 0x00010000, 0xFFFE8001], 0),
            FormatCase("2_10_10_10_uint", 54, 9, BufferFormats.UnsignedInt, 4, tenBits, 0),
            FormatCase("10_10_10_2_uint", 48, 8, BufferFormats.UnsignedInt, 4, tenBits, 0),
            FormatCase("10_11_11_float", 36, 6, BufferFormats.FloatingPoint, 4, smallFloats, 0),
            FormatCase("11_11_10_float", 43, 7, BufferFormats.FloatingPoint, 4, smallFloats, 0),
            FormatCase("8_8_8_8_unorm_bgra", 56, 10, BufferFormats.Unorm, 4, bytes, 1, BufferFormats.Bgra),

            // 32-bit components in memory order read their own dwords (prelude fmt_raw); a swizzled one decodes.
            FormatCase("32_float", 22, 4, BufferFormats.FloatingPoint, 4, [0x3FC00000, 0x80000000, 0x7F800000, 0xC1200000], 0),
            FormatCase("32_32_32_float", 74, 13, BufferFormats.FloatingPoint, 12, [.. wide, .. wide, .. wide], 0),
            FormatCase("32_32_32_32_sint", 76, 14, BufferFormats.SignedInt, 16, [.. wide, .. wide, .. wide, .. wide], 0),
            FormatCase("32_32_32_32_float_bgra", 77, 14, BufferFormats.FloatingPoint, 16, [.. wide, .. wide, .. wide, .. wide], 0, BufferFormats.Bgra),
        ];
    }

    private static PixelCase FormatCase(string suffix, int unified, int layout, int numeric, int stride, uint[] words, int ulps, int[]? swizzle = null)
    {
        var perElement = stride / 4;
        var select = swizzle ?? BufferFormats.Identity;

        Expected[] Expect(PixelContext pixel)
        {
            var element = words.Skip((pixel.X & 3) * perElement).Take(perElement).ToArray();
            var decoded = BufferFormats.Decode(element, layout, numeric);

            return [.. select.Select(component => Tolerant(decoded[component], ulps))];
        }

        var user = new uint[] { 1, (uint)stride << 16, 0x1000, BufferFormats.Word3(unified, select) };

        return new PixelCase($"buffer_load_format[{suffix}]", FormatProgram, 4, Expect) { Bindings = Raw(1, words, user) };
    }

    private static Expected Tolerant(uint bits, int ulps)
    {
        if (ulps > 0)
        {
            return Expected.Ulp(bits, ulps);
        }

        return Expected.Exact(bits);
    }

    /// <summary>A 4x4 R32G32B32A32_FLOAT texture: texel (i, j) = (i, j, i + 10 j, 0.5 + i).</summary>
    private static uint[] Texel(int i, int j)
    {
        return [F(i), F(j), F(i + (10 * j)), F(0.5 + i)];
    }

    private static uint[] FloatTexture()
    {
        return [.. Enumerable.Range(0, 16).SelectMany(index => Texel(index % 4, index / 4))];
    }

    // T# dword0 = SRV heap index, S# dword0 = sampler heap index; dmask packs the enabled channels.
    private static PixelCase ImageLoadSample()
    {
        const string Program = """
            v_cvt_u32_f32_e32 v10, v2
            v_cvt_u32_f32_e32 v11, v3
            v_and_b32_e32 v10, 3, v10
            v_and_b32_e32 v11, 3, v11
            image_load v[20:23], v[10:11], s[0:7] dmask:0xf dim:SQ_RSRC_IMG_2D
            v_cvt_f32_u32_e32 v12, v10
            v_cvt_f32_u32_e32 v13, v11
            v_add_f32_e32 v12, 0.5, v12
            v_add_f32_e32 v13, 0.5, v13
            v_mul_f32_e32 v12, 0x3e800000, v12
            v_mul_f32_e32 v13, 0x3e800000, v13
            image_sample_lz v[24:27], v[12:13], s[0:7], s[8:11] dmask:0xf dim:SQ_RSRC_IMG_2D
            image_sample_lz v[28:29], v[12:13], s[0:7], s[8:11] dmask:0x5 dim:SQ_RSRC_IMG_2D
            image_load v[30:31], v[10:11], s[0:7] dmask:0xa dim:SQ_RSRC_IMG_2D
            s_waitcnt vmcnt(0)
            """;
        var bindings = new Bindings
        {
            UserSgprs = [2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
            Textures = [new TextureResource(2, Format.R32G32B32A32_Float, 4, 4, FloatTexture())],
            Samplers = [new SamplerResource(0, Linear: false)],
        };

        return new PixelCase("image_load_sample", Program, 12, pixel =>
        {
            var t = Texel(pixel.X & 3, pixel.Y & 3);

            return [.. t, .. t, t[0], t[2], t[1], t[3]];
        })
        {
            Bindings = bindings,
        };
    }

    // Two S#s built from literals (a root signature's static samplers): clamp with point filtering, read past the right
    // edge, and a less-or-equal comparison sampler. Each gets the sampler its words describe at the slot they give
    // (StaticSampler); before, every literal S# read one trilinear wrap sampler without comparison.
    private static PixelCase LiteralSamplers()
    {
        const string Program = """
            v_cvt_u32_f32_e32 v10, v2
            v_cvt_u32_f32_e32 v11, v3
            v_and_b32_e32 v10, 3, v10
            v_and_b32_e32 v11, 3, v11
            v_cvt_f32_u32_e32 v12, v10
            v_cvt_f32_u32_e32 v13, v11
            v_add_f32_e32 v12, 0.5, v12
            v_add_f32_e32 v13, 0.5, v13
            v_mul_f32_e32 v12, 0x3e800000, v12
            v_mul_f32_e32 v13, 0x3e800000, v13
            v_add_f32_e32 v14, 1.0, v12
            v_mov_b32_e32 v15, v13
            s_mov_b32 s8, 0x92
            s_mov_b32 s9, 0xc00000
            s_mov_b32 s10, 0x14000000
            s_mov_b32 s11, 0
            image_sample_lz v[20:23], v[14:15], s[0:7], s[8:11] dmask:0xf dim:SQ_RSRC_IMG_2D
            s_mov_b32 s12, 0x3092
            s_mov_b32 s13, 0xc00000
            s_mov_b32 s14, 0x14000000
            s_mov_b32 s15, 0
            v_mov_b32_e32 v16, 0.5
            v_mov_b32_e32 v17, v12
            v_mov_b32_e32 v18, v13
            image_sample_c_lz v24, v[16:18], s[16:23], s[12:15] dmask:0x1 dim:SQ_RSRC_IMG_2D
            s_waitcnt vmcnt(0)
            """;
        var bindings = new Bindings
        {
            UserSgprs = [2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 3],
            Textures =
            [
                new TextureResource(2, Format.R32G32B32A32_Float, 4, 4, FloatTexture()),
                new TextureResource(3, Format.R32_Float, 4, 4, [.. Enumerable.Range(0, 16).Select(index => F((index + 0.5) / 16))]),
            ],
            Samplers = [LiteralSampler([0x92, 0xc00000, 0x14000000, 0], comparison: false), LiteralSampler([0x3092, 0xc00000, 0x14000000, 0], comparison: true)],
        };

        // The clamped sample reads column 3; the compare of 0.5 passes from texel 8 on (less or equal).
        return new PixelCase("literal_samplers", Program, 5, pixel => [.. Texel(3, pixel.Y & 3), Lit((pixel.X & 3) + (4 * (pixel.Y & 3)))])
        {
            Bindings = bindings,
        };
    }

    // A literal S# built before a branch and used after it, its filter word by s_brev_b32 (40 reversed is 0x14000000):
    // the sample still reads the sampler its words describe (wrap in x, point: one texture width to the right is the
    // same column), not the sampler heap slot its first dword numbers (0x90, a clamping sampler here: column 3).
    // PS_CompositeNMB samples its colour LUTs that way.
    private static PixelCase LiteralSamplerAcrossBlocks()
    {
        const string Program = """
            v_cvt_u32_f32_e32 v10, v2
            v_cvt_u32_f32_e32 v11, v3
            v_and_b32_e32 v10, 3, v10
            v_and_b32_e32 v11, 3, v11
            v_cvt_f32_u32_e32 v12, v10
            v_cvt_f32_u32_e32 v13, v11
            v_add_f32_e32 v12, 0.5, v12
            v_add_f32_e32 v13, 0.5, v13
            v_mul_f32_e32 v12, 0x3e800000, v12
            v_mul_f32_e32 v13, 0x3e800000, v13
            v_add_f32_e32 v14, 1.0, v12
            v_mov_b32_e32 v15, v13
            s_mov_b32 s8, 0x90
            s_mov_b32 s9, 0xc00000
            s_brev_b32 s10, 40
            s_mov_b32 s11, 0
            s_cmp_eq_u32 s9, 0
            s_cbranch_scc1 .Lskip
            image_sample_lz v[20:23], v[14:15], s[0:7], s[8:11] dmask:0xf dim:SQ_RSRC_IMG_2D
            .Lskip:
            s_waitcnt vmcnt(0)
            """;
        var bindings = new Bindings
        {
            UserSgprs = [2, 0, 0, 0, 0, 0, 0, 0],
            Textures = [new TextureResource(2, Format.R32G32B32A32_Float, 4, 4, FloatTexture())],
            Samplers = [LiteralSampler([0x90, 0xc00000, 0x14000000, 0], comparison: false)],
        };

        return new PixelCase("literal_sampler_across_blocks", Program, 4, pixel => [.. Texel(pixel.X & 3, pixel.Y & 3)])
        {
            Bindings = bindings,
        };
    }

    // Texel offsets past D3D's -8..7 (RDNA reaches -32..31) move into the coordinates: +9 on x and -10 on y, clamped to
    // the edge, read column 3 and row 0. The unorm modifier takes texel coordinates (x + 0.5, y + 0.5).
    private static PixelCase OffsetsAndTexelCoordinates()
    {
        const string Program = """
            v_cvt_u32_f32_e32 v10, v2
            v_cvt_u32_f32_e32 v11, v3
            v_and_b32_e32 v10, 3, v10
            v_and_b32_e32 v11, 3, v11
            v_cvt_f32_u32_e32 v12, v10
            v_cvt_f32_u32_e32 v13, v11
            v_add_f32_e32 v12, 0.5, v12
            v_add_f32_e32 v13, 0.5, v13
            v_mov_b32_e32 v18, v12
            v_mov_b32_e32 v19, v13
            v_mul_f32_e32 v12, 0x3e800000, v12
            v_mul_f32_e32 v13, 0x3e800000, v13
            s_mov_b32 s8, 0x92
            s_mov_b32 s9, 0xc00000
            s_mov_b32 s10, 0x14000000
            s_mov_b32 s11, 0
            v_mov_b32_e32 v14, 9
            v_mov_b32_e32 v15, v12
            v_mov_b32_e32 v16, v13
            image_sample_lz_o v[20:23], v[14:16], s[0:7], s[8:11] dmask:0xf dim:SQ_RSRC_IMG_2D
            v_mov_b32_e32 v14, 0x3600
            image_sample_lz_o v[24:27], v[14:16], s[0:7], s[8:11] dmask:0xf dim:SQ_RSRC_IMG_2D
            image_sample_lz v[28:31], v[18:19], s[0:7], s[8:11] dmask:0xf dim:SQ_RSRC_IMG_2D unorm
            s_waitcnt vmcnt(0)
            """;
        var bindings = new Bindings
        {
            UserSgprs = [2, 0, 0, 0, 0, 0, 0, 0],
            Textures = [new TextureResource(2, Format.R32G32B32A32_Float, 4, 4, FloatTexture())],
            Samplers = [LiteralSampler([0x92, 0xc00000, 0x14000000, 0], comparison: false)],
        };

        return new PixelCase("image_offsets_texel_coordinates", Program, 12, pixel => [.. Texel(3, pixel.Y & 3), .. Texel(pixel.X & 3, 0), .. Texel(pixel.X & 3, pixel.Y & 3)])
        {
            Bindings = bindings,
        };
    }

    private static uint Lit(int texel)
    {
        if (texel >= 8)
        {
            return F(1);
        }

        return F(0);
    }

    /// <summary>The desktop sampler a literal S# gets, at its slot (StaticSampler, DesktopSampler).</summary>
    internal static SamplerResource LiteralSampler(uint[] words, bool comparison)
    {
        var description = System.Runtime.InteropServices.MemoryMarshal.Cast<uint, Vortice.Direct3D12.SamplerDescription>(Translation.DesktopSampler.Describe(words, comparison))[0];

        return new SamplerResource(Translation.StaticSampler.SlotOf(words, comparison), Linear: false) { Description = description };
    }

    // image_load from an integer-format texture returns the raw integer bits (the known typed-view question).
    private static PixelCase ImageLoadInteger()
    {
        const string Program = """
            v_cvt_u32_f32_e32 v10, v2
            v_cvt_u32_f32_e32 v11, v3
            v_and_b32_e32 v10, 3, v10
            v_and_b32_e32 v11, 3, v11
            image_load v[20:23], v[10:11], s[0:7] dmask:0xf dim:SQ_RSRC_IMG_2D
            s_waitcnt vmcnt(0)
            """;
        uint[] texels = [.. Enumerable.Range(0, 64).Select(index => 0x80000000 + (0x101 * (uint)index))];
        var bindings = new Bindings
        {
            UserSgprs = [2, 0, 0, 0, 0, 0, 0, 0],
            Textures = [new TextureResource(2, Format.R32G32B32A32_UInt, 4, 4, texels)],
        };

        return new PixelCase("image_load_uint_format", Program, 4, pixel =>
        {
            var start = (((pixel.Y & 3) * 4) + (pixel.X & 3)) * 4;

            return [.. texels.Skip(start).Take(4).Select(Expected.Exact)];
        })
        {
            Bindings = bindings,
        };
    }

    // Bilinear sample midway between texels 1 and 2.
    private static PixelCase ImageSampleLinear()
    {
        const string Program = """
            v_cvt_u32_f32_e32 v11, v3
            v_and_b32_e32 v11, 3, v11
            v_cvt_f32_u32_e32 v13, v11
            v_add_f32_e32 v13, 0.5, v13
            v_mul_f32_e32 v13, 0x3e800000, v13
            v_mov_b32 v12, 0.5
            image_sample_lz v[20:23], v[12:13], s[0:7], s[8:11] dmask:0xf dim:SQ_RSRC_IMG_2D
            s_waitcnt vmcnt(0)
            """;
        var bindings = new Bindings
        {
            UserSgprs = [2, 0, 0, 0, 0, 0, 0, 0, 3],
            Textures = [new TextureResource(2, Format.R32G32B32A32_Float, 4, 4, FloatTexture())],
            Samplers = [new SamplerResource(3, Linear: true)],
        };

        return new PixelCase("image_sample_linear", Program, 4, pixel =>
        {
            var (left, right) = (Texel(1, pixel.Y & 3), Texel(2, pixel.Y & 3));

            return [.. Enumerable.Range(0, 4).Select(index => Expected.Near((FloatBits.Value(left[index]) + FloatBits.Value(right[index])) / 2, 1e-3))];
        })
        {
            Bindings = bindings,
        };
    }

    // image_gather4_l at level 1 of an 8x8 R32_FLOAT texture (level 0 holds 1000 + i + 8j, level 1 i + 4j), plain and with
    // the texel offset (-1, +1): the gather takes the level's 2x2 block, clamped at the edge. Gathering level 0 instead
    // culled most of the world against the wrong depths in victory-gta6's meshlet culling.
    private static PixelCase ImageGatherAtLevel()
    {
        const string Program = """
            v_cvt_u32_f32_e32 v10, v2
            v_cvt_u32_f32_e32 v11, v3
            v_and_b32_e32 v10, 3, v10
            v_and_b32_e32 v11, 3, v11
            v_add_nc_u32_e32 v12, 1, v10
            v_add_nc_u32_e32 v13, 1, v11
            v_cvt_f32_u32_e32 v12, v12
            v_cvt_f32_u32_e32 v13, v13
            v_mul_f32_e32 v12, 0x3e800000, v12
            v_mul_f32_e32 v13, 0x3e800000, v13
            v_mov_b32 v14, 1.0
            image_gather4_l v[20:23], v[12:14], s[0:7], s[8:11] dmask:0x1 dim:SQ_RSRC_IMG_2D
            v_mov_b32 v16, 0x13f
            v_mov_b32 v17, v12
            v_mov_b32 v18, v13
            v_mov_b32 v19, 1.0
            image_gather4_l_o v[24:27], v[16:19], s[0:7], s[8:11] dmask:0x1 dim:SQ_RSRC_IMG_2D
            s_waitcnt vmcnt(0)
            """;
        uint[] texels = [.. Enumerable.Range(0, 64).Select(index => F(1000 + index)), .. Enumerable.Range(0, 16).Select(index => F(index))];
        var bindings = new Bindings
        {
            UserSgprs = [2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
            Textures = [new TextureResource(2, Format.R32_Float, 8, 8, texels) { Levels = 2 }],
            Samplers = [new SamplerResource(0, Linear: false)],
        };

        static Expected[] Block(int i0, int j0)
        {
            var (x0, y0, x1, y1) = (Math.Clamp(i0, 0, 3), Math.Clamp(j0, 0, 3), Math.Clamp(i0 + 1, 0, 3), Math.Clamp(j0 + 1, 0, 3));

            return [F(x0 + (4 * y1)), F(x1 + (4 * y1)), F(x1 + (4 * y0)), F(x0 + (4 * y0))];
        }

        return new PixelCase("image_gather4_l", Program, 8, pixel =>
        {
            var (i, j) = (pixel.X & 3, pixel.Y & 3);

            return [.. Block(i, j), .. Block(i - 1, j + 1)];
        })
        {
            Bindings = bindings,
        };
    }

    // No signature: float SV_Target into R32G32B32A32_FLOAT.
    private static PixelCase FloatExport()
    {
        const string Program = """
            v_cvt_u32_f32_e32 v10, v2
            v_mov_b32 v20, s0
            v_mov_b32 v21, s1
            v_cvt_f32_u32_e32 v22, v10
            v_mov_b32 v23, s2
            """;

        return new PixelCase("export_float_rt", Program, 4, pixel => [F(1.5), 0x3EAAAAAB, F(pixel.X), F(-0.0)])
        {
            Bindings = new Bindings { UserSgprs = [F(1.5), 0x3EAAAAAB, F(-0.0)] },
            FloatTargets = true,
        };
    }

    // A compressed export: v0 packs red and green, v1 blue and alpha (printed "v0, v0, v1, v1").
    private static PixelCase CompressedExport()
    {
        const string Program = """
            v_cvt_pkrtz_f16_f32_e64 v0, s0, s1
            v_cvt_pkrtz_f16_f32_e64 v1, s2, s3
            exp mrt0 v0, v0, v1, v1 done compr vm
            s_endpgm
            """;

        return new PixelCase("export_compressed", Program, 4, _ => [F(1.5), F(0.25), F(-2), F(0.5)])
        {
            Bindings = new Bindings { UserSgprs = [F(1.5), F(0.25), F(-2), F(0.5)] },
            FloatTargets = true,
            Raw = true,
        };
    }

    /// <summary>
    /// The export as TL_PS_PBR_MATERIAL_STANDARD stores render target 0's: two nops, the FP16 pairs and a compressed
    /// export (its FP16 form), over which <see cref="PixelCase.ExportPatch"/> goes.
    /// </summary>
    private const string StoredFp16Export = """
        v_mov_b32 v14, s0
        v_mov_b32 v11, s1
        v_mov_b32 v16, s2
        v_mov_b32 v9, s3
        s_nop 0
        s_nop 0
        v_cvt_pkrtz_f16_f32_e32 v2, v14, v11
        v_cvt_pkrtz_f16_f32_e32 v3, v16, v9
        exp mrt0 v2, v2, v3, v3 done compr vm
        s_endpgm
        """;

    // The 32_ABGR form (TL_PS's own: four nops, exp mrt0 v14, v11, v16, v9) into an RGBA32F target: the values arrive
    // unconverted (1/3, 1e-8, 70000 and -0.0, which the FP16 pairs round, flush and clamp to 65504).
    private static PixelCase PatchedFloatExport()
    {
        uint[] values = [0x3EAAAAAB, F(1e-8), F(70000), F(-0.0)];

        return new PixelCase("export_patch_abgr32", StoredFp16Export, 4, _ => [.. values.Select(Expected.Exact)])
        {
            Bindings = new Bindings { UserSgprs = values },
            FloatTargets = true,
            Raw = true,
            ExportPatch = [0xBF800000, 0xBF800000, 0xBF800000, 0xBF800000, 0xF800180F, 0x09100B0E],
        };
    }

    // A 32_R form (exp mrt0 v14, off, off, off: TL_PS's light target) into a uint target: red's bits as they are, the
    // other channels not written.
    private static PixelCase PatchedRedExport()
    {
        uint[] values = [0x12345678, 0xDEADBEEF, 7, 0x80000000];

        return new PixelCase("export_patch_r32", StoredFp16Export, 4, _ => [0x12345678, 0, 0, 0])
        {
            Bindings = new Bindings { UserSgprs = values },
            Raw = true,
            ExportPatch = [0xBF800000, 0xBF800000, 0xBF800000, 0xBF800000, 0xF8001801, 0x0000000E],
        };
    }

    // An implicit-LOD sample of shared-exponent light (typed image views, class 3) in a 16x16 R32_UINT texture with an
    // 8x8 level 1: one pixel per two texels takes LOD 1, and the point sampler reads level 1's texel (x, y) decoded.
    private static PixelCase SharedExponentImplicitSample()
    {
        const string Program = """
            v_mul_f32_e32 v12, 0x3e000000, v2
            v_mul_f32_e32 v13, 0x3e000000, v3
            s_mov_b32 s20, 0x92
            s_mov_b32 s21, 0xc00000
            s_mov_b32 s22, 0x4000000
            s_mov_b32 s23, 0
            image_sample v[20:22], v[12:13], s[0:7], s[20:23] dmask:0x7 dim:SQ_RSRC_IMG_2D
            s_waitcnt vmcnt(0)
            """;
        const int Base = 16;
        const int Level1 = 8;
        uint[] texels = [.. Enumerable.Range(0, (Base * Base) + (Level1 * Level1)).Select(index => (uint)((index * 29) & 511) | ((uint)((index * 13) & 511) << 9) | ((uint)((index * 7) & 511) << 18) | (14u << 27))];

        return new PixelCase("image_sample_rgb9e5_implicit", Program, 3, pixel =>
        {
            uint bits = texels[(Base * Base) + (pixel.Y * Level1) + pixel.X];
            float scale = MathF.Pow(2, (int)(bits >> 27) - 24);

            return [.. Enumerable.Range(0, 3).Select(channel => Expected.Exact(BitConverter.SingleToUInt32Bits(((bits >> (9 * channel)) & 511) * scale)))];
        })
        {
            Bindings = new Bindings
            {
                UserSgprs = [2, 0, 0, 3],
                Textures = [new TextureResource(2, Format.R32_UInt, Base, Base, texels) { Levels = 2 }],
            },
            FloatTargets = true,
            TypedViews = true,
        };
    }

    // A gather of shared-exponent light (class 3) in a 2x2 R32_UINT texture at its centre: the four texels decoded, the
    // green channel taken, in gather order (0, 1) (1, 1) (1, 0) (0, 0); before, the float declaration read the bits as red.
    private static PixelCase SharedExponentGather()
    {
        const string Program = """
            v_mov_b32_e32 v12, 0.5
            v_mov_b32_e32 v13, 0.5
            s_mov_b32 s20, 0x92
            s_mov_b32 s21, 0xc00000
            s_mov_b32 s22, 0x4000000
            s_mov_b32 s23, 0
            image_gather4 v[20:23], v[12:13], s[0:7], s[20:23] dmask:0x2 dim:SQ_RSRC_IMG_2D
            s_waitcnt vmcnt(0)
            """;
        uint[] texels = [.. Enumerable.Range(0, 4).Select(index => (uint)(index + 1) | ((uint)(index * 40 + 3) << 9) | (5u << 18) | (16u << 27))];

        float Green(int x, int y) => (((texels[(y * 2) + x] >> 9) & 511) * MathF.Pow(2, 16 - 24));

        return new PixelCase("image_gather_rgb9e5", Program, 4, _ => [.. new[] { Green(0, 1), Green(1, 1), Green(1, 0), Green(0, 0) }.Select(value => Expected.Exact(BitConverter.SingleToUInt32Bits(value)))])
        {
            Bindings = new Bindings
            {
                UserSgprs = [2, 0, 0, 3],
                Textures = [new TextureResource(2, Format.R32_UInt, 2, 2, texels)],
            },
            FloatTargets = true,
            TypedViews = true,
        };
    }

    // A point sample of shared-exponent light (class 3) in slice 1 of a 2x2x2 R32_UINT array: the texel decoded (before,
    // only 2D and 3D samples decoded and an array read the bits as red).
    private static PixelCase SharedExponentArraySample()
    {
        const string Program = """
            v_cvt_u32_f32_e32 v10, v2
            v_cvt_u32_f32_e32 v11, v3
            v_and_b32_e32 v10, 1, v10
            v_and_b32_e32 v11, 1, v11
            v_cvt_f32_u32_e32 v12, v10
            v_cvt_f32_u32_e32 v13, v11
            v_add_f32_e32 v12, 0.5, v12
            v_add_f32_e32 v13, 0.5, v13
            v_mul_f32_e32 v12, 0.5, v12
            v_mul_f32_e32 v13, 0.5, v13
            v_mov_b32_e32 v14, 1.0
            s_mov_b32 s20, 0x92
            s_mov_b32 s21, 0xc00000
            s_mov_b32 s22, 0x4000000
            s_mov_b32 s23, 0
            image_sample_lz v[20:22], v[12:14], s[0:7], s[20:23] dmask:0x7 dim:SQ_RSRC_IMG_2D_ARRAY
            s_waitcnt vmcnt(0)
            """;
        uint[] texels = [.. Enumerable.Range(0, 8).Select(index => (uint)(index * 3 + 1) | ((uint)(index * 5 + 2) << 9) | ((uint)(index * 7 + 4) << 18) | (18u << 27))];

        return new PixelCase("image_sample_rgb9e5_array", Program, 3, pixel =>
        {
            uint bits = texels[4 + ((pixel.Y & 1) * 2) + (pixel.X & 1)];
            float scale = MathF.Pow(2, (int)(bits >> 27) - 24);

            return [.. Enumerable.Range(0, 3).Select(channel => Expected.Exact(BitConverter.SingleToUInt32Bits(((bits >> (9 * channel)) & 511) * scale)))];
        })
        {
            Bindings = new Bindings
            {
                UserSgprs = [2, 0, 0, 3],
                Textures = [new TextureResource(2, Format.R32_UInt, 2, 2, texels) { Kind = TextureKind.Texture2DArray, Layers = 2 }],
            },
            FloatTargets = true,
            TypedViews = true,
        };
    }

    // A compressed export into a uint target: the pipeline exports it 32-bit (32_ABGR), so the target takes the values
    // the FP16 pairs were packed from, as bits (PS_VfxVBuffer's 32-bit fields).
    private static PixelCase CompressedUintExport()
    {
        const string Program = """
            v_cvt_pkrtz_f16_f32_e64 v0, s0, s1
            v_cvt_pkrtz_f16_f32_e64 v1, s2, s3
            exp mrt0 v0, v0, v1, v1 done compr vm
            s_endpgm
            """;
        uint[] fields = [0x12345678, 0xFF00ABCD, 300, 0xFFFFFFFC];

        return new PixelCase("export_compressed_uint", Program, 4, _ => [.. fields.Select(Expected.Exact)])
        {
            Bindings = new Bindings { UserSgprs = fields },
            Raw = true,
        };
    }
}
