// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Loading;
using FiveStar.Loader.Shims.D3D12X.Pipelines;
using FiveStar.Loader.Shims.Gdk;
using FiveStar.Loader.Shims.Network;

namespace FiveStar.Loader.Host;

/// <summary>Turns the command line and the process environment into the loader's options.</summary>
internal static class HostComposition
{
    private const string WorkFolder = "work";
    private const string DataFolder = "FiveStar";
    private const string GdkDataFolder = "GdkData";

    /// <summary>How long after the pipelines are set up large compiles count as in the game's load window.</summary>
    /// <summary><c>--frame-rate</c> and <c>--check-view-liveness</c>, static settings of the D3D12.X shim.</summary>
    private static void ApplyPacingAndChecks(HostOptions options)
    {
        Shims.D3D12X.Present.FramePacer.Rate = options.FrameRate;
        Shims.D3D12X.DebugLayer.ViewLiveness.Enabled = options.CheckViewLiveness;
        Shims.D3D12X.DebugLayer.NanScan.Filter = options.NanScan;
        Shims.D3D12X.DebugLayer.NanScan.Dwords = options.NanScanDwords;
        Shims.D3D12X.DebugLayer.ExposureTrace.Interval = options.TraceExposure;
        Shims.D3D12X.DebugLayer.ViewTrace.Filter = options.TraceViews;

        Shims.D3D12X.DebugLayer.DispatchCaptures.Specs = options.CaptureDispatch;
        Shims.D3D12X.DebugLayer.DispatchCaptures.Folder = options.CaptureDispatchDirectory;
        Shims.D3D12X.DebugLayer.DispatchCaptures.TranslatedFolder = options.TranslatedDirectory;

        Shims.D3D12X.Resources.GpuTextureRegistry.RetireUnmapped = options.RetireUnmappedTextures;
        Shims.D3D12X.Resources.GpuTextureRegistry.RetireUnreferenced = !options.KeepUnmappedTextures;
        Shims.D3D12X.Resources.GpuTextureRegistry.StaleInFirstPage = options.StaleInFirstPage;
    }

    private static readonly TimeSpan LoadWindowSpan = TimeSpan.FromSeconds(120);

    /// <summary>The shipped MSVC runtime, mapped from the target's directory unless --system-crt is given.</summary>
    public static readonly IReadOnlyList<string> ShippedCrt =
    [
        "msvcp140.dll", "msvcp140_1.dll", "msvcp140_2.dll", "msvcp140_atomic_wait.dll", "msvcp140_codecvt_ids.dll",
        "vcruntime140.dll", "vcruntime140_1.dll", "concrt140.dll", "vccorlib140.dll", "vcomp140.dll",
    ];

    public static LoaderOptions CreateLoaderOptions(HostOptions options)
    {
        var manual = new HashSet<string>(options.ManualModules, StringComparer.OrdinalIgnoreCase);

        if (!options.SystemCrt)
        {
            manual.UnionWith(ShippedCrt);
        }

        return new LoaderOptions(options.SearchDirectories, manual, options.Unresolved, options.CallEntryPoints, WorkDirectory(options), options.MainModule, Environment.ProcessId);
    }

    public static GdkOptions CreateGdkOptions(HostOptions options)
    {
        string dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), DataFolder, GdkDataFolder);
        uint deviceType = options.DeviceType ?? SystemService.XboxScarlettAnaconda;

        var network = new NetworkOptions(options.NetworkMode, options.NetworkLevel);

        return new GdkOptions(options.PackageRoot, options.SaveDirectory, dataRoot, Environment.CurrentDirectory, deviceType, Environment.OSVersion.Version, network);
    }

    /// <summary>The binding and draw-diagnostic switches the options set.</summary>
    private static void ApplyBindingSwitches(HostOptions options)
    {
        Shims.D3D12X.MeshLaunch.Enabled = options.MeshLaunch;
        Shims.D3D12X.ClampBarriers.Requested = options.ClampEnhancedBarriers;

        Shims.D3D12X.IndirectGather.Enabled = options.IndirectGather;
        Shims.D3D12X.IndirectGather.Strict = options.IndirectGatherStrict;
        Shims.D3D12X.IndirectBatch.Enabled = options.IndirectBatch;
        Shims.D3D12X.Binding.PipelineStores.Enabled = options.IndirectGather;

        Shims.D3D12X.Present.GpuKeepAwake.Mode = options.GpuKeepAwake;
        Shims.D3D12X.Pipelines.DepthFormats.TypelessDepth = options.TypelessDepth;
        Shims.D3D12X.XboxDeviceMethods.NoGpuTimeout = options.NoGpuTimeout;
        Shims.D3D12X.Pipelines.PipelineEviction.SetBudget(options.PipelineShaderBudget);

        Shims.D3D12X.BackgroundProcessing.Keep = options.DriverBackgroundWork;
        Shims.D3D12X.BackgroundProcessing.DiscardOnly = options.DiscardDriverMeasurements;
        Shims.D3D12X.Pipelines.ShaderTableGuard.Enabled = !options.NoShaderTableGuard;
        Shims.D3D12X.Binding.GpuUploadHeaps.Enabled = options.GpuUploadHeaps;
        Shims.D3D12X.Binding.VramTwins.RefreshPresents = options.VramTwinPresents;
        Shims.D3D12X.Binding.VramTwins.PoolOnly = options.VramTwinPoolOnly;

        Shims.D3D12X.DebugLayer.DrawCensus.Passes = options.CensusPasses;
        Shims.D3D12X.Binding.PassControls.Rules = Shims.D3D12X.Binding.PassRules.Parse(options.HoldPasses, options.StubPasses);
        Shims.D3D12X.Binding.BufferRecords.HeapIndexOnly = options.BufferRecordsHeapIndex;
        Shims.D3D12X.Binding.RawBufferViews.Enabled = !options.TypedBufferViews;
        Shims.D3D12X.DebugLayer.NullAddressCheck.Enabled = options.CheckNullAddresses;
        Shims.D3D12X.Resources.DepthTwins.Enabled = options.DepthTwins;

        Shims.D3D12X.DebugLayer.TargetClears.Census = options.TargetClearCensus;
        Shims.D3D12X.DebugLayer.TargetClears.ClearEachFrame = options.ClearTargetsEachFrame;

        // No acceleration structure is built without --raytracing: programs made on demand miss every BVH node.
        Shims.D3D12X.Binding.BvhMiss.Enabled = !options.RayTracing;
        Shims.D3D12X.Pipelines.RayTracingPrograms.StandIns = !options.RayTracing;

        // Held passes leave fully lit shadow masks, black reflections and a grey screen-probe light; with --rt-traversal
        // the passes it leaves held keep theirs, and the screen probes trace their own light.
        Shims.D3D12X.Binding.RtStandIns.Enabled = (!options.RayTracing || options.RtTraversal) && !options.NoRtStandIns;
        Shims.D3D12X.Binding.RtStandIns.ScreenProbeLight = !options.RtTraversal;
        Shims.D3D12X.Binding.WindowGuard.Enabled = !options.NoWindowGuard;
        Shims.D3D12X.Pipelines.EssentialPipelines.Lighting = !options.NoLightingWaits;
        Shims.D3D12X.Resources.MapReadback.Enabled = !options.NoMapReadback;
        Shims.D3D12X.Resources.MapReadback.Wait = options.MapReadbackWait;
    }

    /// <summary>The in-place window of <c>--in-place-window</c> (MB), or the default for 0.</summary>
    private static ulong InPlaceWindow(ulong megabytes)
    {
        if (megabytes == 0)
        {
            return Shims.D3D12X.SectionHeaps.WindowBytes;
        }

        return Shims.D3D12X.SectionHeaps.InPlaceWindowOf(megabytes);
    }

    public static PipelineFolders CreatePipelineFolders(HostOptions options)
    {
        CompileBudget.Configure(options.MaxDriverCompiles, options.CompileCeilingBytes);
        Shims.D3D12X.Pipelines.JournalPrebuild.Jobs = options.PrebuildJobs;
        var boot = System.Diagnostics.Stopwatch.StartNew();
        CompileBudget.LoadWindow = () => Diagnostics.GameHangWatch.Loading(boot.Elapsed, LoadWindowSpan);

        ApplyTextureLimits(options);
        Shims.D3D12X.Pipelines.PipelineWaits.Current = Shims.D3D12X.Pipelines.PipelineWaits.Parse(options.WaitPipelines);
        Shims.D3D12X.SectionHeaps.InPlaceWindow = InPlaceWindow(options.InPlaceWindowMegabytes);
        Shims.D3D12X.Resources.TypelessTargets.Enabled = options.TypelessTargets;
        Shims.D3D12X.Binding.ImageViewClasses.Enabled = options.TypedImageViews;

        ApplyPacingAndChecks(options);

        Shims.D3D12X.DebugLayer.MeshProbes.Filter = options.ProbePipeline;

        ApplyBindingSwitches(options);
        string? cache = null;

        if (!options.NoPipelineCache)
        {
            cache = options.PipelineCache ?? Path.Combine(WorkDirectory(options), PipelineLibraryCache.FolderName);
        }

        UnbuildablePipelines.OpenIn(cache);
        PipelineHost.Current = options.HostPipelines ? new PipelineHost(Environment.ProcessPath!) : null;
        var folders = new PipelineFolders(options.ShaderDirectory, options.TranslatedDirectory, cache) { EagerBuilds = options.EagerPipelines };

        if (folders.Programs is not null && !options.NoOnDemand)
        {
            folders = StartOnDemand(options, folders);
        }

        // Root constants need programs compiled for them: without programs made on demand the user data stays in the ring.
        if (options.UserDataConstants && folders.OnDemand is null)
        {
            Shims.D3D12X.Binding.UserDataConstants.Enabled = false;
            folders = folders with { OnDemandNote = $"{folders.OnDemandNote}; --user-data-constants needs programs made on demand: user data stays a root CBV".TrimStart(';', ' ') };
        }

        // Windows open as programs report them: programs made ahead of time report nothing.
        if (options.EvictWindows && folders.OnDemand is null)
        {
            folders = folders with { OnDemandNote = $"{folders.OnDemandNote}; --evict-windows needs programs made on demand: section windows open as before".TrimStart(';', ' ') };
        }

        if (!folders.Enabled)
        {
            return folders;
        }

        return folders with { Index = ProgramIndexLoad.Start(folders.Programs!) };
    }

    /// <summary>Programs made on demand into the program cache, with the replacements of <c>--shader-overrides</c>.</summary>
    private static PipelineFolders StartOnDemand(HostOptions options, PipelineFolders folders)
    {
        var root = options.ProgramCache ?? Path.Combine(WorkDirectory(options), OnDemandStart.FolderName);
        Shims.D3D12X.DebugLayer.PoolStoreTrace.Enabled = options.TracePoolStores;
        Shims.D3D12X.Binding.UserDataConstants.Enabled = options.UserDataConstants;
        Shims.D3D12X.Binding.WindowFeedback.Enabled = options.EvictWindows;
        OnDemandStart.Translation = OnDemandTranslation(options, root);

        var overrides = ReadOverrides(options.ShaderOverrides, out var note);
        OnDemandStart.Overrides = overrides;
        var onDemand = OnDemandStart.Begin(root, options.Dxc, options.DxcBackend, options.ShaderJobs, out var message);

        return folders with { OnDemand = onDemand, OnDemandNote = message + note };
    }

    /// <summary>The replacements in <paramref name="folder"/> (none for null) and a note on them for the log.</summary>
    private static FiveStar.Shaders.Compilation.ShaderOverrides? ReadOverrides(string? folder, out string note)
    {
        note = string.Empty;

        if (folder is null)
        {
            return null;
        }

        if (!Directory.Exists(folder))
        {
            note = $"; --shader-overrides {folder}: no such folder, nothing replaced";

            return null;
        }

        var overrides = FiveStar.Shaders.Compilation.ShaderOverrides.Open(folder);
        note = $"; {overrides.Names.Count} programs replaced from {folder} (--shader-overrides)";

        return overrides;
    }

    /// <summary>Placed texture heaps, kept aliases and the budget of views' textures over memory never mapped.</summary>
    private static void ApplyTextureLimits(HostOptions options)
    {
        Shims.D3D12X.Resources.PlacedTextureHeaps.Enabled = options.PlacedTextures;
        Shims.D3D12X.Resources.GpuTextureRegistry.KeptAliases = options.KeptAliases;
        Shims.D3D12X.Resources.GpuTextures.UnmappedViewBudget = options.UnmappedViewBudgetBytes;
        Shims.D3D12X.Resources.TextureRefreshWorker.WaitAtSubmit = options.TextureRefreshWait;
    }

    /// <summary>
    /// The translation of programs made on demand. Programs earlier boots saw sampling shared-exponent light
    /// (Binding.SharedExponentSamplers, listed in the program cache <paramref name="root"/>) join --shared-exponent-programs.
    /// </summary>
    private static FiveStar.Shaders.Translation.TranslationOptions OnDemandTranslation(HostOptions options, string root)
    {
        IReadOnlyList<string> sharedExponent = [.. options.SharedExponentPrograms, .. Shims.D3D12X.Binding.SharedExponentSamplers.Load(root)];

        return FiveStar.Shaders.Compilation.CorpusOptions.DefaultTranslation with
        {
            Loops = options.LoopCaps, SuperstepCarry = options.SuperstepCarry, RoundsPerStop = options.SuperstepRoundsPerStop, FastWaves = options.FastWaves,
            TypedImageViews = options.TypedImageViews, SharedExponentPrograms = sharedExponent, MeshPayload = options.MeshLaunch,
        };
    }

    /// <summary>--work-dir, or the work folder beside the loader.</summary>
    private static string WorkDirectory(HostOptions options)
    {
        return options.WorkDirectory ?? Path.Combine(AppContext.BaseDirectory, WorkFolder);
    }

    public static LoaderEnvironment CreateEnvironment()
    {
        return new LoaderEnvironment(Environment.SystemDirectory, TimeProvider.System, Environment.FailFast);
    }
}
