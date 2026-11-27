// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Host;
using FiveStar.Loader.Shims.D3D12X.Binding;
using FiveStar.Shaders.Disassembly;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>
/// The records a held pass's stand-in fills are those its programs store textures through (StoredRecords), not every
/// record they read: a program that reads the global table past another pass's outputs and stores through one record of
/// its own table lists that record alone.
/// </summary>
public sealed class StoredRecordsTests
{
    private const int InstructionBytes = 4;

    [Fact]
    public void A_store_lists_the_record_its_resource_was_loaded_from()
    {
        // The shape of the tiled sun-shadow traces: the global table in s4 read far (a tile buffer, depth), the pass's
        // table in s5 copied to s84 and its record 10 stored through.
        var stored = StoredRecords.Find(Program(
            "s_mov_b32 s84, s5",
            "s_mov_b32 s85, 0",
            "s_mov_b32 s5, 0",
            "s_load_dwordx4 s[8:11], s[4:5], 0x780",
            "s_load_dwordx8 s[52:59], s[4:5], 0x8c0",
            "image_load v[2:5], v[0:1], s[52:59] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm",
            "s_load_dwordx8 s[24:31], s[84:85], 0x140",
            "image_store v[2:5], v[0:1], s[24:31] dmask:0xf dim:SQ_RSRC_IMG_2D unorm"));

        Assert.Equal([(5, 10u)], stored);
    }

    [Fact]
    public void A_table_pointer_needs_no_copy_and_a_null_offset_is_record_zero()
    {
        var stored = StoredRecords.Find(Program(
            "s_mov_b32 s5, 0",
            "s_load_dwordx8 s[16:23], s[4:5], null",
            "image_store_mip v[2:5], v[0:2], s[16:23] dmask:0xf dim:SQ_RSRC_IMG_2D",
            "s_load_dwordx8 s[16:23], s[4:5], 0x7e0",
            "image_atomic_umax v2, v[0:1], s[16:23] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm"));

        Assert.Equal([(4, 0u), (4, 63u)], stored.OrderBy(record => record.Record));
    }

    [Fact]
    public void A_resource_whose_record_is_not_known_is_not_listed()
    {
        // A computed offset, a pointer without a zero high dword, a T# overwritten after its load, and a pointer that
        // is itself loaded: none names a record.
        var stored = StoredRecords.Find(Program(
            "s_mov_b32 s5, 0",
            "s_load_dwordx8 s[16:23], s[4:5], s24",
            "image_store v[2:5], v[0:1], s[16:23] dmask:0xf dim:SQ_RSRC_IMG_2D",
            "s_load_dwordx8 s[32:39], s[6:7], 0x40",
            "image_store v[2:5], v[0:1], s[32:39] dmask:0xf dim:SQ_RSRC_IMG_2D",
            "s_load_dwordx8 s[40:47], s[4:5], 0x60",
            "s_mov_b32 s40, s3",
            "image_store v[2:5], v[0:1], s[40:47] dmask:0xf dim:SQ_RSRC_IMG_2D",
            "s_load_dwordx2 s[4:5], s[4:5], 0x20",
            "s_mov_b32 s5, 0",
            "s_load_dwordx8 s[48:55], s[4:5], 0x80",
            "image_store v[2:5], v[0:1], s[48:55] dmask:0xf dim:SQ_RSRC_IMG_2D"));

        Assert.Empty(stored);
    }

    [Fact]
    public void The_whole_reach_is_an_option()
    {
        Assert.False(new CommandLineParser([]).Parse().RtStandInsReach);
        Assert.True(new CommandLineParser(["--rt-stand-ins-reach"]).Parse().RtStandInsReach);
    }

    private static List<Instruction> Program(params string[] lines)
    {
        return [.. lines.Select((line, index) => new Instruction(index * InstructionBytes, InstructionBytes, line))];
    }
}
