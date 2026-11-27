// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Shaders.Tests.Reference;

namespace FiveStar.Shaders.Tests.Cases;

/// <summary>
/// Compute cases for wave operations, each as a wave32 and a wave64 program. Expected values model the AMD wave size on
/// the PC wave's lanes; the full-wave cases need a PC wave as wide as the AMD one.
/// </summary>
public static class ComputeWaveCases
{
    // v12 = per-thread test value.
    private const string ValueProgram = """
        v_mul_u32_u24_e32 v12, 0x10001, v41
        v_add_nc_u32_e32 v12, 0x123, v12
        """;

    private const string LaneIdProgram = """
        v_mbcnt_lo_u32_b32 v24, -1, 0
        v_mbcnt_hi_u32_b32 v24, -1, v24
        """;

    private const string ReadLaneProgram = """
        v_readlane_b32 s20, v12, 2
        v_readlane_b32 s22, v12, s8
        v_readfirstlane_b32 s23, v12
        v_mov_b32 v20, s20
        v_mov_b32 v21, s22
        v_mov_b32 v22, s23
        v_mov_b32 v23, 0x55
        v_writelane_b32 v23, s20, 1
        v_mbcnt_lo_u32_b32 v24, -1, 0
        v_mbcnt_hi_u32_b32 v24, -1, v24
        """;

    private const string BackwardPermuteProgram = LaneIdProgram + """

        v_xor_b32_e32 v1, 1, v24
        v_lshlrev_b32_e32 v1, 2, v1
        ds_bpermute_b32 v20, v1, v12
        v_xor_b32_e32 v2, 2, v24
        v_lshlrev_b32_e32 v2, 2, v2
        v_add_nc_u32_e32 v2, {WRAP}, v2
        ds_bpermute_b32 v21, v2, v12
        ds_bpermute_b32 v22, v1, v12 offset:4
        v_mov_b32 v23, 0x77
        v_and_b32_e32 v3, 1, v24
        v_cmp_eq_u32_e32 {VCC}, 0, v3
        s_and_saveexec_{B} {SAVE}, {VCC}
        ds_bpermute_b32 v23, v1, v12
        s_waitcnt lgkmcnt(0)
        s_mov_{B} {EXEC}, {SAVE}
        """;

    private const string DppProgram = """
        v_mov_b32 v21, 0x99
        v_mov_b32 v22, 0x99
        v_mov_b32 v23, 0x99
        v_mov_b32 v24, 0x99
        v_mov_b32 v25, 0x99
        v_mov_b32 v26, 0x99
        v_mov_b32 v27, 0x99
        v_mov_b32 v28, 0x99
        v_mov_b32 v29, 0x99
        v_mov_b32_dpp v20, v12 quad_perm:[2,3,0,1] row_mask:0xf bank_mask:0xf
        v_mov_b32_dpp v21, v12 row_shr:1 row_mask:0xf bank_mask:0xf bound_ctrl:0
        v_mov_b32_dpp v22, v12 row_shl:1 row_mask:0xf bank_mask:0xf
        v_mov_b32_dpp v23, v12 row_ror:1 row_mask:0xf bank_mask:0xf
        v_mov_b32_dpp v24, v12 row_mirror row_mask:0xf bank_mask:0xf
        v_mov_b32_dpp v25, v12 row_half_mirror row_mask:0xf bank_mask:0xf
        v_mov_b32_dpp v26, v12 quad_perm:[1,0,3,2] row_mask:0xe bank_mask:0xf
        v_mov_b32_dpp v27, v12 quad_perm:[1,0,3,2] row_mask:0xf bank_mask:0x2
        v_mov_b32_dpp v28, v12 dpp8:[1,0,3,2,5,4,7,6]
        v_mov_b32 v29, 0x99
        v_mov_b32_dpp v29, v12 row_ror:4 row_mask:0xf bank_mask:0xf
        """;

    private const string WaterfallProgram = """
        s_mul_i32 s39, s12, 64
        v_add_nc_u32_e32 v41, s39, v0
        v_mul_u32_u24_e32 v5, 9, v41
        v_lshrrev_b32_e32 v5, 2, v5
        v_and_b32_e32 v5, 3, v5
        s_mov_{B} {SAVE2}, {EXEC}
        s_mov_b32 s16, 0
        v_mov_b32 v20, -1
        .Lloop:
        v_readfirstlane_b32 s10, v5
        v_cmp_eq_u32_e32 {VCC}, s10, v5
        s_and_saveexec_{B} {SAVE}, {VCC}
        v_mov_b32 v20, s10
        v_mov_b32 v21, s16
        s_bcnt1_i32_{B} s17, {EXEC}
        v_mov_b32 v22, s17
        s_add_u32 s16, s16, 1
        s_xor_{B} {EXEC}, {EXEC}, {SAVE}
        s_cbranch_execnz .Lloop
        s_mov_{B} {EXEC}, {SAVE2}
        v_mov_b32 v23, s16
        {STORE}
        """;

    private static readonly (string Mode, string Arguments)[] Swizzles =
    [
        ("QUAD_PERM", "3,2,1,0"), ("SWAP", "1"), ("SWAP", "2"), ("REVERSE", "4"), ("BROADCAST", "4,1"), ("BITMASK_PERM", "\"ppp1i\""), ("SWAP", "16"),
        ("REVERSE", "32"), ("BROADCAST", "32,5"), ("BITMASK_PERM", "\"0i1pp\""),
    ];

    private static readonly uint[] PermlaneSelect = [0x01234567, 0x89ABCDEF];

    /// <summary>Cases that run on any PC wave size.</summary>
    public static IEnumerable<ComputeCase> All()
    {
        return Variants("cs_readlane", ReadLaneCase)
            .Concat(Variants("cs_ds_swizzle", SwizzleCase))
            .Concat(Variants("cs_ds_bpermute", BackwardPermuteCase))
            .Concat(Variants("cs_ds_permute", ForwardPermuteCase))
            .Concat(Variants("cs_dpp", DppCase))
            .Concat(Variants("cs_waterfall", WaterfallCase))
            .Append(VccHighBranchCase())
            .Append(VccLoopExitCase())
            .Append(ExecLowHalfCase())
            .Append(MaskHighHalfPathCase())
            .Append(MoveLowHalfAfterMaskCase())
            .Append(LowDwordOperationsOnMaskCase())
            .Concat(Variants("cs_mask_on_taken_path", MaskOnTakenPathCase))
            .Concat(Variants("cs_integer_on_taken_path", IntegerOnTakenPathCase))
            .Concat(Variants("cs_mask_after_integer", MaskAfterIntegerCase))
            .Append(MergePathLoopCase())
            .Append(UniformFirstLaneCase());
    }

    /// <summary>The name of <see cref="UniformFirstLaneCase"/>.</summary>
    public const string UniformFirstLane = "cs_uniform_first_lane_w64";

    // A readfirstlane of a value every lane holds (s8 + 5 under a full EXEC) is that value, in the superstep form without
    // a stop (Waves.UniformWaveOperations); one of the same value written only by the even lanes still reads the first
    // active lane, since the odd lanes keep an older value.
    private static ComputeCase UniformFirstLaneCase()
    {
        const string Program = """
            v_mov_b32_e32 v12, s8
            v_add_nc_u32_e32 v12, 5, v12
            v_readfirstlane_b32 s23, v12
            v_mov_b32_e32 v13, v0
            v_and_b32_e32 v1, 1, v0
            v_cmp_eq_u32_e32 vcc, 0, v1
            s_and_saveexec_b64 s[30:31], vcc
            v_mov_b32_e32 v13, s23
            v_readfirstlane_b32 s24, v13
            s_mov_b64 exec, s[30:31]
            v_mov_b32_e32 v20, s23
            v_mov_b32_e32 v21, s24
            v_mov_b32_e32 v22, v13
            """;

        static uint Written(ComputeThread thread)
        {
            if ((thread.GroupIndex & 1) == 0)
            {
                return 12u;
            }

            return (uint)thread.GroupIndex;
        }

        return new ComputeCase(UniformFirstLane, Program, 3, thread => [12u, 12u, Written(thread)]) { Wave = 64, User = [7] };
    }

    // GpuParticleSort~CS_GpuSorting_Sort's merge-path search: a divergent loop whose lanes leave at different trips. The
    // back edge restores EXEC from the mask saved before a v_cmpx and the loop mask, a lane leaves through v_cmpx and
    // s_andn2_b64 of the loop mask, and the branch back reads the SCC of that s_andn2 (any lane left in either half).
    // Lane t of a wave runs (t & 7) + 1 trips, or (t & 7) + 33 in its upper half, so the high half leaves last.
    // vfx_triangleClip~CS_DefragHeap_ComputeRelocations' shape: a branch on "any active lane" over a loop whose exit
    // reads a lane value through readlane. With no lane of the console wave active the console skips the loop; run
    // with every lane inactive it reads no progress and never leaves (w98: the round bound every frame). Not run: only
    // its superstep form is read (Waves.UniformWaveOperations must not enter the loop always).
    public static ComputeCase LoopInSkippedPathCase()
    {
        const string Program = """
            v_mov_b32_e32 v5, 0
            v_cmp_gt_u32_e32 vcc, s8, v0
            s_and_saveexec_b64 s[30:31], vcc
            s_cbranch_execz .Lskip
            .Lloop:
            v_add_nc_u32_e32 v5, 1, v5
            v_readlane_b32 s3, v5, 63
            s_cmp_lt_u32 s3, 4
            s_cbranch_scc1 .Lloop
            .Lskip:
            s_mov_b64 exec, s[30:31]
            s_mov_b32 s3, 0
            v_mov_b32_e32 v20, v5
            """;

        return new ComputeCase("cs_loop_in_skipped_path_w64", Program, 1, null) { Wave = 64 };
    }

    private static ComputeCase MergePathLoopCase()
    {
        const string Program = """
            v_and_b32_e32 v1, 7, v0
            v_add_nc_u32_e32 v1, 1, v1
            v_and_b32_e32 v4, 63, v0
            v_cmp_le_u32_e64 s[56:57], 32, v4
            v_cndmask_b32_e64 v3, 0, 32, s[56:57]
            v_add_nc_u32_e32 v1, v1, v3
            v_mov_b32_e32 v2, 0
            s_mov_b64 s[54:55], exec
            s_mov_b64 s[52:53], exec
            s_mov_b64 s[50:51], exec
            .Lloop:
            s_and_b64 exec, s[50:51], s[52:53]
            v_add_nc_u32_e32 v2, 1, v2
            v_add_nc_u32_e32 v1, -1, v1
            s_mov_b64 s[50:51], exec
            v_cmpx_eq_u32_e32 0, v1
            s_andn2_b64 s[52:53], s[52:53], exec
            s_cbranch_scc1 .Lloop
            s_mov_b64 exec, s[54:55]
            v_mov_b32_e32 v20, v2
            """;

        static uint Trips(ComputeThread thread)
        {
            uint lane = (uint)thread.Id % 64;

            uint trips = (lane & 7) + 1;

            if (lane >= 32)
            {
                trips += 32;
            }

            return trips;
        }

        return new ComputeCase("cs_merge_path_loop", Program, 1, thread => [Trips(thread)]) { Wave = 64 };
    }

    // A loop's back-edge block (s_and exec, s[12:13], s[80:81]) reads the saveexec mask of the loop's end, but the
    // register's last write in program order is an integer load: the integer read must see the mask's bits. Here the
    // mask path is taken and the integer path is emitted last; every lane reads the mask of threads 0-3.
    private static ComputeCase MaskOnTakenPathCase(string name, int wave)
    {
        const string Program = """
            s_cmp_eq_u32 s8, 0
            s_cbranch_scc1 .Linteger
            v_cmp_gt_u32_e64 MASK40, 4, v0
            s_branch .Ljoin
            .Linteger:
            s_mov_b32 s40, 7
            .Ljoin:
            v_mov_b32 v20, s40
            """;

        static uint LowThreads(ComputeThread thread)
        {
            return (uint)Enumerable.Range(0, Math.Min(thread.WaveThreads.Length, 32))
                .Where(lane => thread.WaveThreads[lane] < 4).Sum(lane => 1L << lane);
        }

        return new ComputeCase(name, Pair40(Program, wave), 1, thread => [LowThreads(thread)]) { Wave = wave, User = [1] };
    }

    // The converse: an integer written on the path taken, a mask on the path emitted last, read as a mask.
    private static ComputeCase IntegerOnTakenPathCase(string name, int wave)
    {
        const string Program = """
            s_cmp_eq_u32 s8, 0
            s_cbranch_scc1 .Lmask
            s_mov_b32 s40, 6
            s_mov_b32 s41, 0
            s_branch .Ljoin
            .Lmask:
            v_cmp_gt_u32_e64 MASK40, 4, v0
            .Ljoin:
            v_cndmask_b32_e64 v20, 0, 1, MASK40
            """;

        return new ComputeCase(name, Pair40(Program, wave), 1, thread => [Convert.ToUInt32(thread.Lane is 1 or 2)]) { Wave = wave, User = [1] };
    }

    // A register read as an integer, then written as a mask on both paths and read as one where they meet: every read
    // sees one form on every path (MaskFormAnalysis), so the mask stays a bool with no ballot or lane_bit.
    private static ComputeCase MaskAfterIntegerCase(string name, int wave)
    {
        const string Program = """
            s_mov_b32 s40, 5
            v_mov_b32 v21, s40
            s_cmp_eq_u32 s8, 0
            s_cbranch_scc1 .Lother
            v_cmp_gt_u32_e64 MASK40, 4, v0
            s_branch .Ljoin
            .Lother:
            v_cmp_gt_u32_e64 MASK40, 2, v0
            .Ljoin:
            v_cndmask_b32_e64 v20, 0, 1, MASK40
            """;

        return new ComputeCase(name, Pair40(Program, wave), 2, thread => [Convert.ToUInt32(thread.GroupIndex < 4), 5]) { Wave = wave, User = [1] };
    }

    private static string Pair40(string program, int wave)
    {
        var pair = "s40";

        if (wave == 64)
        {
            pair = "s[40:41]";
        }

        return program.Replace("MASK40", pair, StringComparison.Ordinal);
    }

    // Wave64 s_cbranch_vccnz / vccz on VCC written as an integer test all 64 bits: only vcc_hi is set, then neither half.
    private static ComputeCase VccHighBranchCase()
    {
        const string Program = """
            s_mov_b32 vcc_lo, 0
            s_mov_b32 vcc_hi, 1
            v_mov_b32 v20, 1
            s_cbranch_vccnz .Ltaken
            v_mov_b32 v20, 2
            .Ltaken:
            s_mov_b32 vcc_hi, 0
            v_mov_b32 v21, 3
            s_cbranch_vccz .Lzero
            v_mov_b32 v21, 4
            .Lzero:
            """;

        return new ComputeCase("cs_vcc_hi_branch_w64", Program, 2, _ => [1, 3]) { Wave = 64 };
    }

    // Wave64: VCC is an integer on one path into the join and a lane mask on the other, so its branches read the integer
    // halves. The loop's exit must see the compare of its own block: with that ballot dropped the loop ran to the cap.
    private static ComputeCase VccLoopExitCase()
    {
        const string Program = """
            v_mov_b32 v20, 0
            s_mov_b32 s20, 0
            s_cmp_eq_u32 s8, 0
            s_cbranch_scc1 .Linteger
            v_cmp_eq_u32_e32 vcc, 1, v20
            s_branch .Ljoin
            .Linteger:
            s_mov_b32 vcc_lo, 0
            s_mov_b32 vcc_hi, 0
            .Ljoin:
            s_cbranch_vccnz .Lend
            .Lloop:
            s_add_u32 s20, s20, 1
            v_mov_b32 v21, s20
            v_cmp_eq_u32_e32 vcc, 3, v21
            s_cbranch_vccz .Lloop
            v_mov_b32 v20, s20
            .Lend:
            """;

        return new ComputeCase("cs_vcc_loop_exit_w64", Program, 1, _ => [3u]) { Wave = 64, User = [0] };
    }

    // Wave64 s_mov_b32 exec_lo with a constant writes only the low half of EXEC: lanes 0-15 are off, 16-63 stay on.
    private static ComputeCase ExecLowHalfCase()
    {
        const string Program = """
            v_mov_b32 v20, 0
            s_mov_b32 exec_lo, 0xffff0000
            v_mov_b32 v20, 1
            s_mov_b64 exec, -1
            """;

        return new ComputeCase("cs_exec_lo_half_w64", Program, 1, thread => [LowHalfOff(thread.Lane)]) { Wave = 64 };
    }

    // Wave64: s43 holds an integer on the path taken; a v_cmp into s[42:43] on the path not taken must not turn the
    // later integer read of s43 into the high half of that mask.
    private static ComputeCase MaskHighHalfPathCase()
    {
        const string Program = """
            s_mov_b32 s43, 5
            s_cmp_eq_u32 s8, 0
            s_cbranch_scc1 .Lskip
            v_cmp_eq_u32_e64 s[42:43], v0, v0
            .Lskip:
            v_mov_b32 v20, s43
            """;

        return new ComputeCase("cs_mask_high_half_path_w64", Program, 1, _ => [5u]) { Wave = 64, User = [0] };
    }

    // Wave64: s40 is a lane mask in emission order where the loop head moves it with s_mov_b32, but the head is only
    // reached from the back edge, where s40 is a count. The move is of one dword: s43 gets the count and s44, the dword
    // after it, keeps its value (cascadeshadows_classify~CS_WriteRasterClassificationResults keeps the low dword of its
    // output V# base there; as a mask move it took the high lanes' bits and the lists went to the start of the heap).
    private static ComputeCase MoveLowHalfAfterMaskCase()
    {
        const string Program = """
            s_mov_b32 s44, 7
            v_cmp_gt_u32_e64 s[40:41], 4, v0
            s_mov_b32 s42, 0
            s_mov_b32 s43, 0
            s_branch .Lbody
            .Lhead:
            s_mov_b32 s43, s40
            .Lbody:
            s_add_i32 s40, s42, 10
            s_add_i32 s42, s42, 1
            s_cmp_gt_u32 s42, 2
            s_cbranch_scc0 .Lhead
            v_mov_b32 v20, s43
            v_mov_b32 v21, s44
            """;

        return new ComputeCase("cs_mov32_after_mask_w64", Program, 2, _ => [11u, 7u]) { Wave = 64 };
    }

    // Wave64: s_and_b32, s_cselect_b32 and s_bcnt1_i32_b32 on the low dword of a lane mask are integer operations of
    // that dword: the constant is a bit pattern, the count stops at 32 lanes, and the register after the destination
    // (s44, s51) keeps its value.
    private static ComputeCase LowDwordOperationsOnMaskCase()
    {
        const string Program = """
            s_mov_b32 s44, 7
            s_mov_b32 s51, 9
            v_cmp_eq_u32_e64 s[40:41], v0, v0
            v_cmp_ne_u32_e64 s[46:47], v0, v0
            s_and_b32 s43, s40, 6
            v_mov_b32 v20, s43
            v_mov_b32 v21, s44
            s_cmp_eq_u32 s51, 9
            s_cselect_b32 s50, s46, s40
            v_mov_b32 v22, s50
            v_mov_b32 v23, s51
            s_bcnt1_i32_b32 s52, s40
            v_mov_b32 v24, s52
            """;

        return new ComputeCase("cs_low_dword_ops_on_mask_w64", Program, 5, thread =>
        {
            int lanes = Math.Min(thread.WaveSize, 32);
            uint low = lanes == 32 ? uint.MaxValue : (1u << lanes) - 1;

            return [low & 6, 7u, 0u, 9u, (uint)lanes];
        })
        {
            Wave = 64,
        };
    }

    private static uint LowHalfOff(int lane)
    {
        if (lane < 16)
        {
            return 0;
        }

        return 1;
    }

    /// <summary>Cases that need the PC wave to be as wide as the AMD wave.</summary>
    public static IEnumerable<ComputeCase> FullWave()
    {
        return Variants("cs_permlane", PermlaneCase).Concat(Variants("cs_wave_strict", StrictCase));
    }

    private static IEnumerable<ComputeCase> Variants(string name, Func<string, int, ComputeCase> build)
    {
        return [build(name + "_w32", 32), build(name + "_w64", 64)];
    }

    private static uint Value(int thread) => unchecked((uint)((thread * 0x10001) + 0x123));

    private static uint[] Values(ComputeThread thread) => [.. thread.WaveThreads.Select(Value)];

    /// <summary>The wave32 or wave64 spelling of masks, EXEC, VCC and the bpermute wrap.</summary>
    private static string ForWave(string program, int wave)
    {
        var (vcc, size, save, saveAgain, exec, wrap) = ("vcc_lo", "b32", "s30", "s34", "exec_lo", "0x80");

        if (wave == 64)
        {
            (vcc, size, save, saveAgain, exec, wrap) = ("vcc", "b64", "s[30:31]", "s[34:35]", "exec", "0x100");
        }

        return program.Replace("{VCC}", vcc, StringComparison.Ordinal)
            .Replace("{B}", size, StringComparison.Ordinal)
            .Replace("{SAVE2}", saveAgain, StringComparison.Ordinal)
            .Replace("{SAVE}", save, StringComparison.Ordinal)
            .Replace("{EXEC}", exec, StringComparison.Ordinal)
            .Replace("{WRAP}", wrap, StringComparison.Ordinal);
    }

    // v_readlane (constant / SGPR lane), readfirstlane, writelane, mbcnt, a v_cmp mask read as an integer (lo / hi, via
    // vcc_lo / vcc_hi and via s10 / s11 of v_cmp_e64 s[10:11]; user SGPR s11 preloaded with 0x5eed5eed), s_bcnt1(exec).
    private static ComputeCase ReadLaneCase(string name, int wave)
    {
        const string Tail32 = """
            v_cmp_gt_u32_e32 vcc_lo, 3, v24
            v_mov_b32 v25, vcc_lo
            v_mov_b32 v26, 0
            s_bcnt1_i32_b32 s24, exec_lo
            v_mov_b32 v27, s24
            v_cmp_gt_u32_e64 s10, 3, v24
            v_mov_b32 v28, s10
            v_mov_b32 v29, 0
            """;
        const string Tail64 = """
            v_cmp_gt_u32_e32 vcc, 3, v24
            v_mov_b32 v25, vcc_lo
            v_mov_b32 v26, vcc_hi
            s_bcnt1_i32_b64 s24, exec
            v_mov_b32 v27, s24
            v_cmp_gt_u32_e64 s[10:11], 3, v24
            v_mov_b32 v28, s10
            v_mov_b32 v29, s11
            """;
        var tail = wave == 64 ? Tail64 : Tail32;

        return new ComputeCase(name, $"{ValueProgram}\n{ReadLaneProgram}\n{tail}", 10, thread => ReadLaneExpected(thread, wave))
        {
            Wave = wave, Groups = (2, 1, 1), User = [3, 0, 0, 0x5EED5EED],
        };
    }

    private static Expected[] ReadLaneExpected(ComputeThread thread, int wave)
    {
        var values = Values(thread);
        var (low, high) = CrossLane.Ballot([.. Enumerable.Range(0, thread.WaveSize).Select(lane => lane < 3)]);
        var highHalf = wave == 64 ? Expected.Exact(high) : Expected.Any;
        var written = thread.Lane == 1 ? Read(values, 2) : 0x55;

        return [Read(values, 2), Read(values, 3), Read(values, 0), written, thread.Lane, low, highHalf, thread.WaveSize, low, highHalf];
    }

    private static Expected Read(uint[] values, int lane)
    {
        if (lane < values.Length)
        {
            return values[lane];
        }

        return Expected.Any;
    }

    // ds_swizzle QUAD_PERM / SWAP / REVERSE / BROADCAST / BITMASK_PERM (groups of 32 lanes).
    private static ComputeCase SwizzleCase(string name, int wave)
    {
        var lines = Swizzles.Select((swizzle, index) => $"ds_swizzle_b32 v{20 + index}, v12 offset:swizzle({swizzle.Mode},{swizzle.Arguments})");
        var program = $"{ValueProgram}\n{string.Join("\n", lines)}\ns_waitcnt lgkmcnt(0)";

        return new ComputeCase(name, program, Swizzles.Length, thread =>
        {
            var values = Values(thread);

            return [.. Swizzles.Select(swizzle => Read(values, SwizzleSource(thread.Lane, swizzle.Mode, swizzle.Arguments)))];
        })
        {
            Wave = wave, Groups = (2, 1, 1),
        };
    }

    private static int SwizzleSource(int lane, string mode, string arguments)
    {
        int[] numbers = mode == "BITMASK_PERM" ? [] : arguments.Split(',').Select(int.Parse).ToArray();

        switch (mode)
        {
            case "QUAD_PERM":
                return CrossLane.QuadPermute(lane, numbers);
            case "SWAP":
                return CrossLane.Swap(lane, numbers[0]);
            case "REVERSE":
                return CrossLane.ReverseGroup(lane, numbers[0]);
            case "BROADCAST":
                return CrossLane.Broadcast(lane, numbers[0], numbers[1]);
            default:
                return CrossLane.BitmaskPermute(lane, arguments.Trim('"'));
        }
    }

    // ds_bpermute: lane = (addr / 4) % wave (wave32 wraps at 32), the offset field, an inactive source lane reads 0.
    private static ComputeCase BackwardPermuteCase(string name, int wave)
    {
        return new ComputeCase(name, $"{ValueProgram}\n{ForWave(BackwardPermuteProgram, wave)}", 4, thread =>
        {
            var values = Values(thread);
            var lanes = Enumerable.Range(0, thread.WaveSize).ToArray();
            var all = lanes.Select(_ => true).ToArray();
            var even = lanes.Select(lane => lane % 2 == 0).ToArray();

            var neighbour = lanes.Select(lane => (uint)((lane ^ 1) * 4)).ToArray();
            var wrapped = CrossLane.BackwardPermute(values, [.. lanes.Select(lane => (uint)(((lane ^ 2) * 4) + (4 * wave)))], all, wave);
            var shifted = CrossLane.BackwardPermute(values, [.. neighbour.Select(address => address + 4)], all, wave);
            var masked = thread.Lane % 2 == 0 ? Expected.Maybe(CrossLane.BackwardPermute(values, neighbour, even, wave)[thread.Lane]) : 0x77;

            return
            [
                Expected.Maybe(CrossLane.BackwardPermute(values, neighbour, all, wave)[thread.Lane]), Expected.Maybe(wrapped[thread.Lane]),
                Expected.Maybe(shifted[thread.Lane]), masked,
            ];
        })
        {
            Wave = wave, Groups = (2, 1, 1),
        };
    }

    // ds_permute_b32 (forward permute): lane (addr / 4) receives this lane's value.
    private static ComputeCase ForwardPermuteCase(string name, int wave)
    {
        const string Program = """
            v_xor_b32_e32 v1, 3, v24
            v_lshlrev_b32_e32 v1, 2, v1
            ds_permute_b32 v20, v1, v12
            s_waitcnt lgkmcnt(0)
            """;

        return new ComputeCase(name, $"{ValueProgram}\n{LaneIdProgram}\n{Program}", 1, thread =>
        {
            var lanes = Enumerable.Range(0, thread.WaveSize).ToArray();
            var result = CrossLane.ForwardPermute(Values(thread), [.. lanes.Select(lane => (uint)((lane ^ 3) * 4))], [.. lanes.Select(_ => true)], wave);

            return [Expected.Maybe(result[thread.Lane])];
        })
        {
            Wave = wave,
        };
    }

    // DPP16 quad_perm / row_shr (bound_ctrl) / row_shl (keeps old) / row_ror / row_mirror / row_half_mirror, row_mask /
    // bank_mask keep old, DPP8.
    private static ComputeCase DppCase(string name, int wave)
    {
        Dpp[] moves =
        [
            new() { QuadPattern = [2, 3, 0, 1] }, new() { RowControl = "row_shr", Shift = 1, BoundControl = true },
            new() { RowControl = "row_shl", Shift = 1 }, new() { RowControl = "row_ror", Shift = 1 }, new() { RowControl = "row_mirror" },
            new() { RowControl = "row_half_mirror" }, new() { QuadPattern = [1, 0, 3, 2], RowMask = 0xE },
            new() { QuadPattern = [1, 0, 3, 2], BankMask = 0x2 }, new() { Dpp8Pattern = [1, 0, 3, 2, 5, 4, 7, 6] },
            new() { RowControl = "row_ror", Shift = 4 },
        ];

        return new ComputeCase(name, $"{ValueProgram}\n{DppProgram}", 10, thread =>
        {
            var values = Values(thread);

            return [.. moves.Select(move => Expected.Maybe(move.Move(values, 0x99)[thread.Lane]))];
        })
        {
            Wave = wave, Groups = (2, 1, 1),
        };
    }

    // Waterfall loop: readfirstlane key, v_cmp + s_and_saveexec, s_bcnt1(exec), s_xor exec, s_cbranch_execnz.
    private static ComputeCase WaterfallCase(string name, int wave)
    {
        return new ComputeCase(name, ForWave(WaterfallProgram, wave), 4, WaterfallExpected) { Wave = wave, Groups = (2, 1, 1), Raw = true };
    }

    private static Expected[] WaterfallExpected(ComputeThread thread)
    {
        var keys = thread.WaveThreads.Select(id => ((id * 9) >> 2) & 3).ToArray();
        var remaining = Enumerable.Range(0, keys.Length).ToList();
        var results = new Dictionary<int, (int Key, int Iteration, int Count)>();
        var iteration = 0;

        while (remaining.Count > 0)
        {
            var key = keys[remaining[0]];
            var group = remaining.Where(lane => keys[lane] == key).ToList();

            foreach (var lane in group)
            {
                results[lane] = (key, iteration, group.Count);
            }

            remaining.RemoveAll(lane => keys[lane] == key);
            iteration++;
        }

        var own = results[thread.Lane];

        return [own.Key, own.Iteration, own.Count, iteration];
    }

    // v_permlane16 / v_permlanex16 (16-lane rows, nibble selects from s8 / s9).
    private static ComputeCase PermlaneCase(string name, int wave)
    {
        const string Program = """
            v_permlane16_b32 v20, v12, s8, s9
            v_permlanex16_b32 v21, v12, s8, s9
            """;

        return new ComputeCase(name, $"{ValueProgram}\n{Program}", 2, thread =>
        {
            var values = Values(thread);
            var same = CrossLane.Permlane16(values, PermlaneSelect[0], PermlaneSelect[1], crossRow: false);
            var cross = CrossLane.Permlane16(values, PermlaneSelect[0], PermlaneSelect[1], crossRow: true);

            return [Expected.Maybe(same[thread.Lane]), Expected.Maybe(cross[thread.Lane])];
        })
        {
            Wave = wave, User = PermlaneSelect, FullWave = true,
        };
    }

    // readlane of the last lane, a v_cmp mask as integers (hi half = lanes 32..63), an integer SGPR (pair) as a lane mask.
    private static ComputeCase StrictCase(string name, int wave)
    {
        var last = wave - 1;
        var compare = wave == 64 ? "s[10:11]" : "s10";
        var high = wave == 64 ? "v_mov_b32 v22, s11" : "v_mov_b32 v22, 0";
        var mask = wave == 64 ? "s_mov_b32 s14, 1\ns_mov_b32 s15, 2\nv_cndmask_b32_e64 v23, 0, 1, s[14:15]" : "s_mov_b32 s14, 0x80000001\nv_cndmask_b32_e64 v23, 0, 1, s14";
        var program = $"""
            {ValueProgram}
            v_readlane_b32 s20, v12, {last}
            v_mov_b32 v20, s20
            {LaneIdProgram}
            v_mul_u32_u24_e32 v1, 0xaaab, v24
            v_lshrrev_b32_e32 v1, 17, v1
            v_mul_u32_u24_e32 v1, 3, v1
            v_cmp_eq_u32_e64 {compare}, v1, v24
            v_mov_b32 v21, s10
            {high}
            {mask}
            """;
        var laneMask = wave == 64 ? 0x200000001UL : 0x80000001UL;

        return new ComputeCase(name, program, 5, thread =>
        {
            var (low, highBits) = CrossLane.Ballot([.. Enumerable.Range(0, thread.WaveSize).Select(lane => lane % 3 == 0)]);

            return [Values(thread)[thread.WaveSize - 1], low, highBits, (uint)((laneMask >> thread.Lane) & 1), thread.Lane];
        })
        {
            Wave = wave, FullWave = true,
        };
    }
}
