// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using FiveStar.Loader.Diagnostics;
using FiveStar.Loader.Native;

namespace FiveStar.Loader.Shims.D3D12X.Pipelines;

/// <summary>
/// Pipeline creations the driver takes too long for. The game creates pipelines in deferred jobs and waits for
/// all of them before it finishes a frame (docs/d3d12x-pipelines.md); the console builds them at once, the desktop
/// driver compiles some translated programs for minutes. A creation that has not returned within the budget hands the
/// game a <see cref="StandInPipelineObject"/> and goes on in the background; the stand-in becomes the desktop pipeline
/// when the driver is done. Until then the command lists skip the work it would run, unless the stand-in is named by
/// <c>--wait-pipelines</c> (<see cref="Waits"/>): its first use then waits for the pipeline.
/// </summary>
internal sealed unsafe class StandInPipelines
{
    /// <summary>A build that takes longer than this is named in the log when it ends.</summary>
    private static readonly TimeSpan SlowBuild = TimeSpan.FromMinutes(1);

    /// <summary>How long the first use of a small compute stand-in waits for its pipeline (made on that thread if queued).</summary>
    public static readonly TimeSpan SmallBudget = TimeSpan.FromMilliseconds(250);

    /// <summary>Pipelines whose skipped work is logged by name.</summary>
    private const int SkipsLogged = 64;

    /// <summary>Names listed in the summary's count of work skipped before later work of the same list ran.</summary>
    private const int SkipsShown = 4;

    /// <summary>The kind of a stand-in that never builds.</summary>
    private const string HeldKind = "held";

    /// <summary>Names listed in the summary's most skipped pipelines.</summary>
    private const int MostSkippedShown = 10;

    private readonly ConcurrentDictionary<nint, string> _names = new();
    private readonly ConcurrentDictionary<nint, StandInUse> _uses = new();
    private readonly ConcurrentDictionary<string, long> _skippedByName = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _skippedBefore = new(StringComparer.Ordinal);
    private readonly HashSet<nint> _small = [];
    private readonly HashSet<nint> _essential = [];
    private readonly HashSet<nint> _lighting = [];
    private int _lightingMade;
    private int _lightingMissed;
    private readonly ConcurrentDictionary<nint, string> _kinds = new();
    private readonly ConcurrentDictionary<string, string> _skippedKinds = new(StringComparer.Ordinal);
    private int _essentialMade;
    private int _essentialMissed;
    private int _smallMade;
    private int _smallNotReady;
    private int _smallProgramsInline;
    private int _smallOverBudget;
    private readonly HashSet<nint> _waited = [];
    private int _waitedFor;
    private int _waitsPastLimit;

    private readonly object _gate = new();
    private readonly HashSet<nint> _live = [];
    private readonly Dictionary<nint, Action> _waiting = [];
    private readonly Dictionary<nint, StandInWork> _work = [];
    private readonly TimeSpan _budget;
    private readonly PipelineBuildQueue _builds;
    private readonly PipelineBuildQueue _heavyBuilds;
    private readonly OnceLog _warnings;
    private readonly nint* _vtable;
    private readonly GCHandle _self;
    private int _created;
    private int _ready;
    private int _deferred;
    private int _used;
    private int _onDemand;
    private long _skipped;
    private long _skippedAfterChain;
    private long _recorded;

    /// <summary>How a built stand-in's pipeline is made again after it was given up, and the shader bytes of the built ones.</summary>
    private readonly Dictionary<nint, Func<PendingBuild?>> _again = [];
    private readonly Dictionary<nint, long> _bytes = [];
    private long _usedBytes;
    private long _evicted;
    private long _madeAgain;
    private long _heldUses;
    /// <param name="budget">How long a creation may take before the game gets a stand-in.</param>
    /// <param name="builds">The threads that call the driver (started by the caller).</param>
    /// <param name="heavyBuilds">The same queue, or the thread for pipelines of <see cref="PipelineFactory.HeavyShaderBytes"/> or more, which wait for more memory without holding up the others.</param>
    /// <param name="warnings">Where stand-ins and late pipelines are reported, once per name.</param>
    public StandInPipelines(TimeSpan budget, PipelineBuildQueue builds, PipelineBuildQueue heavyBuilds, OnceLog warnings)
    {
        _budget = budget;
        _builds = builds;
        _heavyBuilds = heavyBuilds;
        _warnings = warnings;
        _vtable = StandInPipelineMethods.CreateVtable();
        _self = GCHandle.Alloc(this, GCHandleType.Weak);
    }

    /// <summary>The stand-ins a command list waits for at their first use instead of skipping their work; null for none.</summary>
    public PipelineWaits? Waits { get; init; } = PipelineWaits.Current;

    /// <summary>Stand-ins handed out, how many have their pipeline, the work skipped meanwhile and the work recorded.</summary>
    public string Summary => $"{Volatile.Read(ref _created)} stand-in pipelines ({Volatile.Read(ref _deferred)} built on first use, {Volatile.Read(ref _used)} of them used, {Volatile.Read(ref _onDemand)} with programs made on demand, {Volatile.Read(ref _ready)} built since, {Count(queue => queue.Backlog)} queued, {Count(queue => queue.WaitingForMemory)} build threads waiting for memory), {Interlocked.Read(ref _skipped)} draws and dispatches skipped, {Interlocked.Read(ref _skippedAfterChain)} dispatches and ExecuteIndirects skipped after a skipped one of their list,{Interlocked.Read(ref _recorded)} recorded, {SkippedBeforeSummary()}, {Volatile.Read(ref _smallMade)} small pipelines made at first use ({Volatile.Read(ref _smallProgramsInline)} with programs made inline or found ready,{Volatile.Read(ref _smallNotReady)} not waited for: programs not made, {Volatile.Read(ref _smallOverBudget)} over the first-use budget), {Volatile.Read(ref _waitedFor)} waited for by name ({Volatile.Read(ref _waitsPastLimit)} past the limit), {Volatile.Read(ref _essentialMade)} clears, blits and UI pipelines made at first use ({Volatile.Read(ref _essentialMissed)} not within {EssentialPipelines.Limit.TotalSeconds:F0} s), {Volatile.Read(ref _lightingMade)} lighting pipelines waited for at first use ({Volatile.Read(ref _lightingMissed)} not within {EssentialPipelines.LightingLimit.TotalSeconds:F0} s), {MostSkippedSummary()}";

    /// <summary>
    /// Runs <paramref name="create"/> and writes its pipeline to <paramref name="result"/>; when it takes longer than
    /// the budget, or when earlier creations still wait for a thread, writes a stand-in instead and returns S_OK.
    /// <paramref name="built"/> is told the desktop pipeline when there is one; <paramref name="heavy"/> builds on the heavy thread.
    /// </summary>
    public int Create(Func<PipelineResult> create, nint* result, string what, Action<nint>? built, bool heavy = false, bool small = false, Func<PendingBuild?>? again = null, long bytes = 0)
    {
        var budget = _budget;
        var queue = _builds;

        if (heavy)
        {
            queue = _heavyBuilds;
        }

        if (queue.Backlog > 0)
        {
            budget = TimeSpan.Zero;
        }

        long started = Stopwatch.GetTimestamp();
        var queued = queue.Queue(create, urgent: false);
        var build = queued.Done.Task;
        bool done;

        using (BlockingWaits.Enter("pipeline creation budget"))
        {
            done = build.Wait(budget);
        }

        // A build that failed within the budget (held: --skip-pipelines, ray tracing; or refused by the driver) gives a
        // stand-in that never builds: the console never fails a creation, and a failure handed to the game stalled its
        // loading at the press-a-button screen (boots w66d to w69).
        if (done && (HResult.Failed(build.Result.Hr) || build.Result.Pipeline == 0))
        {
            *result = Unbuilt(what);

            return HResult.Ok;
        }

        if (done)
        {
            *result = build.Result.Pipeline;
            built?.Invoke(build.Result.Pipeline);

            return build.Result.Hr;
        }

        nint standIn = NewStandIn(what, small, "built at creation");
        queued.Use = Use(standIn);

        Track(standIn, new StandInWork { Creation = (queued, queue) });
        Again(standIn, again);
        build.ContinueWith(task => Complete(standIn, task.Result, what, started, built, bytes), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        Interlocked.Increment(ref _created);
        _warnings.Warn("standin", $"[d3d12_xs] pipeline {what} not built in time: the game gets a stand-in, its draws and dispatches are skipped until the driver is done (counted in the sample summary)");
        *result = standIn;

        return HResult.Ok;
    }

    /// <summary>
    /// Writes a stand-in whose build starts only when a command list first sets it (<see cref="Desktop"/>): the game
    /// creates tens of thousands of pipelines while loading and draws with a small part of them. <paramref name="used"/>
    /// runs then, before the build is queued.
    /// </summary>
    public int Deferred(Func<PipelineResult> create, nint* result, string what, Action<nint>? built, bool heavy, Action used, bool small = false, Func<PendingBuild?>? again = null, long bytes = 0)
    {
        nint standIn = NewStandIn(what, small, "built at first use");
        ((StandInPipelineObject*)standIn)->Deferred = 1;

        lock (_gate)
        {
            _waiting[standIn] = () =>
            {
                used();
                var queue = heavy ? _heavyBuilds : _builds;
                long started = Stopwatch.GetTimestamp();
                var queued = queue.Queue(create, urgent: true, Use(standIn));
                Track(standIn, new StandInWork { Creation = (queued, queue) });
                queued.Done.Task.ContinueWith(task => Complete(standIn, task.Result, what, started, built, bytes), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            };
        }

        Again(standIn, again);
        Interlocked.Increment(ref _created);
        Interlocked.Increment(ref _deferred);
        *result = standIn;

        return HResult.Ok;
    }

    /// <summary>
    /// Writes a stand-in whose programs are made on demand (<see cref="OnDemandPrograms"/>): <paramref name="programs"/>
    /// starts making them (given the stand-in's use when a command list waits, null for a prefetch) and, once they are ready, <paramref name="prepare"/> gives
    /// the build, queued as in <see cref="Create"/>; null leaves the stand-in unbuilt. With <paramref name="used"/> this
    /// starts when a command list first sets the stand-in (as <see cref="Deferred"/>), else now. A small compute
    /// stand-in has <paramref name="programsNow"/>, which makes its programs on the thread of its first use.
    /// </summary>
    public int Waiting(Func<StandInUse?, Task> programs, Func<PendingBuild?> prepare, nint* result, Action? used, string what = "", Func<bool>? programsNow = null, string? names = null)
    {
        nint standIn = NewStandIn(what, programsNow is not null, "programs on demand", names);
        Track(standIn, new StandInWork { Programs = programs, ProgramsNow = programsNow });
        Again(standIn, prepare);
        Interlocked.Increment(ref _created);
        Interlocked.Increment(ref _onDemand);

        void Start(bool firstUse)
        {
            long started = Stopwatch.GetTimestamp();
            StandInUse? use = null;

            if (firstUse)
            {
                use = Use(standIn);
            }

            programs(use).ContinueWith(_ => Queue(standIn, prepare, started), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }

        if (used is null)
        {
            Start(firstUse: false);
        }
        else
        {
            ((StandInPipelineObject*)standIn)->Deferred = 1;
            Interlocked.Increment(ref _deferred);

            lock (_gate)
            {
                _waiting[standIn] = () =>
                {
                    used();
                    Start(firstUse: true);
                };
            }
        }

        *result = standIn;

        return HResult.Ok;
    }

    /// <summary>Queues the build of a stand-in whose programs are ready; logs a build that cannot be made.</summary>
    private void Queue(nint standIn, Func<PendingBuild?> prepare, long started)
    {
        PendingBuild? build;

        try
        {
            build = prepare();
        }
        catch (Exception exception) when (exception is InvalidDataException or NotSupportedException)
        {
            _warnings.Warn($"prepare-{exception.Message}", $"[d3d12_xs] pipeline with programs made on demand not built: {exception.Message}");

            return;
        }

        if (build is null)
        {
            return;
        }

        var queue = build.Heavy ? _heavyBuilds : _builds;
        PipelineBuild queued;

        lock (_gate)
        {
            // A stand-in a command list set already goes first; one set later is promoted then (Promote).
            queued = queue.Queue(build.Create, Volatile.Read(ref ((StandInPipelineObject*)standIn)->Used) == 1, Use(standIn));

            if (_work.TryGetValue(standIn, out var work))
            {
                work.Creation = (queued, queue);
            }
        }

        queued.Done.Task.ContinueWith(task => Complete(standIn, task.Result, build.What, started, build.Built, build.ShaderBytes), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    /// <summary>The shader bytes of the built stand-ins a list has set (the ones whose shaders the driver holds) that can be made again; the pipelines given up so far and those made again.</summary>
    public long LiveShaderBytes => Interlocked.Read(ref _usedBytes);

    /// <inheritdoc cref="LiveShaderBytes"/>
    public (long Evicted, long MadeAgain) Evictions => (Interlocked.Read(ref _evicted), Interlocked.Read(ref _madeAgain));

    /// <summary>Keeps how <paramref name="standIn"/>'s pipeline is made again (<see cref="Evict"/>); none for null.</summary>
    private void Again(nint standIn, Func<PendingBuild?>? again)
    {
        if (again is null)
        {
            return;
        }

        lock (_gate)
        {
            _again[standIn] = again;
        }
    }

    /// <summary>
    /// Gives up the built pipelines no command list has set for <paramref name="idle"/> presents (never a clear, blit,
    /// UI or lighting one, whose missing work shows at once): their stand-ins are pending again and build at their next
    /// use, their draws skipped meanwhile as before the first build. The desktop pipelines go to
    /// <paramref name="released"/>: lists recorded with them may still run, so the caller releases them later. Returns
    /// the shader bytes given up.
    /// </summary>
    public long Evict(long idle, List<nint> released)
    {
        long now = StandInUse.Frame;
        long bytes = 0;

        lock (_gate)
        {
            foreach (nint standIn in _again.Keys)
            {
                var item = (StandInPipelineObject*)standIn;
                nint desktop = item->Desktop;

                // One no list ever set holds none of the driver's shader memory: nothing to gain.
                if (desktop == 0 || item->Counted == 0 || now - Volatile.Read(ref item->LastUsed) < idle || _essential.Contains(standIn) || _lighting.Contains(standIn))
                {
                    continue;
                }

                item->Counted = 0;
                Volatile.Write(ref item->Evicted, 1);
                Volatile.Write(ref item->Desktop, 0);
                released.Add(desktop);
                _evicted++;

                if (_bytes.Remove(standIn, out long size))
                {
                    bytes += size;
                }
            }

            _usedBytes -= bytes;
        }

        return bytes;
    }

    /// <summary>Whether <paramref name="standIn"/> has its pipeline, noting its use; one given up starts its build again.</summary>
    [HotPath]
    private bool Built(StandInPipelineObject* standIn)
    {
        // Built and set by a list before, or free to be: one the guard holds at its first use is not.
        if (Volatile.Read(ref standIn->Desktop) != 0 && (standIn->Counted == 1 || !ShaderTableGuard.Holding || Kept((nint)standIn)))
        {
            Volatile.Write(ref standIn->LastUsed, StandInUse.Frame);

            if (standIn->Counted == 0)
            {
                CountUsed(standIn);
            }

            return true;
        }

        if (Volatile.Read(ref standIn->Evicted) == 1 && Interlocked.Exchange(ref standIn->Evicted, 0) == 1)
        {
            MakeAgain((nint)standIn);
        }

        return false;
    }

    /// <summary>Whether <paramref name="standIn"/> is a clear, blit, UI or lighting pipeline, which is never given up or held.</summary>
    private bool Kept(nint standIn)
    {
        lock (_gate)
        {
            return _essential.Contains(standIn) || _lighting.Contains(standIn);
        }
    }

    /// <summary>
    /// Whether the first use of <paramref name="pipeline"/> is held because the driver's shader tables are nearly full
    /// (<see cref="ShaderTableGuard"/>): it stays pending, built or not, and nothing of it is started.
    /// </summary>
    private bool Held(nint pipeline, ref nint desktop, ref bool pending)
    {
        if (!ShaderTableGuard.Holding || Kept(pipeline))
        {
            return false;
        }

        Interlocked.Increment(ref _heldUses);
        desktop = 0;
        pending = true;

        return true;
    }

    /// <summary>Uses of pipelines held at their first use (<see cref="ShaderTableGuard"/>).</summary>
    public long HeldUses => Interlocked.Read(ref _heldUses);

    /// <summary>The first time a list sets a built pipeline: its shader bytes count as held by the driver from now on.</summary>
    private void CountUsed(StandInPipelineObject* standIn)
    {
        lock (_gate)
        {
            if (standIn->Counted == 0)
            {
                standIn->Counted = 1;
                _usedBytes += _bytes.GetValueOrDefault((nint)standIn);
            }
        }
    }

    /// <summary>A command list set a stand-in whose pipeline was given up: its build is prepared and queued again, off this thread.</summary>
    private void MakeAgain(nint standIn)
    {
        Func<PendingBuild?>? prepare;

        lock (_gate)
        {
            _again.TryGetValue(standIn, out prepare);
        }

        if (prepare is null)
        {
            return;
        }

        Interlocked.Increment(ref _madeAgain);
        long started = Stopwatch.GetTimestamp();
        _ = Task.Run(() => Queue(standIn, prepare, started));
    }

    private void Track(nint standIn, StandInWork work)
    {
        lock (_gate)
        {
            _work[standIn] = work;
        }
    }

    /// <summary>
    /// A command list set <paramref name="standIn"/> for the first time: its programs and its creation go before all
    /// speculative work (pipelines built at creation from the journal, prefetched programs).
    /// </summary>
    private void Promote(nint standIn)
    {
        StandInWork? work;

        lock (_gate)
        {
            _work.TryGetValue(standIn, out work);
        }

        if (work is null)
        {
            return;
        }

        if (work.Programs is not null)
        {
            _ = work.Programs(Use(standIn));
        }

        lock (_gate)
        {
            var creation = work.Creation;

            if (creation.HasValue)
            {
                creation.Value.Queue.Promote(creation.Value.Build);
            }
        }
    }

    /// <summary>
    /// The desktop pipeline behind <paramref name="pipeline"/>: the built pipeline of a stand-in (0 and
    /// <paramref name="pending"/> while the driver works), any other pointer as it is.
    /// </summary>
    [HotPath]
    public nint Desktop(nint pipeline, out bool pending)
    {
        pending = false;

        if (pipeline == 0 || *(nint*)pipeline != (nint)_vtable)
        {
            return pipeline;
        }

        var standIn = (StandInPipelineObject*)pipeline;
        nint desktop = Volatile.Read(ref standIn->Desktop);
        pending = desktop == 0;

        if (Built(standIn) || Held(pipeline, ref desktop, ref pending))
        {
            return desktop;
        }

        // Used first, so a deferred stand-in started below queues its creation as urgent.
        bool firstUse = pending && Volatile.Read(ref standIn->Used) == 0 && Interlocked.Exchange(ref standIn->Used, 1) == 0;

        if (firstUse)
        {
            Promote(pipeline);
        }

        if (pending && Volatile.Read(ref standIn->Deferred) == 1 && Interlocked.Exchange(ref standIn->Deferred, 0) == 1)
        {
            Start(pipeline);
        }

        if (firstUse && IsIn(_essential, pipeline))
        {
            desktop = MakeEssential(pipeline, EssentialPipelines.Limit, ref _essentialMade, ref _essentialMissed);
            pending = desktop == 0;
        }
        else if (firstUse && IsIn(_lighting, pipeline))
        {
            desktop = MakeEssential(pipeline, EssentialPipelines.LightingLimit, ref _lightingMade, ref _lightingMissed);
            pending = desktop == 0;
        }
        else if (firstUse && IsIn(_small, pipeline))
        {
            desktop = MakeWithinBudget(pipeline);
            pending = desktop == 0;
        }

        if (pending && Waits is not null)
        {
            desktop = WaitIfNamed(pipeline, Waits);
            pending = desktop == 0;
        }

        return desktop;
    }

    /// <summary>
    /// The first use of a stand-in <paramref name="waits"/> names: this thread waits for its pipeline (made first, as the
    /// first use promoted it) up to the limit, the game's hang watchdog paused meanwhile. Past the limit the stand-in
    /// stays and is not waited for again; its work is skipped as without the option. A heavy mesh pipeline's compile can
    /// take minutes and several gigabytes, so the limit bounds the frame it holds.
    /// </summary>
    private nint WaitIfNamed(nint pipeline, PipelineWaits waits)
    {
        string name = Name(pipeline);

        if (!waits.Matches(name) || !FirstWait(pipeline))
        {
            return 0;
        }

        var standIn = (StandInPipelineObject*)pipeline;
        long started = Stopwatch.GetTimestamp();
        long deadline = started + (long)(waits.Limit.TotalSeconds * Stopwatch.Frequency);

        using (WatchdogPause.Enter())
        using (BlockingWaits.Enter("a pipeline named by --wait-pipelines"))
        {
            while (Volatile.Read(ref standIn->Desktop) == 0 && Stopwatch.GetTimestamp() < deadline)
            {
                Thread.Sleep(1);
            }
        }

        nint desktop = Volatile.Read(ref standIn->Desktop);
        Waited(name, desktop != 0, Stopwatch.GetElapsedTime(started));

        return desktop;
    }

    private bool FirstWait(nint pipeline)
    {
        lock (_gate)
        {
            return _waited.Add(pipeline);
        }
    }

    private void Waited(string name, bool built, TimeSpan took)
    {
        Interlocked.Increment(ref _waitedFor);

        if (built)
        {
            _warnings.Warn($"waited-{name}", $"[d3d12_xs] waited {took.TotalSeconds:F1} s for pipeline {name} at its first use (--wait-pipelines)");

            return;
        }

        Interlocked.Increment(ref _waitsPastLimit);
        _warnings.Warn($"wait-limit-{name}", $"[d3d12_xs] pipeline {name} not built within {took.TotalSeconds:F0} s of its first use (--wait-pipelines): its work is skipped until it is");
    }

    /// <summary>
    /// <see cref="MakeNow"/> while the game's threads have first-use time left (<see cref="FirstUseBudget"/>), the time
    /// it took spent from it; otherwise the stand-in stays.
    /// </summary>
    private nint MakeWithinBudget(nint pipeline)
    {
        if (!FirstUseBudget.Available())
        {
            Interlocked.Increment(ref _smallOverBudget);

            return 0;
        }

        long started = Stopwatch.GetTimestamp();
        nint desktop = MakeNow(pipeline, SmallBudget, essential: false);

        if (desktop != 0)
        {
            Interlocked.Increment(ref _smallMade);
        }

        FirstUseBudget.Spend(Stopwatch.GetTimestamp() - started);

        return desktop;
    }

    /// <summary>Queues the build of a deferred stand-in on its first use.</summary>
    private void Start(nint standIn)
    {
        Action? start;

        lock (_gate)
        {
            _waiting.Remove(standIn, out start);
        }

        Interlocked.Increment(ref _used);
        start?.Invoke();
    }

    /// <summary>Counts a draw or dispatch a command list recorded (draws per frame: its growth over the presents).</summary>
    [HotPath]
    public void Recorded(nint skippedBefore)
    {
        Interlocked.Increment(ref _recorded);

        if (skippedBefore != 0)
        {
            CountSkippedBefore(skippedBefore);
        }
    }

    /// <summary>Counts a dispatch or ExecuteIndirect dropped because an earlier one of its recording was.</summary>
    [HotPath]
    public void SkippedAfterChain()
    {
        Interlocked.Increment(ref _skippedAfterChain);
    }

    /// <summary>Counts a dispatch a command list dropped because its pipeline is still a stand-in.</summary>
    [HotPath]
    public void Skipped(nint standIn)
    {
        Interlocked.Increment(ref _skipped);
        CountSkipped(standIn);

        if (_uses.TryGetValue(standIn, out var use))
        {
            use.Skipped(StandInUse.Frame);
        }
    }

    /// <summary>How much the game misses <paramref name="standIn"/>, kept while it lives.</summary>
    private StandInUse Use(nint standIn)
    {
        return _uses.GetOrAdd(standIn, static _ => new StandInUse());
    }

    /// <summary>The last reference went: releases the desktop pipeline (or lets the build release it) and frees the stand-in.</summary>
    public void Retire(StandInPipelineObject* standIn)
    {
        nint desktop;

        lock (_gate)
        {
            _live.Remove((nint)standIn);
            _waiting.Remove((nint)standIn);
            _work.Remove((nint)standIn);
            _again.Remove((nint)standIn);

            if (_bytes.Remove((nint)standIn, out long size) && standIn->Counted == 1 && standIn->Desktop != 0)
            {
                _usedBytes -= size;
            }

            _small.Remove((nint)standIn);
            _essential.Remove((nint)standIn);
            _lighting.Remove((nint)standIn);
            _waited.Remove((nint)standIn);

            desktop = standIn->Desktop;
        }

        _names.TryRemove((nint)standIn, out _);
        _uses.TryRemove((nint)standIn, out _);
        _kinds.TryRemove((nint)standIn, out _);
        ComObject.Release(desktop);
        NativeMemory.Free(standIn);
    }

    /// <summary>
    /// A stand-in that never gets a pipeline, for a creation that failed: the console never fails one and the game
    /// reads a compute shader's pipeline without a null check (0x14248c30d, the shader object's +0x120).
    /// </summary>
    public nint Unbuilt()
    {
        return Unbuilt(string.Empty);
    }

    /// <summary>A stand-in named <paramref name="what"/> that never gets a pipeline: a held one (--skip-pipelines, ray tracing).</summary>
    public nint Unbuilt(string what)
    {
        Interlocked.Increment(ref _created);

        return NewStandIn(what, kind: HeldKind);
    }

    /// <summary>Counts skipped work by pipeline name; the first <see cref="SkipsLogged"/> names are logged.</summary>
    private void CountSkipped(nint standIn)
    {
        var name = Name(standIn);
        var count = _skippedByName.AddOrUpdate(name, 1, static (_, value) => value + 1);

        if (count == 1)
        {
            _skippedKinds[name] = _kinds.GetValueOrDefault(standIn, string.Empty);
        }

        if (count == 1 && _skippedByName.Count <= SkipsLogged)
        {
            _warnings.Warn($"skipped-{name}", $"[d3d12_xs] work of pipeline {name} skipped: it is a stand-in until the driver is done");
        }
    }

    /// <summary>A list ran work after skipping the stand-in's: its consumers read what the skipped work did not write.</summary>
    private void CountSkippedBefore(nint standIn)
    {
        _skippedBefore.AddOrUpdate(Name(standIn), 1, static (_, value) => value + 1);
    }

    private string SkippedBeforeSummary()
    {
        // A snapshot: LINQ over the dictionary itself sizes an array by Count, then copies while lists add entries
        // (ArgumentException on the sampler thread, which ended a boot at 4,200 presents).
        var entries = _skippedBefore.ToArray();
        var total = entries.Sum(entry => entry.Value);
        var top = string.Join(", ", entries.OrderByDescending(entry => entry.Value).Take(SkipsShown).Select(entry => $"{entry.Key} x{entry.Value}"));

        return $"{total} lists ran work after a skipped one ({top})";
    }

    /// <summary>The pipelines with the most skipped draws and dispatches, each with how its pipeline comes.</summary>
    private string MostSkippedSummary()
    {
        // A snapshot, as in SkippedBeforeSummary.
        var entries = _skippedByName.ToArray();
        var top = entries.OrderByDescending(entry => entry.Value).Take(MostSkippedShown)
            .Select(entry => $"{entry.Key} x{entry.Value} ({_skippedKinds.GetValueOrDefault(entry.Key, "?")})");

        return $"most skipped: {string.Join(", ", top)}";
    }

    /// <summary>Where a stand-in's build is, for the census: built since, waiting for its programs, still building, or released.</summary>
    public string State(nint standIn)
    {
        lock (_gate)
        {
            if (!_live.Contains(standIn))
            {
                return "released";
            }

            if (Volatile.Read(ref ((StandInPipelineObject*)standIn)->Desktop) != 0)
            {
                return "built since";
            }

            if (_waiting.ContainsKey(standIn))
            {
                return "waiting for its programs";
            }

            return "still building";
        }
    }

    /// <summary>The programs of a stand-in, or "unnamed".</summary>
    public string Name(nint standIn)
    {
        return _names.GetValueOrDefault(standIn, "unnamed");
    }

    private bool IsIn(HashSet<nint> set, nint standIn)
    {
        lock (_gate)
        {
            return set.Contains(standIn);
        }
    }

    /// <summary>
    /// The first use of an essential stand-in (<see cref="EssentialPipelines"/>): its programs and pipeline are made
    /// now, up to <paramref name="limit"/>, the hang watchdog paused; no first-use budget applies. Counted in
    /// <paramref name="made"/> or <paramref name="missed"/>.
    /// </summary>
    private nint MakeEssential(nint pipeline, TimeSpan limit, ref int made, ref int missed)
    {
        long started = Stopwatch.GetTimestamp();
        nint desktop;

        using (WatchdogPause.Enter())
        {
            desktop = MakeNow(pipeline, limit, essential: true);
        }

        if (desktop != 0)
        {
            Interlocked.Increment(ref made);

            return desktop;
        }

        Interlocked.Increment(ref missed);
        var took = Stopwatch.GetElapsedTime(started);
        _warnings.Warn($"essential-{Name(pipeline)}", $"[d3d12_xs] pipeline {Name(pipeline)} not made within {took.TotalSeconds:F1} s of its first use: its work is skipped until it is");

        return 0;
    }

    /// <summary>
    /// The first use of a small or essential stand-in: its programs, when not ready, are made on a task of their own, and
    /// its creation runs there too (claimed from the queue), this thread waiting up to <paramref name="budget"/>; the
    /// desktop pipeline, or 0 when the programs cannot be made here or the budget ran out, and the stand-in stays as before.
    /// An essential one keeps waiting when its programs come from the workers instead, and starts its build while other
    /// compiles wait in the compile budget (a tiny one never waits there).
    /// </summary>
    private nint MakeNow(nint pipeline, TimeSpan budget, bool essential)
    {
        var standIn = (StandInPipelineObject*)pipeline;
        var deadline = Stopwatch.GetTimestamp() + (long)(budget.TotalSeconds * Stopwatch.Frequency);
        Task<bool>? programs = null;
        Task? build = null;
        using var wait = BlockingWaits.Enter("small or essential pipeline at first use");

        while (Stopwatch.GetTimestamp() < deadline && Volatile.Read(ref standIn->Desktop) == 0)
        {
            StandInWork? work;

            lock (_gate)
            {
                _work.TryGetValue(pipeline, out work);
            }

            var creation = work?.Creation;

            // Programs not ready: made once on a task (a small program takes milliseconds, the workers' queue a minute
            // or more while loading), then the creation is queued within moments. A small one is never slept on: the
            // Subrender workers of a loading-screen frame meeting hundreds of such stand-ins slept 250 ms on each, and
            // the game's hang watchdog ended the story load (docs/debugging.md#hang-watchdog).
            if (!creation.HasValue)
            {
                programs ??= MakePrograms(work);
                bool failed = programs is null || (programs.IsCompleted && !programs.Result);

                if (work is null || (failed && !essential))
                {
                    break;
                }

                Thread.Sleep(1);

                continue;
            }

            // Not while the build would wait for memory (Build waits without a limit), nor, for a small one, while other
            // compiles wait in the compile budget. On a pool thread: this thread waits only until the deadline, and a build
            // that takes longer finishes there (a 15.6 GB machine spent 6.2 s of a 5.2 s heartbeat stall in this path, #13).
            if (build is null && (essential || CompileBudget.Waiting == 0) && WarmMemoryGuard.RoomForBuild(0))
            {
                var claim = creation.Value;
                build = Task.Run(() => claim.Queue.RunNow(claim.Build));
            }

            Thread.Sleep(1);
        }

        return Volatile.Read(ref standIn->Desktop);
    }

    /// <summary>
    /// Starts making a small stand-in's programs on a pool thread; null when it has no such work. The first use waits
    /// for them only within its budget: made on the game's thread, 4 programs took 5.3 s of a 5.2 s heartbeat stall on a
    /// 15.6 GB machine (#13). Once made, the creation is queued and its build goes on after the first use gave up.
    /// </summary>
    private Task<bool>? MakePrograms(StandInWork? work)
    {
        var make = work?.ProgramsNow;

        if (make is null)
        {
            Interlocked.Increment(ref _smallNotReady);

            return null;
        }

        return Task.Run(() =>
        {
            bool ready = make();

            if (ready)
            {
                Interlocked.Increment(ref _smallProgramsInline);
            }
            else
            {
                Interlocked.Increment(ref _smallNotReady);
            }

            return ready;
        });
    }

    /// <summary>A count over the build queues (one when the heavy pipelines share the others' threads).</summary>
    private int Count(Func<PipelineBuildQueue, int> count)
    {
        if (ReferenceEquals(_builds, _heavyBuilds))
        {
            return count(_builds);
        }

        return count(_builds) + count(_heavyBuilds);
    }

    /// <param name="what">Its programs' names; essential ones (<see cref="EssentialPipelines"/>) are made at first use.</param>
    /// <param name="small">Whether it is made at first use within the first-use budget.</param>
    /// <param name="kind">How its pipeline comes, for the summary's most skipped pipelines.</param>
    /// <param name="names">All its programs' names when <paramref name="what"/> names only some (those made on demand).</param>
    private nint NewStandIn(string what = "", bool small = false, string kind = "", string? names = null)
    {
        var standIn = (StandInPipelineObject*)NativeMemory.AllocZeroed((nuint)sizeof(StandInPipelineObject));
        standIn->Vtable = (nint)_vtable;
        standIn->References = 1;
        standIn->Owner = GCHandle.ToIntPtr(_self);
        _uses[(nint)standIn] = new StandInUse();

        _kinds[(nint)standIn] = kind;
        string classified = names ?? what;
        bool essential = kind != HeldKind && EssentialPipelines.Matches(classified);
        bool lighting = kind != HeldKind && !essential && EssentialPipelines.IsLighting(classified);

        lock (_gate)
        {
            _live.Add((nint)standIn);

            if (small)
            {
                _small.Add((nint)standIn);
            }

            if (essential)
            {
                _essential.Add((nint)standIn);
            }

            if (lighting)
            {
                _lighting.Add((nint)standIn);
            }
        }

        if (what.Length > 0)
        {
            _names[(nint)standIn] = what;
        }

        return (nint)standIn;
    }

    private void Complete(nint standIn, PipelineResult built, string what, long started, Action<nint>? notify, long bytes = 0)
    {
        var took = Stopwatch.GetElapsedTime(started);

        if (HResult.Failed(built.Hr) || built.Pipeline == 0)
        {
            lock (_gate)
            {
                _work.Remove(standIn);
            }

            _warnings.Warn($"late-{what}", $"[d3d12_xs] pipeline {what} failed after {took.TotalSeconds:F0} s: {HResult.Format(built.Hr)}; its draws and dispatches stay skipped");

            return;
        }

        notify?.Invoke(built.Pipeline);

        lock (_gate)
        {
            _work.Remove(standIn);

            if (_live.Contains(standIn))
            {
                Volatile.Write(ref ((StandInPipelineObject*)standIn)->LastUsed, StandInUse.Frame);
                Volatile.Write(ref ((StandInPipelineObject*)standIn)->Desktop, built.Pipeline);
                Interlocked.Increment(ref _ready);

                if (_again.ContainsKey(standIn))
                {
                    _bytes[standIn] = bytes;
                    ((StandInPipelineObject*)standIn)->Counted = 0;
                }

                if (took > SlowBuild)
                {
                    _warnings.Warn($"late-{what}", $"[d3d12_xs] pipeline {what} built after {took.TotalSeconds:F0} s");
                }

                return;
            }
        }

        ComObject.Release(built.Pipeline);
    }
}
