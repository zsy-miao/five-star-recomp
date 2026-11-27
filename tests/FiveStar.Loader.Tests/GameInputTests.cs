// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using FiveStar.Loader.Diagnostics;
using FiveStar.Loader.Shims.Input;
using FiveStar.Loader.Tests.Support;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>The GameInput replacement: the keyboard-and-mouse gamepad mapping and the native interfaces.</summary>
public sealed unsafe class GameInputTests
{
    private const float Tolerance = 1e-5f;

    private static nint _connected;

    [Fact]
    public void Wasd_drive_the_left_stick_on_the_unit_circle()
    {
        var config = InputConfigFile.Defaults();
        var keys = new PressedKeys();
        keys.Set('W', true);
        keys.Set('D', true);
        keys.Set('C', true);

        var diagonal = VirtualGamepad.Map(config, keys, 0, 0, 0.016f);
        keys.Set('D', false);
        var forward = VirtualGamepad.Map(config, keys, 0, 0, 0.016f);

        Assert.Equal(MathF.Sqrt(0.5f), diagonal.LeftX, Tolerance);
        Assert.Equal(MathF.Sqrt(0.5f), diagonal.LeftY, Tolerance);
        Assert.Equal(GameInputLayout.GamepadB, diagonal.Buttons);
        Assert.Equal(0f, forward.LeftX);
        Assert.Equal(1f, forward.LeftY);
    }

    [Fact]
    public void A_script_pad_key_presses_its_button_without_a_keyboard_key()
    {
        var config = InputConfigFile.Defaults();
        var keys = new PressedKeys();
        int padA = KeyNames.Parse("PadA")!.Value;
        keys.Set(padA, true);

        var state = VirtualGamepad.Map(config, keys, 0, 0, 0.016f);

        Assert.Equal(0x4u, state.Buttons);
        Assert.False(KeyNames.IsKeyboardKey(padA));

        // The d-pad too: the default mapping gives it no key.
        int padDown = KeyNames.Parse("PadDown")!.Value;
        keys.Set(padA, false);
        keys.Set(padDown, true);

        Assert.Equal(GameInputLayout.GamepadDPadDown, VirtualGamepad.Map(config, keys, 0, 0, 0.016f).Buttons);
        Assert.True(KeyNames.IsPadKey(padDown) && !KeyNames.IsKeyboardKey(padDown));

        // And the left stick: PadLUp pushes it forward.
        int stickUp = KeyNames.Parse("PadLUp")!.Value;
        keys.Set(padDown, false);
        keys.Set(stickUp, true);

        Assert.Equal(1f, VirtualGamepad.Map(config, keys, 0, 0, 0.016f).LeftY);
        Assert.True(KeyNames.IsPadKey(stickUp) && !KeyNames.IsKeyboardKey(stickUp));
    }

    [Fact]
    public void Mouse_speed_moves_the_right_stick_and_saturates()
    {
        var config = InputConfigFile.Parse(["mouse-sensitivity 2", "mouse-curve 1"]);

        // 2 x 250 px/s / 1000 px/s = half; 2 x 5000 px/s clamps to the edge; screen down is stick down.
        var half = VirtualGamepad.Mouse(config, 25, 0, 0.1f);
        var fast = VirtualGamepad.Mouse(config, 300, 400, 0.1f);
        var curved = VirtualGamepad.Mouse(config with { MouseCurve = 2 }, 25, 0, 0.1f);

        Assert.Equal(0.5f, half.X, Tolerance);
        Assert.Equal(0.6f, fast.X, Tolerance);
        Assert.Equal(-0.8f, fast.Y, Tolerance);
        Assert.Equal(0.25f, curved.X, Tolerance);
    }

    [Fact]
    public void GetCurrentReading_through_the_vtable_returns_the_source_state()
    {
        using var test = TestRuntime.Create();
        var source = new FakeInputSource { Gamepad = new GamepadState { Buttons = 0x4, LeftTrigger = 0.25f, LeftX = -0.5f, RightY = 1f } };
        var config = InputConfigFile.Defaults() with { VirtualGamepad = false };
        var log = new LoaderLog(TextWriter.Null, null, false);
        using var service = new GameInputService(source, source, config, TimeProvider.System, test.Runtime.Stubs, test.Runtime.CodeHeap, log);

        nint input = service.Create();
        nint* inputTable = *(nint**)input;
        nint reading = 0;
        int hr = ((delegate* unmanaged<nint, uint, nint, nint*, int>)inputTable[4])(input, 0x40000, 0, &reading);
        nint* readingTable = *(nint**)reading;
        GamepadState state;

        byte hasGamepad = ((delegate* unmanaged<nint, GamepadState*, byte>)readingTable[22])(reading, &state);
        MouseState mouse;
        byte hasMouse = ((delegate* unmanaged<nint, MouseState*, byte>)readingTable[16])(reading, &mouse);

        nint device = 0;
        ((delegate* unmanaged<nint, nint*, void>)readingTable[6])(reading, &device);
        nint info = ((delegate* unmanaged<nint, nint>)(*(nint**)device)[3])(device);
        uint released = ((delegate* unmanaged<nint, uint>)readingTable[2])(reading);
        int unimplemented = ((delegate* unmanaged<nint, nint*, int>)inputTable[14])(input, &reading);

        Assert.Equal(0, hr);
        Assert.Equal(1, hasGamepad);
        Assert.Equal(source.Gamepad, state);
        Assert.Equal(0, hasMouse);

        Assert.Equal(0x40000u, *(uint*)(info + 104));
        Assert.Equal(1u, released);
        Assert.Equal(unchecked((int)0x80004001), unimplemented);
    }

    /// <summary>
    /// The IGameInput calls of game_scarlett_master.exe, by its slots: RegisterDeviceCallback (9) for keyboards with
    /// blocking enumeration, GetDeviceInfo (device 3) for the id at +0x20, FindDeviceFromId (16), GetCurrentReading (4),
    /// GetKeyCount (14) and GetKeyState (15), GetPreviousReading (6) and GetGamepadState (22), UnregisterCallback (13).
    /// </summary>
    [Fact]
    public void The_game_slots_reach_keyboard_and_gamepad_state()
    {
        using var test = TestRuntime.Create();
        var source = new FakeInputSource { Gamepad = new GamepadState { Buttons = 0x4 }, HeldKeys = ['W'] };
        var config = InputConfigFile.Defaults() with { VirtualGamepad = false };
        using var service = new GameInputService(source, source, config, TimeProvider.System, test.Runtime.Stubs, test.Runtime.CodeHeap, new LoaderLog(TextWriter.Null, null, false));
        nint input = service.Create();
        nint* table = *(nint**)input;

        _connected = 0;
        ulong token = 0;
        int registered = ((delegate* unmanaged<nint, nint, uint, uint, uint, nint, nint, ulong*, int>)table[9])(input, 0, 0x10, 1, 2, 0, (nint)(delegate* unmanaged<ulong, nint, nint, ulong, uint, uint, void>)&OnDevice, &token);
        nint keyboard = _connected;

        nint info = ((delegate* unmanaged<nint, nint>)(*(nint**)keyboard)[3])(keyboard);
        nint found = 0;
        int lookup = ((delegate* unmanaged<nint, nint, nint*, int>)table[16])(input, info + 0x20, &found);

        nint keys = 0;
        ((delegate* unmanaged<nint, uint, nint, nint*, int>)table[4])(input, 0x10, keyboard, &keys);
        nint* keyTable = *(nint**)keys;
        uint keyCount = ((delegate* unmanaged<nint, uint>)keyTable[14])(keys);
        KeyState key;
        ((delegate* unmanaged<nint, uint, KeyState*, uint>)keyTable[15])(keys, 1, &key);

        nint first = 0;
        nint previous = 0;
        ((delegate* unmanaged<nint, uint, nint, nint*, int>)table[4])(input, 0x40000, 0, &first);
        source.Gamepad = new GamepadState { Buttons = 0x8 };

        Thread.Sleep(TimeSpan.FromMilliseconds(5));
        nint latest = 0;
        ((delegate* unmanaged<nint, uint, nint, nint*, int>)table[4])(input, 0x40000, 0, &latest);
        int hrPrevious = ((delegate* unmanaged<nint, nint, uint, nint, nint*, int>)table[6])(input, latest, 0x40000, 0, &previous);

        GamepadState earlier;
        ((delegate* unmanaged<nint, GamepadState*, byte>)(*(nint**)previous)[22])(previous, &earlier);
        byte unregistered = ((delegate* unmanaged<nint, ulong, ulong, byte>)table[13])(input, token, 5000);

        Assert.Equal(0, registered);
        Assert.Equal(0, lookup);
        Assert.Equal(keyboard, found);
        Assert.Equal(1u, keyCount);

        Assert.Equal((byte)'W', key.VirtualKey);
        Assert.Equal(0, hrPrevious);
        Assert.Equal(0x4u, earlier.Buttons);
        Assert.Equal(1, unregistered);
    }

    /// <summary>GameInputDeviceCallback: keeps the last device reported connected.</summary>
    [UnmanagedCallersOnly]
    private static void OnDevice(ulong token, nint context, nint device, ulong timestamp, uint current, uint previous)
    {
        if ((current & 1) != 0)
        {
            _connected = device;
        }
    }
}
