// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.RegularExpressions;
using FiveStar.Shaders.Translation.Core;
using static FiveStar.Shaders.Translation.Emitters.Operands;

namespace FiveStar.Shaders.Translation.Emitters;

/// <summary>Scalar integer arithmetic: add/sub with carry and overflow in SCC, shifts, min/max, bit fields, packing.</summary>
internal sealed partial class ScalarArithmeticEmitter
{
    // SOPK forms (s_addk, s_mulk, s_cmovk) read and write their first operand.
    private const int Accumulator = 0;
    private const int Constant = 1;

    private readonly TranslationState _state;
    private readonly OperandAccess _operands;

    public ScalarArithmeticEmitter(TranslationState state, OperandAccess operands)
    {
        _state = state;
        _operands = operands;
    }

    /// <summary>Translates the instruction when it belongs to this family; false otherwise.</summary>
    public bool TryTranslate(string op, IReadOnlyList<Operand> a)
    {
        var binary = BinaryOperator(op);

        if (binary is not null)
        {
            Binary(op, binary, a);

            return true;
        }

        var shift = Shift64Pattern().Match(op);

        if (shift.Success)
        {
            Shift64(shift.Groups[1].Value == "lshl", a);

            return true;
        }

        var shiftAdd = ShiftAddPattern().Match(op);

        if (shiftAdd.Success)
        {
            Write(a, $"({Read(a, Source0)} << {shiftAdd.Groups[1].Value}) + {Read(a, Source1)}");

            return true;
        }

        return TryUnaryOrSpecial(op, a);
    }

    private static string? BinaryOperator(string op)
    {
        switch (op)
        {
            case "s_add_u32":
            case "s_add_i32":
                return "+";
            case "s_sub_u32":
            case "s_sub_i32":
                return "-";
            case "s_mul_i32":
                return "*";
            case "s_lshl_b32":
                return "<<";
            case "s_lshr_b32":
                return ">>";
            default:
                return null;
        }
    }

    private void Binary(string op, string symbol, IReadOnlyList<Operand> a)
    {
        var shiftMask = symbol is "<<" or ">>" ? " & 31" : string.Empty;
        var sources = _operands.Snap(Read(a, Source0), Read(a, Source1));
        var (x, y) = (sources[0], sources[1]);
        Write(a, $"{x} {symbol} ({y}{shiftMask})");
        var result = RegisterKey.Sgpr(a[Destination].Low).Name;

        switch (op)
        {
            case "s_add_u32":
                _operands.SetScc($"({result} < {x})");
                break;
            case "s_sub_u32":
                _operands.SetScc($"({y} > {x})");
                break;
            case "s_add_i32":
                _operands.SetScc($"((({x} ^ {result}) & ({y} ^ {result})) >> 31 != 0)");
                break;
            case "s_sub_i32":
                _operands.SetScc($"((({x} ^ {y}) & ({x} ^ {result})) >> 31 != 0)");
                break;
            case "s_lshl_b32":
            case "s_lshr_b32":
                _operands.SetScc($"({result} != 0)");
                break;
            default:
                break;
        }
    }

    private void Shift64(bool left, IReadOnlyList<Operand> a)
    {
        var sources = _operands.Snap(_operands.Read(a[Source0], Low), _operands.Read(a[Source0], High), Read(a, Source1));
        var symbol = left ? "<<" : ">>";
        var low = a[Destination].At(Low);
        var high = a[Destination].At(High);
        var (lowValue, highValue, amount) = (sources[0], sources[1], sources[^1]);

        _state.Emit($"  {{ uint64_t _v = ((uint64_t){highValue} << 32 | {lowValue}) {symbol} ({amount} & 63);");
        _state.Emit($"    {low.Name} = (uint)_v; {high.Name} = (uint)(_v >> 32); scc = _v != 0; }}");

        foreach (var key in new[] { low, high })
        {
            _state.Touch(key);
            _state.Constants.Remove(key);
            _operands.WroteInteger(key);
        }
    }

    private bool TryUnaryOrSpecial(string op, IReadOnlyList<Operand> a)
    {
        switch (op)
        {
            case "s_brev_b32":
                ReverseBits(a);
                break;
            case "s_flbit_i32_b32":
                Write(a, $"firstbithigh_amd({Read(a, Source0)})");
                break;
            case "s_flbit_i32":
                Write(a, $"flbit_signed({Read(a, Source0)})");
                break;
            case "s_abs_i32":
                Write(a, $"asuint(abs(asint({Read(a, Source0)})))");
                SetSccNonZero(a);
                break;
            case "s_addc_u32":
            case "s_subb_u32":
                Carry(op == "s_addc_u32", a);
                break;
            case "s_mulk_i32":
                Write(a, $"{Read(a, Accumulator)} * {Read(a, Constant)}");
                break;
            case "s_addk_i32":
                Write(a, $"{Read(a, Accumulator)} + {Read(a, Constant)}");
                break;
            case "s_cmovk_i32":
            case "s_cmov_b32":
                Write(a, $"(scc ? {Read(a, Constant)} : {Read(a, Accumulator)})");
                break;
            default:
                return TryBitField(op, a);
        }

        return true;
    }

    /// <summary>
    /// s_brev_b32. Of a constant it is written as the constant it gives: programs build an S#'s filter word this way
    /// (s_brev_b32 s30, 40 is 0x14000000), and a literal S# is known by its four constants (OperandAccess.SamplerIndex).
    /// </summary>
    private void ReverseBits(IReadOnlyList<Operand> a)
    {
        if (a[Source0].IsImmediate)
        {
            Write(a, Hlsl.HexUint(BlockConstants.Reverse(a[Source0].Value)));

            return;
        }

        Write(a, $"reversebits({Read(a, Source0)})");
    }

    private void Carry(bool add, IReadOnlyList<Operand> a)
    {
        var sources = _operands.Snap(Read(a, Source0), Read(a, Source1));
        var (x, y) = (sources[0], sources[1]);

        if (add)
        {
            Write(a, $"{x} + {y} + (scc ? 1u : 0u)");
            _operands.SetScc($"(((uint64_t){x} + (uint64_t){y} + (scc ? 1 : 0)) >> 32) != 0");

            return;
        }

        Write(a, $"{x} - {y} - (scc ? 1u : 0u)");
        _operands.SetScc($"((uint64_t){y} + (scc ? 1 : 0)) > (uint64_t){x}");
    }

    private bool TryBitField(string op, IReadOnlyList<Operand> a)
    {
        switch (op)
        {
            case "s_bitset0_b32":
            case "s_bitset1_b32":
                BitSet(op, a);
                break;
            case "s_ashr_i32":
                Write(a, $"asuint(asint({Read(a, Source0)}) >> ({Read(a, Source1)} & 31))");
                SetSccNonZero(a);
                break;
            case "s_min_u32":
            case "s_max_u32":
            case "s_min_i32":
            case "s_max_i32":
                MinMax(op, a);
                break;
            case "s_bfe_u32":
            case "s_bfe_i32":
                BitFieldExtract(op, a);
                break;
            case "s_bfm_b32":
                Write(a, $"(((1u << ({Read(a, Source0)} & 31)) - 1) << ({Read(a, Source1)} & 31))");
                break;
            case "s_bfm_b64":
                BitFieldMask64(a);
                break;
            default:
                return TryPack(op, a);
        }

        return true;
    }

    private void BitSet(string op, IReadOnlyList<Operand> a)
    {
        var value = Read(a, Accumulator);
        var update = op == "s_bitset0_b32" ? "& ~" : "| ";
        Write(a, $"{value} {update}(1u << ({Read(a, Constant)} & 31))");
    }

    private void MinMax(string op, IReadOnlyList<Operand> a)
    {
        var sources = _operands.Snap(Read(a, Source0), Read(a, Source1));
        var (x, y) = (sources[0], sources[1]);
        var function = op.Contains("min", StringComparison.Ordinal) ? "min" : "max";
        var signed = op.EndsWith("i32", StringComparison.Ordinal);

        if (signed)
        {
            (x, y) = ($"asint({x})", $"asint({y})");
            Write(a, $"asuint({function}({x}, {y}))");
        }
        else
        {
            Write(a, $"{function}({x}, {y})");
        }

        // SCC = 1 when src0 was selected.
        var comparison = function == "min" ? "<" : ">";
        _operands.SetScc($"({x} {comparison} {y})");
    }

    private void BitFieldExtract(string op, IReadOnlyList<Operand> a)
    {
        var sign = op.EndsWith("u32", StringComparison.Ordinal) ? "u" : "i";
        var value = Read(a, Source0);
        var offset = Read(a, Source1);
        Write(a, $"bfe_{sign}({value}, {offset} & 31, ({Read(a, Source1)} >> 16) & 0x7f)");
        SetSccNonZero(a);
    }

    private void BitFieldMask64(IReadOnlyList<Operand> a)
    {
        var name = _operands.SnapOne($"(((1ull << ({Read(a, Source0)} & 63)) - 1ull) << ({Read(a, Source1)} & 63))");
        var last = _state.Body.Count - 1;
        _state.Body[last] = _state.Body[last].Replace($"uint {name} =", $"uint64_t {name} =", StringComparison.Ordinal);
        _operands.Write(a[Destination], $"(uint){name}", Low);
        _operands.Write(a[Destination], $"(uint)({name} >> 32)", High);
    }

    private bool TryPack(string op, IReadOnlyList<Operand> a)
    {
        switch (op)
        {
            case "s_pack_ll_b32_b16":
                Write(a, $"({Read(a, Source0)} & 0xffff) | ({Read(a, Source1)} << 16)");
                break;
            case "s_pack_lh_b32_b16":
                Write(a, $"({Read(a, Source0)} & 0xffff) | ({Read(a, Source1)} & 0xffff0000)");
                break;
            case "s_pack_hh_b32_b16":
                Write(a, $"({Read(a, Source0)} >> 16) | ({Read(a, Source1)} & 0xffff0000)");
                break;
            case "s_mul_hi_u32":
                Write(a, $"mul_hi_u({Read(a, Source0)}, {Read(a, Source1)})");
                break;
            default:
                return false;
        }

        return true;
    }

    private string Read(IReadOnlyList<Operand> a, int position)
    {
        return _operands.Read(a[position]);
    }

    private void Write(IReadOnlyList<Operand> a, string value)
    {
        _operands.Write(a[Destination], value);
    }

    private void SetSccNonZero(IReadOnlyList<Operand> a)
    {
        _operands.SetScc($"({RegisterKey.Sgpr(a[Destination].Low).Name} != 0)");
    }

    [GeneratedRegex(@"^s_(lshl|lshr)_b64$")]
    private static partial Regex Shift64Pattern();

    [GeneratedRegex(@"^s_lshl([1-4])_add_u32$")]
    private static partial Regex ShiftAddPattern();
}
