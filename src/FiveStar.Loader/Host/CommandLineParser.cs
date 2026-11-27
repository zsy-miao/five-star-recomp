// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using FiveStar.Loader.Loading;
using FiveStar.Loader.Shims.Network;

namespace FiveStar.Loader.Host;

/// <summary>Parses the fivestar-loader command line; <see cref="HostUsage"/> lists the options.</summary>
internal sealed class CommandLineParser
{
    private const double BytesPerGigabyte = 1L << 30;
    private const long BytesPerMebibyte = 1L << 20;

    private readonly string[] _args;
    private readonly HostOptions _options = new();
    private int _index;

    public CommandLineParser(string[] args)
    {
        _args = args;
    }

    /// <summary>Parses every argument; stops early at --help and --list-safe-calls.</summary>
    public HostOptions Parse()
    {
        for (_index = 0; _index < _args.Length; _index++)
        {
            if (!Apply(_args[_index]))
            {
                break;
            }
        }

        return _options;
    }

    /// <summary>Applies one option; false ends parsing.</summary>
    private bool Apply(string option)
    {
        switch (option)
        {
            case "--load":
                return Add(_options.Loads);
            case "--call":
                return Add(_options.Calls);
            case "--manual":
                return AddManual();
            case "--system-crt":
                return SetSystemCrt();
            case "--search":
                return AddSearchDirectory();
            case "--unresolved":
                return SetUnresolved();
            case "--no-entry":
                return DisableEntryPoints();
            case "--no-shim":
                return Add(_options.DisabledShims);
            case "--no-exports":
                return DisableExportListing();
            case "--unload":
                return SetUnload();
            case "--work-dir":
                return SetWorkDirectory();
            case "--log":
                return SetLogFile();
            case "--main":
                return SetMainModule();
            case "--patch-peb":
                return SetPatchPeb();
            case "--package-root":
                return SetPackageRoot();
            case "--save-dir":
                return SetSaveDirectory();
            case "--gdk-abi":
                return SetGdkAbi();
            case "--device-type":
                return SetDeviceType();
            case "--patches":
                return AddFullPath(_options.Patches);
            case "--mods":
                return AddFullPath(_options.ModFolders);
            case "--input":
                return SetInputConfig();
            case "-v":
                return SetVerbose();
            case "--list-safe-calls":
                return StopWithListSafeCalls();
            case "-h":
            case "--help":
                return StopWithHelp();
            default:
                return ApplyOther(option);
        }
    }

    /// <summary>Options handled past the main switch, to keep <see cref="Apply"/> within the line limit.</summary>
    private bool ApplyOther(string option)
    {
        switch (option)
        {
            case "--shader-dir":
                return SetShaderDirectory();
            case "--translated-dir":
                return SetTranslatedDirectory();
            case "--classify":
                return SetClassifyInput();
            case "--classify-out":
                return SetClassifyOutput();
            case "--run":
                return SetRunMain();
            case "--run-timeout":
                return SetRunTimeout();
            case "--input-script":
                return SetInputScript();
            case "--allow-real-input" or "--script-input-only":
                return SetRealInput(option);
            case "--sample-threads":
                return SetSampleThreads();
            case "--profile-threads" or "--profile-managed" or "--cpu-sample":
                return SetProfile(option);
            case "--trace-module":
                return Add(_options.TracedModules);
            case "--trace-system":
                return SetTraceSystem();
            case "--ffx":
                return SetFidelityFxLibrary();
            case "--d3d12-debug" or "--d3d12-gpu-validation" or "--d3d12-dred":
                return SetD3D12Debug(option);
            case "--d3d12-skip":
                return Add(_options.D3D12Skips);
            case "--d3d12-trace-queues":
                return SetTraceQueues();
            case "--d3d12-breadcrumbs" or "--gpu-timings":
                return SetBreadcrumbs(option);
            case "--d3d12-trace-draws" or "--d3d12-trace-pipeline" or "--d3d12-trace-records" or "--d3d12-watch-target" or "--d3d12-watch-limit" or "--d3d12-watch-rendered" or "--log-draws" or "--capture-when-world" or "--d3d12-dump-targets" or "--dump-frame" or "--dump-frame-dir" or "--dump-frame-max" or "--dump-frame-filter" or "--dump-frame-min-width" or "--probe-pipeline" or "--census-passes" or "--hold-passes" or "--stub-passes":
                return SetTraceDraws(option);
            case "--capture-dir" or "--capture-presents" or "--pix-capture" or "--pix-capture-dir":
                return SetCapture(option);
            case "--depth-func-override" or "--no-class-depth-bounds":
                return SetDepthFuncOverride(option);
            case "--present-test":
                return SetPresentTest();
            case "--raytracing" or "--raytracing-gpu" or "--rt-traversal" or "--no-rt-stand-ins":
                return SetRayTracing(option);
            case "--xdsp-convolution":
                return SetXDspConvolution();
            case "--network":
                return SetNetworkMode();
            case "--network-level":
                return SetNetworkLevel();
            case "--cpu":
                return SetCpu();
            default:
                return ApplyCacheAndStorage(option);
        }
    }

    /// <summary>The pipeline-cache, warm-up and DirectStorage options, <c>--watch-transient</c>, <c>--screens</c> and the audio capture.</summary>
    private bool ApplyCacheAndStorage(string option)
    {
        switch (option)
        {
            case "--watch-transient" or "--watch-hang" or "--skip-compute-chains" or "--empty-indirect-after-skip" or "--retire-waits-all-queues" or "--no-split-at-waits" or "--user-data-constants" or "--buffer-records-heap-index" or "--typed-buffer-views" or "--check-null-addresses" or "--no-window-guard" or "--trace-pool-stores" or "--watch-pool-pages":
                return SetWatch(option);
            case "--watch-4g-lines":
                return SetFourGiBLines();
            case "--watch-movies" or "--break-line-writes":
                return SetMoviesOrLineBreaks(option);
            case "--screens":
                return SetScreensFile();
            case "--audio-capture":
                return SetAudioCapture();
            case "--record-audio":
                return SetRecordAudio();
            case "--desktop-timers":
                _options.DesktopTimers = true;

                return true;
            case "--bcpack-missing":
                return SetBcPackMissing();
            case "--bcpack-tables":
                return SetBcPackTables();
            case "--pipeline-cache":
                return SetPipelineCache();
            case "--no-pipeline-cache":
                return DisablePipelineCache();
            case "--warm-pipelines":
                return SetWarmSource();
            case "--warm-jobs" or "--warm-step" or "--warm-processes" or "--warm-shard":
                return SetWarmSplit(option);
            case "--warm-match":
                return Add(_options.WarmMatches);
            case "--eager-pipelines":
                _options.EagerPipelines = true;

                return true;
            case "--warm-used-only":
                _options.WarmUsedOnly = true;

                return true;
            case "--program-cache" or "--shader-overrides":
                return SetProgramFolder(option);
            case "--no-on-demand":
                _options.NoOnDemand = true;

                return true;
            case "--dxc":
                _options.Dxc = Path.GetFullPath(Next());

                return true;
            case "--shader-jobs" or "--loop-caps" or "--force-cull-none" or "--skip-pipelines" or "--mirror-table-records" or "--plain-supersteps" or "--host-pipelines" or "--superstep-round-budget" or "--fast-waves":
                return SetShaderJobs(option);
            case "--shader-backend":
                return SetShaderBackend();
            default:
                return ApplyMemory(option);
        }
    }

    /// <summary><c>--program-cache</c> or <c>--shader-overrides</c>: a folder of programs.</summary>
    private bool SetProgramFolder(string option)
    {
        var folder = Path.GetFullPath(Next());

        if (option == "--shader-overrides")
        {
            _options.ShaderOverrides = folder;

            return true;
        }

        _options.ProgramCache = folder;

        return true;
    }

    private bool SetPipelineCache()
    {
        _options.PipelineCache = Path.GetFullPath(Next());

        return true;
    }

    private bool DisablePipelineCache()
    {
        _options.NoPipelineCache = true;

        return true;
    }

    private bool SetWarmSource()
    {
        _options.WarmSource = Path.GetFullPath(Next());

        return true;
    }

    /// <summary>--warm-jobs n, --warm-step k, --warm-processes n, or --warm-shard i/n (set by a split warm-up's parent).</summary>
    private bool SetWarmSplit(string option)
    {
        if (option == "--warm-jobs")
        {
            return SetWarmJobs();
        }

        if (option == "--warm-step")
        {
            return SetWarmStep();
        }

        if (option == "--warm-processes")
        {
            _options.WarmProcesses = int.Parse(Next(), CultureInfo.InvariantCulture);

            return true;
        }

        _options.WarmShard = WarmShards.ParseShard(Next()) ?? throw new CommandLineException("--warm-shard takes i/n with 0 <= i < n");

        return true;
    }

    private bool SetWarmJobs()
    {
        _options.WarmJobs = int.Parse(Next(), CultureInfo.InvariantCulture);

        return true;
    }

    /// <summary>
    /// --shader-jobs N, --loop-caps victory|default|compute,geometry,pixel, --plain-supersteps, --fast-waves, --host-pipelines (programs made on demand) or
    /// --force-cull-none text or --skip-pipelines text,....
    /// </summary>
    private bool SetShaderJobs(string option)
    {
        if (option == "--mirror-table-records")
        {
            return SetMirrorTableRecords(Next());
        }

        if (option == "--skip-pipelines")
        {
            _options.SkipPipelines = Shims.D3D12X.Pipelines.SkippedPipelines.Parse(Next());

            return true;
        }

        if (option == "--plain-supersteps")
        {
            _options.SuperstepCarry = false;

            return true;
        }

        if (option == "--fast-waves")
        {
            _options.FastWaves = true;

            return true;
        }

        if (option == "--host-pipelines")
        {
            _options.HostPipelines = true;

            return true;
        }

        if (option == "--force-cull-none")
        {
            _options.ForceCullNone = Next();

            return true;
        }

        if (option == "--superstep-round-budget")
        {
            _options.SuperstepRoundsPerStop = int.Parse(Next(), CultureInfo.InvariantCulture);

            return true;
        }

        if (option == "--loop-caps")
        {
            _options.LoopCaps = FiveStar.Shaders.Translation.Core.LoopCaps.Parse(Next()) ?? throw new ArgumentException("--loop-caps takes victory, default or compute,geometry,pixel");

            return true;
        }

        _options.ShaderJobs = int.Parse(Next(), CultureInfo.InvariantCulture);

        return true;
    }

    /// <summary>--mirror-table-records reach, full or a count.</summary>
    private bool SetMirrorTableRecords(string value)
    {
        _options.MirrorTableReach = value.Equals("reach", StringComparison.OrdinalIgnoreCase);

        if (!_options.MirrorTableReach)
        {
            _options.MirrorTableRecords = Shims.D3D12X.Binding.DescriptorWindow.ParseTableRecords(value) ?? throw new ArgumentException("--mirror-table-records takes reach, full or a count from 1 to 1024");
        }

        return true;
    }

    private bool SetCompileLimit(string option)
    {
        var value = double.Parse(Next(), CultureInfo.InvariantCulture);

        if (option == "--max-driver-compiles")
        {
            _options.MaxDriverCompiles = (int)value;
        }
        else
        {
            _options.CompileCeilingBytes = (long)(value * BytesPerGigabyte);
        }

        return true;
    }

    private bool SetShaderBackend()
    {
        var backend = Next();

        if (backend is not ("dxil" or "dxc"))
        {
            throw new CommandLineException($"--shader-backend {backend}: expected dxil or dxc");
        }

        _options.DxcBackend = backend == "dxc";

        return true;
    }

    private bool SetWarmStep()
    {
        _options.WarmStep = int.Parse(Next(), CultureInfo.InvariantCulture);

        return true;
    }

    private string Next()
    {
        if (_index + 1 >= _args.Length)
        {
            throw new CommandLineException($"{_args[_index]} needs a value");
        }

        _index++;

        return _args[_index];
    }

    private bool Add(List<string> values)
    {
        values.Add(Next());

        return true;
    }

    private bool AddFullPath(List<string> values)
    {
        values.Add(Path.GetFullPath(Next()));

        return true;
    }

    private bool AddManual()
    {
        foreach (string module in Next().Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            _options.ManualModules.Add(ModuleNames.NormalizeImport(module));
        }

        return true;
    }

    private bool SetSystemCrt()
    {
        _options.SystemCrt = true;

        return true;
    }

    private bool AddSearchDirectory()
    {
        _options.SearchDirectories.Add(Path.GetFullPath(Next()));

        return true;
    }

    private bool SetUnresolved()
    {
        _options.Unresolved = Enum.Parse<UnresolvedMode>(Next(), true);

        return true;
    }

    private bool DisableEntryPoints()
    {
        _options.CallEntryPoints = false;

        return true;
    }

    private bool DisableExportListing()
    {
        _options.ListExports = false;

        return true;
    }

    private bool SetUnload()
    {
        _options.Unload = true;

        return true;
    }

    private bool SetWorkDirectory()
    {
        _options.WorkDirectory = Path.GetFullPath(Next());

        return true;
    }

    private bool SetLogFile()
    {
        _options.LogFile = Next();

        return true;
    }

    private bool SetMainModule()
    {
        _options.MainModule = Path.GetFileName(Next()).ToLowerInvariant();

        return true;
    }

    private bool SetPatchPeb()
    {
        _options.PatchPeb = true;

        return true;
    }

    private bool SetPackageRoot()
    {
        _options.PackageRoot = Path.GetFullPath(Next());

        return true;
    }

    private bool SetSaveDirectory()
    {
        _options.SaveDirectory = Path.GetFullPath(Next());

        return true;
    }

    private bool SetShaderDirectory()
    {
        _options.ShaderDirectory = Path.GetFullPath(Next());

        return true;
    }

    private bool SetTranslatedDirectory()
    {
        _options.TranslatedDirectory = Path.GetFullPath(Next());

        return true;
    }

    private bool SetGdkAbi()
    {
        _options.GdkAbiTable = Next();

        return true;
    }

    private bool SetDeviceType()
    {
        _options.DeviceType = uint.Parse(Next(), CultureInfo.InvariantCulture);

        return true;
    }

    private bool SetInputConfig()
    {
        _options.InputConfig = Path.GetFullPath(Next());

        return true;
    }

    private bool SetRealInput(string option)
    {
        _options.AllowRealInput = option == "--allow-real-input";

        return true;
    }

    private bool SetInputScript()
    {
        _options.InputScript = Path.GetFullPath(Next());

        return true;
    }

    private bool SetScreensFile()
    {
        _options.ScreensFile = Path.GetFullPath(Next());

        return true;
    }

    private bool SetRunMain()
    {
        _options.RunMain = true;

        return true;
    }

    private bool SetRunTimeout()
    {
        _options.RunTimeout = TimeSpan.FromSeconds(double.Parse(Next(), CultureInfo.InvariantCulture));

        return true;
    }

    private bool SetSampleThreads()
    {
        _options.SampleThreads = TimeSpan.FromSeconds(double.Parse(Next(), CultureInfo.InvariantCulture));

        return true;
    }

    /// <summary><c>--retire-unmapped-textures</c>.</summary>
    private bool SetRetireUnmapped()
    {
        _options.RetireUnmappedTextures = true;

        return true;
    }

    /// <summary><c>--d3d12-nan-scan &lt;pipeline text&gt;</c>, its dwords, and <c>--trace-exposure &lt;n&gt;</c>.</summary>
    private bool SetNanScan(string option)
    {
        if (option == "--trace-exposure")
        {
            _options.TraceExposure = Math.Max(1, int.Parse(Next(), CultureInfo.InvariantCulture));

            return true;
        }

        if (option == "--trace-views")
        {
            _options.TraceViews = Next();

            return true;
        }

        if (option == "--d3d12-nan-scan-dwords")
        {
            _options.NanScanDwords = Shims.D3D12X.DebugLayer.NanScan.ParseDwords(Next());

            return true;
        }

        _options.NanScan = Next();

        return true;
    }

    /// <summary><c>--capture-dispatch &lt;text&gt;[@n],...</c> and <c>--capture-dispatch-dir &lt;dir&gt;</c>.</summary>
    private bool SetCaptureDispatch(string option)
    {
        if (option == "--capture-dispatch-dir")
        {
            _options.CaptureDispatchDirectory = Path.GetFullPath(Next());

            return true;
        }

        _options.CaptureDispatch = Shims.D3D12X.DebugLayer.DispatchCaptures.Parse(Next());

        return true;
    }

    /// <summary><c>--check-view-liveness</c>.</summary>
    private bool SetViewLiveness()
    {
        _options.CheckViewLiveness = true;

        return true;
    }

    /// <summary><c>--frame-rate &lt;30|60|uncapped&gt;</c>.</summary>
    private bool SetFrameRate()
    {
        _options.FrameRate = FrameRates.Parse(Next());

        return true;
    }

    private bool SetAudioCapture()
    {
        _options.AudioCapture = Path.GetFullPath(Next());

        return true;
    }

    /// <summary>The memory map and the memory limits.</summary>
    private bool ApplyMemory(string option)
    {
        switch (option)
        {
            case "--memory-map":
                return SetMemoryMap();
            case "--frame-rate":
                return SetFrameRate();
            case "--check-view-liveness":
                return SetViewLiveness();
            case "--d3d12-nan-scan" or "--d3d12-nan-scan-dwords" or "--trace-exposure" or "--trace-views":
                return SetNanScan(option);
            case "--capture-dispatch" or "--capture-dispatch-dir":
                return SetCaptureDispatch(option);
            case "--retire-unmapped-textures":
                return SetRetireUnmapped();
            case "--keep-unmapped-textures":
                _options.KeepUnmappedTextures = true;

                return true;
            case "--stale-in-first-page":
                _options.StaleInFirstPage = true;

                return true;
            case "--min-free-memory":
                return SetMemoryFloor();
            case "--max-commit":
                return SetCommitCeiling();
            case "--section-heap-budget":
                _options.SectionHeapBudget = Gigabytes(Next());

                return true;
            case "--in-place-window":
                _options.InPlaceWindowMegabytes = ulong.Parse(Next(), CultureInfo.InvariantCulture);

                return true;
            case "--evict-windows":
                _options.EvictWindows = true;

                return true;
            case "--reserve-gpu-system-memory":
                _options.ReservedSystemMemory = Gigabytes(Next());

                return true;
            case "--max-driver-compiles" or "--compile-ceiling":
                return SetCompileLimit(option);
            default:
                return ApplyShaderTable(option) || ApplyResources(option);
        }
    }

    /// <summary>The options about the driver's shader tables (docs/running.md#developer-mode).</summary>
    private bool ApplyShaderTable(string option)
    {
        switch (option)
        {
            case "--driver-background-work":
                _options.DriverBackgroundWork = true;

                return true;
            case "--discard-driver-measurements":
                _options.DiscardDriverMeasurements = true;

                return true;
            case "--no-shader-table-guard":
                _options.NoShaderTableGuard = true;

                return true;
            case "--pipeline-shader-budget":
                _options.PipelineShaderBudget = int.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture);

                return true;
            default:
                return false;
        }
    }

    /// <summary>Texture, view and pipeline-build options.</summary>
    private bool ApplyResources(string option)
    {
        switch (option)
        {
            case "--placed-textures":
                _options.PlacedTextures = true;

                return true;
            case "--typed-image-views":
                _options.TypedImageViews = true;

                return true;
            case "--no-shared-exponent-copies":
                _options.SharedExponentCopies = false;

                return true;
            case "--shared-exponent-programs":
                _options.SharedExponentPrograms = Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                return true;
            case "--d3d12-probe-traced":
                _options.D3D12ProbeTraced = true;

                return true;
            case "--depth-twins":
                _options.DepthTwins = true;

                return true;
            case "--typeless-targets":
                _options.TypelessTargets = true;

                return true;
            case "--texture-refresh-wait":
                _options.TextureRefreshWait = true;

                return true;
            case "--committed-textures":
                _options.PlacedTextures = false;

                return true;
            case "--prebuild-jobs":
                _options.PrebuildJobs = int.Parse(Next(), CultureInfo.InvariantCulture);

                return true;
            case "--wait-pipelines":
                _options.WaitPipelines = Next();

                return true;
            case "--kept-aliases":
                _options.KeptAliases = Math.Max(0, int.Parse(Next(), CultureInfo.InvariantCulture));

                return true;
            case "--unmapped-view-budget":
                _options.UnmappedViewBudgetBytes = Math.Max(0, long.Parse(Next(), CultureInfo.InvariantCulture)) * BytesPerMebibyte;

                return true;
            default:
                return ApplyTargetClears(option);
        }
    }

    /// <summary>The accumulating-image diagnostics: the clear census and the per-frame clear.</summary>
    private bool ApplyTargetClears(string option)
    {
        switch (option)
        {
            case "--target-clear-census":
                _options.TargetClearCensus = true;

                return true;
            case "--clear-targets-each-frame":
                _options.ClearTargetsEachFrame = true;

                return true;
            default:
                return ApplyPerformance(option);
        }
    }

    /// <summary>Frame-time measurement and the A/B switches of the perf work.</summary>
    private bool ApplyPerformance(string option)
    {
        switch (option)
        {
            case "--gpu-upload-heaps":
                _options.GpuUploadHeaps = true;

                return true;
            case "--mesh-launch":
                _options.MeshLaunch = true;

                return true;
            case "--no-lighting-waits":
                _options.NoLightingWaits = true;

                return true;
            case "--no-map-readback":
                _options.NoMapReadback = true;

                return true;

            case "--map-readback-wait":
                _options.MapReadbackWait = true;

                return true;
            case "--clamp-enhanced-barriers":
                _options.ClampEnhancedBarriers = true;

                return true;
            case "--no-draws":
                _options.NoDraws = true;

                return true;
            case "--no-gpu-timeout":
                _options.NoGpuTimeout = true;

                return true;
            case "--d3dkmt-watch":
                _options.D3dkmtWatch = true;

                return true;

            case "--typeless-depth":
                _options.TypelessDepth = true;

                return true;
            case "--gc-sustained":
                _options.SustainedGc = true;

                return true;
            case "--frame-log":
                _options.FrameLog = Math.Max(1, int.Parse(Next(), CultureInfo.InvariantCulture));

                return true;
            default:
                return ApplyGpuWork(option);
        }
    }

    /// <summary>The A/B switches of how our own GPU work runs: indirect-argument gathering, the keep-awake queue.</summary>
    private bool ApplyGpuWork(string option)
    {
        switch (option)
        {
            case "--indirect-gather":
                _options.IndirectGather = true;

                return true;
            case "--indirect-batch":
                _options.IndirectGather = true;
                _options.IndirectBatch = true;

                return true;
            case "--indirect-gather-strict":
                _options.IndirectGather = true;
                _options.IndirectGatherStrict = true;

                return true;
            case "--indirect-gather-relaxed":
                _options.IndirectGather = true;
                _options.IndirectGatherStrict = false;

                return true;
            case "--gpu-keep-awake":
                _options.GpuKeepAwake = Shims.D3D12X.Present.GpuKeepAwakeMode.On;

                return true;
            case "--gpu-keep-awake-turns":
                _options.GpuKeepAwake = Shims.D3D12X.Present.GpuKeepAwakeMode.Turns;

                return true;
            case "--output":
                _options.OutputSize = OutputSize(Next());

                return true;
            case "--aftermath" or "--aftermath-dll" or "--aftermath-markers" or "--aftermath-resources" or "--aftermath-shader-info" or "--aftermath-selftest":
                return SetAftermath(option);
            default:
                throw new CommandLineException($"unknown argument {option}");
        }
    }

    /// <summary>The parts of WxH.</summary>
    private const int OutputParts = 2;

    /// <summary>One of the title's output modes as WxH (<see cref="Mods.OutputSizePatches.Modes"/>).</summary>
    private static (int Width, int Height) OutputSize(string text)
    {
        var parts = text.Split('x', 'X');

        if (parts.Length == OutputParts && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int width)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int height) && Mods.OutputSizePatches.IsMode(width, height))
        {
            return (width, height);
        }

        throw new CommandLineException($"--output takes one of the title's modes: {string.Join(", ", Mods.OutputSizePatches.Modes.Select(mode => $"{mode.Width}x{mode.Height}"))}");
    }

    private bool SetMemoryFloor()
    {
        _options.MinimumFreeMemory = Gigabytes(Next());

        return true;
    }

    private bool SetCommitCeiling()
    {
        _options.MaximumCommit = Gigabytes(Next());

        return true;
    }

    private static ulong Gigabytes(string text)
    {
        return (ulong)(double.Parse(text, CultureInfo.InvariantCulture) * BytesPerGigabyte);
    }

    private bool SetMemoryMap()
    {
        _options.MemoryMap = TimeSpan.FromSeconds(double.Parse(Next(), CultureInfo.InvariantCulture));

        return true;
    }

    /// <summary>--profile-threads, --profile-managed &lt;start&gt;[:&lt;seconds&gt;] or --cpu-sample &lt;ms&gt;[:&lt;seconds&gt;].</summary>
    private bool SetProfile(string option)
    {
        if (option == "--profile-threads")
        {
            _options.ProfileThreads = true;

            return true;
        }

        string value = Next();

        if (option == "--cpu-sample")
        {
            if (!Diagnostics.CpuSampler.TryParse(value, out var interval, out var report))
            {
                throw new CommandLineException($"--cpu-sample {value}: expected <milliseconds>[:<report seconds>]");
            }

            (_options.CpuSampleInterval, _options.CpuSampleReport) = (interval, report);

            return true;
        }

        if (!Diagnostics.ManagedProfile.TryParse(value, out var start, out var length))
        {
            throw new CommandLineException($"--profile-managed {value}: expected <start seconds>[:<seconds>]");
        }

        (_options.ProfileManagedStart, _options.ProfileManagedLength) = (start, length);

        return true;
    }

    private bool SetTraceSystem()
    {
        _options.TraceSystemCalls = true;

        return true;
    }

    private bool SetFidelityFxLibrary()
    {
        _options.FidelityFxLibrary = Path.GetFullPath(Next());

        return true;
    }

    /// <summary>--d3d12-debug, --d3d12-gpu-validation, which implies it, or --d3d12-dred (DRED without the debug layer).</summary>
    private bool SetD3D12Debug(string option)
    {
        if (option == "--d3d12-dred")
        {
            _options.D3D12Dred = true;

            return true;
        }

        _options.D3D12Debug = true;
        _options.D3D12GpuValidation |= option == "--d3d12-gpu-validation";

        return true;
    }

    private bool SetTraceQueues()
    {
        _options.D3D12TraceQueues = true;

        return true;
    }

    /// <summary><c>--d3d12-breadcrumbs</c> or <c>--gpu-timings</c>.</summary>
    private bool SetBreadcrumbs(string option)
    {
        if (option == "--gpu-timings")
        {
            _options.GpuTimings = true;

            return true;
        }

        _options.D3D12Breadcrumbs = true;

        return true;
    }

    /// <summary><c>--watch-movies</c> and <c>--break-line-writes</c>.</summary>
    private bool SetMoviesOrLineBreaks(string option)
    {
        if (option == "--watch-movies")
        {
            _options.WatchMovies = true;
        }
        else
        {
            _options.BreakLineWrites = true;
        }

        return true;
    }

    /// <summary><c>--watch-4g-lines</c>.</summary>
    private bool SetFourGiBLines()
    {
        _options.WatchFourGiBLines = true;

        return true;
    }

    /// <summary><c>--watch-transient</c>, <c>--watch-hang</c> or <c>--skip-compute-chains</c>.</summary>
    private bool SetWatch(string option)
    {
        if (option == "--watch-hang")
        {
            _options.WatchHang = true;
        }
        else if (option == "--trace-pool-stores")
        {
            _options.TracePoolStores = true;
        }
        else if (option == "--retire-waits-all-queues")
        {
            _options.RetireWaitsAllQueues = true;
        }
        else if (option == "--watch-pool-pages")
        {
            _options.WatchPoolPages = true;
        }
        else if (option == "--no-split-at-waits")
        {
            _options.SplitAtWaits = false;
        }
        else if (option == "--user-data-constants")
        {
            _options.UserDataConstants = true;
        }
        else if (option == "--buffer-records-heap-index")
        {
            _options.BufferRecordsHeapIndex = true;
        }
        else if (option == "--typed-buffer-views")
        {
            _options.TypedBufferViews = true;
        }
        else if (option == "--check-null-addresses")
        {
            _options.CheckNullAddresses = true;
        }
        else if (option == "--no-window-guard")
        {
            _options.NoWindowGuard = true;
        }
        else if (option == "--skip-compute-chains")
        {
            _options.SkipComputeChains = true;
        }
        else if (option == "--empty-indirect-after-skip")
        {
            _options.EmptyIndirectAfterSkip = true;
        }
        else
        {
            _options.WatchTransient = true;
        }

        return true;
    }

    /// <summary><c>--d3d12-trace-draws</c>, or <c>--d3d12-trace-pipeline &lt;text&gt;</c>, which also names pipelines to trace.</summary>
    /// <summary>The draw trace options, and --probe-pipeline &lt;text&gt; (no trace).</summary>
    private bool SetTraceDraws(string option)
    {
        if (option == "--probe-pipeline")
        {
            _options.ProbePipeline = Next();

            return true;
        }

        if (option is "--hold-passes" or "--stub-passes")
        {
            return SetPassRules(option);
        }

        _options.D3D12TraceDraws = true;

        if (option == "--census-passes")
        {
            _options.CensusPasses = [.. Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        }

        if (option == "--d3d12-trace-pipeline")
        {
            _options.D3D12TracePipeline = Next();
        }

        if (option == "--d3d12-trace-records")
        {
            _options.D3D12TraceRecords = Shims.D3D12X.DebugLayer.TraceRecords.Parse(Next());
        }

        SetWatchTarget(option);

        if (option == "--log-draws")
        {
            _options.LogDraws = Enum.TryParse<Shims.D3D12X.DebugLayer.DrawLogLevel>(Next(), ignoreCase: true, out var level) ? level : throw new CommandLineException("--log-draws takes off, brief or full");
        }

        if (option == "--capture-when-world")
        {
            _options.CaptureWhenWorld = true;
        }

        SetDumps(option);

        return true;
    }

    /// <summary><c>--dump-frame</c>: a present number, <c>world[+n]</c> or <c>Ns</c> (seconds after the loader started).</summary>
    private void SetDumpFrame(string text)
    {
        if (Shims.D3D12X.DebugLayer.FrameStageDump.TryParseSeconds(text, out long seconds))
        {
            _options.DumpFrameSeconds = seconds;

            return;
        }

        if (!Shims.D3D12X.DebugLayer.FrameStageDump.TryParseWhen(text, out long present, out long worldOffset))
        {
            throw new CommandLineException("--dump-frame takes a present number (2100), world or world+n (n presents after the first world frame), or Ns (N seconds after start)");
        }

        _options.DumpFrame = present;
        _options.DumpFrameWorldOffset = worldOffset;
    }

    /// <summary><c>--d3d12-dump-targets</c> and <c>--dump-frame n</c> with <c>-dir</c>, <c>-max</c> and <c>-filter</c>.</summary>
    private void SetDumps(string option)
    {
        if (option == "--d3d12-dump-targets")
        {
            _options.D3D12DumpTargets = true;
        }

        if (option == "--dump-frame")
        {
            SetDumpFrame(Next());
        }

        if (option == "--dump-frame-dir")
        {
            _options.DumpFrameDirectory = Path.GetFullPath(Next());
        }

        if (option == "--dump-frame-max")
        {
            _options.DumpFrameMax = int.TryParse(Next(), NumberStyles.None, CultureInfo.InvariantCulture, out int max) && max > 0 ? max : throw new CommandLineException("--dump-frame-max takes a positive count");
        }

        if (option == "--dump-frame-filter")
        {
            _options.DumpFrameFilter = Next();
        }

        if (option == "--dump-frame-min-width")
        {
            _options.DumpFrameMinimumWidth = ulong.TryParse(Next(), NumberStyles.None, CultureInfo.InvariantCulture, out ulong width) && width > 0 ? width : throw new CommandLineException("--dump-frame-min-width takes a positive width");
        }
    }

    /// <summary><c>--d3d12-watch-target WxH:format</c>, <c>--d3d12-watch-limit n</c> and <c>--d3d12-watch-rendered</c>.</summary>
    private void SetWatchTarget(string option)
    {
        if (option == "--d3d12-watch-target")
        {
            _options.D3D12WatchTarget = Shims.D3D12X.DebugLayer.TargetShape.Parse(Next()) ?? throw new CommandLineException("--d3d12-watch-target takes WxH:format[,WxH:format...], e.g. 2560x1440:10");
        }

        if (option == "--d3d12-watch-limit")
        {
            _options.D3D12WatchLimit = int.TryParse(Next(), out int limit) && limit > 0 ? limit : throw new CommandLineException("--d3d12-watch-limit takes a positive count");
        }

        if (option == "--d3d12-watch-rendered")
        {
            _options.D3D12WatchRendered = true;
        }
    }

    /// <summary><c>--hold-passes consumer=producer|...;...</c> or <c>--stub-passes pass=r,g,b,a;...</c> (no trace).</summary>
    private bool SetPassRules(string option)
    {
        if (option == "--hold-passes")
        {
            _options.HoldPasses = Next();
        }
        else
        {
            _options.StubPasses = Next();
        }

        return true;
    }

    private bool SetCaptureDirectory()
    {
        _options.CaptureDirectory = Path.GetFullPath(Next());

        return true;
    }

    private bool SetCapturePresents()
    {
        _options.CapturePresents.Clear();

        foreach (string present in Next().Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            _options.CapturePresents.Add(long.Parse(present, CultureInfo.InvariantCulture));
        }

        return true;
    }

    /// <summary>The PNG readback (<c>--capture-dir</c>, <c>--capture-presents</c>) and PIX GPU captures (<c>--pix-capture</c>, <c>--pix-capture-dir</c>).</summary>
    private bool SetCapture(string option)
    {
        switch (option)
        {
            case "--capture-dir":
                return SetCaptureDirectory();
            case "--capture-presents":
                return SetCapturePresents();
            case "--pix-capture-dir":
                _options.PixCaptureDirectory = Path.GetFullPath(Next());

                return true;
            default:
                foreach (string present in Next().Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    _options.PixCapturePresents.Add(long.Parse(present, CultureInfo.InvariantCulture));
                }

                return true;
        }
    }

    /// <summary>
    /// <c>--aftermath [dir]</c> (the folder is optional: the next argument when it is not an option, else
    /// <c>aftermath</c>), <c>--aftermath-dll &lt;path&gt;</c> and the feature switches, which imply it.
    /// </summary>
    private bool SetAftermath(string option)
    {
        _options.AftermathDirectory ??= Path.GetFullPath("aftermath");

        if (option == "--aftermath-dll")
        {
            _options.AftermathLibrary = Path.GetFullPath(Next());
        }
        else if (option == "--aftermath-markers")
        {
            _options.AftermathMarkers = true;
        }
        else if (option == "--aftermath-resources")
        {
            _options.AftermathResources = true;
        }
        else if (option == "--aftermath-shader-info")
        {
            _options.AftermathShaderInfo = true;
        }
        else if (option == "--aftermath-selftest")
        {
            _options.AftermathSelfTest = true;
        }
        else if (_index + 1 < _args.Length && !_args[_index + 1].StartsWith('-'))
        {
            _options.AftermathDirectory = Path.GetFullPath(Next());
        }

        return true;
    }

    /// <summary><c>--depth-func-override text=Func[;text=Func...]</c> (Never, Less, Equal, LessEqual, Greater, NotEqual, GreaterEqual, Always), or <c>--no-class-depth-bounds</c>.</summary>
    private bool SetDepthFuncOverride(string option)
    {
        if (option == "--no-class-depth-bounds")
        {
            _options.NoClassDepthBounds = true;

            return true;
        }

        _options.DepthFuncOverrides = Shims.D3D12X.Pipelines.DepthFuncOverride.Parse(Next()) ?? throw new CommandLineException("--depth-func-override takes text=Func[;text=Func...], e.g. TL_PS_NoDir=Always");

        return true;
    }

    private bool SetPresentTest()
    {
        _options.PresentTest = true;

        return true;
    }

    /// <summary>
    /// --raytracing (the DLL's CPU paths), --raytracing-gpu (the inventory of its GPU builder), --rt-traversal (the
    /// game's traversal programs run, with --raytracing's paths) or --no-rt-stand-ins.
    /// </summary>
    private bool SetRayTracing(string option)
    {
        if (option == "--no-rt-stand-ins")
        {
            _options.NoRtStandIns = true;

            return true;
        }

        if (option == "--rt-traversal")
        {
            _options.RtTraversal = true;
        }

        if (option == "--raytracing-gpu")
        {
            _options.RayTracingGpuInventory = true;
        }
        else
        {
            _options.RayTracing = true;
        }

        return true;
    }

    /// <summary>--network offline | sealed-online.</summary>
    private bool SetNetworkMode()
    {
        string value = Next();

        _options.NetworkMode = value switch
        {
            "offline" => NetworkMode.Offline,
            "sealed-online" => NetworkMode.SealedOnline,
            _ => throw new CommandLineException($"--network {value}: expected offline or sealed-online"),
        };

        return true;
    }

    /// <summary>--network-level: a level name or its XNetworkingConnectivityLevelHint value (0..4).</summary>
    private bool SetNetworkLevel()
    {
        string value = Next();

        if (!Enum.TryParse<ConnectivityLevel>(value, true, out var level) || !Enum.IsDefined(level))
        {
            throw new CommandLineException($"--network-level {value}: expected 0..4 or Unknown, None, LocalAccess, InternetAccess, ConstrainedInternetAccess");
        }

        _options.NetworkLevel = level;

        return true;
    }

    /// <summary>--cpu host | intel.</summary>
    private bool SetCpu()
    {
        string value = Next();

        _options.AssumeIntelCpu = value switch
        {
            "host" => false,
            "intel" => true,
            _ => throw new CommandLineException($"--cpu {value}: expected host or intel"),
        };

        return true;
    }

    /// <summary>--bcpack-missing placeholder | fail.</summary>
    private bool SetBcPackMissing()
    {
        string value = Next();

        _options.FailMissingBcPack = value switch
        {
            "placeholder" => false,
            "fail" => true,
            _ => throw new CommandLineException($"--bcpack-missing {value}: expected placeholder or fail"),
        };

        return true;
    }

    private bool SetBcPackTables()
    {
        _options.BcPackTables = Path.GetFullPath(Next());

        return true;
    }

    private bool SetXDspConvolution()
    {
        _options.XDspConvolution = true;

        return true;
    }

    private bool SetRecordAudio()
    {
        _options.RecordAudioProcess = int.Parse(Next(), CultureInfo.InvariantCulture);
        _options.AudioCapture = Path.GetFullPath(Next());

        return true;
    }

    private bool SetVerbose()
    {
        _options.Verbose = true;

        return true;
    }

    private bool SetClassifyInput()
    {
        _options.ClassifyInput = Path.GetFullPath(Next());

        return true;
    }

    private bool SetClassifyOutput()
    {
        _options.ClassifyOutput = Path.GetFullPath(Next());

        return true;
    }

    private bool StopWithListSafeCalls()
    {
        _options.ListSafeCalls = true;

        return false;
    }

    private bool StopWithHelp()
    {
        _options.Help = true;

        return false;
    }
}
