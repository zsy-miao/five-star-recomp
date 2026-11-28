// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using FiveStar.Loader.Native;
using FiveStar.Shaders.Containers;
using FiveStar.Shaders.Hardware;

namespace FiveStar.Loader.Shims.D3D12X.Pipelines;

/// <summary>
/// Turns a game pipeline description into a desktop ID3D12PipelineState: each shader becomes its translated program
/// (found by code-block hash prefix in the ahead-of-time translation, never compiled here), the root signature becomes
/// the one translated programs share, the vertex elements are renamed to the translated attributes and the
/// fixed-function descs pass through. Created graphics pipelines are remembered so slot 55 can derive from them.
/// </summary>
internal sealed unsafe class PipelineFactory
{
    /// <summary>
    /// DXIL size from which a pipeline is built alone: the NVIDIA driver took over 40 GB and minutes for one of the
    /// 10 MB ray-traced shadow programs (docs/d3d12x-pipelines.md), so two at once can exhaust memory.
    /// </summary>
    public const long HeavyShaderBytes = 4L << 20;

    /// <summary>
    /// A compute shader container this small (the culling chain's argument initialisers are a few hundred bytes) gets
    /// its pipeline made on its first use, within <see cref="StandInPipelines.SmallBudget"/>, rather than a stand-in
    /// whose skipped dispatch would leave its consumers stale counters.
    /// </summary>
    public const int SmallComputeShaderBytes = 16 << 10;

    /// <summary>
    /// A graphics pipeline whose programs are this small (full-screen clears and blits, the Scaleform UI shaders) gets
    /// the same first-use build: a skipped VS_Clear + PS_Clear draw left the UI target uncleared, and its frames piled up
    /// (two credit cards overlaid in boot w115, made while 2,620 builds were queued ahead of it).
    /// </summary>
    public const int SmallGraphicsShaderBytes = 32 << 10;

    private const int MegabyteShift = 20;
    private const int GigabyteShift = 30;

    /// <summary>D3D12_RASTERIZER_DESC.CullMode and D3D12_CULL_MODE_NONE (d3d12.h).</summary>
    private const int CullModeOffset = 4;
    private const int NoCulling = 1;

    private readonly object _gate = new();
    private readonly object _heavy = new();
    private readonly nint _device2;
    private readonly nint _rootSignature;
    private readonly IProgramLibrary _programs;
    private readonly ProgramSignatures _signatures;
    private readonly OnceLog _warnings;
    private readonly RectanglePipelines? _rectangles;
    private readonly Dictionary<nint, GraphicsPipelineDescription> _created = [];
    private readonly Lazy<PipelineHostRequest?> _hostRequest;

    /// <param name="device2">A desktop ID3D12Device2 (the caller keeps it alive).</param>
    /// <param name="rootSignature">The root signature of translated programs on that device (<see cref="BindlessRoot"/>).</param>
    /// <param name="programs">The translated programs.</param>
    /// <param name="signatures">The input signatures of the extracted programs, for cache layouts.</param>
    /// <param name="warnings">Where failures are reported, once each.</param>
    /// <param name="rectangles">Told every graphics pipeline, for its rectangle-list variant; null for none.</param>
    /// <param name="standIns">Hands out pipelines the driver takes too long for.</param>
    /// <param name="cache">The disk cache of built pipelines; null for none.</param>
    public PipelineFactory(
        nint device2, nint rootSignature, IProgramLibrary programs, ProgramSignatures signatures, OnceLog warnings, RectanglePipelines? rectangles, StandInPipelines standIns,
        PipelineLibraryCache? cache)
    {
        _device2 = device2;
        _rootSignature = rootSignature;
        _programs = programs;
        _signatures = signatures;

        _warnings = warnings;

        _rectangles = rectangles;
        StandIns = standIns;
        Cache = cache;
        _hostRequest = new Lazy<PipelineHostRequest?>(HostRequest);
    }

    /// <summary>The disk cache of built pipelines, or null.</summary>
    public PipelineLibraryCache? Cache { get; }

    /// <summary>Pipelines handed out before the driver built them.</summary>
    public StandInPipelines StandIns { get; }

    /// <summary>Bytes of DXIL the program library keeps in memory.</summary>
    public long ProgramBytes => _programs.HeldBytes;

    /// <summary>The creations running now (<see cref="PipelineSlots"/> marks them).</summary>
    public PipelineCreationsInFlight InFlight { get; } = new();

    /// <summary>The driver compiles running now on build threads, by pipeline, with their age and DXIL size.</summary>
    public PipelineCreationsInFlight Compiling { get; } = new();

    /// <summary>
    /// Where the pipelines the game uses are recorded, for the warm-up and the next boot; null for none. A pipeline is
    /// recorded when a command list first sets it, or at creation without <see cref="Lazy"/>.
    /// </summary>
    public PipelineJournal? Journal { get; init; }

    /// <summary>
    /// Whether, of the pipelines the cache does not have, only the journal's are built at creation and the others when
    /// a command list first sets them: the game creates tens of thousands while loading and draws with a small part of
    /// them. Otherwise every one is built at creation.
    /// </summary>
    public bool Lazy { get; init; }

    /// <summary>
    /// Makes the programs the library lacks when a pipeline needs them (the library is the same object); null when every
    /// program comes from the ahead-of-time translation.
    /// </summary>
    public OnDemandPrograms? OnDemand { get; init; }

    /// <summary>
    /// Whether <see cref="PrepareGraphics"/> makes a missing linked producer on the calling thread (the warm-up) instead
    /// of leaving it to the program workers (<see cref="ProducerLinks"/>).
    /// </summary>
    public bool LinkInline { get; init; }

    public int Graphics(GraphicsPipelineDescription description, Guid* iid, nint* result)
    {
        // --no-draws: a stand-in that never builds, before any program is looked for or made (nor the journal written).
        if (SkippedPipelines.All)
        {
            string names = ProgramNames(description.Shaders);
            *result = StandIns.Unbuilt(names);

            return Remember(description, HResult.Ok, result, names);
        }

        return MakeGraphics(description, iid, result);
    }

    private int MakeGraphics(GraphicsPipelineDescription description, Guid* iid, nint* result)
    {
        var deferral = Deferral(Journal?.Contains(description) ?? false, () => Journal?.Record(description));
        var lacking = Lacking(description.Shaders);

        // A held pair (--skip-pipelines; ray tracing without --raytracing) by the names of all its programs, made or not:
        // its stand-in never builds, and neither its programs nor its linked producer are made. Named by the programs it
        // lacked only, the skipped foliage pairs had their producers linked in the compile host, where a game thread's
        // request then waited (boots w69c and w69d).
        string all = ProgramNames(description.Shaders);

        if (SkippedPipelines.Skips(all))
        {
            *result = StandIns.Unbuilt(all);

            return Remember(description, HResult.Ok, result, all);
        }

        if (lacking is not null || PendingLink(description) is not null)
        {
            // Programs first, then the producer linked to the pixel shader when the pair needs it (ProducerLinks).
            lacking ??= [];
            var onDemand = OnDemand!;
            Func<StandInUse?, Task> work = use => Task.WhenAll(lacking.Select(names => onDemand.Request(names, use is not null, use)))
                .ContinueWith(_ => PendingLink(description)?.Invoke(use) ?? Task.CompletedTask, TaskScheduler.Default).Unwrap();

            var wanted = *iid;
            var what = string.Join(" + ", lacking.Select(names => names[0]));

            if (lacking.Count == 0)
            {
                // Named by its programs, so --wait-pipelines and the skip counts can tell the pairs apart.
                what = $"{string.Join(" + ", Loadable(description)?.Select(program => program.Name) ?? [])} (linked producer)";
            }

            // A pipeline --skip-pipelines names makes no programs or producer: its build fails at once (Build).
            if (SkippedPipelines.Skips(what))
            {
                work = _ => Task.CompletedTask;
            }

            var waiting = StandIns.Waiting(work, () => Prepared(PrepareGraphics(description), wanted), result, deferral, what, ProgramsNow(description, all, lacking, onDemand), all);

            return Remember(description, waiting, result, "programs made on demand");
        }

        var recipe = PrepareGraphics(description);

        if (recipe is null)
        {
            return HResult.NotFound;
        }

        InFlight.Describe(recipe.Names);
        Action<nint> built = pipeline => NoteBuilt(pipeline, recipe);
        var small = recipe.Stream.ShaderBytes <= SmallGraphicsShaderBytes;
        var hr = Create(recipe.Stream, iid, result, recipe.Names, built, deferral, small, GraphicsAgain(description, *iid));

        return Remember(description, hr, result, recipe.Names);
    }

    /// <summary>The build of <paramref name="description"/> made again, for a pipeline given up (<see cref="PipelineEviction"/>).</summary>
    private Func<PendingBuild?> GraphicsAgain(GraphicsPipelineDescription description, Guid iid)
    {
        return () => Prepared(PrepareGraphics(description), iid);
    }

    public int Compute(ComputePipelineDescription description, Guid* iid, nint* result)
    {
        // --no-draws: a stand-in that never builds, before any program is looked for or made.
        if (SkippedPipelines.All)
        {
            *result = StandIns.Unbuilt(ProgramNames([description.Shader]));

            return HResult.Ok;
        }

        var deferral = Deferral(Journal?.Contains(description) ?? false, () => Journal?.Record(description));
        var lacking = Lacking([description.Shader]);
        var small = description.Shader.Bytes.Length <= SmallComputeShaderBytes;

        if (lacking is not null)
        {
            return Later(lacking, () => PrepareCompute(description), iid, result, deferral, small);
        }

        var recipe = PrepareCompute(description);

        if (recipe is null)
        {
            return HResult.NotFound;
        }

        InFlight.Describe(recipe.Names);
        var again = *iid;
        var hr = Create(recipe.Stream, iid, result, recipe.Names, null, deferral, small, () => Prepared(PrepareCompute(description), again));
        Report(hr, recipe.Names);

        return hr;
    }

    /// <summary>
    /// The desktop stream of a graphics description: its translated programs, the vertex elements renamed to the
    /// translated attributes. Null when a shader has no translation (reported once).
    /// </summary>
    public PipelineRecipe? PrepareGraphics(GraphicsPipelineDescription description)
    {
        var programs = new List<TranslatedProgram>();
        IReadOnlyList<SignatureElement>? inputs = null;

        foreach (var source in description.Shaders)
        {
            var program = Resolve(source);

            if (program is null)
            {
                return null;
            }

            if (program.Stage == HardwareStage.Vertex)
            {
                inputs = VertexInputs(source, program);
            }

            programs.Add(program);
        }

        if (OnDemand is not null)
        {
            var linked = OnDemand.Links.Apply(programs, LinkInline);

            // A failed link: the pair as compiled does not link either, so the pipeline stays a stand-in.
            if (linked is null)
            {
                return null;
            }

            programs = linked;
        }

        var desktop = description;

        if (programs.Exists(program => program.Stage == HardwareStage.Vertex))
        {
            desktop = description with { InputLayout = InputLayoutMapping.Map(description.InputLayout, inputs) };
        }

        var names = string.Join(" + ", programs.Select(program => program.Name));
        desktop = CullNone(desktop, names);

        // --depth-func-override, then the tiled lighting's class test as depth bounds: the description (so its key)
        // carries them, rectangle variants included.
        if (DepthFuncOverride.Rules.Count > 0)
        {
            desktop = desktop with { DepthStencil = DepthFuncOverride.Apply(desktop.DepthStencil, names) };
        }

        desktop = desktop with { DepthStencil = ClassDepthBounds.Apply(desktop.DepthStencil, names) };

        return new PipelineRecipe(DesktopPipelineBuilder.GraphicsStream(_device2, _rootSignature, desktop, programs), desktop, programs, names);
    }

    /// <summary>
    /// <c>--force-cull-none &lt;text&gt;</c>: pipelines whose programs' names hold the text cull no faces, to rule the
    /// emitted triangles' winding in or out when a pass draws nothing. Null for none.
    /// </summary>
    public static string? CullNoneFor { get; set; }

    /// <summary>
    /// How a small or essential pair (<see cref="EssentialPipelines"/>) makes its lacking programs on the thread of its
    /// first use rather than in the workers' queue, which kept the UI's blits and Scaleform programs skipped in boot w115
    /// (programs ready after 58 s on average); null for any other pair.
    /// </summary>
    private static Func<bool>? ProgramsNow(GraphicsPipelineDescription description, string names, List<IReadOnlyList<string>> lacking, OnDemandPrograms onDemand)
    {
        if (!EssentialPipelines.Matches(names) && !SmallGraphics(description))
        {
            return null;
        }

        return () => lacking.All(onDemand.MakeNow);
    }

    /// <summary>Whether a graphics description's console shaders total <see cref="SmallGraphicsShaderBytes"/> or less.</summary>
    private static bool SmallGraphics(GraphicsPipelineDescription description)
    {
        long bytes = 0;

        foreach (var source in description.Shaders)
        {
            bytes += source.Bytes.Length;
        }

        return bytes <= SmallGraphicsShaderBytes;
    }

    private static GraphicsPipelineDescription CullNone(GraphicsPipelineDescription description, string names)
    {
        if (CullNoneFor is null || !names.Contains(CullNoneFor, StringComparison.Ordinal))
        {
            return description;
        }

        byte[] rasterizer = [.. description.Rasterizer];
        BitConverter.TryWriteBytes(rasterizer.AsSpan(CullModeOffset), NoCulling);

        return description with { Rasterizer = rasterizer };
    }

    /// <summary>Whether a graphics description's programs and linked producer are all made (nothing to make on demand).</summary>
    public bool Ready(GraphicsPipelineDescription description)
    {
        return Lacking(description.Shaders) is null && PendingLink(description) is null;
    }

    /// <summary>Whether a compute description's program is made.</summary>
    public bool Ready(ComputePipelineDescription description)
    {
        return Lacking([description.Shader]) is null;
    }

    /// <summary>The desktop stream of a compute description; null when its shader has no translation.</summary>
    public PipelineRecipe? PrepareCompute(ComputePipelineDescription description)
    {
        var program = Resolve(description.Shader);

        if (program is null)
        {
            return null;
        }

        if (DebugLayer.DispatchCaptures.Enabled)
        {
            DebugLayer.DispatchCaptures.NoteProgram(program.Name, program.Dxil);
        }

        return new PipelineRecipe(DesktopPipelineBuilder.ComputeStream(_rootSignature, program), null, [program], program.Name);
    }

    /// <summary>A graphics pipeline made by this factory with some of its fixed-function descs replaced.</summary>
    public int Derive(nint basePipeline, IReadOnlyList<PipelineStateOverride> overrides, Guid* iid, nint* result)
    {
        GraphicsPipelineDescription? known;

        lock (_gate)
        {
            _created.TryGetValue(basePipeline, out known);
        }

        if (known is null)
        {
            InFlight.Describe("derive from an unknown base");
            _warnings.Warn("derive-unknown-base", $"[d3d12_xs] pipeline derive: base 0x{basePipeline:x} was not created here");

            return HResult.InvalidArgument;
        }

        var description = known;

        foreach (var entry in overrides)
        {
            description = Apply(description, entry);
        }

        return Graphics(description, iid, result);
    }

    private GraphicsPipelineDescription Apply(GraphicsPipelineDescription description, PipelineStateOverride entry)
    {
        switch ((OverrideKind)entry.Kind)
        {
            case OverrideKind.Rasterizer:
                return description with { Rasterizer = entry.Payload[..PipelineLayout.RasterizerDescSize] };
            case OverrideKind.DepthStencil:
                return description with { DepthStencil = DesktopStateBytes.WidenDepthStencil(entry.Payload) };
            case OverrideKind.Blend:
                return description with { Blend = entry.Payload[..PipelineLayout.BlendDescSize] };
            default:
                _warnings.Warn($"override-{entry.Kind}", $"[d3d12_xs] pipeline derive: override kind {entry.Kind} ignored");

                return description;
        }
    }

    /// <summary>
    /// The pipeline of <paramref name="stream"/>: from the cache on this thread when it is there, else built on the
    /// build threads (<see cref="StandInPipelines"/>) and stored in the cache. Takes ownership of the stream, whose
    /// native copy is freed while it waits for a build thread.
    /// </summary>
    private int Create(DesktopStream stream, Guid* iid, nint* result, string names, Action<nint>? built, Action? deferral, bool small = false, Func<PendingBuild?>? again = null)
    {
        Action<nint> named = pipeline =>
        {
            QueueProgress.Name(pipeline, names);
            built?.Invoke(pipeline);
        };

        // A held pipeline (--skip-pipelines; ray tracing without --raytracing, by what is known and the markers: this is
        // the game's thread, which must not read programs) is a stand-in that never builds, at once, and is not loaded
        // from the cache. One that only its build finds held fails there, and the creation hands out a stand-in
        // (StandInPipelines.Create): the game never gets a failure.
        if (SkippedPipelines.Skips(names) || RayTracingPrograms.Skips(names))
        {
            stream.Dispose();
            *result = StandIns.Unbuilt(names);

            return HResult.Ok;
        }

        // While the game loads, a cached pipeline is loaded on a build thread behind a stand-in, not here: the driver may
        // recompile it on load (seconds each), and the game waits for all its startup creations, at most 8 at a time
        // (boots w141-w148 stood minutes before the start screen, NVIDIA's compiler busy under the cache loads).
        bool loadHere = !CompileBudget.LoadWindow();

        if (loadHere && Cache is not null && !HResult.Failed(Cache.Load(stream, iid, result)))
        {
            stream.Dispose();
            named(*result);

            return HResult.Ok;
        }

        *result = 0;
        var wanted = *iid;
        stream.Release();

        var heavy = stream.ShaderBytes >= HeavyShaderBytes;
        // Build loads a cached pipeline itself, under the compile budget: the driver's recompiles on load take its memory
        // too (w149, which loaded them without it, reached 35 GB private in 90 s).
        // A cached one is a load, not a build of ours: urgent, so the load window's hold on heavy compiles does not keep
        // it back (w150 waited 20 s at first use for cached pipelines held until the window closed).
        bool cached = !loadHere && Cache is not null && Cache.Contains(stream.Key);
        Func<PipelineResult> make = cached ? () => Urgently(() => Build(stream, wanted, names)) : () => Build(stream, wanted, names);

        long bytes = stream.ShaderBytes;

        if (deferral is not null)
        {
            return StandIns.Deferred(make, result, names, named, heavy, deferral, small, again, bytes);
        }

        return StandIns.Create(make, result, names, named, heavy, small, again, bytes);
    }

    /// <summary>Runs <paramref name="make"/> with this thread's compiles urgent (<see cref="CompileBudget.Urgent"/>).</summary>
    private static PipelineResult Urgently(Func<PipelineResult> make)
    {
        bool was = CompileBudget.Urgent;
        CompileBudget.Urgent = true;

        try
        {
            return make();
        }
        finally
        {
            CompileBudget.Urgent = was;
        }
    }

    /// <summary>
    /// What runs on the first use of a pipeline built then: <paramref name="record"/>. Null (build at creation) without
    /// <see cref="Lazy"/>, which records at once, or for a pipeline an earlier boot used.
    /// </summary>
    private Action? Deferral(bool used, Action record)
    {
        if (!Lazy)
        {
            record();

            return null;
        }

        if (used)
        {
            return null;
        }

        return record;
    }

    /// <summary>
    /// The programs of <paramref name="shaders"/> that <see cref="OnDemand"/> has to make, each as the extracted programs
    /// of its prefix; null when every one is loadable now (or has no extracted program or no readable code, which
    /// <see cref="Resolve"/> reports).
    /// </summary>
    private List<IReadOnlyList<string>>? Lacking(IEnumerable<ShaderSource> shaders)
    {
        if (OnDemand is null)
        {
            return null;
        }

        List<IReadOnlyList<string>>? lacking = null;

        foreach (var source in shaders)
        {
            IReadOnlyList<string>? names;

            try
            {
                names = OnDemand.Lacking(ProgramKeys.Prefix(source));
            }
            catch (InvalidDataException)
            {
                return null;
            }

            if (names is not null)
            {
                lacking ??= [];
                lacking.Add(names);
            }
        }

        return lacking;
    }

    /// <summary>The first extracted program name of each of <paramref name="shaders"/>, joined by " + ".</summary>
    private string ProgramNames(IEnumerable<ShaderSource> shaders)
    {
        var names = new List<string>();

        foreach (var source in shaders)
        {
            try
            {
                var found = _programs.Names(ProgramKeys.Prefix(source));

                if (found.Count > 0)
                {
                    names.Add(found[0]);
                }
            }
            catch (InvalidDataException)
            {
                // A shader without a hash prefix names no program.
            }
        }

        return string.Join(" + ", names);
    }

    /// <summary>
    /// Makes the programs of <paramref name="shaders"/> the library lacks and waits for them (the warm-up); true when
    /// none is missing now.
    /// </summary>
    public bool MakePrograms(IEnumerable<ShaderSource> shaders)
    {
        var lacking = Lacking(shaders);

        if (lacking is null)
        {
            return true;
        }

        using var wait = Diagnostics.BlockingWaits.Enter("programs made on demand for a pipeline");
        var made = Task.WhenAll(lacking.Select(names => OnDemand!.Request(names, urgent: true))).GetAwaiter().GetResult();

        return made.All(ready => ready);
    }

    /// <summary>
    /// The work that makes the linked producer of a description whose programs are all loadable, when the pair needs one
    /// that is not made yet; else null.
    /// </summary>
    private Func<StandInUse?, Task>? PendingLink(GraphicsPipelineDescription description)
    {
        if (OnDemand is null)
        {
            return null;
        }

        var programs = Loadable(description);

        if (programs is null)
        {
            return null;
        }

        return OnDemand.Links.Pending(programs);
    }

    /// <summary>The description's programs, each loadable from the library; null when one is not.</summary>
    private List<TranslatedProgram>? Loadable(GraphicsPipelineDescription description)
    {
        var programs = new List<TranslatedProgram>();

        foreach (var source in description.Shaders)
        {
            TranslatedProgram? program;

            try
            {
                program = _programs.Find(ProgramKeys.Prefix(source));
            }
            catch (InvalidDataException)
            {
                return null;
            }

            if (program is null)
            {
                return null;
            }

            programs.Add(program);
        }

        return programs;
    }

    /// <summary>
    /// A stand-in for a pipeline whose programs are made on demand; once they are ready its recipe is made and built as
    /// in <see cref="Create"/>. With a deferral this starts on first use.
    /// </summary>
    private int Later(List<IReadOnlyList<string>> lacking, Func<PipelineRecipe?> prepare, Guid* iid, nint* result, Action? deferral, bool small = false)
    {
        var wanted = *iid;
        var onDemand = OnDemand!;
        Func<StandInUse?, Task> programs = use => Task.WhenAll(lacking.Select(names => onDemand.Request(names, use is not null, use)));

        Func<bool>? programsNow = null;

        if (small)
        {
            programsNow = () => lacking.All(onDemand.MakeNow);
        }

        var what = string.Join(" + ", lacking.Select(names => names[0]));

        return StandIns.Waiting(programs, () => Prepared(prepare(), wanted), result, deferral, what, programsNow);
    }

    /// <summary>The build of a recipe whose programs are ready; null when a program could not be made.</summary>
    private PendingBuild? Prepared(PipelineRecipe? recipe, Guid iid)
    {
        if (recipe is null)
        {
            return null;
        }

        var stream = recipe.Stream;
        stream.Release();

        Action<nint> built = pipeline =>
        {
            QueueProgress.Name(pipeline, recipe.Names);
            NoteBuilt(pipeline, recipe);
        };

        return new PendingBuild(() => Build(stream, iid, recipe.Names), stream.ShaderBytes >= HeavyShaderBytes, recipe.Names, built, stream.ShaderBytes);
    }

    /// <summary>
    /// A built graphics pipeline's rectangle-list variants and its class-test bounds (<see cref="ClassDepthBounds"/>),
    /// whether it was built at once, loaded from the cache or built later. Unmarked, a pipeline built at once kept
    /// GREATER_EQUAL without the bounds floor and passed every lower class: the additive SSS resolve lit whole 128-pixel
    /// tiles of class-0 walls (w385, w421).
    /// </summary>
    private void NoteBuilt(nint pipeline, PipelineRecipe recipe)
    {
        if (recipe.Desktop is null)
        {
            return;
        }

        _rectangles?.Remember(pipeline, recipe.Desktop, recipe.Programs);
        ClassDepthBounds.Built(pipeline, recipe.Desktop.DepthStencil);
    }

    /// <summary>
    /// Whether a build may start on this thread: always on a build thread; on a game thread (<see cref="CompileBudget.NoWait"/>)
    /// only one that is not heavy and fits the compile budget now (<paramref name="ticket"/>: counted in it then, for <see cref="CompileBudget.Exit"/>).
    /// Anything else is pending, its stream kept for the build thread that takes it.
    /// </summary>
    private static bool MayStartHere(long shaderBytes, out CompileTicket? ticket)
    {
        ticket = null;

        if (!CompileBudget.NoWait && !CompileBudget.GiveBack)
        {
            return true;
        }

        // A game thread never builds a heavy one; a build thread with a speculative one gives it back rather than wait.
        if (CompileBudget.NoWait && shaderBytes >= HeavyShaderBytes)
        {
            return false;
        }

        ticket = CompileBudget.TryEnterNow(shaderBytes);

        return ticket is not null;
    }

    /// <summary>
    /// Builds the pipeline of <paramref name="stream"/> on this thread and stores it in the cache, or loads it when an
    /// earlier build stored it meanwhile; disposes the stream (not when pending: E_PENDING on a game thread that may not
    /// start it, <see cref="MayStartHere"/>).
    /// Pipelines with <see cref="HeavyShaderBytes"/> of DXIL or more are built one at a time, and no build starts while
    /// the machine lacks the memory it is expected to take (<see cref="CompileBudget"/>), since one driver compile can
    /// take gigabytes.
    /// </summary>
    public PipelineResult Build(DesktopStream stream, Guid iid, string what)
    {
        // Before the compile budget and the driver, on a build thread (reading the console code of an unmarked program
        // there, never on a game thread: boots w66d to w69 read them on the game's creating threads and its loading
        // stopped): a held ray-tracing pipeline takes no compile slot.
        if (SkippedPipelines.Skips(what) || RayTracingPrograms.Skips(what, RayTracingPrograms.Folder, scan: true))
        {
            stream.Dispose();

            return new PipelineResult(HResult.Fail, 0);
        }

        if (!MayStartHere(stream.ShaderBytes, out CompileTicket? ticket))
        {
            return new PipelineResult(HResult.Pending, 0);
        }

        using (stream)
        {
            // The game asks for the same desktop pipeline again while its first build waits in the queue (game
            // pipelines that differ only in what the desktop form drops): the build of the first one stored it.
            nint cached = 0;

            if (Cache is not null && !HResult.Failed(Cache.Load(stream, &iid, &cached)))
            {
                CompileBudget.Exit(ticket);

                return new PipelineResult(HResult.Ok, cached);
            }

            if (ticket is null && !CompileBudget.Buildable(stream.ShaderBytes))
            {
                return Unbuildable(stream, what);
            }

            ticket ??= CompileBudget.Enter(stream.ShaderBytes);

            Compiling.Begin(PipelineCreationsInFlight.NoSlot);
            Compiling.Describe($"{what} ({stream.ShaderBytes >> MegabyteShift} MB of DXIL)");
            var measure = CompileBudget.Measure(stream.ShaderBytes);

            try
            {
                return Compile(stream, iid, what);
            }
            finally
            {
                Compiling.End();
                CompileBudget.Measured(measure, what);
                CompileBudget.Exit(ticket);
            }
        }
    }

    /// <summary>The driver compile of an admitted pipeline: in a pipeline host first when one is on, the heaviest one at a time.</summary>
    private PipelineResult Compile(DesktopStream stream, Guid iid, string what)
    {
        var stopped = BuildInHost(stream, what);

        if (stopped.HasValue)
        {
            return stopped.Value;
        }

        if (stream.ShaderBytes < HeavyShaderBytes)
        {
            return BuildNow(stream, iid);
        }

        lock (_heavy)
        {
            return BuildNow(stream, iid);
        }
    }

    /// <summary>
    /// A heavy pipeline (<see cref="CompileBudget.IsHeavy"/>) built first in a pipeline host (<c>--host-pipelines</c>):
    /// one the host's limits ended is recorded unbuildable and stays a stand-in (the failure returned); otherwise null,
    /// and the loader creates it, from the driver's cache when the host built it.
    /// </summary>
    private PipelineResult? BuildInHost(DesktopStream stream, string what)
    {
        var host = PipelineHost.Current;

        if (host is null || !CompileBudget.IsHeavy(stream.ShaderBytes))
        {
            return null;
        }

        var request = _hostRequest.Value;

        if (request is null)
        {
            return null;
        }

        long limit = PipelineHost.MemoryLimit(WarmMemoryGuard.AvailablePhysical(), CompileBudget.VendorId);

        // Start and end in the log, not only as details: a host's driver compile overlaps whatever else the GPU does.
        ShimContext.Log?.Info($"  [d3d12_xs] pipeline host: {what} ({stream.ShaderBytes >> MegabyteShift} MB of DXIL) started, limit {limit >> GigabyteShift} GB");
        var result = host.Build(request, stream, limit, PipelineHost.DefaultTimeLimit);
        string line = $"[d3d12_xs] pipeline host: {what} ({stream.ShaderBytes >> MegabyteShift} MB of DXIL) {result.Outcome} (0x{result.HResult:x8}) after {result.Seconds:F0} s, peak {result.PeakBytes >> GigabyteShift} GB of {limit >> GigabyteShift} GB";

        if (result.Reason is not null)
        {
            ShimContext.Log?.Warn($"{line}: {result.Reason}; the loader builds it itself");

            return null;
        }

        if (result.Outcome == PipelineHostOutcome.Crashed)
        {
            ShimContext.Log?.Warn(line + ": the driver crashed in the host, so the loader does not build it; it stays a stand-in");

            return new PipelineResult(HResult.Fail, 0);
        }

        if (!result.Killed)
        {
            ShimContext.Log?.Info("  " + line);

            return null;
        }

        ShimContext.Log?.Warn(line + "; it stays a stand-in");
        _ = UnbuildablePipelines.Add(stream.Key);

        return new PipelineResult(HResult.OutOfMemory, 0);
    }

    /// <summary>The adapter and root signature a pipeline host builds against; null when either is unknown.</summary>
    private PipelineHostRequest? HostRequest()
    {
        var root = BindlessRoot.Serialized();

        if (root is null)
        {
            return null;
        }

        Marshal.AddRef(_device2);
        using var device = new Vortice.Direct3D12.ID3D12Device(_device2);

        return new PipelineHostRequest(device.AdapterLuid, root, 0);
    }

    /// <summary>A pipeline whose compile could never be admitted here: logged once, recorded, left a stand-in.</summary>
    private static PipelineResult Unbuildable(DesktopStream stream, string what)
    {
        if (UnbuildablePipelines.Add(stream.Key))
        {
            long estimate = CompileBudget.Estimate(stream.ShaderBytes) >> GigabyteShift;
            ShimContext.Log?.Warn($"[d3d12_xs] pipeline {what} ({stream.ShaderBytes >> MegabyteShift} MB of DXIL) not built: its compile is expected to take {estimate} GB, more than this machine can give; it stays a stand-in");
        }

        return new PipelineResult(HResult.OutOfMemory, 0);
    }

    private PipelineResult BuildNow(DesktopStream stream, Guid iid)
    {
        nint pipeline = 0;
        int hr = stream.Create(_device2, &iid, &pipeline);

        if (!HResult.Failed(hr) && pipeline != 0)
        {
            Cache?.Store(stream.Key, pipeline);
        }

        return new PipelineResult(hr, pipeline);
    }

    /// <summary>The vertex shader's inputs: from its container, or for a cache code block from its program's container.</summary>
    private IReadOnlyList<SignatureElement>? VertexInputs(ShaderSource source, TranslatedProgram program)
    {
        if (source.Kind != ShaderSourceKind.CacheCode)
        {
            return InputLayoutMapping.Inputs(source.Bytes);
        }

        var inputs = _signatures.Inputs(program.Name);

        if (inputs is null)
        {
            _warnings.Warn($"signature-{program.Name}", $"[d3d12_xs] no DXBC container for {program.Name}: its cache input layout is taken in attribute order");
        }

        return inputs;
    }

    private TranslatedProgram? Resolve(ShaderSource source)
    {
        byte[] prefix;

        try
        {
            prefix = ProgramKeys.Prefix(source);
        }
        catch (InvalidDataException exception)
        {
            _warnings.Warn($"key-{exception.Message}", $"[d3d12_xs] pipeline shader not readable: {exception.Message}");

            return null;
        }

        var program = _programs.Find(prefix);

        if (program is null)
        {
            var key = ProgramKeys.Text(prefix);
            var names = _programs.Names(prefix);
            _warnings.Warn($"program-{key}", $"[d3d12_xs] no translated program for shader {key}");

            if (names.Count == 0)
            {
                InFlight.Describe(PipelineFailureTally.MissingProgram + key);
            }
            else
            {
                InFlight.Describe($"no loadable form of {names[0]}");
            }
        }

        return program;
    }

    private int Remember(GraphicsPipelineDescription description, int hr, nint* result, string names)
    {
        Report(hr, names);

        if (HResult.Failed(hr) || *result == 0)
        {
            return hr;
        }

        lock (_gate)
        {
            _created[*result] = description;
        }

        return hr;
    }

    private void Report(int hr, string what)
    {
        if (HResult.Failed(hr))
        {
            InFlight.Describe($"desktop creation {HResult.Format(hr)} ({what})");
            _warnings.Warn($"create-{hr:x8}-{what}", $"[d3d12_xs] desktop pipeline creation failed ({what}): {HResult.Format(hr)}");
        }
    }
}
