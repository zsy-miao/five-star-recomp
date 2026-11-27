// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace FiveStar.Loader.Shims.D3D12X.Present;

/// <summary>The user32 and kernel32 calls presenting makes: finding the game's window and waiting precisely.</summary>
internal static unsafe class PresentNative
{
    /// <summary>INFINITE (winbase.h).</summary>
    public const uint Infinite = 0xFFFFFFFF;

    /// <summary>CREATE_WAITABLE_TIMER_HIGH_RESOLUTION (synchapi.h).</summary>
    public const uint HighResolutionTimer = 0x2;

    /// <summary>TIMER_ALL_ACCESS (winnt.h).</summary>
    public const uint TimerAllAccess = 0x1F0003;

    private const string User = "user32";
    private const string Kernel = "kernel32";

    [DllImport(User)]
    public static extern int EnumWindows(delegate* unmanaged<nint, nint, int> callback, nint context);

    [DllImport(User)]
    public static extern uint GetWindowThreadProcessId(nint window, uint* processId);

    [DllImport(User)]
    public static extern int IsWindowVisible(nint window);

    [DllImport(User)]
    public static extern int IsWindow(nint window);

    [DllImport(User)]
    public static extern int IsIconic(nint window);

    [DllImport(User)]
    public static extern int GetClientRect(nint window, PlaneRect* rect);

    [DllImport(User)]
    public static extern int GetWindowRect(nint window, PlaneRect* rect);

    /// <summary>MonitorFromWindow; <paramref name="flags"/> MONITOR_DEFAULTTONEAREST (2) always gives one.</summary>
    [DllImport(User)]
    public static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport(User)]
    public static extern int GetMonitorInfoW(nint monitor, MonitorInfo* info);

    [DllImport(User)]
    public static extern int SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport(Kernel)]
    public static extern uint GetCurrentProcessId();

    [DllImport(Kernel)]
    public static extern nint GetConsoleWindow();

    [DllImport(Kernel)]
    public static extern nint CreateWaitableTimerExW(nint attributes, char* name, uint flags, uint access);

    /// <summary>SetWaitableTimer: a negative <paramref name="dueTime"/> is relative, in 100 ns units.</summary>
    [DllImport(Kernel)]
    public static extern int SetWaitableTimer(nint timer, long* dueTime, int period, nint routine, nint context, int resume);

    [DllImport(Kernel)]
    public static extern uint WaitForSingleObject(nint handle, uint milliseconds);

    [DllImport(Kernel)]
    public static extern nint CreateEventW(nint attributes, int manualReset, int initialState, char* name);

    [DllImport(Kernel)]
    public static extern int CloseHandle(nint handle);
}
