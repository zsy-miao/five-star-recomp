// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.RegularExpressions;
using FiveStar.Shaders.Translation.Core;
using static FiveStar.Shaders.Translation.Emitters.Operands;

namespace FiveStar.Shaders.Translation.Emitters;

/// <summary>Scalar ALU (SOP1/SOP2/SOPC/SOPK) instructions: moves, compares, arithmetic, shifts and bit operations.</summary>
internal sealed partial class SaluEmitter
{
    // s_cmp / s_cmpk / s_bitcmp operands are both sources.
    private const int Left = 0;
    private const int Right = 1;
    private const uint AllOnes = 0xFFFFFFFF;

    private readonly TranslationState _state;
    private readonly OperandAccess _operands;
    private readonly ScalarLogicEmitter _logic;
    private readonly ScalarArithmeticEmitter _arithmetic;

    public SaluEmitter(TranslationState state, OperandAccess operands, ScalarLogicEmitter logic, ScalarArithmeticEmitter arithmetic)
    {
        _state = state;
        _operands = operands;
        _logic = logic;
        _arithmetic = arithmetic;
    }

    public void Translate(string op, IReadOnlyList<Operand> a)
    {
        if (_logic.TryTranslate(op, a) || TryCompare(op, a) || _arithmetic.TryTranslate(op, a))
        {
            return;
        }

        switch (op)
        {
            case "s_wqm_b32":
            case "s_wqm_b64":
                WholeQuadMode(a);
                break;
            case "s_bitcmp0_b32":
            case "s_bitcmp1_b32":
                BitCompare(op, a);
                break;
            case "s_bcnt1_i32_b32":
            case "s_bcnt1_i32_b64":
                BitCount(op, a);
                break;
            case "s_mov_b32":
            case "s_movk_i32":
                Move32(a);
                break;
            case "s_mov_b64":
                Move64(a);
                break;
            case "s_not_b32":
                Not32(a);
                break;
            case "s_not_b64":
                Not64(a);
                break;
            case "s_ff1_i32_b64":
                FindFirstOne64(a);
                break;
            case "s_ff1_i32_b32":
                FindFirstOne32(a);
                break;
            default:
                throw new UnsupportedProgramException(op);
        }
    }

    /// <summary>The whole-quad mask of <paramref name="source"/>: a lane's bit is set when any lane of its quad has it.</summary>
    public static string QuadMask(string source)
    {
        var bit = $"(({source}) ? 1u : 0u)";

        return $"(({bit} | QuadReadAcrossX({bit}) | QuadReadAcrossY({bit}) | QuadReadAcrossDiagonal({bit})) != 0)";
    }

    /// <summary>s_wqm: on PS, a lane is live when any lane of its quad is (no short-circuit around the quad reads).</summary>
    private void WholeQuadMode(IReadOnlyList<Operand> a)
    {
        var source = _operands.Mask(a[Source0]);
        var quad = QuadMask(source);
        var update = _state.Stage == ShaderStage.Pixel ? quad : source;
        _operands.WriteMask(a[Destination], update);
        _operands.SetScc($"{WaveOps.AnyTrue}({_operands.Mask(a[Destination])})");
    }

    private bool TryCompare(string op, IReadOnlyList<Operand> a)
    {
        var compare = ComparePattern().Match(op);

        if (compare.Success)
        {
            Compare(compare.Groups[1].Value, compare.Groups["type"].Value, a);

            return true;
        }

        var compareConstant = CompareConstantPattern().Match(op);

        if (compareConstant.Success)
        {
            CompareConstant(compareConstant.Groups[1].Value, compareConstant.Groups["type"].Value == "u32", a);

            return true;
        }

        return false;
    }

    public static string CompareSymbol(string condition)
    {
        switch (condition)
        {
            case "eq":
                return "==";
            case "lg":
                return "!=";
            case "gt":
                return ">";
            case "ge":
                return ">=";
            case "lt":
                return "<";
            default:
                return "<=";
        }
    }

    private void Compare(string condition, string type, IReadOnlyList<Operand> a)
    {
        var symbol = CompareSymbol(condition);

        if (type == "u64")
        {
            Compare64(condition, symbol, a);

            return;
        }

        var x = _operands.Read(a[Left]);
        var y = _operands.Read(a[Right]);

        if (type == "i32")
        {
            x = $"asint({x})";
            y = $"asint({y})";
        }

        _operands.SetScc($"({x} {symbol} {y})");
    }

    private void Compare64(string condition, string symbol, IReadOnlyList<Operand> a)
    {
        var left = a[Left];
        var right = a[Right];
        var equal = condition == "eq";

        if (_operands.IsMask(left) && _operands.IsMask(right) && right.IsImmediate && right.Value == 0)
        {
            var negation = equal ? "!" : string.Empty;
            _operands.SetScc($"{negation}{WaveOps.AnyTrue}({_operands.Mask(left)})");

            return;
        }

        var join = equal ? "&&" : "||";
        var lowLeft = _operands.Read(left, Low);
        var lowRight = _operands.Read(right, Low);
        var highLeft = left.IsImmediate ? "0u" : _operands.Read(left, High);
        var highRight = right.IsImmediate ? "0u" : _operands.Read(right, High);
        _operands.SetScc($"(({lowLeft} {symbol} {lowRight}) {join} ({highLeft} {symbol} {highRight}))");
    }

    /// <summary>s_cmpk: compare an SGPR with a 16-bit constant (sign-extended for the i32 forms).</summary>
    private void CompareConstant(string condition, bool unsigned, IReadOnlyList<Operand> a)
    {
        var symbol = CompareSymbol(condition);

        if (unsigned)
        {
            _operands.SetScc($"({_operands.Read(a[Left])} {symbol} {_operands.Read(a[Right])})");

            return;
        }

        _operands.SetScc($"(asint({_operands.Read(a[Left])}) {symbol} (asint({_operands.Read(a[Right])} << 16) >> 16))");
    }

    private void BitCompare(string op, IReadOnlyList<Operand> a)
    {
        var expected = op == "s_bitcmp1_b32" ? "1" : "0";
        _operands.SetScc($"((({_operands.Read(a[Left])} >> ({_operands.Read(a[Right])} & 31)) & 1) == {expected})");
    }

    private void BitCount(string op, IReadOnlyList<Operand> a)
    {
        var source = a[Source0];

        if (_operands.IsMask(source) && !source.IsImmediate)
        {
            _operands.Write(a[Destination], $"{WaveOps.CountBits}({_operands.Mask(source)})");
        }
        else
        {
            var count = $"countbits({_operands.Read(source)})";

            if (op.EndsWith("64", StringComparison.Ordinal))
            {
                count += $" + countbits({_operands.Read(source, High)})";
            }

            _operands.Write(a[Destination], count);
        }

        SetSccNonZero(a);
    }

    private void Move32(IReadOnlyList<Operand> a)
    {
        if (TryMoveMask(a))
        {
            return;
        }

        _operands.Write(a[Destination], _operands.Read(a[Source0]));
    }

    private void Move64(IReadOnlyList<Operand> a)
    {
        if (TryMoveMask(a))
        {
            return;
        }

        var source = a[Source0];
        _operands.Write(a[Destination], _operands.Read(source, Low), Low);

        // An inline constant is sign-extended to 64 bits; only -1 has a non-zero high dword here.
        string high;

        if (!source.IsImmediate)
        {
            high = _operands.Read(source, High);
        }
        else
        {
            high = source.Value == AllOnes ? "0xffffffffu" : "0u";
        }

        _operands.Write(a[Destination], high, High);
    }

    /// <summary>A move of a lane mask, or a constant into EXEC, stays a mask.</summary>
    private bool TryMoveMask(IReadOnlyList<Operand> a)
    {
        var source = a[Source0];

        // A move of every dword a mask has. On wave64, s_mov_b32 moves one dword of the pair and leaves the register
        // after its destination alone: as a mask move it also wrote that register (the mask's high lanes), and it read
        // its source as a mask where only the emission order had left one there (a loop head reached from its back
        // edge alone, the source a count by then).
        bool whole = a[Destination].High > a[Destination].Low || !_state.IsWave64;

        if (whole && _operands.HoldsMask(source))
        {
            _operands.WriteMask(a[Destination], _operands.Mask(source));

            return true;
        }

        // A constant that sets or clears every lane EXEC holds; any other constant (s_mov_b32 exec_lo, 0xffff0000 on
        // wave64) is an integer write of its bits, which keeps the other half (OperandAccess.Write).
        if (a[Destination].Key == SpecialRegisters.Exec && source.IsImmediate && whole && (source.Value == 0 || source.Value == AllOnes))
        {
            var constant = source.Value != 0 ? "true" : "false";
            _operands.WriteMask(a[Destination], constant);

            return true;
        }

        return false;
    }

    private void Not32(IReadOnlyList<Operand> a)
    {
        _operands.Write(a[Destination], $"~{_operands.Read(a[Source0])}");
        SetSccNonZero(a);
    }

    private void Not64(IReadOnlyList<Operand> a)
    {
        var source = a[Source0];

        if (_operands.IsMask(source) && !source.IsImmediate)
        {
            _operands.WriteMask(a[Destination], $"!{_operands.Mask(source)}");

            return;
        }

        var halves = _operands.Snap(_operands.Read(source, Low), _operands.Read(source, High));
        _operands.Write(a[Destination], $"~{halves[Low]}", Low);
        _operands.Write(a[Destination], $"~{halves[High]}", High);
    }

    private void FindFirstOne64(IReadOnlyList<Operand> a)
    {
        var source = a[Source0];

        if (_operands.IsMask(source) && !source.IsImmediate)
        {
            var ballot = _operands.SnapPair($"{WaveOps.Ballot}({_operands.Mask(source)})");
            var first = $"({ballot}.x != 0 ? firstbitlow({ballot}.x) : ({ballot}.y != 0 ? 32u + firstbitlow({ballot}.y) : 0xffffffffu))";
            _operands.Write(a[Destination], first);
            _state.MaskLanes.Add(a[Destination].At(0));

            return;
        }

        var halves = _operands.Snap(_operands.Read(source, Low), _operands.Read(source, High));
        var (low, high) = (halves[Low], halves[High]);
        _operands.Write(a[Destination], $"({low} != 0 ? firstbitlow({low}) : ({high} != 0 ? 32u + firstbitlow({high}) : 0xffffffffu))");
    }

    private void FindFirstOne32(IReadOnlyList<Operand> a)
    {
        var source = a[Source0];
        string value;
        var fromMask = _operands.IsMask(source) && !source.IsImmediate;

        if (fromMask)
        {
            value = $"{WaveOps.Ballot}({_operands.Mask(source)}).x";
        }
        else
        {
            value = _operands.Read(source);
        }

        _operands.Write(a[Destination], $"firstbitlow({value})");

        if (fromMask)
        {
            _state.MaskLanes.Add(a[Destination].At(0));
        }
    }

    /// <summary>SCC = the destination SGPR is non-zero.</summary>
    public void SetSccNonZero(IReadOnlyList<Operand> a)
    {
        _operands.SetScc($"({RegisterKey.Sgpr(a[Destination].Low).Name} != 0)");
    }

    [GeneratedRegex(@"^s_cmp_(eq|lg|gt|ge|lt|le)_(?<type>u32|i32|u64)$")]
    private static partial Regex ComparePattern();

    [GeneratedRegex(@"^s_cmpk_(eq|lg|gt|ge|lt|le)_(?<type>u32|i32)$")]
    private static partial Regex CompareConstantPattern();
}
