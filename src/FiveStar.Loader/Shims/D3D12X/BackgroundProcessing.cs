// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Diagnostics;
using FiveStar.Loader.Native;

namespace FiveStar.Loader.Shims.D3D12X;

/// <summary>
/// Turns the driver's background shader work off for the device (ID3D12Device6::SetBackgroundProcessingMode,
/// D3D12_BACKGROUND_PROCESSING_MODE_DISABLE_BACKGROUND_WORK). NVIDIA's driver recompiles the shaders in use on the
/// "D3D Background Thread"s and keeps the results ("optional shaders") in the same 512 MiB tables as the first
/// compiles; in boot w222 they were 169 of the 524 MiB at which the tables could not grow and the device was removed,
/// and all of the growth of its last two minutes. Windows refuses the mode outside developer mode
/// (DXGI_ERROR_SDK_COMPONENT_MISSING, 0x887a002d: issue #64's machine, where the optional shaders then reached 285
/// MiB); the refusal is a warning that says so, and the driver's measurements are discarded every
/// <see cref="DiscardSeconds"/> s instead (the same call with the mode every machine may set and
/// D3D12_MEASUREMENTS_ACTION_DISCARD_PREVIOUS): what the driver optimises from is thrown away before it adds up.
/// <c>--driver-background-work</c> leaves the driver's default; <c>--discard-driver-measurements</c> discards them
/// where the mode is not refused too (to measure what discarding alone does).
/// </summary>
internal static unsafe class BackgroundProcessing
{
    /// <summary>Seconds between two discards of the driver's measurements.</summary>
    public const int DiscardSeconds = 2;

    /// <summary>ID3D12Device6::SetBackgroundProcessingMode; D3D12_BACKGROUND_PROCESSING_MODE_ALLOWED and _DISABLE_BACKGROUND_WORK; D3D12_MEASUREMENTS_ACTION_KEEP_ALL and _DISCARD_PREVIOUS (d3d12.h).</summary>
    private const int SetBackgroundProcessingModeSlot = 65;
    private const int Allowed = 0;
    private const int DisableBackgroundWork = 2;
    private const int KeepAll = 0;
    private const int DiscardPrevious = 3;

    /// <summary>DXGI_ERROR_SDK_COMPONENT_MISSING: what the call returns with Windows developer mode off.</summary>
    private const int ComponentMissing = unchecked((int)0x887A002D);

    private const int MillisecondsPerSecond = 1000;

    private static readonly Guid Device6Iid = new("c70b221b-40e4-4a17-89af-025a0727a6dc");

    /// <summary>Whether the driver keeps its default (<c>--driver-background-work</c>).</summary>
    public static bool Keep { get; set; }

    /// <summary>Whether the measurements are discarded even where the mode is set (<c>--discard-driver-measurements</c>), the work left on.</summary>
    public static bool DiscardOnly { get; set; }

    /// <summary>Whether the background work is off: false when it was left on, refused, or not asked for yet.</summary>
    public static bool Off { get; private set; }

    /// <summary>Whether turning the background work off was asked for and refused (Windows developer mode off).</summary>
    public static bool Refused { get; private set; }

    /// <summary>Sets the mode on <paramref name="device"/>, once it is made.</summary>
    public static void Apply(nint device, LoaderLog log)
    {
        if (Keep || device == 0)
        {
            return;
        }

        nint device6 = 0;
        Guid iid = Device6Iid;
        int hr = ((delegate* unmanaged<nint, Guid*, nint*, int>)ComObject.Slot(device, ComObject.QueryInterfaceSlot))(device, &iid, &device6);

        if (HResult.Failed(hr) || device6 == 0)
        {
            log.Warn($"[d3d12_xs] the driver's background shader work stays on: no ID3D12Device6 ({HResult.Format(hr)}). On an NVIDIA card its results fill the driver's shader tables (512 MiB), and the device is removed when they are full");

            return;
        }

        if (!DiscardOnly)
        {
            hr = Set(device6, DisableBackgroundWork, KeepAll);
            Off = !HResult.Failed(hr);
        }

        if (Off)
        {
            ComObject.Release(device6);
            log.Info($"  [d3d12_xs] driver background shader work off -> {HResult.Format(hr)}");

            return;
        }

        if (!DiscardOnly)
        {
            Refused = true;
            log.Warn($"[d3d12_xs] the driver's background shader work could not be turned off: ID3D12Device6::SetBackgroundProcessingMode(DISABLE_BACKGROUND_WORK) -> {HResult.Format(hr)}{Meaning(hr)}. On an NVIDIA card the driver's recompiles then fill its shader tables (512 MiB) within minutes of play and the device is removed (DXGI_ERROR_DEVICE_REMOVED, no GPU fault). Turn on Windows developer mode (Settings > System > For developers) and start again; until then the driver's measurements are discarded every {DiscardSeconds} s, which may slow the recompiles (docs/running.md#developer-mode)");
        }

        // The reference is kept for the thread, which lives as long as the process.
        nint kept = device6;
        var thread = new Thread(() => Discard(kept, log)) { IsBackground = true, Name = "fivestar driver measurements" };
        thread.Start();
    }

    private static string Meaning(int hr)
    {
        if (hr == ComponentMissing)
        {
            return " (DXGI_ERROR_SDK_COMPONENT_MISSING: Windows developer mode is off)";
        }

        return string.Empty;
    }

    private static int Set(nint device6, int mode, int action)
    {
        return ((delegate* unmanaged<nint, int, int, nint, int*, int>)ComObject.Slot(device6, SetBackgroundProcessingModeSlot))(device6, mode, action, 0, null);
    }

    /// <summary>Discards the driver's measurements for as long as the process lives; the first result is logged, a failure ends it.</summary>
    private static void Discard(nint device6, LoaderLog log)
    {
        int hr = Set(device6, Allowed, DiscardPrevious);
        log.Info($"  [d3d12_xs] driver measurements discarded every {DiscardSeconds} s (SetBackgroundProcessingMode(ALLOWED, DISCARD_PREVIOUS)) -> {HResult.Format(hr)}");

        while (!HResult.Failed(hr) && !DeviceLoss.Lost)
        {
            Thread.Sleep(DiscardSeconds * MillisecondsPerSecond);
            hr = Set(device6, Allowed, DiscardPrevious);
        }
    }
}
