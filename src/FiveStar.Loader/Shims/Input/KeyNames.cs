// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Frozen;
using System.Globalization;

namespace FiveStar.Loader.Shims.Input;

/// <summary>Key names of the input config and the Windows virtual-key codes they stand for (WinUser.h VK_*).</summary>
internal static class KeyNames
{
    public const string None = "none";

    public const int MouseLeft = 0x01;
    public const int MouseRight = 0x02;
    public const int MouseMiddle = 0x04;
    public const int MouseButton4 = 0x05;
    public const int MouseButton5 = 0x06;

    // VK_SHIFT, VK_CONTROL, VK_MENU: GetAsyncKeyState reports them besides the left and right keys.
    private const int Shift = 0x10;
    private const int Control = 0x11;
    private const int Alt = 0x12;

    /// <summary>
    /// Unassigned virtual keys (0x88-0x8F and 0x97-0x9E in WinUser.h) that stand for virtual gamepad buttons in an
    /// input script: PadA, PadB, PadX, PadY, PadMenu, PadView, the d-pad as PadUp, PadDown, PadLeft, PadRight, and the
    /// left stick pushed to an edge as PadLUp, PadLDown, PadLLeft, PadLRight. They press the button or push the stick
    /// alone, without a keyboard key, and no keyboard reading carries them.
    /// </summary>
    public const int FirstPadKey = 0x88;
    public const int LastPadKey = 0x8F;
    public const int FirstPadArrow = 0x97;
    public const int LastPadArrow = 0x9A;
    public const int PadStickUp = 0x9B;
    public const int PadStickDown = 0x9C;
    public const int PadStickLeft = 0x9D;
    public const int PadStickRight = 0x9E;

    private const int FirstFunctionKey = 0x70;
    private const int FunctionKeyCount = 24;
    private const string HexPrefix = "0x";

    private static readonly (string Name, int Key)[] Listed =
    [
        ("MouseLeft", MouseLeft), ("MouseRight", MouseRight), ("MouseMiddle", MouseMiddle), ("Mouse4", MouseButton4),
        ("Mouse5", MouseButton5), ("Backspace", 0x08), ("Tab", 0x09), ("Enter", 0x0D), ("CapsLock", 0x14),
        ("Escape", 0x1B), ("Space", 0x20), ("PageUp", 0x21), ("PageDown", 0x22), ("End", 0x23), ("Home", 0x24),
        ("Left", 0x25), ("Up", 0x26), ("Right", 0x27), ("Down", 0x28), ("Insert", 0x2D), ("Delete", 0x2E),
        ("ScrollLock", 0x91), ("LeftShift", 0xA0), ("RightShift", 0xA1), ("LeftCtrl", 0xA2), ("RightCtrl", 0xA3),
        ("LeftAlt", 0xA4), ("RightAlt", 0xA5),
        ("PadA", FirstPadKey), ("PadB", FirstPadKey + 1), ("PadX", FirstPadKey + 2), ("PadY", FirstPadKey + 3),
        ("PadMenu", FirstPadKey + 4), ("PadView", FirstPadKey + 5),
        ("PadUp", FirstPadArrow), ("PadDown", FirstPadArrow + 1), ("PadLeft", FirstPadArrow + 2), ("PadRight", LastPadArrow),
        ("PadLUp", PadStickUp), ("PadLDown", PadStickDown), ("PadLLeft", PadStickLeft), ("PadLRight", PadStickRight),
    ];

    /// <summary>The virtual gamepad button each pad key presses, from <see cref="FirstPadKey"/> on.</summary>
    public static readonly uint[] PadButtons =
    [
        GameInputLayout.GamepadA, GameInputLayout.GamepadB, GameInputLayout.GamepadX, GameInputLayout.GamepadY,
        GameInputLayout.GamepadMenu, GameInputLayout.GamepadView,
    ];

    /// <summary>The d-pad button each pad arrow presses, from <see cref="FirstPadArrow"/> on.</summary>
    public static readonly uint[] PadArrows =
    [
        GameInputLayout.GamepadDPadUp, GameInputLayout.GamepadDPadDown, GameInputLayout.GamepadDPadLeft, GameInputLayout.GamepadDPadRight,
    ];

    private static readonly FrozenDictionary<string, int> Names = BuildNames();

    /// <summary>The virtual key of a config name: a listed name, a letter, a digit, F1..F24 or a 0x-prefixed code; null for "none".</summary>
    public static int? Parse(string name)
    {
        if (name.Equals(None, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (Names.TryGetValue(name, out int key))
        {
            return key;
        }

        bool hex = name.StartsWith(HexPrefix, StringComparison.OrdinalIgnoreCase);

        if (hex && int.TryParse(name.AsSpan(HexPrefix.Length), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code) && code > 0 && code < InputLimits.VirtualKeyCount)
        {
            return code;
        }

        throw new FormatException($"unknown key name '{name}'");
    }

    /// <summary>Whether a virtual key is an input script's pad key (<see cref="FirstPadKey"/>, <see cref="FirstPadArrow"/>, <see cref="PadStickUp"/>).</summary>
    [HotPath]
    public static bool IsPadKey(int virtualKey)
    {
        if (virtualKey >= FirstPadArrow && virtualKey <= PadStickRight)
        {
            return true;
        }

        return virtualKey >= FirstPadKey && virtualKey <= LastPadKey;
    }

    /// <summary>Whether a virtual key belongs in a keyboard reading: not a mouse button and not a side-less modifier.</summary>
    [HotPath]
    public static bool IsKeyboardKey(int virtualKey)
    {
        if (virtualKey <= MouseButton5 || IsPadKey(virtualKey))
        {
            return false;
        }

        return virtualKey != Shift && virtualKey != Control && virtualKey != Alt;
    }

    private static FrozenDictionary<string, int> BuildNames()
    {
        var names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, key) in Listed)
        {
            names[name] = key;
        }

        // VK_0..VK_9 and VK_A..VK_Z equal their ASCII characters.
        for (char c = '0'; c <= '9'; c++)
        {
            names[c.ToString()] = c;
        }

        for (char c = 'A'; c <= 'Z'; c++)
        {
            names[c.ToString()] = c;
        }

        for (int i = 0; i < FunctionKeyCount; i++)
        {
            names[$"F{i + 1}"] = FirstFunctionKey + i;
        }

        return names.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }
}
