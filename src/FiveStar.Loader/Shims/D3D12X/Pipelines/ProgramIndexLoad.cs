// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using FiveStar.Shaders.Cache;

namespace FiveStar.Loader.Shims.D3D12X.Pipelines;

/// <summary>
/// The <see cref="ProgramIndex"/> of --shader-dir, built on a worker thread from loader start. The game creates its
/// device on a thread its hang watchdog waits for (docs/loader.md), so that thread only picks up the finished index.
/// </summary>
internal sealed class ProgramIndexLoad
{
    private readonly Task<ProgramIndex> _index;

    private ProgramIndexLoad(Task<ProgramIndex> index)
    {
        _index = index;
    }

    /// <summary>Starts indexing <paramref name="folder"/>.</summary>
    public static ProgramIndexLoad Start(string folder)
    {
        return new ProgramIndexLoad(Task.Run(() => DebugLayer.PoolStoreTrace.Rename(DebugLayer.MeshProbes.Rename(ProgramIndex.Load(folder)))));
    }

    /// <summary>The index, waiting for it when it is not built yet; <paramref name="waited"/> is the time spent waiting.</summary>
    public ProgramIndex Wait(out TimeSpan waited)
    {
        long start = Stopwatch.GetTimestamp();
        using var wait = Diagnostics.BlockingWaits.Enter("program index load");
        var index = _index.GetAwaiter().GetResult();
        waited = Stopwatch.GetElapsedTime(start);

        return index;
    }
}
