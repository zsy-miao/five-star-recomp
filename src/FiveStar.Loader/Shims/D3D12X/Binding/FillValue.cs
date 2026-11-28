// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Shims.D3D12X.Binding;

/// <summary>
/// What a stand-in fill writes: a colour to float views (alpha 1), <paramref name="Packed"/> to uint views. With a
/// <paramref name="Share"/> above 0 it writes that share of the level measured on the GPU instead
/// (<see cref="UavFill.Measure"/>), at least <paramref name="Least"/> per channel, packed as R9G9B9E5 for uint views;
/// <paramref name="Tint"/> is how much of the level's colour it keeps (1 all of it, 0 a grey of its luminance).
/// <paramref name="Other"/> is what a replacement writes where a uint view holds <paramref name="Packed"/> (<see cref="UavFill.Replace"/>).
/// </summary>
internal readonly record struct FillValue(float Red, float Green, float Blue, uint Packed, float Share = 0, float Least = 0, float Tint = 1, uint Other = 0)
{
    private const int MantissaBits = 9;
    private const int ExponentBias = 15;
    private const int MaxExponent = 31;
    private const int GreenShift = 9;
    private const int BlueShift = 18;
    private const int ExponentShift = 27;
    private const uint MantissaMax = (1u << MantissaBits) - 1;

    /// <summary>A grey of <paramref name="level"/> for float views, packed as R9G9B9E5 for uint views (the screen-probe light).</summary>
    public static FillValue Shared(float level)
    {
        return new FillValue(level, level, level, SharedExponent(level, level, level));
    }

    /// <summary>The R9G9B9E5_SHAREDEXP bits of a colour (DXGI's encoding: one exponent, 9-bit mantissas).</summary>
    public static uint SharedExponent(float red, float green, float blue)
    {
        float largest = Math.Max(red, Math.Max(green, blue));

        if (largest <= 0)
        {
            return 0;
        }

        int exponent = Math.Clamp((int)Math.Floor(Math.Log2(largest)) + 1 + ExponentBias, 0, MaxExponent);
        double scale = Math.ScaleB(1.0, exponent - ExponentBias - MantissaBits);

        if (Math.Round(largest / scale) > MantissaMax && exponent < MaxExponent)
        {
            exponent++;
            scale = Math.ScaleB(scale, 1);
        }

        uint Mantissa(float value) => (uint)Math.Clamp(Math.Round(value / scale), 0, MantissaMax);

        return Mantissa(red) | (Mantissa(green) << GreenShift) | (Mantissa(blue) << BlueShift) | ((uint)exponent << ExponentShift);
    }
}
