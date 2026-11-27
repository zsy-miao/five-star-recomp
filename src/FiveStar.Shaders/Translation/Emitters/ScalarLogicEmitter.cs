// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.RegularExpressions;
using FiveStar.Shaders.Translation.Core;
using static FiveStar.Shaders.Translation.Emitters.Operands;

namespace FiveStar.Shaders.Translation.Emitters;

/// <summary>
/// Scalar logic that may act on lane masks: s_and/or/xor/... (on masks: bool logic; on integers: bitwise), the
/// EXEC save/write forms, s_wqm, s_cselect and the 64-bit moves and nots. SCC is set as the hardware does.
/// </summary>
internal sealed partial class ScalarLogicEmitter
{
    private const string Bits64 = "64";

    private readonly TranslationState _state;
    private readonly OperandAccess _operands;

    public ScalarLogicEmitter(TranslationState state, OperandAccess operands)
    {
        _state = state;
        _operands = operands;
    }

    /// <summary>Translates the instruction when it belongs to this family; false otherwise.</summary>
    public bool TryTranslate(string op, IReadOnlyList<Operand> a)
    {
        var saveExec = SaveExecPattern().Match(op);

        if (saveExec.Success)
        {
            SaveExec(saveExec.Groups[1].Value, a);

            return true;
        }

        var writeExec = WriteExecPattern().Match(op);

        if (writeExec.Success)
        {
            WriteExec(writeExec.Groups[1].Value, a);

            return true;
        }

        var logic = LogicPattern().Match(op);

        if (logic.Success)
        {
            Logic(logic.Groups[1].Value, logic.Groups["bits"].Value == Bits64, a);

            return true;
        }

        var select = SelectPattern().Match(op);

        if (select.Success)
        {
            Select(select.Groups[1].Value == Bits64, a);

            return true;
        }

        return false;
    }

    /// <summary>The bool expression of a lane-mask logic operation.</summary>
    public static string MaskLogic(string kind, string p, string q)
    {
        switch (kind)
        {
            case "and":
                return $"{p} && {q}";
            case "or":
                return $"{p} || {q}";
            case "xor":
                return $"{p} != {q}";
            case "andn2":
                return $"{p} && !{q}";
            case "orn2":
                return $"{p} || !{q}";
            case "nand":
                return $"!({p} && {q})";
            case "nor":
                return $"!({p} || {q})";
            default:
                return $"{p} == {q}";
        }
    }

    private static string IntegerLogic(string kind, string p, string q)
    {
        switch (kind)
        {
            case "and":
                return $"{p} & {q}";
            case "or":
                return $"{p} | {q}";
            case "xor":
                return $"{p} ^ {q}";
            case "andn2":
                return $"{p} & ~{q}";
            case "orn2":
                return $"{p} | ~{q}";
            case "nand":
                return $"~({p} & {q})";
            case "nor":
                return $"~({p} | {q})";
            default:
                return $"~({p} ^ {q})";
        }
    }

    /// <summary>s_*_saveexec: SDST = EXEC; EXEC = f(SRC, EXEC); SCC = EXEC != 0.</summary>
    private void SaveExec(string kind, IReadOnlyList<Operand> a)
    {
        var source = _operands.Mask(a[Source0]);
        _state.Emit("  { bool _old = active;");
        string update;

        if (kind == "andn1")
        {
            update = $"(!_old) && {source}";
        }
        else if (kind == "orn1")
        {
            update = $"(!_old) || {source}";
        }
        else
        {
            update = MaskLogic(kind, source, "_old");
        }

        _state.Emit($"    active = {update};");
        var key = a[Destination].At(0);
        _operands.MarkMask(key);
        _state.Emit($"    {key.MaskName} = _old; }}");
        _operands.ShadowInteger(key);
        _operands.SetScc($"{WaveOps.AnyTrue}(active)");
    }

    /// <summary>s_andn2/orn2_wrexec: EXEC = SRC op ~EXEC; SDST = EXEC.</summary>
    private void WriteExec(string kind, IReadOnlyList<Operand> a)
    {
        var p = _operands.Mask(a[Source0]);
        var update = kind == "andn2" ? $"{p} && !active" : $"{p} || !active";
        _operands.WriteMask(OperandParser.Parse("exec"), update);
        _operands.WriteMask(a[Destination], "active");
        _operands.SetScc($"{WaveOps.AnyTrue}(active)");
    }

    private void Logic(string kind, bool wide, IReadOnlyList<Operand> a)
    {
        // A mask operation takes every dword of the masks: on wave64 a _b32 form works on one dword as an integer
        // (its constant is a bit pattern, and the register after its destination is not written), as SaluEmitter's moves.
        var whole = wide || !_state.IsWave64;
        var anyMask = whole && (_operands.HoldsMask(a[Source0]) || _operands.HoldsMask(a[Source1]));

        if (anyMask && _operands.IsMask(a[Source0]) && _operands.IsMask(a[Source1]))
        {
            var update = MaskLogic(kind, _operands.Mask(a[Source0]), _operands.Mask(a[Source1]));
            _operands.WriteMask(a[Destination], update);
            _operands.SetScc($"{WaveOps.AnyTrue}({_operands.Mask(a[Destination])})");

            return;
        }

        var dwords = wide ? PairDwords : 1;
        var p = Enumerable.Range(0, dwords).Select(dword => _operands.Read(a[Source0], dword)).ToArray();
        var q = Enumerable.Range(0, dwords).Select(dword => _operands.Read(a[Source1], dword)).ToArray();
        p = _operands.Snap(p);
        q = _operands.Snap(q);

        for (var dword = 0; dword < dwords; dword++)
        {
            _operands.Write(a[Destination], IntegerLogic(kind, p[dword], q[dword]), dword);
        }

        var nonZero = Enumerable.Range(0, dwords).Select(dword => $"({a[Destination].At(dword).Name} != 0)");
        _operands.SetScc(string.Join(" || ", nonZero));
    }

    private void Select(bool wide, IReadOnlyList<Operand> a)
    {
        var first = a[Source0];
        var second = a[Source1];
        var whole = wide || !_state.IsWave64;

        if (whole && _operands.IsMask(first) && _operands.IsMask(second) && (!first.IsImmediate || !second.IsImmediate))
        {
            _operands.WriteMask(a[Destination], $"(scc ? {_operands.Mask(first)} : {_operands.Mask(second)})");

            return;
        }

        var dwords = wide ? PairDwords : 1;
        var values = Enumerable.Range(0, dwords).Select(dword => $"(scc ? {_operands.Read(first, dword)} : {_operands.Read(second, dword)})");
        var snapped = _operands.Snap(values.ToArray());

        for (var dword = 0; dword < dwords; dword++)
        {
            _operands.Write(a[Destination], snapped[dword], dword);
        }
    }

    [GeneratedRegex(@"^s_(and|or|xor|andn2|orn2|nand|nor|xnor|andn1|orn1)_saveexec_b(32|64)$")]
    private static partial Regex SaveExecPattern();

    [GeneratedRegex(@"^s_(andn2|orn2)_wrexec_b(32|64)$")]
    private static partial Regex WriteExecPattern();

    [GeneratedRegex(@"^s_(and|or|xor|andn2|orn2|nand|nor|xnor)_b(?<bits>32|64)$")]
    private static partial Regex LogicPattern();

    [GeneratedRegex(@"^s_cselect_b(32|64)$")]
    private static partial Regex SelectPattern();
}
