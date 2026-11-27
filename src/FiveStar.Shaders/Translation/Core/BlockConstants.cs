// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Shaders.Disassembly;

namespace FiveStar.Shaders.Translation.Core;

/// <summary>
/// The SGPRs that hold the same constant at the start of a block on every path into it. A program builds a literal S#
/// in four SGPRs with s_mov_b32 and s_brev_b32 and may sample with it in a later block (PS_CompositeNMB sets
/// s[28:31] before a branch and reads its colour LUTs inside it); the translator's constants ended at each block, so
/// such a sample took the S#'s first dword as a sampler heap index. A forward pass over the blocks: an immediate move
/// sets a constant, any other instruction that may write an SGPR ends it, and a block starts with what all its
/// predecessors agree on.
/// </summary>
internal static class BlockConstants
{
    /// <summary>
    /// Scalar instructions whose first operand is read, not written (as <see cref="SamplerLoads"/>); every other one
    /// counts as a write of its first operand.
    /// </summary>
    private static readonly string[] Readers = ["s_cmp", "s_bitcmp", "s_cbranch", "s_branch", "s_setpc", "s_store", "s_buffer_store", "s_atomic", "s_sendmsg", "s_waitcnt", "s_setreg"];

    /// <summary>Vector operands that can name a written SGPR: the destination and a carry-out or compare mask.</summary>
    private const int VectorWriteOperands = 2;

    /// <summary>A move's operands: the destination and its source.</summary>
    private const int MoveOperands = 2;

    private const uint AllOnes = 0xFFFFFFFF;

    /// <summary>The constants at the start of each reachable block, by SGPR number.</summary>
    public static Dictionary<long, Dictionary<int, uint>> AtEntry(IReadOnlyList<Instruction> instructions, HashSet<long> leaders, IReadOnlyDictionary<long, List<long>> successors, HashSet<long> reachable)
    {
        var entry = new Dictionary<long, Dictionary<int, uint>>();
        var blocks = Blocks(instructions, leaders);

        if (instructions.Count == 0)
        {
            return entry;
        }

        entry[instructions[0].Address] = [];
        var pending = new Queue<long>();
        pending.Enqueue(instructions[0].Address);

        while (pending.Count > 0)
        {
            long block = pending.Dequeue();

            if (!reachable.Contains(block) || !blocks.TryGetValue(block, out var body) || !successors.TryGetValue(block, out var next))
            {
                continue;
            }

            var state = new Dictionary<int, uint>(entry[block]);

            foreach (var instruction in body)
            {
                Step(instruction, state);
            }

            foreach (long successor in next)
            {
                if (Meet(entry, successor, state))
                {
                    pending.Enqueue(successor);
                }
            }
        }

        return entry;
    }

    /// <summary>Narrows a block's entry to what it shares with <paramref name="state"/>; whether the entry changed (or was new).</summary>
    private static bool Meet(Dictionary<long, Dictionary<int, uint>> entry, long block, Dictionary<int, uint> state)
    {
        if (!entry.TryGetValue(block, out var known))
        {
            entry[block] = new Dictionary<int, uint>(state);

            return true;
        }

        var gone = known.Where(pair => !state.TryGetValue(pair.Key, out uint value) || value != pair.Value).Select(pair => pair.Key).ToList();

        foreach (int register in gone)
        {
            known.Remove(register);
        }

        return gone.Count > 0;
    }

    private static Dictionary<long, List<Instruction>> Blocks(IReadOnlyList<Instruction> instructions, HashSet<long> leaders)
    {
        var blocks = new Dictionary<long, List<Instruction>>();
        List<Instruction>? current = null;

        foreach (var instruction in instructions)
        {
            if (leaders.Contains(instruction.Address) || current is null)
            {
                current = [];
                blocks[instruction.Address] = current;
            }

            current.Add(instruction);
        }

        return blocks;
    }

    /// <summary>One instruction's effect: the SGPRs it may write lose their constant, an immediate move gives one.</summary>
    private static void Step(Instruction instruction, Dictionary<int, uint> state)
    {
        var (mnemonic, arguments) = Split(instruction);

        if (arguments.Count == 0)
        {
            return;
        }

        if (mnemonic.StartsWith("v_", StringComparison.Ordinal))
        {
            for (int index = 0; index < arguments.Count && index < VectorWriteOperands; index++)
            {
                Forget(Parse(arguments[index]), arguments[index], state, wide: true);
            }

            return;
        }

        // A call's callee may write any SGPR.
        if (mnemonic.StartsWith("s_call", StringComparison.Ordinal))
        {
            state.Clear();

            return;
        }

        if (!mnemonic.StartsWith("s_", StringComparison.Ordinal) || Readers.Any(prefix => mnemonic.StartsWith(prefix, StringComparison.Ordinal)))
        {
            return;
        }

        var destination = Parse(arguments[0]);
        Forget(destination, arguments[0], state, Wide(mnemonic));

        if (destination is null || destination.Kind != OperandKind.Scalar || arguments.Count != MoveOperands)
        {
            return;
        }

        var source = Parse(arguments[1]);

        if (source is null || !source.IsImmediate)
        {
            return;
        }

        Move(mnemonic, destination, source.Value, state);
    }

    /// <summary>The constants an immediate move leaves, as the translator writes them (SaluEmitter's moves, s_brev_b32).</summary>
    private static void Move(string mnemonic, Operand destination, uint value, Dictionary<int, uint> state)
    {
        switch (mnemonic)
        {
            case "s_mov_b32" or "s_movk_i32" when destination.High == destination.Low:
                state[destination.Low] = value;
                break;
            case "s_brev_b32" when destination.High == destination.Low:
                state[destination.Low] = Reverse(value);
                break;
            case "s_mov_b64" when destination.High == destination.Low + 1:
                state[destination.Low] = value;
                state[destination.High] = value == AllOnes ? AllOnes : 0;
                break;
            default:
                break;
        }
    }

    /// <summary>
    /// Ends the constants of the SGPRs an operand names, and with <paramref name="wide"/> of the one after a single
    /// register (a mask's high half on wave64, a 64-bit result named by its low register); an SGPR operand that does
    /// not parse ends them all. Not the one after for a 32-bit write: an S# set from its last dword down (s30, s29,
    /// s28, s31 in PS_CompositeNMB) would lose each dword to the next move.
    /// </summary>
    private static void Forget(Operand? operand, string text, Dictionary<int, uint> state, bool wide)
    {
        if (operand is null)
        {
            if (text.TrimStart('-', '|').StartsWith('s'))
            {
                state.Clear();
            }

            return;
        }

        if (operand.Kind != OperandKind.Scalar)
        {
            return;
        }

        int last = wide && operand.High == operand.Low ? operand.High + 1 : operand.High;

        for (int register = operand.Low; register <= last; register++)
        {
            state.Remove(register);
        }
    }

    /// <summary>Whether a scalar instruction may write two SGPRs through a destination named by one: the 64-bit forms and those that save or write EXEC.</summary>
    private static bool Wide(string mnemonic)
    {
        return mnemonic.Contains("64", StringComparison.Ordinal) || mnemonic.Contains("exec", StringComparison.Ordinal) || mnemonic.StartsWith("s_wqm", StringComparison.Ordinal);
    }

    /// <summary>The bits of <paramref name="value"/> in reverse order (s_brev_b32).</summary>
    public static uint Reverse(uint value)
    {
        const int Bits = 32;
        uint reversed = 0;

        for (int bit = 0; bit < Bits; bit++)
        {
            reversed = (reversed << 1) | ((value >> bit) & 1);
        }

        return reversed;
    }

    private static Operand? Parse(string text)
    {
        try
        {
            return OperandParser.Parse(text);
        }
        catch (Exception exception) when (exception is UnsupportedProgramException or FormatException or OverflowException)
        {
            return null;
        }
    }

    private static (string Mnemonic, List<string> Arguments) Split(Instruction instruction)
    {
        var line = instruction.Text.Split(';')[0].Split("//")[0].Trim();
        var space = line.IndexOfAny([' ', '\t']);

        if (space < 0)
        {
            return (line, []);
        }

        return (line[..space], ArgumentList.Split(InstructionModifiers.Strip(line[space..].TrimStart())));
    }
}
