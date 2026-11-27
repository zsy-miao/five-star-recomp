// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Shaders.Disassembly;

namespace FiveStar.Shaders.Translation.Core;

/// <summary>
/// Splits a program into basic blocks and translates the reachable ones. Each block ends by setting <c>pc</c>: every
/// branch condition is scalar (SCC) or a wave-wide reduction, so all lanes of a wave follow the same <c>pc</c> and
/// divergence lives only in the per-lane <c>active</c> flag, as on the hardware.
/// </summary>
internal sealed class ControlFlowTranslator
{
    private readonly TranslationState _state;
    private readonly OperandAccess _operands;
    private readonly InstructionTranslator _instructions;

    // How the block being translated ends, once its terminator has been translated.
    private BlockExit? _exit;

    // Each block's successors, and the SGPR constants every path brings into it (BlockConstants).
    private SortedDictionary<long, List<long>> _successors = [];
    private Dictionary<long, Dictionary<int, uint>> _entryConstants = [];

    // A first-lane exchange whose result the next block reads first: (resume address, destination, site).
    private (long Resume, string Destination, int Site)? _exchange;

    public ControlFlowTranslator(TranslationState state, OperandAccess operands, InstructionTranslator instructions)
    {
        _state = state;
        _operands = operands;
        _instructions = instructions;
    }

    /// <summary>Translates the program into <see cref="TranslationState.Blocks"/>.</summary>
    public void Run()
    {
        LongJumpRewriter.Rewrite(_state.Instructions);
        var leaders = FindLeaders();

        if (_state.Stage == ShaderStage.Mesh)
        {
            DetectMeshYield(leaders);
        }

        var reachable = Reachable(leaders);
        _entryConstants = BlockConstants.AtEntry(_state.Instructions, leaders, _successors, reachable);
        TranslateBlocks(leaders, reachable);
    }

    /// <summary>Block leaders: the entry, branch targets and the instruction after a branch or an early s_endpgm.</summary>
    private HashSet<long> FindLeaders()
    {
        var instructions = _state.Instructions;
        var leaders = new HashSet<long>();

        if (instructions.Count > 0)
        {
            leaders.Add(instructions[0].Address);
        }

        for (var index = 0; index < instructions.Count; index++)
        {
            var instruction = instructions[index];
            var mnemonic = instruction.Mnemonic;

            if (mnemonic == "s_swappc_b64" && index == 0 && _state.Stage == ShaderStage.Vertex)
            {
                // A call into the driver-generated fetch shader (vertex attribute loads), emulated by fetch inputs.
                _state.UsesFetchShader = true;
                continue;
            }

            RejectIndirect(mnemonic);

            if (BranchTargets.IsBranch(mnemonic))
            {
                _state.HasControlFlow = true;
                leaders.Add(BranchTargets.Target(instruction.Text));
                leaders.Add(instruction.Next);
            }

            if (mnemonic == "s_endpgm" && index + 1 < instructions.Count)
            {
                _state.HasControlFlow = true;
                leaders.Add(instruction.Next);
            }
        }

        return leaders;
    }

    private static void RejectIndirect(string mnemonic)
    {
        string[] indirect = ["s_setpc", "s_swappc", "s_cbranch_cdbg", "s_cbranch_g_fork", "s_cbranch_join"];

        if (indirect.Any(prefix => mnemonic.StartsWith(prefix, StringComparison.Ordinal)))
        {
            throw new UnsupportedProgramException($"{mnemonic} (indirect control flow)");
        }
    }

    /// <summary>
    /// A mesh shader runs in direct-output mode when every export follows a barrier that follows GS_ALLOC_REQ (in
    /// program order); each s_barrier then ends a block and becomes a yield point.
    /// </summary>
    private void DetectMeshYield(HashSet<long> leaders)
    {
        var seenAllocation = false;
        var seenBarrier = false;
        var exportsAfterBarrier = true;

        foreach (var instruction in _state.Instructions)
        {
            var text = instruction.Text;

            if (text.Contains("MSG_GS_ALLOC_REQ", StringComparison.Ordinal))
            {
                seenAllocation = true;
            }
            else if (text.StartsWith("s_barrier", StringComparison.Ordinal) && seenAllocation)
            {
                seenBarrier = true;
            }
            else if (text.StartsWith("exp ", StringComparison.Ordinal) && !seenBarrier)
            {
                exportsAfterBarrier = false;
            }
        }

        _state.MeshYield = exportsAfterBarrier && seenBarrier;

        if (!_state.MeshYield)
        {
            return;
        }

        foreach (var instruction in _state.Instructions)
        {
            if (instruction.Text.StartsWith("s_barrier", StringComparison.Ordinal) || _state.FirstLaneExchanges.ContainsKey(instruction.Address))
            {
                leaders.Add(instruction.Next);
                _state.HasControlFlow = true;
            }
        }
    }

    /// <summary>Blocks reachable from the entry; data after the final s_endpgm (constant tables) is never code.</summary>
    private HashSet<long> Reachable(HashSet<long> leaders)
    {
        var successors = new SortedDictionary<long, List<long>>();
        var last = new Dictionary<long, Instruction>();
        var current = 0L;

        foreach (var instruction in _state.Instructions)
        {
            if (leaders.Contains(instruction.Address))
            {
                current = instruction.Address;
                successors[current] = [];
            }

            last[current] = instruction;

            if (BranchTargets.IsBranch(instruction.Mnemonic))
            {
                successors[current].Add(BranchTargets.Target(instruction.Text));
            }
        }

        AddFallThrough(successors, last);
        _successors = successors;

        return Walk(successors);
    }

    private static void AddFallThrough(SortedDictionary<long, List<long>> successors, Dictionary<long, Instruction> last)
    {
        var blocks = successors.Keys.ToList();

        for (var index = 0; index + 1 < blocks.Count; index++)
        {
            var mnemonic = last[blocks[index]].Mnemonic;

            if (mnemonic is not "s_branch" and not "s_endpgm")
            {
                successors[blocks[index]].Add(blocks[index + 1]);
            }
        }
    }

    private HashSet<long> Walk(SortedDictionary<long, List<long>> successors)
    {
        var reachable = new HashSet<long>();
        var pending = new Stack<long>();

        if (_state.Instructions.Count > 0)
        {
            pending.Push(_state.Instructions[0].Address);
        }

        while (pending.Count > 0)
        {
            var block = pending.Pop();

            if (reachable.Contains(block) || !successors.TryGetValue(block, out var next))
            {
                continue;
            }

            reachable.Add(block);

            foreach (var successor in next)
            {
                pending.Push(successor);
            }
        }

        return reachable;
    }

    private void TranslateBlocks(HashSet<long> leaders, HashSet<long> reachable)
    {
        int? current = null;
        var terminated = false;
        var skip = false;

        foreach (var instruction in _state.Instructions)
        {
            var isLeader = leaders.Contains(instruction.Address);

            if (isLeader)
            {
                skip = !reachable.Contains(instruction.Address);
            }

            if (skip)
            {
                continue;
            }

            if (isLeader)
            {
                CloseBlock(current, terminated, instruction.Address);
                current = instruction.Address;
                _state.Forms.Begin(instruction.Address);

                _state.Body = [];
                terminated = false;
                _exit = null;
                SeedConstants(instruction.Address);
                ReadExchange(instruction.Address);
            }

            if (!terminated)
            {
                terminated = TranslateInstruction(instruction);
            }
        }

        CloseBlock(current, terminated, null);
    }

    /// <summary>A block starts with the constants all its predecessors leave in the SGPRs, and no others.</summary>
    private void SeedConstants(long block)
    {
        _state.Constants.Clear();

        if (!_entryConstants.TryGetValue(block, out var constants))
        {
            return;
        }

        foreach (var (register, value) in constants)
        {
            _state.Constants[RegisterKey.Sgpr(register)] = value;
        }
    }

    /// <summary>Ends a block; one that has no terminator falls through to <paramref name="next"/> (null: past the end).</summary>
    private void CloseBlock(int? block, bool terminated, long? next)
    {
        if (block is null)
        {
            return;
        }

        if (!terminated)
        {
            if (next is null)
            {
                _state.Emit("  pc = PC_END;");
                _exit = new BlockExit(BlockExitKind.End, null, null, null);
            }
            else
            {
                _state.Emit($"  pc = {Hlsl.Hex(next.Value)};");
                _exit = new BlockExit(BlockExitKind.Jump, next, null, null);
            }
        }

        var exit = _exit ?? throw new InvalidOperationException("block without exit");
        _state.Blocks.Add(new CodeBlock(block.Value, _state.Body, exit));
    }

    /// <summary>Translates one instruction; true when it ends its block.</summary>
    private bool TranslateInstruction(Instruction instruction)
    {
        _state.Translated.Add(instruction);
        var line = instruction.Text.Split(';')[0].Split("//")[0].Trim();

        if (line.Length == 0)
        {
            return false;
        }

        var space = line.IndexOfAny([' ', '\t']);
        var mnemonic = space < 0 ? line : line[..space];
        var operands = space < 0 ? string.Empty : line[space..].TrimStart();

        if (BranchTargets.IsBranch(mnemonic))
        {
            EmitBranch(mnemonic, BranchTargets.Target(operands), instruction.Next);

            return true;
        }

        return TranslateLinear(instruction, mnemonic, operands);
    }

    private bool TranslateLinear(Instruction instruction, string mnemonic, string operands)
    {
        if (mnemonic == "s_endpgm")
        {
            _state.Emit("  pc = PC_END;");
            _exit = new BlockExit(BlockExitKind.End, null, null, null);

            return true;
        }

        if (mnemonic == "v_readfirstlane_b32" && _state.FirstLaneDirect.TryGetValue(instruction.Address, out var first))
        {
            _operands.Write(OperandParser.Parse(operands.Split(',')[0].Trim()), first, 0);

            return false;
        }

        if (mnemonic == "v_readfirstlane_b32" && _state.FirstLaneExchanges.TryGetValue(instruction.Address, out var site))
        {
            Exchange(instruction, operands, site);

            return true;
        }

        if (mnemonic == "s_barrier" && _state.MeshYield)
        {
            _state.Emit($"  pc_resume = {Hlsl.Hex(instruction.Next)}; pc = PC_YIELD;");
            _exit = new BlockExit(BlockExitKind.Yield, instruction.Next, null, null);

            return true;
        }

        if (mnemonic == "s_swappc_b64" && _state.UsesFetchShader && instruction.Address == _state.Instructions[0].Address)
        {
            return false;
        }

        if (mnemonic == "s_getpc_b64")
        {
            TranslateGetPc(instruction, operands);

            return false;
        }

        _state.Address = instruction.Address;
        _instructions.Translate(mnemonic, operands);

        return false;
    }

    /// <summary>
    /// A v_readfirstlane under a full EXEC whose value the group exchanges: the console wave's first lane (lane 0) deposits
    /// it, the block yields to the group's barrier step, and the next block reads it (<see cref="ReadExchange"/>). The
    /// slot alternates with the barrier step's parity, so a deposit in one round never meets a read of the last.
    /// </summary>
    private void Exchange(Instruction instruction, string operands, int site)
    {
        var parts = operands.Split(',');
        var source = OperandParser.Parse(parts[1].Trim());

        _state.Address = instruction.Address;
        _state.Emit($"  if ({WaveOps.GroupLane} == 0u) vw_first_put({site}u, ms_step, {_operands.Read(source)});");
        _state.Emit($"  pc_resume = {Hlsl.Hex(instruction.Next)}; pc = PC_YIELD;");
        _exit = new BlockExit(BlockExitKind.Yield, instruction.Next, null, null);
        _exchange = (instruction.Next, parts[0].Trim(), site);
    }

    /// <summary>The first statement of the block after an exchange: the destination takes the exchanged value.</summary>
    private void ReadExchange(long address)
    {
        if (_exchange is null || _exchange.Value.Resume != address)
        {
            return;
        }

        var exchange = _exchange.Value;
        _operands.Write(OperandParser.Parse(exchange.Destination), $"vw_first({exchange.Site}u, ms_step)", 0);
        _exchange = null;
    }

    /// <summary>s_getpc_b64: a PC-relative pointer into the program's own code (embedded constant tables).</summary>
    private void TranslateGetPc(Instruction instruction, string operands)
    {
        var destination = OperandParser.Parse(operands.Split(',')[0].Trim());

        if (_state.Code is null)
        {
            throw new UnsupportedProgramException("s_getpc_b64 without code bytes");
        }

        _state.UsesCode = true;
        _operands.Write(destination, Hlsl.HexUint(instruction.Next), 0);
        _operands.Write(destination, "CODE_TAG", 1);
    }

    private void EmitBranch(string mnemonic, long target, int next)
    {
        var condition = BranchCondition(mnemonic);

        if (condition is null)
        {
            _state.Emit($"  pc = {Hlsl.Hex(target)};");
            _exit = new BlockExit(BlockExitKind.Jump, target, null, null);

            return;
        }

        _state.Emit($"  pc = ({condition}) ? {Hlsl.Hex(target)} : {Hlsl.Hex(next)};");
        _exit = new BlockExit(BlockExitKind.Conditional, target, next, condition);
    }

    private string? BranchCondition(string mnemonic)
    {
        switch (mnemonic)
        {
            case "s_branch":
                return null;
            case "s_cbranch_scc0":
                return "!scc";
            case "s_cbranch_scc1":
                return "scc";
            case "s_cbranch_execz":
                return $"!{WaveOps.AnyTrue}(active)";
            case "s_cbranch_execnz":
                return $"{WaveOps.AnyTrue}(active)";
            case "s_cbranch_vccz":
                return "!" + VccAny();
            default:
                return VccAny();
        }
    }

    private string VccAny()
    {
        var vcc = OperandParser.Parse("vcc");

        if (_state.Masks.Contains(SpecialRegisters.Vcc))
        {
            return $"{WaveOps.AnyTrue}({_operands.Mask(vcc)})";
        }

        var low = _operands.Read(vcc, 0);

        if (!_state.IsWave64)
        {
            return $"({low} != 0)";
        }

        // A wave64 VCC held as an integer has lanes 32-63 in vcc_hi.
        return $"(({low} | {_operands.Read(vcc, 1)}) != 0)";
    }
}
