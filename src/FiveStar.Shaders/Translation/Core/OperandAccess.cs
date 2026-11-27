// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text.RegularExpressions;

namespace FiveStar.Shaders.Translation.Core;

/// <summary>
/// Reads and writes operands as HLSL. Registers are uint variables; EXEC is the per-lane bool <c>active</c>; SGPRs that
/// hold lane masks are per-lane bools <c>m_sN</c>. VALU and VMEM results are written only on active lanes.
/// </summary>
internal sealed partial class OperandAccess
{
    private const int LowDword = 0;
    private const int HighDword = 1;
    private const string Halves = "xy";

    private readonly TranslationState _state;
    private readonly ImplicitInputs _inputs;

    public OperandAccess(TranslationState state, ImplicitInputs inputs)
    {
        _state = state;
        _inputs = inputs;
    }

    /// <summary>The uint value of one dword of an operand.</summary>
    public string Read(Operand operand, int component = 0)
    {
        if (operand.Kind == OperandKind.Immediate)
        {
            return Hlsl.HexUint(operand.Value);
        }

        if (operand.Kind == OperandKind.Expression)
        {
            return operand.Expression;
        }

        var key = operand.At(component);

        if (operand.Kind == OperandKind.Scalar && key == SpecialRegisters.Exec)
        {
            // EXEC lives in `active`; s_exec is only refreshed by integer writes, so rebuild the bits from the lanes.
            return SnapOne($"{WaveOps.Ballot}(active).{Halves[LowDword]}");
        }

        if (operand.Kind == OperandKind.Scalar && _state.Masks.Contains(operand.At(0)) && !_state.Coherent.Contains(operand.At(0)))
        {
            // A lane mask used as an integer: rebuild it with a ballot in program lane numbers. Ballots are snapped into
            // temporaries so they never run under a per-lane condition (HLSL 2021 short-circuits ?:, && and ||).
            _state.Forms.Read(operand.At(0), mask: true);
            var half = component <= HighDword ? Halves[component] : Halves[LowDword];

            return SnapOne($"{WaveOps.Ballot}({Mask(operand)}).{half}");
        }

        if (operand.Kind == OperandKind.Scalar && key.Index == SpecialRegisters.ExecHi && _state.IsWave64)
        {
            // exec_hi named on its own: the high dword of the lanes.
            return SnapOne($"{WaveOps.Ballot}(active).y");
        }

        // The high dword of a wave64 lane mask (s11 after v_cmp_e64 s[10:11]) is its integer variable: every mask write
        // sets it (ShadowInteger), so a read sees whichever write ran last on the path taken; the mask tracking is by
        // emission order, which on another path left s43 an integer the culling program adds to (s_add_i32 s32, s43, s32).

        if (key.Kind == RegisterKey.Scalar)
        {
            _state.IntegerUses.Add(key);
            _state.Forms.Read(key, mask: false);
        }

        if (!_state.Written.Contains(key))
        {
            _inputs.Load(key);
        }

        _state.Variables.Add(key);

        return key.Name;
    }

    /// <summary>The float value of an operand with its neg/abs modifiers.</summary>
    public string ReadFloat(Operand operand, int component = 0)
    {
        var value = $"asfloat({Read(operand, component)})";

        return ApplyModifiers(operand, value);
    }

    /// <summary>The int value of an operand; integer operands take no modifiers.</summary>
    public string ReadInt(Operand operand, int component = 0)
    {
        if (operand.Negate || operand.Absolute)
        {
            throw new UnsupportedProgramException("int modifier");
        }

        return $"asint({Read(operand, component)})";
    }

    /// <summary>One f16 half of an operand as float, with neg/abs; an inline float constant is its f16 value, in the low half.</summary>
    public string ReadHalf(Operand operand, bool high)
    {
        var value = Read(operand);
        var constant = OperandParser.HalfConstant(operand);

        if (constant is not null)
        {
            value = Hlsl.HexUint(constant.Value);
        }

        var half = high ? $"f16tof32({value} >> 16)" : $"f16tof32({value})";

        return ApplyModifiers(operand, half);
    }

    /// <summary>Applies the neg/abs input modifiers of an operand to a float expression.</summary>
    public static string ApplyModifiers(Operand operand, string value)
    {
        if (operand.Absolute)
        {
            value = $"abs({value})";
        }

        if (operand.Negate)
        {
            value = $"(-{value})";
        }

        return value;
    }

    /// <summary>Writes one dword of a destination. Writes to null are dropped.</summary>
    public void Write(Operand destination, string value, int component = 0)
    {
        if (destination.Kind == OperandKind.Immediate)
        {
            return;
        }

        var key = destination.At(component);
        _state.Touch(key);
        TrackConstant(key, value);

        if (key.Kind == RegisterKey.Scalar && (key.Index == SpecialRegisters.ExecLo || key.Index == SpecialRegisters.ExecHi))
        {
            // EXEC stays a mask: `active` is refreshed from the bits, and reads of EXEC as an integer use a ballot anyway.
            WriteExecInteger(key, value);

            return;
        }

        _state.MaskLanes.Remove(key);

        if (key.Kind == RegisterKey.Scalar)
        {
            _state.Emit($"  {key.Name} = {value};");
            WroteInteger(key);

            return;
        }

        WriteVector(key, value);
    }

    /// <summary>After an integer write of an SGPR: it no longer holds a mask.</summary>
    public void WroteInteger(RegisterKey key)
    {
        // An integer write of either dword ends a wave64 mask in the pair below it.
        _state.Forms.Write(key, mask: false);

        if (_state.IsWave64)
        {
            _state.Forms.Write(key.Plus(-1), mask: false);
        }

        _state.Masks.Remove(key);
        _state.MaskHighHalves.Remove(key);
        _state.IntegerUses.Add(key);
    }

    /// <summary>Writes the low or high 16 bits of a destination, keeping the other half.</summary>
    public void WriteHalf(Operand destination, string value16, bool high)
    {
        var current = "0u";

        if (_state.Written.Contains(destination.At(0)))
        {
            current = Read(destination);
        }

        if (high)
        {
            Write(destination, $"(({current}) & 0xffffu) | (({value16}) << 16)");
        }
        else
        {
            Write(destination, $"(({current}) & 0xffff0000u) | (({value16}) & 0xffffu)");
        }
    }

    /// <summary>Writes a lane mask (per-lane bool). Writing EXEC sets <c>active</c>.</summary>
    public void WriteMask(Operand destination, string condition)
    {
        if (destination.Kind == OperandKind.Immediate)
        {
            return;
        }

        var key = destination.At(0);

        if (key == SpecialRegisters.Exec)
        {
            _state.Emit($"  active = {condition};");

            return;
        }

        MarkMask(key);
        _state.Emit($"  {key.MaskName} = {condition};");
        ShadowInteger(key);
    }

    /// <summary>
    /// After a mask write: on wave64 the integer variable of its high dword gets the mask's high lanes, and the low
    /// dword's variable gets its low lanes when the register is an integer on another path
    /// (<see cref="TranslationState.Coherent"/>). Such a register is then read from its integer variables in either
    /// form (lane_bit for a mask read), so a read is right whichever path wrote it last.
    /// </summary>
    public void ShadowInteger(RegisterKey key)
    {
        if (key.Kind != RegisterKey.Scalar || key == SpecialRegisters.Exec)
        {
            return;
        }

        if (_state.Coherent.Contains(key))
        {
            var low = SnapOne($"{WaveOps.Ballot}({key.MaskName}).x");
            _state.Variables.Add(key);
            _state.Emit($"  {key.Name} = {low};");
        }

        if (!_state.IsWave64)
        {
            return;
        }

        var high = key.Plus(1);
        var bits = SnapOne($"{WaveOps.Ballot}({key.MaskName}).y");
        _state.Touch(high);
        _state.Variables.Add(high);
        _state.Emit($"  {high.Name} = {bits};");
    }

    /// <summary>Records that a register now holds a lane mask (and, on wave64, that the next one is its high half).</summary>
    public void MarkMask(RegisterKey key)
    {
        _state.Forms.Write(key, mask: true);
        _state.Touch(key);
        _state.Masks.Add(key);
        _state.MasksEver.Add(key);
        _state.Constants.Remove(key);

        if (_state.IsWave64)
        {
            _state.MaskHighHalves.Add(key.Plus(1));
            _state.Constants.Remove(key.Plus(1));
        }
    }

    /// <summary>True when an operand is an immediate or a register currently holding a lane mask.</summary>
    public bool IsMask(Operand operand)
    {
        if (operand.Kind == OperandKind.Immediate)
        {
            return true;
        }

        return HoldsMask(operand);
    }

    /// <summary>True when an operand is a register currently holding a lane mask.</summary>
    public bool HoldsMask(Operand operand)
    {
        var key = operand.Key;

        return key is not null && _state.Masks.Contains(key.Value);
    }

    /// <summary>This lane's bit of a mask operand as a bool expression.</summary>
    public string Mask(Operand operand)
    {
        var key = operand.Key;

        if (key is not null && key.Value == SpecialRegisters.Exec)
        {
            return "active";
        }

        if (key is not null && _state.Masks.Contains(key.Value) && !_state.Coherent.Contains(key.Value))
        {
            _state.Forms.Read(key.Value, mask: true);

            return key.Value.MaskName;
        }

        if (operand.Kind == OperandKind.Immediate)
        {
            var constant = operand.Value != 0 ? "true" : "false";

            return constant;
        }

        if (operand.Kind != OperandKind.Scalar)
        {
            throw new UnsupportedProgramException($"mask read of non-mask {operand.Text}");
        }

        // An integer SGPR pair used as a lane mask (loaded or computed with scalar ALU): test this lane's bit.
        var low = Read(OperandParser.Sgpr(operand.Low));
        var high = "0u";

        if (operand.High > operand.Low || (_state.IsWave64 && key is not null && _state.Coherent.Contains(key.Value)))
        {
            high = Read(OperandParser.Sgpr(operand.Low + 1));
        }

        return $"lane_bit({low}, {high})";
    }

    /// <summary>Copies values into fresh temporaries so multi-dword writes cannot clobber their own inputs.</summary>
    public string[] Snap(params string[] values)
    {
        var names = new string[values.Length];

        for (var index = 0; index < values.Length; index++)
        {
            var name = "_t" + _state.NextTemporary().ToString(CultureInfo.InvariantCulture);
            _state.Emit($"  uint {name} = {values[index]};");
            names[index] = name;
        }

        return names;
    }

    /// <summary>Copies one value into a fresh temporary.</summary>
    public string SnapOne(string value)
    {
        return Snap(value)[0];
    }

    /// <summary>Copies a uint2 value (a lane-mask ballot) into a fresh temporary.</summary>
    public string SnapPair(string value)
    {
        var name = "_t" + _state.NextTemporary().ToString(CultureInfo.InvariantCulture);
        _state.Emit($"  uint2 {name} = {value};");

        return name;
    }

    public void SetScc(string condition)
    {
        _state.Emit($"  scc = {condition};");
    }

    /// <summary>
    /// The SamplerDescriptorHeap index of an S#: a literal S# built from constants gets its static-sampler slot (for a
    /// sample, or with <paramref name="comparison"/> a depth compare), otherwise dword0 holds the heap index.
    /// </summary>
    public string SamplerIndex(Operand sampler, bool comparison)
    {
        const int SamplerDwords = 4;

        var words = new uint[SamplerDwords];

        for (var dword = 0; dword < SamplerDwords; dword++)
        {
            if (!_state.Constants.TryGetValue(sampler.At(dword), out var word))
            {
                return Read(sampler, 0);
            }

            words[dword] = (uint)word;
        }

        // FORCE_UNNORMALIZED (texel coordinates) and FORCE_DEGAMMA (sRGB decode) act in the shader, which no S# of the
        // corpus asks for (shadPS4 applies them there; facts only).
        if ((words[0] & StaticSampler.ShaderSideBits) != 0)
        {
            throw new UnsupportedProgramException($"literal S# with unnormalized coordinates or degamma: {words[0]:x8}");
        }

        var found = StaticSampler.Of(words, comparison);

        if (!_state.StaticSamplers.Any(existing => existing.SameAs(found)))
        {
            _state.StaticSamplers.Add(found);
        }

        return $"STATIC_SAMPLER_BASE + {found.Slot - StaticSampler.FirstSlot}u";
    }

    private void TrackConstant(RegisterKey key, string value)
    {
        var literal = HexLiteralPattern().Match(value);

        if (literal.Success)
        {
            _state.Constants[key] = long.Parse(literal.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

            return;
        }

        foreach (var (known, constant) in _state.Constants)
        {
            if (known.Name == value)
            {
                _state.Constants[key] = constant;

                return;
            }
        }

        _state.Constants.Remove(key);
    }

    private void WriteExecInteger(RegisterKey key, string value)
    {
        // An integer write to EXEC: refresh the per-lane flag from the mask bits. EXEC usually lives only in `active`,
        // so the half not written is taken from it first; s_exec and s_exech were stale for a 32-bit write before.
        var other = key.Index == SpecialRegisters.ExecLo ? RegisterKey.Sgpr(SpecialRegisters.ExecHi) : SpecialRegisters.Exec;
        var otherHalf = key.Index == SpecialRegisters.ExecLo ? Halves[HighDword] : Halves[LowDword];
        var current = SnapOne($"{WaveOps.Ballot}(active).{otherHalf}");
        _state.Emit($"  {other.Name} = {current};");

        _state.Emit($"  {key.Name} = {value};");
        _state.Variables.Add(SpecialRegisters.Exec);
        _state.Variables.Add(RegisterKey.Sgpr(SpecialRegisters.ExecHi));
        _state.Emit("  active = lane_bit(s_exec, s_exech);");
    }

    private void WriteVector(RegisterKey key, string value)
    {
        if (WaveIntrinsicPattern().IsMatch(value))
        {
            // Wave and quad intrinsics must run in uniform control flow: hoist them out of the active guard.
            value = SnapOne(value);
        }

        var keep = _state.DppKeep;

        if (keep is not null)
        {
            value = $"({keep} ? ({value}) : {key.Name})";
        }

        if (PureExpression.IsPure(value))
        {
            _state.Emit($"  {key.Name} = select(active, (uint)({value}), {key.Name});");

            return;
        }

        _state.Emit($"  if (active) {key.Name} = {value};");
    }

    [GeneratedRegex(@"^0x([0-9a-f]+)u$")]
    private static partial Regex HexLiteralPattern();

    [GeneratedRegex(@"\b(Wave|Quad|vw_)\w*\(|quad_perm\(|readfirstlane\(")]
    private static partial Regex WaveIntrinsicPattern();
}
