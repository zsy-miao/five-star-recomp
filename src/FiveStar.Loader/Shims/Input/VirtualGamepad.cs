// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Shims.Input;

/// <summary>
/// Turns held keys and mouse motion into a gamepad state: keys to buttons and full triggers, four keys to the left
/// stick (diagonals on the unit circle), mouse speed to the right stick, clamped to the unit circle and then shaped by
/// the curve exponent.
/// </summary>
internal static class VirtualGamepad
{
    /// <summary>Mouse speed in pixels per second that deflects the right stick fully at sensitivity 1.</summary>
    public const float ReferenceSpeed = 1000f;

    private const float Full = 1f;

    private static readonly float Diagonal = MathF.Sqrt(0.5f);

    [HotPath]
    public static GamepadState Map(InputConfig config, PressedKeys keys, float mouseDeltaX, float mouseDeltaY, float seconds)
    {
        var state = default(GamepadState);
        state.Buttons = Buttons(config.Buttons, keys);
        state.LeftTrigger = Pressure(keys.IsDown(config.LeftTrigger));
        state.RightTrigger = Pressure(keys.IsDown(config.RightTrigger));
        (state.LeftX, state.LeftY) = Stick(config.LeftStick, keys);
        (state.RightX, state.RightY) = Mouse(config, mouseDeltaX, mouseDeltaY, seconds);

        return state;
    }

    /// <summary>Mouse motion over <paramref name="seconds"/> as a stick position; screen Y grows down, stick Y up.</summary>
    [HotPath]
    public static (float X, float Y) Mouse(InputConfig config, float deltaX, float deltaY, float seconds)
    {
        if (seconds <= 0)
        {
            return (0, 0);
        }

        float scale = config.MouseSensitivity / (ReferenceSpeed * seconds);
        float x = deltaX * scale;
        float y = -deltaY * scale;
        float magnitude = MathF.Sqrt((x * x) + (y * y));

        if (magnitude == 0)
        {
            return (0, 0);
        }

        float shaped = MathF.Pow(Math.Clamp(magnitude, 0, Full), config.MouseCurve);
        float factor = shaped / magnitude;

        return (x * factor, y * factor);
    }

    [HotPath]
    private static uint Buttons(IReadOnlyList<ButtonBinding> bindings, PressedKeys keys)
    {
        uint buttons = 0;

        for (int i = 0; i < bindings.Count; i++)
        {
            if (keys.IsDown(bindings[i].Key))
            {
                buttons |= bindings[i].Button;
            }
        }

        // An input script's pad keys (PadA and the rest) press their button whatever the bindings say.
        for (int i = 0; i < KeyNames.PadButtons.Length; i++)
        {
            if (keys.IsDown(KeyNames.FirstPadKey + i))
            {
                buttons |= KeyNames.PadButtons[i];
            }
        }

        for (int i = 0; i < KeyNames.PadArrows.Length; i++)
        {
            if (keys.IsDown(KeyNames.FirstPadArrow + i))
            {
                buttons |= KeyNames.PadArrows[i];
            }
        }

        return buttons;
    }

    [HotPath]
    private static (float X, float Y) Stick(StickKeys stick, PressedKeys keys)
    {
        // An input script's stick keys (PadLUp and the rest) push the left stick whatever the bindings say.
        float x = Pressure(keys.IsDown(stick.Right) || keys.IsDown(KeyNames.PadStickRight)) - Pressure(keys.IsDown(stick.Left) || keys.IsDown(KeyNames.PadStickLeft));
        float y = Pressure(keys.IsDown(stick.Up) || keys.IsDown(KeyNames.PadStickUp)) - Pressure(keys.IsDown(stick.Down) || keys.IsDown(KeyNames.PadStickDown));

        if (x != 0 && y != 0)
        {
            return (x * Diagonal, y * Diagonal);
        }

        return (x, y);
    }

    [HotPath]
    private static float Pressure(bool down)
    {
        float value = down ? Full : 0;

        return value;
    }
}
