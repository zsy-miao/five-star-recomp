// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace FiveStar.Shaders.Translation.Waves;

/// <summary>
/// The <see cref="ValueFact"/> of a translated expression. Any expression is exact when everything it reads is and it
/// calls no function of the hardware wave; a few shapes keep exact bits: shifts and masks by constants, the mesh
/// thread id (<c>in_gid.x * N + in_gi</c>, whose bits from N up are the group's), and <c>readfirstlane</c>, whose
/// result is the console's in the bits that are exact and equal on every active lane, provided the hardware wave holds
/// an active lane.
/// </summary>
internal static partial class ValueRules
{
    private const int ShiftMask = 31;

    public static ValueFact Evaluate(string value, WindowState state, int threads)
    {
        var text = value.Trim();
        var first = FirstLanePattern().Match(text);

        if (first.Success && state.FastWaves)
        {
            return ValueFact.Scalar;
        }

        if (first.Success)
        {
            return FirstLane(state.Get(first.Groups["x"].Value), state.Get(WindowNames.Active));
        }

        var literal = Literal(text);

        if (literal is not null)
        {
            return ValueFact.Scalar with { Constant = literal };
        }

        var lanes = LaneMask(text, state);

        if (lanes is not null)
        {
            return lanes.Value;
        }

        if (WindowNames.IsMask(text) || NamePattern().IsMatch(text))
        {
            return state.Get(text);
        }

        var thread = ThreadPattern().Match(text);

        if (thread.Success)
        {
            return ThreadId(int.Parse(thread.Groups["n"].Value, CultureInfo.InvariantCulture), threads);
        }

        var shift = ShiftPattern().Match(text);

        if (shift.Success && Constant(shift.Groups["b"].Value, state) is not null)
        {
            return Shift(state.Get(shift.Groups["a"].Value), shift.Groups["op"].Value, (int)(Constant(shift.Groups["b"].Value, state)!.Value & ShiftMask));
        }

        var bitwise = BitwisePattern().Match(text);

        if (bitwise.Success)
        {
            return Bitwise(bitwise.Groups["a"].Value, bitwise.Groups["op"].Value, bitwise.Groups["b"].Value, state);
        }

        return Generic(text, state);
    }

    /// <summary>Exact when everything read is exact and no function depends on the hardware wave.</summary>
    public static bool IsExact(IEnumerable<string> names, IEnumerable<string> calls, WindowState state)
    {
        return !calls.Any(call => IsWindowCall(call, state)) && names.All(name => state.Get(name).IsExact);
    }

    /// <summary>A call whose result depends on the hardware wave; with fast waves not <c>readfirstlane</c> (the window's first lane is taken for the console's).</summary>
    public static bool IsWindowCall(string call, WindowState state)
    {
        return WindowNames.IsWindowFunction(call) && !(state.FastWaves && call == "readfirstlane");
    }

    /// <summary>The fact of a branch or select condition: a whole expression, or <c>[!]vw_any(mask)</c>.</summary>
    public static ValueFact Condition(string condition, WindowState state)
    {
        var text = condition.Trim();

        return LaneMask(text, state) ?? Generic(text, state);
    }

    /// <summary>
    /// The lane-mask shapes: <c>[!]vw_any(mask)</c>, a ballot half <c>vw_ballot(mask).x</c> (each window lane's own bit
    /// is the console's when the mask is) and <c>lane_bit(lo, hi)</c> of such integers (this lane's bit); else null.
    /// </summary>
    private static ValueFact? LaneMask(string text, WindowState state)
    {
        var any = AnyPattern().Match(text);

        if (any.Success)
        {
            return Any(any.Groups["mask"].Value, state);
        }

        var ballot = BallotPattern().Match(text);

        if (ballot.Success)
        {
            var ownBits = state.Get(ballot.Groups["mask"].Value).IsExact;

            return ValueFact.Unknown with { Uniform = ValueFact.AllBits, AllUniform = ValueFact.AllBits, OwnBits = ownBits };
        }

        var bit = LaneBitPattern().Match(text);

        if (bit.Success && Operand(bit.Groups["lo"].Value, state).HasOwnBits && Operand(bit.Groups["hi"].Value, state).HasOwnBits)
        {
            return ValueFact.ExactValue;
        }

        return null;
    }

    /// <summary>
    /// <c>vw_any(mask)</c> of a mask that is active and a uniform value: on a window holding an active lane both the
    /// window and the console answer the uniform value.
    /// </summary>
    private static ValueFact Any(string mask, WindowState state)
    {
        var active = state.Get(WindowNames.Active);
        var fact = state.Get(mask);
        var activeAnd = mask == WindowNames.Active || fact.Kind == MaskKind.ActiveAnd;

        if (fact.Kind == MaskKind.Uniform && mask != WindowNames.Active)
        {
            // The same on every lane, and a window holds at least one lane.
            return ValueFact.Scalar with { Exact = fact.Exact, Held = fact.Held };
        }

        if (!active.IsExact || !fact.IsHeld || !activeAnd)
        {
            return ValueFact.Scalar with { Exact = 0, Held = 0 };
        }

        return ValueFact.Scalar with { Exact = Bits(active.NonEmpty && fact.IsExact) };
    }

    private static ValueFact Generic(string text, WindowState state)
    {
        var names = WindowStatement.Names(text);
        var calls = WindowStatement.Functions(text);
        var window = calls.Any(call => IsWindowCall(call, state));
        var varies = calls.Any(WindowNames.IsLaneFunction) || names.Any(WindowNames.IsLaneInput);
        var exact = Bits(IsExact(names, calls, state));
        var held = Bits(!window && names.All(name => state.Get(name).IsHeld));

        var uniform = Bits(!window && !varies && names.All(name => state.Get(name).IsActiveUniform));
        var allUniform = Bits(!window && !varies && names.All(name => state.Get(name).AllUniform == ValueFact.AllBits));

        return new ValueFact(exact, held, uniform, allUniform, MaskKind.Other, false, null);
    }

    /// <summary>
    /// readfirstlane: the first active lane's value, on a window holding an active lane the console's in the bits that
    /// are equal on all lanes, and when the window's active lanes are the console's, in the bits equal on the active
    /// lanes.
    /// </summary>
    private static ValueFact FirstLane(ValueFact source, ValueFact active)
    {
        // The prelude's readfirstlane gives 0 on a window without an active lane that does not hold console lane 0.
        var exact = 0u;
        var held = source.Held & source.AllUniform;

        if (active.NonEmpty)
        {
            exact = source.Exact & source.AllUniform;
        }

        if (active.IsExact)
        {
            held |= source.Held & source.Uniform;

            if (active.NonEmpty)
            {
                exact |= source.Exact & source.Uniform;
            }
        }

        return ValueFact.Scalar with { Exact = exact, Held = held };
    }

    /// <summary><c>in_gid.x * n + in_gi</c>: the bits from n up hold the group id when n is a power of two of at least the group size.</summary>
    private static ValueFact ThreadId(int multiplier, int threads)
    {
        var uniform = 0u;

        if (BitOperations.IsPow2(multiplier) && multiplier >= threads)
        {
            uniform = ~(uint)(multiplier - 1);
        }

        return ValueFact.ExactValue with { Uniform = uniform, AllUniform = uniform };
    }

    private static ValueFact Shift(ValueFact source, string operation, int amount)
    {
        if (operation == ">>")
        {
            var filled = ~(ValueFact.AllBits >> amount);

            return new ValueFact(
                (source.Exact >> amount) | filled, (source.Held >> amount) | filled, (source.Uniform >> amount) | filled,
                (source.AllUniform >> amount) | filled, MaskKind.Other, false, null);
        }

        var low = (uint)((1UL << amount) - 1);

        return new ValueFact(
            (source.Exact << amount) | low, (source.Held << amount) | low, (source.Uniform << amount) | low,
            (source.AllUniform << amount) | low, MaskKind.Other, false, null);
    }

    /// <summary><c>a &amp; b</c> and <c>a | b</c>: a bit an operand constant fixes is exact and uniform.</summary>
    private static ValueFact Bitwise(string left, string operation, string right, WindowState state)
    {
        var first = Operand(left, state);
        var second = Operand(right, state);
        var fixedBits = Fixed(Constant(left, state), operation) | Fixed(Constant(right, state), operation);

        var exact = (first.Exact & second.Exact) | fixedBits;
        var held = (first.Held & second.Held) | fixedBits;
        var uniform = (first.Uniform & second.Uniform) | fixedBits;
        var allUniform = (first.AllUniform & second.AllUniform) | fixedBits;
        var ownBits = first.HasOwnBits && second.HasOwnBits;

        return new ValueFact(exact, held, uniform, allUniform, MaskKind.Other, false, null) { OwnBits = ownBits };
    }

    /// <summary>The bits a constant operand decides: its zeros for and, its ones for or (none for xor).</summary>
    private static uint Fixed(long? constant, string operation)
    {
        if (constant is null || operation == "^")
        {
            return 0;
        }

        var bits = (uint)constant.Value;

        if (operation == "&")
        {
            return ~bits;
        }

        return bits;
    }

    private static ValueFact Operand(string text, WindowState state)
    {
        if (Literal(text) is not null)
        {
            return ValueFact.Scalar;
        }

        return state.Get(text);
    }

    private static long? Constant(string text, WindowState state)
    {
        var literal = Literal(text);

        if (literal is not null)
        {
            return literal;
        }

        return state.Get(text).Constant;
    }

    private static long? Literal(string text)
    {
        var match = LiteralPattern().Match(text);

        if (!match.Success)
        {
            return null;
        }

        if (match.Groups["hex"].Success)
        {
            return long.Parse(match.Groups["hex"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        return long.Parse(match.Groups["dec"].Value, CultureInfo.InvariantCulture);
    }

    private static uint Bits(bool all)
    {
        if (all)
        {
            return ValueFact.AllBits;
        }

        return 0;
    }

    [GeneratedRegex(@"^!?vw_any\((?<mask>[A-Za-z_]\w*)\)$")]
    private static partial Regex AnyPattern();

    [GeneratedRegex(@"^readfirstlane\((?<x>[A-Za-z_]\w*), active\)$")]
    private static partial Regex FirstLanePattern();

    [GeneratedRegex(@"^(?:0x(?<hex>[0-9a-f]{1,8})u|(?<dec>\d{1,9})u?)$")]
    private static partial Regex LiteralPattern();

    [GeneratedRegex(@"^[A-Za-z_]\w*$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^\(in_gid\.x \* (?<n>\d{1,6})u \+ in_gi\)$")]
    private static partial Regex ThreadPattern();

    [GeneratedRegex(@"^(?<a>[A-Za-z_]\w*) (?<op>>>|<<) \((?<b>[A-Za-z_]\w*|0x[0-9a-f]{1,8}u) & 31\)$")]
    private static partial Regex ShiftPattern();

    [GeneratedRegex(@"^(?<a>[A-Za-z_]\w*|0x[0-9a-f]{1,8}u) (?<op>[&|^]) (?<b>[A-Za-z_]\w*|0x[0-9a-f]{1,8}u)$")]
    public static partial Regex BitwisePattern();

    [GeneratedRegex(@"^vw_ballot\((?<mask>[A-Za-z_]\w*)\)\.[xy]$")]
    public static partial Regex BallotPattern();

    [GeneratedRegex(@"^lane_bit\((?<lo>[A-Za-z_]\w*|0x[0-9a-f]{1,8}u|\d{1,9}u), (?<hi>[A-Za-z_]\w*|0x[0-9a-f]{1,8}u|\d{1,9}u)\)$")]
    public static partial Regex LaneBitPattern();
}
