// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Diagnostics;

namespace FiveStar.Loader.Shims.D3D12X.Pipelines;

/// <summary>
/// Keeps the shaders the driver holds within a budget (<c>--pipeline-shader-budget</c>, off by default: the fix of the
/// device loss it was made for is <see cref="BackgroundProcessing"/>). NVIDIA's driver keeps the code
/// of every shader of a pipeline a submitted list has used, for as long as a pipeline with that shader lives, in tables
/// that cannot pass 512 MiB; the translated programs are large (0.2 MB of DXIL on average), the game never releases a
/// pipeline, and boots w186 to w222 lost the device at about 2,200 shaders, when the tables' growth failed inside
/// ExecuteCommandLists (docs/loader.md, the option's row). Every <see cref="Every"/> presents, while the built
/// stand-ins hold more DXIL than the budget, those no command list has set for a while are given up
/// (<see cref="StandInPipelines.Evict"/>) and built again at their next use; a given-up pipeline is released once the
/// queues have run what was submitted <see cref="ReleaseAfter"/> presents later, as lists recorded with it may still run.
/// </summary>
internal static class PipelineEviction
{
    /// <summary>Presents between checks, and after which a given-up pipeline's release is prepared.</summary>
    public const int Every = 64;
    public const int ReleaseAfter = 32;

    /// <summary>Presents a pipeline must have gone unused: the first value, halved down to the least while the budget stays exceeded.</summary>
    public const int IdleStart = 600;
    public const int IdleLeast = 150;
    private const int IdleDivisor = 2;
    private const int MiBShift = 20;
    private const int TightenNumerator = 3;
    private const int TightenDenominator = 4;

    /// <summary>The default budget: none. In boots w241 and w242 nine tenths of the pipelines in use were set again within 150 presents, so little could be given up.</summary>
    public const int DefaultBudgetMiB = 0;

    private const double MiB = 1024.0 * 1024.0;

    private static readonly List<Retired> Waiting = [];
    private static readonly List<nint> Batch = [];
    private static StandInPipelines? _standIns;
    private static RectanglePipelines? _rectangles;
    private static LoaderLog? _log;
    private static long _idle = IdleStart;
    private static long _released;
    private static long _stillHeld;

    /// <summary>DXIL bytes the built stand-ins may hold before idle ones are given up; 0 gives up none.</summary>
    public static long Budget { get; set; }

    /// <summary>Sets <see cref="Budget"/> in MiB.</summary>
    public static void SetBudget(int megabytes)
    {
        Budget = (long)megabytes << MiBShift;
    }

    /// <summary>
    /// The driver's shader table is nearly at its cap (<see cref="ShaderTableGuard"/>): the budget becomes three
    /// quarters of the <paramref name="dxil"/> bytes the pipelines in use hold now, unless it is lower already.
    /// </summary>
    public static void Tighten(long dxil, LoaderLog? log)
    {
        long wanted = dxil / TightenDenominator * TightenNumerator;

        if (wanted <= 0 || (Budget > 0 && Budget <= wanted))
        {
            return;
        }

        Budget = wanted;
        log?.Warn($"[d3d12_xs] the driver's shader table is nearly full: pipelines unused for a while are given up until the ones in use hold {wanted / MiB:F0} MiB of DXIL (now {dxil / MiB:F0})");
    }

    /// <summary>The stand-ins watched and the rectangle variants dropped with a released pipeline.</summary>
    public static void Attach(StandInPipelines standIns, RectanglePipelines rectangles, LoaderLog log)
    {
        _standIns = standIns;
        _rectangles = rectangles;
        _log = log;
    }

    /// <summary>The DXIL the built stand-ins hold, those given up and built again, for the log; empty before a device.</summary>
    public static string Describe()
    {
        var standIns = _standIns;

        if (standIns is null)
        {
            return string.Empty;
        }

        var (evicted, again) = standIns.Evictions;

        return $"; built pipelines hold {standIns.LiveShaderBytes / MiB:F0} MiB of DXIL (budget {Budget / MiB:F0}), {evicted} given up, {again} built again, {Interlocked.Read(ref _released)} released ({Interlocked.Read(ref _stillHeld)} with a reference left)";
    }

    /// <summary>After present <paramref name="present"/>, on the presenting thread.</summary>
    public static void AfterPresent(long present)
    {
        var standIns = _standIns;

        if (standIns is null || Budget <= 0)
        {
            return;
        }

        if (Waiting.Count > 0)
        {
            ReleaseDue(present);
        }

        if (present % Every != 0)
        {
            return;
        }

        long live = standIns.LiveShaderBytes;

        if (live <= Budget)
        {
            _idle = IdleStart;

            return;
        }

        Batch.Clear();
        long bytes = standIns.Evict(_idle, Batch);

        foreach (nint pipeline in Batch)
        {
            Waiting.Add(new Retired(pipeline, present, null));
        }

        var (evicted, again) = standIns.Evictions;
        _log?.Info($"  [d3d12_xs] pipeline shader budget: {live / MiB:F0} of {Budget / MiB:F0} MiB of DXIL in built pipelines at present {present}; {Batch.Count} unused for {_idle} presents given up ({bytes / MiB:F0} MiB), {evicted} so far, {again} built again, {Interlocked.Read(ref _released)} released; {DriverShaderTables.DescribeWatched()}");

        // Still over the budget with nothing more to give up at this age: younger ones next time.
        if (live - bytes > Budget)
        {
            _idle = Math.Max(IdleLeast, _idle / IdleDivisor);
        }
    }

    /// <summary>
    /// Releases the given-up pipelines whose lists have run: <see cref="ReleaseAfter"/> presents after it was given up
    /// the queues' submitted values are noted, and the pipeline goes once every queue has completed them.
    /// </summary>
    private static void ReleaseDue(long present)
    {
        ulong[]? completed = null;

        for (int i = Waiting.Count - 1; i >= 0; i--)
        {
            var item = Waiting[i];

            if (present - item.At < ReleaseAfter)
            {
                continue;
            }

            if (item.Marks is null)
            {
                Waiting[i] = item with { Marks = QueueProgress.Submitted() };

                continue;
            }

            completed ??= QueueProgress.Completed();

            if (!Reached(completed, item.Marks))
            {
                continue;
            }

            _rectangles?.Forget(item.Pipeline);

            // A reference left would keep the pipeline's shaders in the driver: counted, to be seen in the log.
            if (DesktopDevice.Release(item.Pipeline) != 0)
            {
                Interlocked.Increment(ref _stillHeld);
            }

            Interlocked.Increment(ref _released);
            Waiting.RemoveAt(i);
        }
    }

    private static bool Reached(ulong[] completed, ulong[] marks)
    {
        for (int i = 0; i < marks.Length; i++)
        {
            if (i >= completed.Length || completed[i] < marks[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A given-up desktop pipeline, the present it was given up in, and the queues' submitted values it waits for.</summary>
    private sealed record Retired(nint Pipeline, long At, ulong[]? Marks);
}
