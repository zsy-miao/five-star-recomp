// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Loading;
using FiveStar.Loader.Shims.Network;

namespace FiveStar.Loader.Host;

/// <summary>The parsed command line of fivestar-loader.</summary>
internal sealed record HostOptions
{
    public List<string> Loads { get; } = [];

    public List<string> Calls { get; } = [];

    public List<string> ManualModules { get; } = [];

    public List<string> SearchDirectories { get; } = [];

    public List<string> DisabledShims { get; } = [];

    /// <summary>Patch files and folders of patch files, in order.</summary>
    public List<string> Patches { get; } = [];

    /// <summary>Mod folders, highest priority first.</summary>
    public List<string> ModFolders { get; } = [];

    public bool SystemCrt { get; set; }

    public UnresolvedMode Unresolved { get; set; } = UnresolvedMode.Stub;

    public bool CallEntryPoints { get; set; } = true;

    public bool ListExports { get; set; } = true;

    public bool Unload { get; set; }

    public string? WorkDirectory { get; set; }

    public string? LogFile { get; set; }

    public string? MainModule { get; set; }

    public bool PatchPeb { get; set; }

    public string? PackageRoot { get; set; }

    public string? SaveDirectory { get; set; }

    public string? GdkAbiTable { get; set; }

    public uint? DeviceType { get; set; }

    public string? InputConfig { get; set; }

    /// <summary>Timed key presses added to the desktop input (<c>--input-script</c>).</summary>
    public string? InputScript { get; set; }

    /// <summary>The real keyboard and mouse reach the game beside an input script (<c>--allow-real-input</c>); off: script input only, F9 hands them back.</summary>
    public bool AllowRealInput { get; set; }

    /// <summary>The learned screens of the input script (<c>--screens</c>); null: %LOCALAPPDATA%\FiveStar\screens.txt.</summary>
    public string? ScreensFile { get; set; }

    /// <summary>Extracted FSSM programs (`fivestar shader extract`) for pipeline creation.</summary>
    public string? ShaderDirectory { get; set; }

    /// <summary>Translated DXIL programs (`fivestar shader translate`) for pipeline creation.</summary>
    public string? TranslatedDirectory { get; set; }

    /// <summary>The pipeline disk cache folder (<c>--pipeline-cache</c>); null means the work folder's.</summary>
    public string? PipelineCache { get; set; }

    /// <summary><c>--no-pipeline-cache</c>: pipelines are neither loaded from nor stored in a disk cache.</summary>
    public bool NoPipelineCache { get; set; }

    /// <summary>The console pipeline cache (shaders.rpf or the blob) whose pipelines <c>--warm-pipelines</c> builds.</summary>
    public string? WarmSource { get; set; }

    /// <summary>Pipelines the warm-up builds at once; null for the default (4).</summary>
    public int? WarmJobs { get; set; }

    /// <summary>Processes the warm-up runs in (<c>--warm-processes</c>); null for one per 16 GB of RAM, 1 to 4.</summary>
    public int? WarmProcesses { get; set; }

    /// <summary>The shard this warm-up process builds (<c>--warm-shard i/n</c>, set by the parent run); null for all.</summary>
    public (int Shard, int Count)? WarmShard { get; set; }

    /// <summary>The warm-up builds every n-th pipeline only (a sample).</summary>
    public int WarmStep { get; set; } = 1;

    /// <summary>The warm-up builds only pipelines with a program whose name contains one of these.</summary>
    public List<string> WarmMatches { get; } = [];

    /// <summary>The warm-up builds only the pipelines earlier boots asked for (<c>pipelines-used.bin</c>).</summary>
    public bool WarmUsedOnly { get; set; }

    /// <summary>Every pipeline the cache lacks is built at creation, not on first use.</summary>
    public bool EagerPipelines { get; set; }

    /// <summary>The program cache folder of programs made on demand (<c>--program-cache</c>); null means the work folder's.</summary>
    public string? ProgramCache { get; set; }

    /// <summary>The folder of hand-written programs that replace translations (<c>--shader-overrides</c>); null for none.</summary>
    public string? ShaderOverrides { get; set; }

    /// <summary><c>--no-on-demand</c>: only the ahead-of-time translation provides programs.</summary>
    public bool NoOnDemand { get; set; }

    /// <summary>dxc.exe, dxcompiler.dll or their folder (<c>--dxc</c>); null finds one.</summary>
    public string? Dxc { get; set; }

    /// <summary><c>--shader-backend dxc</c>: programs made on demand compile with dxc instead of the DXIL backend.</summary>
    public bool DxcBackend { get; set; }

    /// <summary>
    /// Read-only console textures go to shared heaps instead of a committed resource each (the default;
    /// <c>--committed-textures</c> turns it off). A released texture's range stays mapped, as console memory does, so a
    /// stale read the game still makes hits memory instead of a freed resource (issue #23; w28 lost the device at 185 s
    /// without it, w29 ran 1,018 s into gameplay with it).
    /// </summary>
    public bool PlacedTextures { get; set; } = true;

    /// <summary>Integer texture views loaded, gathered and stored through integer declarations (<c>--typed-image-views</c>); part of the program cache identity.</summary>
    public bool TypedImageViews { get; set; }

    /// <summary>R9G9B9E5 views of R32_UINT light read a shadow the texels are copied into (on; <c>--no-shared-exponent-copies</c> leaves the decode to the programs).</summary>
    public bool SharedExponentCopies { get; set; } = true;

    /// <summary>Each R9G9B9E5 shadow's first copies are logged with their states (<c>--trace-shadows</c>).</summary>
    public bool TraceShadows { get; set; }

    /// <summary>
    /// With <c>--typed-image-views</c>: the programs whose 2D samples filter shared-exponent light themselves, by name part
    /// (<c>--shared-exponent-programs &lt;text,...&gt;</c>; TranslationOptions.SharedExponentPrograms). By default the indirect
    /// diffuse lighting's, which read the screen probes' R9G9B9E5 light; the filter costs too much DXIL to give every program.
    /// </summary>
    public IReadOnlyList<string> SharedExponentPrograms { get; set; } = ["indirect_diffuse_lighting~"];

    /// <summary>GPU-written UNORM color textures made typeless, so views take the game's sRGB or integer member (<c>--typeless-targets</c>).</summary>
    public bool TypelessTargets { get; set; }

    /// <summary>Submissions wait for the texture refresh (<c>--texture-refresh-wait</c>).</summary>
    public bool TextureRefreshWait { get; set; }

    /// <summary>Threads that build the journal's uncached pipelines from device creation on (<c>--prebuild-jobs</c>; 0 for none).</summary>
    public int PrebuildJobs { get; set; } = Shims.D3D12X.Pipelines.JournalPrebuild.DefaultJobs;

    /// <summary>Name parts of stand-ins waited for at their first use instead of skipped (<c>--wait-pipelines</c>); null for none.</summary>
    public string? WaitPipelines { get; set; }

    /// <summary>Retired textures over exactly one range kept until disposal (<c>--kept-aliases</c>).</summary>
    public int KeptAliases { get; set; } = Shims.D3D12X.Resources.GpuTextureRegistry.DefaultKeptAliases;

    /// <summary>Bytes of textures made for views over reserved memory with no page mapped (<c>--unmapped-view-budget</c>).</summary>
    public long UnmappedViewBudgetBytes { get; set; } = Shims.D3D12X.Resources.GpuTextures.DefaultUnmappedViewBudget;

    /// <summary>The most driver compiles of 256 KB of DXIL or more at once (<c>--max-driver-compiles</c>); 0 for the machine's default.</summary>
    public int MaxDriverCompiles { get; set; }

    /// <summary>The private bytes driver compiles are kept under (<c>--compile-ceiling</c>, given in GB).</summary>
    public long CompileCeilingBytes { get; set; } = Shims.D3D12X.Pipelines.CompileBudget.DefaultCeilingBytes;

    /// <summary>Threads that make programs on demand (<c>--shader-jobs</c>); null for the default.</summary>
    public int? ShaderJobs { get; set; }

    /// <summary>The PC FidelityFX API DLL the console FidelityFX calls are forwarded to; null leaves FidelityFX off.</summary>
    public string? FidelityFxLibrary { get; set; }

    /// <summary>Run the main executable's entry point after loading, with the call trace and fault reporter on.</summary>
    public bool RunMain { get; set; }

    /// <summary>How long --run lets the entry point run before reporting the main thread and exiting; null waits.</summary>
    public TimeSpan? RunTimeout { get; set; }

    /// <summary>
    /// <c>--cpu intel</c>: report the AMD-only instructions as missing and run the emulation self-test through the
    /// exception handler, as on an Intel CPU; the game's own instructions still run natively where the CPU has them.
    /// </summary>
    public bool AssumeIntelCpu { get; set; }

    /// <summary>With --run, log call counts and every thread's stack at this interval (<c>--sample-threads</c>).</summary>
    public TimeSpan? SampleThreads { get; set; }

    /// <summary>With --run, log where the private memory is at this interval (<c>--memory-map</c>).</summary>
    public TimeSpan? MemoryMap { get; set; }

    /// <summary>With --run, end the process below this many free physical bytes (<c>--min-free-memory</c>); 0 for none.</summary>
    public ulong MinimumFreeMemory { get; set; }

    /// <summary>With --run, end the process above this system commit (<c>--max-commit</c>); 0 for none.</summary>
    public ulong MaximumCommit { get; set; }

    /// <summary>The most bytes of heaps over the game's page sections (<c>--section-heap-budget</c>); 0 for the GPU's budget alone.</summary>
    public ulong SectionHeapBudget { get; set; }

    /// <summary>The largest heap opened in place over game memory, in MB (<c>--in-place-window</c>); 0 for the default.</summary>
    public ulong InPlaceWindowMegabytes { get; set; }

    /// <summary>Section windows open as programs ask for them and are released when unused (<c>--evict-windows</c>).</summary>
    public bool EvictWindows { get; set; }

    /// <summary>Bytes of GPU system memory held from device creation, to tighten its budget (<c>--reserve-gpu-system-memory</c>).</summary>
    public ulong ReservedSystemMemory { get; set; }

    /// <summary>With --sample-threads, also a profile burst of the running threads per sample (<c>--profile-threads</c>).</summary>
    public bool ProfileThreads { get; set; }

    /// <summary>With --run, a managed CPU profile from this far into the run (<c>--profile-managed</c>).</summary>
    public TimeSpan? ProfileManagedStart { get; set; }

    /// <summary>How long the managed CPU profile samples.</summary>
    public TimeSpan ProfileManagedLength { get; set; }

    /// <summary>With --run, sample the game's threads at this interval (<c>--cpu-sample</c>).</summary>
    public TimeSpan? CpuSampleInterval { get; set; }

    /// <summary>How often the CPU sampler logs its table.</summary>
    public TimeSpan CpuSampleReport { get; set; }

    /// <summary>With --run, also trace calls into these mapped images (<c>--trace-module</c>).</summary>
    public List<string> TracedModules { get; } = [];

    /// <summary>With --run, also trace calls to System32 imports.</summary>
    public bool TraceSystemCalls { get; set; }

    /// <summary>The D3D12 debug layer, its messages and DRED in the loader log (<c>--d3d12-debug</c>).</summary>
    public bool D3D12Debug { get; set; }

    /// <summary>DRED alone (auto-breadcrumbs, page faults, the removal report), without the slow debug layer (<c>--d3d12-dred</c>).</summary>
    public bool D3D12Dred { get; set; }

    /// <summary>The tiled lighting keeps its depth EQUAL instead of the class depth-bounds test (<c>--no-class-depth-bounds</c>).</summary>
    public bool NoClassDepthBounds { get; set; }

    /// <summary>Depth comparisons (and stencil off) for pipelines by program name (<c>--depth-func-override text=Func;...</c>).</summary>
    public IReadOnlyList<(string Text, int Function)> DepthFuncOverrides { get; set; } = [];

    /// <summary>Pipelines whose programs' names hold this text cull no faces (<c>--force-cull-none &lt;text&gt;</c>).</summary>
    public string? ForceCullNone { get; set; }

    /// <summary>Pipelines whose programs' names hold one of these texts stay stand-ins (<c>--skip-pipelines &lt;text,...&gt;</c>).</summary>
    public IReadOnlyList<string> SkipPipelines { get; set; } = [];

    /// <summary>Records every descriptor-table bind mirrors (<c>--mirror-table-records full|&lt;n&gt;</c>).</summary>
    public uint MirrorTableRecords { get; set; } = Shims.D3D12X.Binding.DescriptorWindow.DefaultTableRecords;

    /// <summary>Draws also mirror as far as their programs read past each table pointer (<c>--mirror-table-records reach</c>).</summary>
    public bool MirrorTableReach { get; set; }

    /// <summary>Loop caps of programs made on demand (<c>--loop-caps victory|default|compute,geometry,pixel</c>); null for the translator's own.</summary>
    public FiveStar.Shaders.Translation.Core.LoopCaps? LoopCaps { get; set; }

    /// <summary>Heavy pipelines are built first in a pipeline host held to memory and time limits (<c>--host-pipelines</c>).</summary>
    public bool HostPipelines { get; set; }

    /// <summary>Superstep forms made on demand carry registers by liveness; <c>--plain-supersteps</c> turns it off.</summary>
    public bool SuperstepCarry { get; set; } = true;

    /// <summary><c>--fast-waves</c>: mesh programs made on demand may run approximately instead of in the superstep form (TranslationOptions.FastWaves).</summary>
    public bool FastWaves { get; set; }

    /// <summary>A superstep program's group rounds per wave-operation stop (<c>--superstep-round-budget</c>); null for the loop caps' bound.</summary>
    public int? SuperstepRoundsPerStop { get; set; }

    /// <summary>With the debug layer, also GPU-based validation (<c>--d3d12-gpu-validation</c>).</summary>
    public bool D3D12GpuValidation { get; set; }

    /// <summary>D3D12.X slots that do nothing, to bisect GPU faults (<c>--d3d12-skip list:51,queue:35</c>).</summary>
    public List<string> D3D12Skips { get; } = [];

    /// <summary>Queue submissions, signals and waits in the log (<c>--d3d12-trace-queues</c>).</summary>
    public bool D3D12TraceQueues { get; set; }

    /// <summary>A GPU marker after every draw and dispatch, for the stall report (<c>--d3d12-breadcrumbs</c>).</summary>
    public bool D3D12Breadcrumbs { get; set; }

    /// <summary>GPU time per pass and queue with the sample line (<c>--gpu-timings</c>).</summary>
    public bool GpuTimings { get; set; }

    /// <summary>Every how many frames the CPU frame breakdown is logged (<c>--frame-log</c>); 0 is off.</summary>
    public int FrameLog { get; set; }

    /// <summary>The GC runs in SustainedLowLatency mode: no blocking full collections unless memory runs low (<c>--gc-sustained</c>).</summary>
    public bool SustainedGc { get; set; }

    /// <summary>Mesh draws launched by an amplification stage of ours instead of clamped by a compute pass (<c>--mesh-launch</c>).</summary>
    public bool MeshLaunch { get; set; }

    /// <summary>Enhanced barriers on the indirect-argument scratch instead of three transitions per ExecuteIndirect (<c>--clamp-enhanced-barriers</c>).</summary>
    public bool ClampEnhancedBarriers { get; set; }

    /// <summary>Indirect records clamped in one prelude per list segment where nothing earlier in it wrote them (<c>--indirect-gather</c>).</summary>
    public bool IndirectGather { get; set; }

    /// <summary><c>--no-draws</c>: no pipeline is built and every draw and dispatch is skipped, to measure the game's own CPU frame (SkippedPipelines.All).</summary>
    public bool NoDraws { get; set; }

    /// <summary>A compute queue of ours keeps the GPU clocked while the game submits (<c>--gpu-keep-awake</c>, <c>--gpu-keep-awake-turns</c>).</summary>
    public Shims.D3D12X.Present.GpuKeepAwakeMode GpuKeepAwake { get; set; }

    /// <summary><c>--output WxH</c>: the title's output size, one of its modes (Mods.OutputSizePatches); null keeps its settings'.</summary>
    public (int Width, int Height)? OutputSize { get; set; }

    /// <summary>
    /// Every memory write, dispatch and memory wait ends gathering (the default); <c>--indirect-gather-relaxed</c> turns on the
    /// narrower rules, which drew lights as squares in some boots until they are proven.
    /// </summary>
    public bool IndirectGatherStrict { get; set; } = true;

    /// <summary>With <c>--indirect-batch</c> (and gathering), indirect records not gathered are clamped in one pass per stretch between barriers after writes.</summary>
    public bool IndirectBatch { get; set; }

    /// <summary>Depth textures in a typed depth format made in its typeless family, so shader views read them (<c>--typeless-depth</c>).</summary>
    public bool TypelessDepth { get; set; }

    /// <summary>The game's command queues are made without the GPU timeout (<c>--no-gpu-timeout</c>): work past Windows' 2 s limit stalls instead of removing the device.</summary>
    public bool NoGpuTimeout { get; set; }

    /// <summary>Failing graphics kernel calls, MarkDeviceAsError and device states in the log (<c>--d3dkmt-watch</c>): who removes the device.</summary>
    public bool D3dkmtWatch { get; set; }

    /// <summary>The driver's background shader work stays on (<c>--driver-background-work</c>); off by default.</summary>
    public bool DriverBackgroundWork { get; set; }

    /// <summary>The driver's measurements are discarded every few seconds and its background work left on (<c>--discard-driver-measurements</c>): what a machine without developer mode gets, to measure.</summary>
    public bool DiscardDriverMeasurements { get; set; }

    /// <summary>First uses of pipelines are not held when the driver's shader tables are nearly full (<c>--no-shader-table-guard</c>).</summary>
    public bool NoShaderTableGuard { get; set; }

    /// <summary>MiB of DXIL the built pipelines may hold before unused ones are given up (<c>--pipeline-shader-budget</c>); 0 gives up none.</summary>
    public int PipelineShaderBudget { get; set; } = Shims.D3D12X.Pipelines.PipelineEviction.DefaultBudgetMiB;
    /// <summary>The descriptor window, the address map and the user-data ring in GPU upload heaps where the device has them (<c>--gpu-upload-heaps</c>).</summary>
    public bool GpuUploadHeaps { get; set; }

    /// <summary>The frame interval the frame events keep (<c>--frame-rate</c>); the game's own by default.</summary>
    public Shims.D3D12X.Present.FrameRate FrameRate { get; set; }

    /// <summary>Check the bound tables' views before each draw and dispatch (<c>--check-view-liveness</c>).</summary>
    public bool CheckViewLiveness { get; set; }

    /// <summary>Scan the inputs of draws whose pipeline contains this text for NaN and Inf (<c>--d3d12-nan-scan</c>); null when off.</summary>
    public string? NanScan { get; set; }

    /// <summary>The floats shown of every NaN-scanned memory input (<c>--d3d12-nan-scan-dwords</c>); null for none.</summary>
    public (int Offset, int Count)? NanScanDwords { get; set; }

    /// <summary>Presents between two exposure lines of one mapped target (<c>--trace-exposure</c>); 0 when off.</summary>
    public int TraceExposure { get; set; }

    /// <summary>Pipeline texts whose work logs its tables' records as resolved (<c>--trace-views</c>); null when off.</summary>
    public string? TraceViews { get; set; }
    /// <summary>The dispatches to capture for <c>fivestar shader replay</c> (<c>--capture-dispatch</c>); empty when off.</summary>
    public IReadOnlyList<Shims.D3D12X.DebugLayer.CaptureSpec> CaptureDispatch { get; set; } = [];

    /// <summary>Where captured dispatches go (<c>--capture-dispatch-dir</c>).</summary>
    public string CaptureDispatchDirectory { get; set; } = Path.GetFullPath("captures");

    /// <summary>Retire textures over memory the game unmaps (<c>--retire-unmapped-textures</c>); off: they are only counted.</summary>
    public bool RetireUnmappedTextures { get; set; }

    /// <summary>Keep textures over unmapped memory no view record names (<c>--keep-unmapped-textures</c>); off: they are retired.</summary>
    public bool KeepUnmappedTextures { get; set; }

    /// <summary>A new texture retires an older one only when it starts in that one's first 4 KiB (<c>--stale-in-first-page</c>); off: any overlap.</summary>
    public bool StaleInFirstPage { get; set; }

    /// <summary>Check the game's transient-memory pool for corrupted nodes (<c>--watch-transient</c>).</summary>
    public bool WatchTransient { get; set; }

    /// <summary>Check the game's small-block pool pages for headers the CPU did not write (<c>--watch-pool-pages</c>).</summary>
    public bool WatchPoolPages { get; set; }

    /// <summary>Watch the first 64 KiB after each 4 GiB line of GPU-writable game memory for changes (<c>--watch-4g-lines</c>).</summary>
    public bool WatchFourGiBLines { get; set; }

    /// <summary>Follow the game's hang watchdog, main state and loading-screen flags (<c>--watch-hang</c>).</summary>
    public bool WatchHang { get; set; }

    /// <summary>Follow the game's Bink movies: state changes and frame numbers (<c>--watch-movies</c>).</summary>
    public bool WatchMovies { get; set; }

    /// <summary>With the 4 GiB line watch, hardware write breakpoints at the start of the game's graphics heap name its CPU writers (<c>--break-line-writes</c>).</summary>
    public bool BreakLineWrites { get; set; }

    /// <summary>Skip the rest of a recording's compute after a skipped dispatch (<c>--skip-compute-chains</c>).</summary>
    public bool SkipComputeChains { get; set; }

    /// <summary>A recording's ExecuteIndirects after a skipped compute dispatch run nothing (<c>--empty-indirect-after-skip</c>).</summary>
    public bool EmptyIndirectAfterSkip { get; set; }

    /// <summary>A submission holding a retire write waits for every other queue (<c>--retire-waits-all-queues</c>).</summary>
    public bool RetireWaitsAllQueues { get; set; }

    /// <summary>A memory wait recorded after work splits its list there; <c>--no-split-at-waits</c> turns it off.</summary>
    public bool SplitAtWaits { get; set; } = true;

    /// <summary>User data as root constants instead of the ring's root CBV (<c>--user-data-constants</c>), with programs made on demand.</summary>
    public bool UserDataConstants { get; set; }

    /// <summary>Buffer descriptor records keep the desktop heap index in dword 0, not the console base (<c>--buffer-records-heap-index</c>).</summary>
    public bool BufferRecordsHeapIndex { get; set; }

    /// <summary>Buffer views at heap slots programs reach by index keep the game's kind, not raw (<c>--typed-buffer-views</c>).</summary>
    public bool TypedBufferViews { get; set; }

    /// <summary>Operations that would reach a null GPU address or read past their buffer are logged and dropped (<c>--check-null-addresses</c>).</summary>
    public bool CheckNullAddresses { get; set; }

    /// <summary>UAVs of depth textures go to colour twins written back into the depth before draws (<c>--depth-twins</c>).</summary>
    public bool DepthTwins { get; set; }

    /// <summary>Census lines of the render targets bound, cleared by the game and never cleared, and of GPU-written textures made over one another (<c>--target-clear-census</c>).</summary>
    public bool TargetClearCensus { get; set; }

    /// <summary>Diagnostic: each render target cleared to 0 at its first bind in a frame (<c>--clear-targets-each-frame</c>).</summary>
    public bool ClearTargetsEachFrame { get; set; }

    /// <summary>Draws the pipeline trace logs also read back their first render target (<c>--d3d12-probe-traced</c>).</summary>
    public bool D3D12ProbeTraced { get; set; }

    /// <summary>Programs' stores through 32-bit pointers reach the descriptor window (<c>--no-window-guard</c>).</summary>
    public bool NoWindowGuard { get; set; }

    /// <summary>Programs made on demand trace stores into the transient pool and the game heap (<c>--trace-pool-stores</c>).</summary>
    public bool TracePoolStores { get; set; }

    /// <summary>Presents read back when --capture-presents is not given: early, after the first seconds, later.</summary>
    private static readonly long[] DefaultCapturePresents = [60, 300, 900, 1500];

    /// <summary>What draws bind and what their targets hold, in the log (<c>--d3d12-trace-draws</c>).</summary>
    public bool D3D12TraceDraws { get; set; }

    /// <summary>With the draw trace, every target of the frame after a captured present as a PNG (<c>--d3d12-dump-targets</c>).</summary>
    public bool D3D12DumpTargets { get; set; }

    /// <summary>The present after which every pass's targets are written as PNGs (<c>--dump-frame n</c>); 0 for none.</summary>
    public long DumpFrame { get; set; }

    /// <summary><c>--dump-frame world+n</c>: n; -1 when a present number was given or none.</summary>
    public long DumpFrameWorldOffset { get; set; } = -1;

    /// <summary><c>--dump-frame Ns</c>: N; -1 for none.</summary>
    public long DumpFrameSeconds { get; set; } = -1;

    /// <summary>Where <c>--dump-frame</c> writes (<c>--dump-frame-dir</c>); null is <c>frame-N</c> in the working directory.</summary>
    public string? DumpFrameDirectory { get; set; }

    /// <summary>PNGs <c>--dump-frame</c> writes at most (<c>--dump-frame-max n</c>).</summary>
    public int DumpFrameMax { get; set; } = Shims.D3D12X.DebugLayer.FrameStageDump.DefaultMax;

    /// <summary>Only passes whose pipeline name holds one of these comma-separated texts (<c>--dump-frame-filter</c>).</summary>
    public string? DumpFrameFilter { get; set; }

    /// <summary>Narrower targets are not dumped (<c>--dump-frame-min-width n</c>).</summary>
    public ulong DumpFrameMinimumWidth { get; set; } = Shims.D3D12X.DebugLayer.FrameStageDump.DefaultMinimumWidth;

    /// <summary>Pipelines whose draws the trace also logs after each captured present (<c>--d3d12-trace-pipeline</c>).</summary>
    public string? D3D12TracePipeline { get; set; }

    /// <summary>Window-record byte offsets the trace reads from user SGPR pointers, each with an optional buffer dword (<c>--d3d12-trace-records</c>).</summary>
    public IReadOnlyList<Shims.D3D12X.DebugLayer.TraceRecord> D3D12TraceRecords { get; set; } = [];

    /// <summary>Render targets of this shape read back after each pass that writes them, after captured presents (<c>--d3d12-watch-target WxH:format</c>).</summary>
    public Shims.D3D12X.DebugLayer.TargetShape? D3D12WatchTarget { get; set; }

    /// <summary>Watched-target readbacks per frame (<c>--d3d12-watch-limit n</c>).</summary>
    public int D3D12WatchLimit { get; set; } = Shims.D3D12X.DebugLayer.DrawTrace.DefaultWatchLimit;

    /// <summary>Only watched targets the game binds as render targets (<c>--d3d12-watch-rendered</c>).</summary>
    public bool D3D12WatchRendered { get; set; }

    /// <summary>How much per-draw text the draw trace writes (<c>--log-draws off|brief|full</c>).</summary>
    public Shims.D3D12X.DebugLayer.DrawLogLevel LogDraws { get; set; } = Shims.D3D12X.DebugLayer.DrawLogLevel.Brief;

    /// <summary>Captures 3 frames 100 presents apart from the first present that draws the world (<c>--capture-when-world</c>).</summary>
    public bool CaptureWhenWorld { get; set; }

    /// <summary>Passes held while a producer is missing this frame (<c>--hold-passes consumer=producer|...;...</c>).</summary>
    public string? HoldPasses { get; set; }

    /// <summary>Passes whose draws clear their targets to a colour instead (<c>--stub-passes pass=r,g,b,a;...</c>).</summary>
    public string? StubPasses { get; set; }

    /// <summary>Texts whose pipelines and targets every census lists in full (<c>--census-passes</c>, with the draw trace).</summary>
    public IReadOnlyList<string> CensusPasses { get; set; } = [];

    /// <summary>Mesh, pixel and compute programs named with one of these comma-separated texts are made with probe code (<c>--probe-pipeline</c>).</summary>
    public string? ProbePipeline { get; set; }

    /// <summary>Where captured presents go as PNG files (<c>--capture-dir</c>); null captures nothing to disk.</summary>
    public string? CaptureDirectory { get; set; }

    /// <summary>The present numbers whose plane is read back (<c>--capture-presents 60,600</c>).</summary>
    public List<long> CapturePresents { get; } = [.. DefaultCapturePresents];

    /// <summary>Presents taken as PIX GPU captures (<c>--pix-capture 600</c>); empty takes none.</summary>
    public List<long> PixCapturePresents { get; } = [];

    /// <summary>Where PIX captures go (<c>--pix-capture-dir</c>); null is <c>pix-captures</c> in the working directory.</summary>
    public string? PixCaptureDirectory { get; set; }

    /// <summary>Where Nsight Aftermath GPU crash dumps go (<c>--aftermath [dir]</c>, default <c>aftermath</c>); null takes none.</summary>
    public string? AftermathDirectory { get; set; }

    /// <summary>GFSDK_Aftermath_Lib.x64.dll or its folder (<c>--aftermath-dll</c>); null looks beside the loader, then in the SDK.</summary>
    public string? AftermathLibrary { get; set; }

    /// <summary>An Aftermath event marker per draw, dispatch and copy, and call stacks (<c>--aftermath-markers</c>).</summary>
    public bool AftermathMarkers { get; set; }

    /// <summary>Aftermath resource tracking, which names the resource at a page fault (<c>--aftermath-resources</c>).</summary>
    public bool AftermathResources { get; set; }

    /// <summary>Aftermath shader debug info for every pipeline (<c>--aftermath-shader-info</c>): much memory.</summary>
    public bool AftermathShaderInfo { get; set; }

    /// <summary>Provoke a GPU page fault and check that Aftermath writes its dump, then exit (<c>--aftermath-selftest</c>).</summary>
    public bool AftermathSelfTest { get; set; }

    /// <summary>Present a test pattern instead of the game's frame (<c>--present-test</c>).</summary>
    public bool PresentTest { get; set; }

    /// <summary>XboxBvhBuilder.dll's CPU paths: console sizes and BLAS rehydration (<c>--raytracing</c>).</summary>
    public bool RayTracing { get; set; }

    /// <summary>The shipped BVH builder's constructor runs for an inventory of its pipelines, never built (<c>--raytracing-gpu</c>).</summary>
    public bool RayTracingGpuInventory { get; set; }

    /// <summary>
    /// <c>--rt-traversal</c>: the game's BVH traversal programs run (with <see cref="RayTracing"/>'s paths, which build
    /// and rehydrate the structures they walk); passes still held by --skip-pipelines keep their stand-in outputs, but the
    /// screen-probe light is no longer overwritten with grey.
    /// </summary>
    public bool RtTraversal { get; set; }

    /// <summary><c>--no-rt-stand-ins</c>: held ray-tracing passes leave their outputs as they are (RtStandIns).</summary>
    public bool NoRtStandIns { get; set; }

    /// <summary>
    /// <c>--rt-stand-ins-reach</c>: a held ray-tracing pass's fill writes every texture UAV in its programs' reach, not
    /// only the records they store through (RtStandIns.WholeReach).
    /// </summary>
    public bool RtStandInsReach { get; set; }

    /// <summary><c>--no-lighting-waits</c>: the lighting pipelines are skipped while stand-ins, as any other (EssentialPipelines).</summary>
    public bool NoLightingWaits { get; set; }

    /// <summary><c>--no-map-readback</c>: a Map of a small GPU-written target leaves the game's memory as it is (MapReadback).</summary>
    public bool NoMapReadback { get; set; }

    /// <summary><c>--map-readback-wait</c>: every such Map waits for the GPU to finish all submitted work, as before AsyncMapReadback.</summary>
    public bool MapReadbackWait { get; set; }

    /// <summary>Run the XDsp convolution effects on the CPU instead of returning silence (<c>--xdsp-convolution</c>).</summary>
    public bool XDspConvolution { get; set; }

    /// <summary>Keep the desktop's 15.6 ms timer ticks instead of 1 ms ones (<c>--desktop-timers</c>), to compare.</summary>
    public bool DesktopTimers { get; set; }

    /// <summary>Record what the game plays to this WAVE file (<c>--audio-capture</c>), from a child recorder process.</summary>
    public string? AudioCapture { get; set; }

    /// <summary>The recorder mode (<c>--record-audio &lt;pid&gt; &lt;file&gt;</c>): the process whose audio is recorded.</summary>
    public int? RecordAudioProcess { get; set; }

    /// <summary>Fail DirectStorage BCPack pages that have no block decoder instead of writing placeholder blocks (<c>--bcpack-missing fail</c>).</summary>
    public bool FailMissingBcPack { get; set; }

    /// <summary>The user's BCPack table file (<c>--bcpack-tables</c>), with which every BCPack codec decodes exactly.</summary>
    public string? BcPackTables { get; set; }

    /// <summary>What XNetworking reports (<c>--network</c>); outbound traffic is sealed in every mode.</summary>
    public NetworkMode NetworkMode { get; set; }

    /// <summary>The connectivity level reported instead of the mode's own (<c>--network-level</c>).</summary>
    public ConnectivityLevel? NetworkLevel { get; set; }

    public bool Verbose { get; set; }

    public bool ListSafeCalls { get; set; }

    /// <summary>File of <c>module!symbol</c> lines to classify through the shim table (the <c>--classify</c> mode).</summary>
    public string? ClassifyInput { get; set; }

    /// <summary>Where the classification goes; null writes it to standard output.</summary>
    public string? ClassifyOutput { get; set; }

    public bool Help { get; set; }
}
