// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Shaders.Containers;
using FiveStar.Shaders.Hardware;
using FiveStar.Shaders.Tests.Reference;
using FiveStar.Shaders.Tests.Toolchain;
using FiveStar.Shaders.Tests.Warp;
using FiveStar.Shaders.Translation;
using Xunit;

namespace FiveStar.Shaders.Tests;

/// <summary>
/// The prelude's emulated wave operations (vw_*) on WARP's 4-lane waves for program waves wider, as wide and narrower
/// (with pixel-style and mesh-style lane numbers): each thread must see its console wave (<see cref="ProgramWave"/>).
/// A translated program names the narrowest hardware wave with console results from what its reads reach; a mesh
/// program also from whether a narrower wave that skips a region sees the console's values after it (s40, written
/// there) and whether its first-lane reads are the console's (v1 differs per lane).
/// </summary>
public sealed class WaveEmulationTests : IClassFixture<WarpFixture>
{
    private const int Threads = 32;
    private const int Results = 8;
    private const int HeapSlot = 1;

    private readonly WarpFixture _fixture;

    public WaveEmulationTests(WarpFixture fixture)
    {
        _fixture = fixture;
    }

    [WarpTheory]
    [InlineData(2, false)]
    [InlineData(4, false)]
    [InlineData(16, false)]
    [InlineData(16, true)]
    [InlineData(64, true)]
    public void Wave_operations_see_the_console_wave(int wave, bool groupLanes)
    {
        var shader = _fixture.Workbench.Compile($"vw_{wave}_{groupLanes}", Program(wave, groupLanes), "cs");
        var output = new BufferResource(HeapSlot, new uint[Threads * Results], Writable: true);
        var words = _fixture.Runner.Compute(new ComputeJob(shader, 1, 1, 1, new Bindings { Buffers = [output] })).Buffers[HeapSlot];
        var model = new ProgramWave(wave, _fixture.ComputeWaveSize, groupLanes);
        var mismatches = new List<string>();

        for (var thread = 0; thread < Threads; thread++)
        {
            var expected = Expected(model, thread, wave);
            var actual = words.AsSpan(thread * Results, Results).ToArray();

            if (!expected.SequenceEqual(actual))
            {
                mismatches.Add($"thread {thread}: expected {string.Join(" ", expected)} actual {string.Join(" ", actual)}");
            }
        }

        Assert.True(mismatches.Count == 0, string.Join("\n", mismatches));
    }

    [WarpTheory]
    [InlineData("v_mov_b32_dpp v1, v0 quad_perm:[1,0,3,2] row_mask:0xf bank_mask:0xf", 4)]
    [InlineData("v_mov_b32_dpp v1, v0 row_shr:1 row_mask:0xf bank_mask:0xf", 16)]
    [InlineData("v_permlanex16_b32 v1, v0, s2, s3", 32)]
    [InlineData("v_readlane_b32 s4, v0, 5", 8)]
    [InlineData("v_readlane_b32 s4, v0, s2", 64)]
    [InlineData("s_ff1_i32_b64 s2, exec\nv_readlane_b32 s4, v0, s2", 4)]
    [InlineData("s_or_saveexec_b64 s[10:11], -1\nv_cndmask_b32_e64 v1, -1, v0, s[10:11]\nv_readlane_b32 s4, v1, 40\ns_mov_b64 exec, s[10:11]", 4)]
    [InlineData("s_or_saveexec_b64 s[10:11], -1\nv_readlane_b32 s4, v0, 40\ns_mov_b64 exec, s[10:11]", 64)]
    public void A_pixel_program_states_the_narrowest_wave_with_console_results(string code, int floor)
    {
        var assembled = _fixture.Workbench.Assemble($"floor_{floor}_{code.Length}", code + "\ns_endpgm", 64);
        var config = new ShaderConfig { Stage = HardwareStage.Pixel, WaveSize = 64 };
        var program = new ShaderProgram(ShaderStage.Pixel, assembled.Instructions, assembled.Code, ShaderSignatures.None, config, null);

        Assert.StartsWith($"// program wave 64; console results on hardware waves >= {floor} lanes\n", ShaderWorkbench.Translate(program), StringComparison.Ordinal);
    }

    // Lanes 0..17 export from a region that the 64-lane console wave runs and a narrower hardware wave above them skips.
    private const string SkippedRegion = """
        v_mbcnt_lo_u32_b32 v1, -1, 0
        v_mbcnt_hi_u32_b32 v1, -1, v1
        s_mov_b32 s40, 0
        v_mov_b32 v2, 0
        v_cmp_gt_u32_e32 vcc, 18, v1
        s_and_saveexec_b64 s[22:23], vcc
        s_cbranch_execz .Lskip
        s_mov_b32 s40, 1
        v_cvt_f32_u32_e32 v2, v1
        .Lskip:
        s_mov_b64 exec, s[22:23]
        """;

    [WarpTheory]
    [InlineData("v_mov_b32 v3, v2", 4, 4)]
    [InlineData("v_mov_b32 v3, s40", 64, 4)]
    [InlineData("v_readfirstlane_b32 s41, v1\nv_mov_b32 v3, s41", 64, 4)]
    [InlineData("v_mad_u64_u32 v[4:5], s[10:11], s2, s3, 0\nv_readfirstlane_b32 s41, v4\nv_mov_b32 v3, s41", 4, 4)]
    [InlineData("v_mad_u64_u32 v[4:5], s[10:11], v1, s3, 0\nv_readfirstlane_b32 s41, v4\nv_mov_b32 v3, s41", 64, 4)]
    [InlineData("v_lshlrev_b64 v[4:5], 2, s[2:3]\nv_readfirstlane_b32 s41, v5\nv_mov_b32 v3, s41", 4, 4)]
    public void A_mesh_program_states_whether_narrower_waves_give_console_results(string tail, int floor, int fastFloor)
    {
        var code = $"{SkippedRegion}\n{tail}\nexp pos0 v2, v3, v2, v2 done\ns_endpgm";
        var assembled = _fixture.Workbench.Assemble($"mesh_floor_{floor}_{tail.Length}", code, 64);
        var config = new ShaderConfig { Stage = HardwareStage.Ngg, WaveSize = 64, UserSgprs = 4, Ngg = new NggConfig(8, 0, 0) };
        var pipeline = new PipelineStateInfo { Stage = "ms", NumThreads = new ThreadGroupSize(128, 1, 1), MaxVertices = 128, MaxPrimitives = 128 };
        var program = new ShaderProgram(ShaderStage.Mesh, assembled.Instructions, assembled.Code, ShaderSignatures.None, config, pipeline);

        Assert.StartsWith($"// program wave 64; console results on hardware waves >= {floor} lanes\n", ShaderWorkbench.Translate(program), StringComparison.Ordinal);

        // Fast waves take the window's first lane for the console's, and every window runs the region that writes s40.
        var fast = HlslTranslator.Translate(program, new TranslationOptions(StructuredControlFlow: false, PinWaveSize: false, EmulateWaveSize: true) { FastWaves = true }).Hlsl;
        Assert.StartsWith($"// program wave 64; console results on hardware waves >= {fastFloor} lanes\n", fast, StringComparison.Ordinal);
        Assert.Equal(fastFloor != floor, fast.Contains(TranslationOptions.FastWavesComment, StringComparison.Ordinal));
        Assert.Equal(!tail.Contains("s40", StringComparison.Ordinal), fast.Contains("pc = (!vw_any(active))", StringComparison.Ordinal));
    }

    // The thread id's group bits (v5 from bit 7 up, the group size being 128) read first under an EXEC of the lanes below
    // the window's first lane's id: a window holds no active lane there, and its readfirstlane gives 0, not the group.
    private const string EmptyWindowRead = """
        v_mbcnt_lo_u32_b32 v1, -1, 0
        v_mbcnt_hi_u32_b32 v1, -1, v1
        v_mov_b32 v2, 0
        v_readfirstlane_b32 s41, v1
        v_cmp_gt_u32_e32 vcc, s41, v1
        s_and_saveexec_b64 s[22:23], vcc
        v_readfirstlane_b32 s42, v5
        s_and_b32 s42, s42, 0xffffff80
        s_mov_b64 exec, s[22:23]
        """;

    // The old value of an atomic: each lane's own.
    private const string AtomicRead = """
        v_mov_b32 v2, 0
        v_mov_b32 v6, 1
        buffer_atomic_add v6, v2, s[0:3], 0 offen glc
        s_waitcnt vmcnt(0)
        v_readfirstlane_b32 s42, v6
        """;

    // v_movreld under the EXEC of lanes 0..17 writes v7 only there (a switch on m0, guarded in each case).
    private const string RelativeWriteRead = """
        v_mbcnt_lo_u32_b32 v1, -1, 0
        v_mbcnt_hi_u32_b32 v1, -1, v1
        v_mov_b32 v2, 0
        v_mov_b32 v6, s2
        v_mov_b32 v7, 0
        v_cmp_gt_u32_e32 vcc, 18, v1
        s_and_saveexec_b64 s[22:23], vcc
        s_mov_b32 m0, 1
        v_movreld_b32 v7, v6
        s_mov_b64 exec, s[22:23]
        v_readfirstlane_b32 s42, v8
        """;

    [WarpTheory]
    [InlineData(EmptyWindowRead)]
    [InlineData(AtomicRead)]
    [InlineData(RelativeWriteRead)]
    public void A_first_lane_read_of_a_value_a_narrower_wave_may_see_otherwise_is_not_the_consoles(string code)
    {
        var assembled = _fixture.Workbench.Assemble($"mesh_read_{code.Length}", $"{code}\nv_mov_b32 v3, s42\nexp pos0 v2, v3, v2, v2 done\ns_endpgm", 64);
        var config = new ShaderConfig { Stage = HardwareStage.Ngg, WaveSize = 64, UserSgprs = 4, Ngg = new NggConfig(8, 0, 0) };
        var pipeline = new PipelineStateInfo { Stage = "ms", NumThreads = new ThreadGroupSize(128, 1, 1), MaxVertices = 128, MaxPrimitives = 128 };
        var program = new ShaderProgram(ShaderStage.Mesh, assembled.Instructions, assembled.Code, ShaderSignatures.None, config, pipeline);
        var hlsl = ShaderWorkbench.Translate(program);

        Assert.StartsWith("// program wave 64; console results on hardware waves >= 64 lanes\n", hlsl, StringComparison.Ordinal);
        Assert.DoesNotContain("s42 = v", hlsl, StringComparison.Ordinal);
    }

    private static string Program(int wave, bool groupLanes)
    {
        var group = groupLanes ? 1 : 0;

        return $$"""
            #define VW_WAVE {{wave}}
            #define VW_EMULATE 1
            #define VW_GROUP_LANES {{group}}
            #include "rdna_prelude.hlsli"
            [numthreads({{Threads}}, 1, 1)]
            void main(uint gi : SV_GroupIndex) {
            #if VW_GROUP_LANES
                g_vw_lane = gi & (VW_WAVE - 1u);
            #endif
                RWByteAddressBuffer o = ResourceDescriptorHeap[{{HeapSlot}}];
                uint x = gi * 7u + 3u;
                bool c = gi % 3u == 0u;
                uint2 m = vw_ballot(c);
                uint r[{{Results}}] = { vw_lane(), vw_read(x, vw_lane() ^ 1u), vw_read(x, VW_WAVE - 1u), m.x, m.y,
                    vw_any(gi == 5u) ? 1u : 0u, vw_count(c), readfirstlane(x, c) };
                for (uint i = 0; i < {{Results}}; i++) o.Store((gi * {{Results}}u + i) * 4u, r[i]);
            }
            """;
    }

    private static uint Value(int? holder)
    {
        if (holder is null)
        {
            return 0;
        }

        return (uint)((holder.Value * 7) + 3);
    }

    private static bool Condition(int thread)
    {
        return thread % 3 == 0;
    }

    private static uint[] Expected(ProgramWave model, int thread, int wave)
    {
        var lane = model.Lane(thread);
        var mask = model.Ballot(thread, Condition);
        var first = mask == 0 ? 0 : System.Numerics.BitOperations.TrailingZeroCount(mask);
        var any = model.Members(thread).Contains(5) ? 1u : 0u;

        return
        [
            (uint)lane, Value(model.Holder(thread, lane ^ 1)), Value(model.Holder(thread, wave - 1)), (uint)mask, (uint)(mask >> 32),
            any, (uint)System.Numerics.BitOperations.PopCount(mask), Value(model.Holder(thread, first)),
        ];
    }
}
