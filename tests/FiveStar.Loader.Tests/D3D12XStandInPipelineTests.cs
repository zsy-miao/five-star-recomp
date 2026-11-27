// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using FiveStar.Loader.Diagnostics;
using FiveStar.Loader.Native;
using FiveStar.Loader.Shims.D3D12X.Pipelines;
using FiveStar.Loader.Tests.Support;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>
/// Pipeline creations the driver takes too long for: the game gets a stand-in at once, the stand-in turns into the
/// desktop pipeline when the build ends, and its last Release releases that pipeline.
/// </summary>
public sealed unsafe class D3D12XStandInPipelineTests
{
    private const int ReleaseSlot = 2;
    private const int Slots = 3;

    /// <summary>Upper bound for the background build in the test; it ends as soon as the gate opens.</summary>
    private static readonly TimeSpan BuildLimit = TimeSpan.FromSeconds(30);

    [Fact]
    public void A_creation_within_the_budget_returns_the_desktop_pipeline_itself()
    {
        using var memory = new NativeBlocks();
        nint pipeline = FakePipeline(memory);
        var standIns = StandIns(TimeSpan.FromMinutes(1));
        nint result = 0;

        int hr = standIns.Create(() => new PipelineResult(HResult.Ok, pipeline), &result, "fast", null);

        Assert.Equal((HResult.Ok, pipeline), (hr, result));
        Assert.Equal(pipeline, standIns.Desktop(result, out bool pending));
        Assert.False(pending);
    }

    [Fact]
    public void A_creation_that_fails_within_the_budget_hands_out_a_stand_in_that_never_builds()
    {
        // A held graphics pair (--skip-pipelines TransformSkinD) on a warm cache: its build fails at once; the game got
        // 0x80004005 and its loading stalled (w69). It gets a stand-in and S_OK.
        const string Held = "sgd1_0736b791_t32_PS_DeferredTextured_1778b45a_Wrapped.trim2 + sgd1_01cb042f_t41_MSR_VS_TransformSkinD_1778b45a_Wrapped.link-314dd11550b9";
        var standIns = StandIns(TimeSpan.FromMinutes(1));
        nint result = 0;

        int hr = standIns.Create(() => new PipelineResult(HResult.Fail, 0), &result, Held, null);

        Assert.Equal(HResult.Ok, hr);
        Assert.NotEqual(0, result);
        Assert.Equal(0, standIns.Desktop(result, out bool pending));
        Assert.True(pending);
        Assert.Equal(Held, standIns.Name(result));
    }

    [Fact]
    public void A_slow_creation_hands_out_a_stand_in_that_becomes_the_pipeline_and_releases_it()
    {
        using var memory = new NativeBlocks();
        using var gate = new ManualResetEventSlim();
        nint pipeline = FakePipeline(memory);
        nint seen = 0;
        var standIns = StandIns(TimeSpan.Zero);
        nint result = 0;

        int hr = standIns.Create(() => Slow(gate, pipeline), &result, "slow", built => seen = built);

        Assert.Equal(HResult.Ok, hr);
        Assert.NotEqual(pipeline, result);
        Assert.Equal(0, standIns.Desktop(result, out bool pending));
        Assert.True(pending);

        gate.Set();
        nint standIn = result;
        Assert.True(SpinWait.SpinUntil(() => standIns.Desktop(standIn, out _) != 0, BuildLimit));
        Assert.Equal((pipeline, pipeline), (standIns.Desktop(result, out pending), seen));
        Assert.False(pending);

        uint left = ((delegate* unmanaged<nint, uint>)ComObject.Slot(result, ReleaseSlot))(result);
        Assert.Equal((0u, 1L), (left, *(long*)(pipeline + sizeof(nint))));
    }

    [Fact]
    public void A_deferred_creation_builds_only_when_a_command_list_first_sets_it()
    {
        using var memory = new NativeBlocks();
        nint pipeline = FakePipeline(memory);
        var standIns = StandIns(TimeSpan.Zero);
        var builds = 0;
        var uses = 0;
        nint result = 0;

        Func<PipelineResult> build = () =>
        {
            Interlocked.Increment(ref builds);

            return new PipelineResult(HResult.Ok, pipeline);
        };

        standIns.Deferred(build, &result, "lazy", null, heavy: false, () => uses++);
        Thread.Sleep(100);
        Assert.Equal(0, Volatile.Read(ref builds));

        nint standIn = result;
        Assert.Equal(0, standIns.Desktop(standIn, out bool pending));
        Assert.True(pending);
        Assert.True(SpinWait.SpinUntil(() => standIns.Desktop(standIn, out _) != 0, BuildLimit));
        Assert.Equal((1, 1), (Volatile.Read(ref builds), uses));
    }

    [Fact]
    public void A_stand_in_named_by_wait_pipelines_is_waited_for_at_its_first_use_up_to_the_limit()
    {
        using var memory = new NativeBlocks();
        using var gate = new ManualResetEventSlim();
        using var never = new ManualResetEventSlim();
        nint pipeline = FakePipeline(memory);

        var standIns = StandIns(TimeSpan.Zero, new PipelineWaits(["deferredtextured", "skin"], TimeSpan.FromMilliseconds(300), []));
        nint named = 0;
        nint other = 0;
        nint late = 0;

        // A G-buffer pair whose build ends 100 ms after its first use, one not named, and a named one past the limit.
        standIns.Deferred(() => Slow(gate, pipeline), &named, "DeferredTextured_ps + TransformD_vs (linked producer)", null, heavy: false, () => OpenLater(gate));
        standIns.Deferred(() => Slow(gate, pipeline), &other, "Particles_cs", null, heavy: false, () => { });
        standIns.Deferred(() => Slow(never, pipeline), &late, "Skin_vs", null, heavy: false, () => { });

        Assert.Equal(pipeline, standIns.Desktop(named, out bool pending));
        Assert.False(pending);
        Assert.Equal(0, standIns.Desktop(late, out pending));
        Assert.True(pending);

        // Neither one past the limit nor one not named is waited for.
        var again = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(0, standIns.Desktop(late, out _));
        Assert.Equal(0, standIns.Desktop(other, out _));
        Assert.True(again.ElapsedMilliseconds < 200);
        Assert.Contains("2 waited for by name (1 past the limit)", standIns.Summary, StringComparison.Ordinal);

        never.Set();
        nint unnamed = other;
        Assert.True(SpinWait.SpinUntil(() => standIns.Desktop(unnamed, out _) != 0, BuildLimit));
    }

    /// <summary>
    /// The first use of a small compute stand-in whose program takes long to make (seconds on a 15.6 GB machine, #13):
    /// the game's thread waits no longer than the first-use budget, and the program and the build finish without it.
    /// </summary>
    [Fact]
    public void A_small_compute_first_use_waits_no_longer_than_its_budget_for_a_slow_program()
    {
        using var memory = new NativeBlocks();
        using var made = new ManualResetEventSlim();
        nint pipeline = FakePipeline(memory);
        var standIns = StandIns(TimeSpan.Zero);
        nint result = 0;

        Func<StandInUse?, Task> programs = _ => Task.Run(made.Wait);
        Func<PendingBuild?> prepare = () => new PendingBuild(() => new PipelineResult(HResult.Ok, pipeline), Heavy: false, "small", _ => { }, 0);
        Func<bool> programsNow = () => made.Wait(BuildLimit);
        standIns.Waiting(programs, prepare, &result, () => { }, "small", programsNow);
        nint standIn = result;

        var clock = System.Diagnostics.Stopwatch.StartNew();
        nint first = standIns.Desktop(standIn, out bool pending);
        var waited = clock.Elapsed;
        made.Set();

        Assert.Equal((0, true), (first, pending));
        Assert.True(waited < StandInPipelines.SmallBudget * 4, $"the first use waited {waited.TotalMilliseconds:F0} ms");
        Assert.True(SpinWait.SpinUntil(() => standIns.Desktop(standIn, out _) != 0, BuildLimit));
        Assert.Contains("(1 with programs made inline", standIns.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// A built pipeline no list has set for a while is given up for the driver's shader memory (boots w186 to w222 lost
    /// the device with its tables full): its stand-in is pending again, and its next use builds it again.
    /// </summary>
    [Fact]
    public void A_pipeline_given_up_is_built_again_at_its_next_use()
    {
        using var memory = new NativeBlocks();
        nint pipeline = FakePipeline(memory);
        var standIns = StandIns(TimeSpan.Zero);
        nint result = 0;
        int builds = 0;

        Func<PendingBuild?> prepare = () => new PendingBuild(() => new PipelineResult(HResult.Ok, pipeline + (0 * Interlocked.Increment(ref builds))), Heavy: false, "given up", _ => { }, 1000);
        standIns.Waiting(_ => Task.CompletedTask, prepare, &result, () => { }, "given up");
        nint standIn = result;
        standIns.Desktop(standIn, out _);
        Assert.True(SpinWait.SpinUntil(() => standIns.Desktop(standIn, out _) != 0, BuildLimit));
        Assert.Equal(1000, standIns.LiveShaderBytes);

        var released = new List<nint>();
        Assert.Equal(1000, standIns.Evict(0, released));
        Assert.Equal([pipeline], released);
        Assert.Equal(0, standIns.LiveShaderBytes);

        Assert.Equal((0, true), (standIns.Desktop(standIn, out bool pending), pending));
        Assert.True(SpinWait.SpinUntil(() => standIns.Desktop(standIn, out _) == pipeline, BuildLimit));
        Assert.Equal((2, 1000L, (1L, 1L)), (Volatile.Read(ref builds), standIns.LiveShaderBytes, standIns.Evictions));
    }

    /// <summary>
    /// With the driver's shader table nearly full (issue #64: the device is removed when it cannot grow) a pipeline no
    /// list has set yet stays a stand-in, built or not; one in use goes on, and first uses go on once there is room.
    /// </summary>
    [Fact]
    public void A_first_use_is_held_while_the_shader_table_is_nearly_full()
    {
        const long MiB = 1L << 20;
        using var memory = new NativeBlocks();
        nint pipeline = FakePipeline(memory);
        var standIns = StandIns(TimeSpan.Zero);
        nint used = 0;
        nint fresh = 0;

        Func<PendingBuild?> prepare = () => new PendingBuild(() => new PipelineResult(HResult.Ok, pipeline), Heavy: false, "held", _ => { }, 1000);
        standIns.Waiting(_ => Task.CompletedTask, prepare, &used, null, "held");
        standIns.Waiting(_ => Task.CompletedTask, prepare, &fresh, null, "held");
        nint first = used;
        nint second = fresh;

        Assert.True(SpinWait.SpinUntil(() => standIns.Desktop(first, out _) == pipeline, BuildLimit));
        Assert.True(SpinWait.SpinUntil(() => standIns.Summary.Contains("2 built since", StringComparison.Ordinal), BuildLimit));

        try
        {
            ShaderTableGuard.Reset();
            ShaderTableGuard.Check(ShaderTableGuard.HoldMiB * MiB, 0);
            Assert.Equal((pipeline, false), (standIns.Desktop(first, out bool pending), pending));
            Assert.Equal((0, true, 1L), (standIns.Desktop(second, out pending), pending, standIns.HeldUses));

            ShaderTableGuard.Check(0, ShaderTableGuard.ResumeMiB * MiB / 4);
            Assert.Equal((pipeline, false), (standIns.Desktop(second, out pending), pending));
        }
        finally
        {
            ShaderTableGuard.Reset();
        }
    }

    /// <summary>
    /// The UI target's clear pair of boot w115: its programs come from the workers after the small budget, the only build
    /// thread is busy with a G-buffer pipeline, and still its first use gets the pipeline rather than a skipped clear.
    /// </summary>
    [Fact]
    public void An_essential_clear_is_made_at_its_first_use_behind_a_busy_queue()
    {
        const string Clear = "exe_0688cd37_t32_VS_Clear_f05fef9c_Wrapped + exe_0688e86a_t32_PS_Clear_f05fef9c_Wrapped";
        const string GBuffer = "sgd1_06e95600_t32_PS_DeferredTextured_942fcba4_Wrapped + sgd1_018ec975_t41_MS_VS_TransformD_c5fd36bb_Wrapped";
        using var memory = new NativeBlocks();
        using var never = new ManualResetEventSlim();
        nint pipeline = FakePipeline(memory);
        var standIns = StandIns(TimeSpan.Zero);

        nint busy = 0;
        nint clear = 0;
        standIns.Create(() => Slow(never, pipeline), &busy, GBuffer, null);

        Func<StandInUse?, Task> programs = _ => Task.Delay(StandInPipelines.SmallBudget * 2);
        Func<PendingBuild?> prepare = () => new PendingBuild(() => new PipelineResult(HResult.Ok, pipeline), Heavy: false, Clear, _ => { }, 0);
        standIns.Waiting(programs, prepare, &clear, () => { }, Clear);

        Assert.Equal(pipeline, standIns.Desktop(clear, out bool pending));
        Assert.False(pending);
        Assert.Contains("1 clears, blits and UI pipelines made at first use", standIns.Summary, StringComparison.Ordinal);
        Assert.Equal((true, true, false), (EssentialPipelines.Matches("sgd2_103ac09a_t32_FVertexEAlpha_a701d3d4_Wrapped"), EssentialPipelines.Matches("sgd2_1034496a_t32_PS_BlitColour_6e5520d1_Wrapped"), EssentialPipelines.Matches(GBuffer)));
        never.Set();
    }

    /// <summary>
    /// A lighting pipeline (boot w120 washed out with the main cascade shadow draw a stand-in) is waited for at its first
    /// use like --wait-pipelines (up to 60 s), here a build that ends 100 ms in; with --no-lighting-waits it is skipped as before.
    /// </summary>
    [Fact]
    public void A_lighting_pipeline_is_waited_for_at_its_first_use_unless_turned_off()
    {
        const string Cascade = "sgd1_00049447_t41_MS_VS_CascadeShadows_draw_f6ce4799_Wrapped";
        using var memory = new NativeBlocks();
        using var gate = new ManualResetEventSlim();
        nint pipeline = FakePipeline(memory);
        var standIns = StandIns(TimeSpan.Zero);
        nint waited = 0;

        standIns.Deferred(() => Slow(gate, pipeline), &waited, Cascade, null, heavy: false, () => OpenLater(gate));
        Assert.Equal(pipeline, standIns.Desktop(waited, out bool pending));
        Assert.False(pending);
        Assert.Contains("1 lighting pipelines waited for at first use", standIns.Summary, StringComparison.Ordinal);

        EssentialPipelines.Lighting = false;

        try
        {
            // Not disposed: the build thread may reach its wait after the test ends.
            var never = new ManualResetEventSlim();
            nint skipped = 0;
            standIns.Deferred(() => Slow(never, pipeline), &skipped, Cascade, null, heavy: false, () => { });
            Assert.Equal(0, standIns.Desktop(skipped, out pending));
            Assert.True(pending);
            never.Set();
        }
        finally
        {
            EssentialPipelines.Lighting = true;
        }
    }

    private static StandInPipelines StandIns(TimeSpan budget, PipelineWaits? waits = null)
    {
        var builds = new PipelineBuildQueue(1, PipelineBuildQueue.AnyTime);
        builds.Start();

        return new StandInPipelines(budget, builds, builds, new OnceLog(new LoaderLog(new StringWriter(), null, false))) { Waits = waits };
    }

    private static void OpenLater(ManualResetEventSlim gate)
    {
        new Thread(() =>
        {
            Thread.Sleep(100);
            gate.Set();
        }).Start();
    }

    private static PipelineResult Slow(ManualResetEventSlim gate, nint pipeline)
    {
        gate.Wait(BuildLimit);

        return new PipelineResult(HResult.Ok, pipeline);
    }

    /// <summary>A COM object whose Release counts its calls in the word after the vtable.</summary>
    private static nint FakePipeline(NativeBlocks memory)
    {
        var vtable = (nint*)memory.Alloc(Slots * sizeof(nint));
        vtable[ReleaseSlot] = (nint)(delegate* unmanaged<nint, uint>)&CountRelease;
        var pipeline = memory.Alloc(2 * sizeof(nint));
        *(nint*)pipeline = (nint)vtable;

        return (nint)pipeline;
    }

    [UnmanagedCallersOnly]
    private static uint CountRelease(nint self)
    {
        Interlocked.Increment(ref *(long*)(self + sizeof(nint)));

        return 0;
    }
}
