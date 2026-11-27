// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Diagnostics;

namespace FiveStar.Loader.Shims.D3D12X.Pipelines;

/// <summary>
/// Keeps the device when NVIDIA's shader tables are nearly full: the driver removes it when a table cannot grow past
/// 512 MiB (boots w186 to w222, issue #64), which ends the game, while a pipeline never used only leaves its draws
/// out. Every <see cref="Every"/> presents the larger table is read where the driver's layout is known
/// (<see cref="DriverShaderTables"/>, driver 32.0.16.2124), else estimated from the DXIL of the pipelines lists have
/// set (w244: 352 MiB of table for 245 MiB of DXIL, so <see cref="EstimateNumerator"/>/<see cref="EstimateDenominator"/>;
/// the driver's background recompiles are not in the estimate). From <see cref="WarnMiB"/> on a warning says so once;
/// from <see cref="HoldMiB"/> on pipelines no list has set yet stay stand-ins (<see cref="Holding"/>,
/// <see cref="StandInPipelines.Desktop"/>): their draws are skipped, the ones in use go on, and clears, blits, UI and
/// lighting pipelines are never held. Below <see cref="ResumeMiB"/> first uses go on again. From
/// <see cref="CriticalMiB"/> on, pipelines idle for a while are given up as well (<see cref="PipelineEviction.Tighten"/>):
/// held at their next use, they stay out until there is room.
/// <c>--no-shader-table-guard</c> turns it off.
/// </summary>
internal static class ShaderTableGuard
{
    public const int Every = 64;
    public const int WarnMiB = 400;
    public const int HoldMiB = 470;
    public const int ResumeMiB = 440;
    public const int CriticalMiB = 495;
    public const int RecompileFactor = 2;
    public const int EstimateNumerator = 3;
    public const int EstimateDenominator = 2;

    private const double MiB = 1024.0 * 1024.0;

    private static StandInPipelines? _standIns;
    private static Func<nint>? _device;
    private static LoaderLog? _log;
    private static bool _warned;
    private static int _holding;

    /// <summary>Whether the guard works (<c>--no-shader-table-guard</c> clears it).</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>Whether first uses of pipelines are held now.</summary>
    public static bool Holding => Volatile.Read(ref _holding) != 0;

    /// <summary>The stand-ins whose DXIL is counted and the desktop device whose tables are read.</summary>
    public static void Attach(StandInPipelines standIns, Func<nint> device, LoaderLog log)
    {
        _standIns = standIns;
        _device = device;
        _log = log;
    }

    /// <summary>After present <paramref name="present"/>, on the presenting thread.</summary>
    public static void AfterPresent(long present)
    {
        var standIns = _standIns;

        if (!Enabled || standIns is null || present % Every != 0)
        {
            return;
        }

        Check(DriverShaderTables.LargestTableBytes(_device?.Invoke() ?? 0), standIns.LiveShaderBytes);
    }

    /// <summary>One check: <paramref name="table"/> bytes read from the driver (0 when unknown) and the DXIL bytes of the pipelines in use.</summary>
    internal static void Check(long table, long dxil)
    {
        double size = table / MiB;
        string source = "read from the driver";

        if (table == 0)
        {
            size = dxil * EstimateNumerator / EstimateDenominator / MiB;
            source = $"estimated from {dxil / MiB:F0} MiB of DXIL in the pipelines in use";

            // The driver's recompiles were as much again where they could not be turned off (issue #64: 285 of 537 MiB).
            if (!BackgroundProcessing.Off)
            {
                size *= RecompileFactor;
                source += ", doubled for the driver's background recompiles";
            }
        }

        // Nearly at the cap although first uses are held (the recompiles of the pipelines in use go on): idle ones go.
        if (size >= CriticalMiB)
        {
            PipelineEviction.Tighten(dxil, _log);
        }

        if (size >= WarnMiB && !_warned)
        {
            _warned = true;
            _log?.Warn($"[d3d12_xs] the driver's shader table is at about {size:F0} of 512 MiB ({source}): at 512 MiB an NVIDIA driver removes the device. From {HoldMiB} MiB on, pipelines not used so far stay stand-ins and their draws are skipped (--no-shader-table-guard turns that off)");
        }

        if (size >= HoldMiB && Interlocked.Exchange(ref _holding, 1) == 0)
        {
            _log?.Warn($"[d3d12_xs] the driver's shader table is at about {size:F0} of 512 MiB ({source}): pipelines no list has set yet are held as stand-ins from now on, their draws skipped, so that the device is not removed");
        }
        else if (size < ResumeMiB && Interlocked.Exchange(ref _holding, 0) != 0)
        {
            _log?.Info($"  [d3d12_xs] the driver's shader table is back at about {size:F0} MiB: first uses of pipelines go on");
        }
    }

    /// <summary>Forgets the state (tests).</summary>
    internal static void Reset()
    {
        _warned = false;
        Volatile.Write(ref _holding, 0);
    }
}
