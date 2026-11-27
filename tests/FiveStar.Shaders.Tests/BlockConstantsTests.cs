// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Shaders.Containers;
using FiveStar.Shaders.Hardware;
using FiveStar.Shaders.Tests.Toolchain;
using FiveStar.Shaders.Translation;
using Xunit;

namespace FiveStar.Shaders.Tests;

/// <summary>
/// A literal S# is known by the constants in its four SGPRs, also when they were set in an earlier block
/// (Core.BlockConstants): only what every path into the sample's block agrees on counts.
/// </summary>
public sealed class BlockConstantsTests : IClassFixture<WarpFixture>
{
    private const string Sample = "image_sample_lz v[20:23], v[14:15], s[0:7], s[8:11] dmask:0xf dim:SQ_RSRC_IMG_2D";
    private const string Static = "SamplerDescriptorHeap[sampler_slot(STATIC_SAMPLER_BASE";
    private const string HeapIndex = "SamplerDescriptorHeap[sampler_slot(s8)]";

    // The S# set from its filter word down, as PS_CompositeNMB sets it, then a branch, then the sample.
    private const string AfterBranch = $"""
        s_mov_b32 s10, 0x14500000
        s_mov_b32 s9, 0xc00000
        s_movk_i32 s8, 0x92
        s_mov_b32 s11, 0
        s_cmp_eq_u32 s9, 0
        s_cbranch_scc1 .Lskip
        {Sample}
        .Lskip:
        s_waitcnt vmcnt(0)
        """;

    // The loop's back edge brings a heap sampler record's index in s8: the head cannot keep the literal.
    private const string LoopHead = $"""
        s_mov_b32 s8, 0x92
        s_mov_b32 s9, 0xc00000
        s_mov_b32 s10, 0x14500000
        s_mov_b32 s11, 0
        s_mov_b32 s20, 0
        .Lloop:
        {Sample}
        s_mov_b32 s8, s2
        s_add_u32 s20, s20, 1
        s_cmp_lt_u32 s20, 2
        s_cbranch_scc1 .Lloop
        s_waitcnt vmcnt(0)
        """;

    // Three of the four dwords are constants, the filter word comes from a user SGPR.
    private const string Partial = $"""
        s_mov_b32 s8, 0x92
        s_mov_b32 s9, 0xc00000
        s_mov_b32 s10, s2
        s_mov_b32 s11, 0
        s_cmp_eq_u32 s9, 0
        s_cbranch_scc1 .Lskip
        {Sample}
        .Lskip:
        s_waitcnt vmcnt(0)
        """;

    // One path into the join leaves another filter word.
    private const string Join = $"""
        s_mov_b32 s8, 0x92
        s_mov_b32 s9, 0xc00000
        s_mov_b32 s10, 0x14500000
        s_mov_b32 s11, 0
        s_cmp_eq_u32 s2, 0
        s_cbranch_scc1 .Ljoin
        s_mov_b32 s10, 0x14000000
        .Ljoin:
        {Sample}
        s_waitcnt vmcnt(0)
        """;

    private readonly WarpFixture _fixture;

    public BlockConstantsTests(WarpFixture fixture)
    {
        _fixture = fixture;
    }

    [WarpFact]
    public void A_literal_sampler_set_before_a_branch_is_one_after_it()
    {
        string hlsl = Translate("cs_literal_sampler_after_branch", AfterBranch);

        Assert.Contains(Static, hlsl, StringComparison.Ordinal);
        Assert.DoesNotContain(HeapIndex, hlsl, StringComparison.Ordinal);
    }

    [WarpTheory]
    [InlineData("cs_literal_sampler_loop_head", LoopHead)]
    [InlineData("cs_literal_sampler_partial", Partial)]
    [InlineData("cs_literal_sampler_join", Join)]
    public void What_some_path_changes_is_no_literal(string name, string source)
    {
        string hlsl = Translate(name, source);

        Assert.Contains(HeapIndex, hlsl, StringComparison.Ordinal);
        Assert.DoesNotContain(Static, hlsl, StringComparison.Ordinal);
    }

    private string Translate(string name, string source)
    {
        var assembled = _fixture.Workbench.Assemble(name, source, 64);
        var compute = new ComputeConfig { Rsrc2 = 0, ScratchEnable = false, GroupSizeEnable = false, ThreadIdComponentCount = 1, LdsBlocks = 0, NumThreads = new ThreadGroupSize(64, 1, 1), GroupIdSgprs = [] };
        var config = new ShaderConfig { Stage = HardwareStage.Compute, WaveSize = 64, UserSgprs = 4, Compute = compute };
        var program = new ShaderProgram(ShaderStage.Compute, assembled.Instructions, assembled.Code, ShaderSignatures.None, config, null);

        return ShaderWorkbench.Translate(program);
    }
}
