// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using FiveStar.Loader.Codegen;
using FiveStar.Loader.Diagnostics;
using FiveStar.Loader.Loading;
using FiveStar.Loader.Native;
using FiveStar.Loader.Shims.D3D12X.Binding;
using FiveStar.Loader.Shims.D3D12X.DebugLayer;
using FiveStar.Loader.Shims.D3D12X.Pipelines;
using FiveStar.Loader.Shims.D3D12X.Present;
using FiveStar.Loader.Shims.D3D12X.Resources;
using FiveStar.Loader.Shims.D3D12X.States;
using FiveStar.Shaders.Cache;
using FiveStar.Shaders.Compilation;

namespace FiveStar.Loader.Shims.D3D12X;

/// <summary>
/// D3D12XboxCreateDevice on a desktop D3D12 device. The game gets wrappers (<see cref="XboxObjects"/>) for the device
/// and every object it creates; this class binds the slots that need managed code: creation (results wrapped), the
/// X-only device and queue slots with a desktop translation, and the getters whose D3D12.X form differs.
/// </summary>
internal sealed unsafe class XboxDeviceFactory : IDisposable
{
    /// <summary>Device slot 127, a committed resource with the CreateCommittedResource3 arguments (*inferred*).</summary>
    private const int CreateCommittedResourceSlot = 127;

    /// <summary>Device slot 74, read as registering a mapped range with the GPU (*inferred*).</summary>
    private const int RegisterGpuRangeSlot = 74;

    /// <summary>Device slot 133, read as CreatePlacedResourceX (*inferred*).</summary>
    private const int CreatePlacedResourceSlot = 133;

    /// <summary>Resource slot 11 (GetGPUVirtualAddress) and descriptor-heap slots 9 and 10 (heap start handles).</summary>
    private const int ResourceGpuAddressSlot = 11;
    private const int HeapCpuStartSlot = 9;
    private const int HeapGpuStartSlot = 10;

    private readonly D3D12Forwarder _d3d12;
    private readonly UnresolvedStubs _stubs;
    private readonly PipelineFolders _pipelineFolders;
    private readonly LoaderLog _log;
    private ProgramIndex? _programIndex;

    public XboxDeviceFactory(D3D12Forwarder d3d12, UnresolvedStubs stubs, CodeHeap heap, XMemPhysicalMemory memory, ImageTable images, PipelineFolders pipelines, LoaderLog log)
    {
        _d3d12 = d3d12;
        _stubs = stubs;
        _pipelineFolders = pipelines;
        _log = log;

        Debugging = new D3D12Debugging(d3d12, images, log);
        Warnings = new OnceLog(log);
        Objects = new XboxObjects(stubs, heap, Debugging, log);
        CommandLists = new XboxCommandLists(Objects);
        ListStates = new CommandListStates();
        RenderTargets = new States.RenderTargetStates();

        MemorySource = new GameMemorySource(memory);
        Textures = new GpuTextures(MemorySource, Descriptions, log);

        SectionHeaps = new SectionHeaps(log);
        Binding.VramTwins.Log = log;
        PlacedResources = new XboxPlacedResources(memory, SectionHeaps, Objects, Textures, log);
        GameMemory = new XboxGameMemoryBuffers(Objects, PlacedResources.Addresses, memory.Graphics, log);
        PlacedResources.GameMemory = GameMemory;

        StalePages = new StaleGpuPages(PlacedResources.Addresses, memory, log);
        memory.ObservePages(StalePages.Changed);
        memory.ObservePages((address, _, frame) => UnmappedTextures(memory, address, frame));
        memory.ObserveWrites((address, bytes) => WrittenTextures(memory, address, bytes));

        Uploads = new UploadArena(Objects, PlacedResources.Addresses, log);
        AddressBuffers = new AddressBufferViews(GameMemory);
        AddressViews = new AddressShaderViews(Textures, AddressBuffers, Warnings, log);
        MemoryViews = new GameMemoryViews(memory, SectionHeaps, Warnings, log);
        PlacedResources.Addresses.Observe(MemoryViews.Add, MemoryViews.Remove);
        memory.ObservePages(MemoryViews.PageChanged);

        var hooks = new TitleMemoryHooks(images);
        Bindings = new XboxBindings(log, Warnings);
        DescriptorHeaps = new XboxDescriptorHeaps(Objects, hooks, Bindings.Window, MemoryViews, log, Warnings);
        CommittedResources = new XboxCommittedResources(Objects, Descriptions, hooks, log);
        Presenter = new Presenter(Objects, Warnings, log);
        Rectangles = new RectangleDraws(log);

        BindDeviceSlots();
        BindObjectSlots();
        XboxBindings.Register(Objects);
    }

    /// <summary>Nsight Aftermath GPU crash dumps (<c>--aftermath</c>), enabled before the device; null when off.</summary>
    public AftermathCrashDumps? Aftermath { get; set; }

    /// <summary>The wrappers and their vtables; <see cref="XboxObjects.SetHandler"/> is where other shims bind slots.</summary>
    public XboxObjects Objects { get; }

    /// <summary>The completed-value cells fence slot 12 hands out.</summary>
    public FenceValueMirror Fences { get; } = new();

    /// <summary>Resource descs made desktop ones, with the device's format support.</summary>
    public DesktopResourceDescriptions Descriptions { get; } = new();

    /// <summary>Allocators lent to lists whose game allocator backs another recording list.</summary>
    public SpareAllocators SpareAllocators { get; } = new();

    /// <summary>Warnings logged once per key, for slots the game calls often.</summary>
    public OnceLog Warnings { get; }

    /// <summary>The opt-in debug layer, info-queue log and DRED report (<c>--d3d12-debug</c>).</summary>
    public D3D12Debugging Debugging { get; }

    /// <summary>The command-list wrappers device slots 12 and 73 hand out.</summary>
    public XboxCommandLists CommandLists { get; }

    /// <summary>The textures each command list moved out of their home state (list slots 9 and 26).</summary>
    public CommandListStates ListStates { get; }

    /// <summary>The render targets that rest in UNORDERED_ACCESS, by the views written of them (<see cref="States.RenderTargetStates"/>).</summary>
    public States.RenderTargetStates RenderTargets { get; }

    /// <summary>The pipeline slots (55, 57, 62, 67, 86) once a device exists and the shader folders are known.</summary>
    public PipelineSlots? Pipelines { get; private set; }

    /// <summary>Rectangle-list draws and their pipeline variants.</summary>
    public RectangleDraws Rectangles { get; }

    /// <summary>Descriptor heaps, root signatures and root arguments on command lists.</summary>
    public XboxBindings Bindings { get; }

    /// <summary>Device slot 14 and the title-memory descriptor handles.</summary>
    public XboxDescriptorHeaps DescriptorHeaps { get; }

    /// <summary>Desktop buffers over the memory the game committed itself, found by address.</summary>
    public XboxGameMemoryBuffers GameMemory { get; }

    /// <summary>The address map through which programs read game memory at console GPU addresses.</summary>
    public GameMemoryViews MemoryViews { get; }

    /// <summary>Device slot 127 and the title memory behind its resources.</summary>
    public XboxCommittedResources CommittedResources { get; }

    /// <summary>The heaps over the game's page sections, which slot 133 and the address map share.</summary>
    public SectionHeaps SectionHeaps { get; }

    /// <summary>The section windows kept as a cache over the budget (<c>--evict-windows</c>); null otherwise.</summary>
    public WindowResidency? Residency { get; private set; }

    /// <summary>Device slot 133 and the console-to-desktop GPU address table.</summary>
    public XboxPlacedResources PlacedResources { get; }

    /// <summary>Pages the game remaps under the buffers of <see cref="PlacedResources"/>, which keep their old frames.</summary>
    public StaleGpuPages StalePages { get; }

    /// <summary>The memory of list slot 148.</summary>
    public UploadArena Uploads { get; }

    /// <summary>Queue slot 32 on a swap chain on the game's window.</summary>
    public Presenter Presenter { get; }

    /// <summary>Device slots 78 to 80, the frame events on a timer.</summary>
    public FramePacer Pacer { get; } = new();

    /// <summary>Keeps the game within a few presents of the GPU (<see cref="GpuFrameThrottle"/>).</summary>
    public GpuFrameThrottle FrameThrottle { get; } = new();

    /// <summary>Desktop textures for console memory, by address (docs/gpu-resources.md).</summary>
    public GpuTextures Textures { get; }

    /// <summary>Where the CPU reads console GPU memory.</summary>
    public GameMemorySource MemorySource { get; }

    /// <summary>Plane and render-target readback (<c>--capture-dir</c>); null when off.</summary>
    public FrameCapture? Capture { get; private set; }

    /// <summary>The draw trace (<c>--d3d12-trace-draws</c>); null when off.</summary>
    public DrawTrace? DrawTrace { get; private set; }

    /// <summary>Buffer SRVs at console GPU addresses (slot 70 buffer views, CBVs in the game's heaps).</summary>
    public AddressBufferViews AddressBuffers { get; }

    /// <summary>Device slot 70, SRVs at console GPU addresses.</summary>
    public AddressShaderViews AddressViews { get; }

    /// <summary>HRESULT D3D12XboxCreateDevice(IGraphicsUnknown* adapter, const PARAMETERS*, REFIID, void** device).</summary>
    public int CreateDevice(nint adapter, byte* parameters, Guid* iid, nint* device)
    {
        if (device == null)
        {
            return HResult.Pointer;
        }

        *device = 0;
        LogParameters(adapter, parameters, iid);
        Guid desktopIid = D3D12XLayout.DeviceIid;
        nint inner;
        Debugging.BeforeDevice();
        int hr = _d3d12.CreateDevice(D3D12XLayout.MinimumFeatureLevel, &desktopIid, &inner);

        if (HResult.Failed(hr))
        {
            _log.Error($"  [d3d12_xs] D3D12CreateDevice failed: {HResult.Format(hr)}");

            return hr;
        }

        Debugging.AttachDevice(inner);
        Aftermath?.AttachDevice(inner);
        BackgroundProcessing.Apply(inner, _log);

        Presenter.Pix?.AfterPresent(0);
        SectionHeaps.Budget = GpuMemoryBudget.Open(inner, _log);
        SystemMemoryReserve.Take(inner, SectionHeaps.Budget, _log);
        SectionHeaps.WindowOpened = MemoryViews.WindowOpened;

        if (WindowFeedback.Enabled)
        {
            Residency = new WindowResidency(SectionHeaps, MemoryViews, FrameThrottle, _log);
            MemoryViews.Ask = Residency.Ask;
            _log.Info("  [d3d12_xs] --evict-windows: section windows open as programs ask for them and the least recently asked are released");
        }

        QueueProgress.Start(_log);
        QueueProgress.Memory = InPlaceSummary;
        GpuBreadcrumbs.Attach(inner, _log);
        GpuTimings.Attach(inner, _log);

        *device = Objects.WrapDevice(inner);
        Bindings.Attach(inner);
        CreatePipelines(inner);

        return HResult.Ok;
    }

    private const int MebibyteShift = 20;

    /// <summary>A page the game mapped or unmapped: the textures over it (at its GPU address, the alias of a CPU range) are marked, or retired (<see cref="GpuTextureRegistry.RetireUnmapped"/>).</summary>
    private void UnmappedTextures(XMemPhysicalMemory memory, nint address, ulong frame)
    {
        nint page = (nint)((ulong)address & ~(XMemPhysicalMemory.PageSize - 1));
        nint alias = memory.GpuAlias(page);
        ulong gpu = (ulong)page;

        if (alias != 0)
        {
            gpu = (ulong)alias;
        }

        if (frame == 0)
        {
            Textures.Registry.Unmapped(gpu, XMemPhysicalMemory.PageSize);

            return;
        }

        Textures.Registry.Mapped(gpu, XMemPhysicalMemory.PageSize);

        // A page mapped under a texture brings other bytes: a streamed mip the game read into the page before mapping it.
        Textures.MarkWritten(gpu, XMemPhysicalMemory.PageSize);
    }

    /// <summary>
    /// <see cref="XMemPhysicalMemory.ObserveWrites"/>: a DirectStorage request wrote game memory at a CPU address; the
    /// textures there (registered at the address, or at its GPU-only alias) are stale.
    /// </summary>
    private void WrittenTextures(XMemPhysicalMemory memory, nint address, ulong bytes)
    {
        Textures.MarkWritten((ulong)address, bytes);
        nint alias = memory.GpuAlias(address);

        if (alias != 0)
        {
            Textures.MarkWritten((ulong)alias, bytes);
        }
    }

    /// <summary>The GPU's memory groups and what of the game's memory the GPU reads in place, for the removal report.</summary>
    private string InPlaceSummary()
    {
        string groups = SectionHeaps.Budget?.Sample() ?? "GPU memory unknown";

        return $"{groups}; in place: {SectionHeaps.Opened >> MebibyteShift} MiB of section heaps, {GameMemory.CoveredBytes >> MebibyteShift} MiB of the game's own memory";
    }

    /// <summary>
    /// HRESULT D3D12ConfigureVirtualMemoryX(const CONFIGURATION*). The desktop device manages its own GPU virtual
    /// addresses, so the call only logs what the game asked for.
    /// </summary>
    public int ConfigureVirtualMemory(byte* configuration)
    {
        uint count = 0;

        if (configuration != null)
        {
            count = *(uint*)(configuration + D3D12XLayout.VirtualMemoryCountOffset);
        }

        _log.Detail($"  [d3d12_xs] D3D12ConfigureVirtualMemoryX(dword@4={count}) -> S_OK (no-op)");

        return HResult.Ok;
    }

    /// <summary>
    /// The pipeline slots on the new device: ID3D12Device2 for pipeline streams, the bindless root signature every
    /// translated program uses, and the translated programs. Without the shader folders the slots stay stubs.
    /// </summary>
    private void CreatePipelines(nint inner)
    {
        string? programs = _pipelineFolders.Programs;
        string? translated = _pipelineFolders.Translated;

        if (_pipelineFolders.OnDemandNote is not null)
        {
            _log.Info($"  [d3d12_xs] {_pipelineFolders.OnDemandNote}");
        }

        if (!_pipelineFolders.Enabled)
        {
            _log.Detail("  [d3d12_xs] pipeline slots stay stubs: no --shader-dir, or neither --translated-dir nor programs made on demand");

            return;
        }

        Guid device2Iid = DesktopDevice.Device2Iid;
        nint device2 = 0;
        nint root = Bindings.Root;

        if (root == 0 || HResult.Failed(DesktopDevice.QueryInterface(inner, &device2Iid, &device2)))
        {
            _log.Warn("[d3d12_xs] pipeline slots stay stubs: no ID3D12Device2 or bindless root signature");

            return;
        }

        var warnings = new OnceLog(_log);
        WaitForProgramIndex();
        var onDemand = _pipelineFolders.OnDemand?.Open(_programIndex!, programs!, translated, device2, warnings, _log);
        IProgramLibrary library = onDemand ?? (IProgramLibrary)TranslatedProgramLibrary.Open(_programIndex!, translated!, device2, warnings, _log);

        // Builds start only while the machine has the memory; heavy ones on their own thread (docs/d3d12x-pipelines.md#stand-ins).
        // The queues keep only the machine's reserves: each compile's own need is the compile budget's to count. Counting
        // every build thread as a compile here (each parked in the budget too) held 7 of 16 threads off the urgent
        // queue in boot w32 while 1,827 creations waited.
        var builds = new PipelineBuildQueue(Environment.ProcessorCount / PipelineSlots.CoresPerBuildThread, _ => WarmMemoryGuard.RoomForBuild(0));
        var heavyBuilds = new PipelineBuildQueue(1, _ => WarmMemoryGuard.RoomForHeavyBuild(0));
        builds.Start();
        heavyBuilds.Start();

        var rectangles = new RectanglePipelines(device2, root, RectangleFolder(translated), library, warnings, builds) { Compiler = _pipelineFolders.OnDemand?.RectangleCompiler };
        Rectangles.Attach(rectangles);
        var standIns = new StandInPipelines(PipelineSlots.CreationBudget, builds, heavyBuilds, warnings);
        PipelineEviction.Attach(standIns, rectangles, _log);
        ShaderTableGuard.Attach(standIns, () => Objects.DesktopDevice, _log);

        var cache = OpenCache(device2);
        var factory = new PipelineFactory(device2, root, library, new ProgramSignatures(programs!), warnings, rectangles, standIns, cache) { Journal = OpenJournal(), Lazy = !_pipelineFolders.EagerBuilds, OnDemand = onDemand };

        Binding.TableReaches.ProgramFolder = programs;
        Pipelines = new PipelineSlots(factory, warnings);
        PipelineSlotHandlers.Register(Objects);
        MakeAhead(onDemand, factory, cache);
        _log.Detail($"  [d3d12_xs] pipeline slots on: programs {programs}, translated {translated ?? "none"}");
    }

    /// <summary>The rectangle-list geometry shaders: in the translation, else in the program cache (made there on demand).</summary>
    private string RectangleFolder(string? translated)
    {
        if (translated is not null && Directory.Exists(Path.Combine(translated, RectangleGeometryShader.Folder)))
        {
            return Path.Combine(translated, RectangleGeometryShader.Folder);
        }

        return Path.Combine(_pipelineFolders.OnDemand?.Root ?? translated ?? string.Empty, RectangleGeometryShader.Folder);
    }

    /// <summary>The journal's programs and pipelines made ahead; not with --no-draws, where no pipeline is ever built.</summary>
    private void MakeAhead(OnDemandPrograms? onDemand, PipelineFactory factory, PipelineLibraryCache? cache)
    {
        if (SkippedPipelines.All)
        {
            return;
        }

        Prefetch(onDemand);
        JournalPrebuild.Start(factory, cache, _pipelineFolders.Cache, _log);
        JournalCoverage.Start(factory, cache, _pipelineFolders.Cache, _log);
    }

    /// <summary>Queues the programs of the journal's pipelines, in first-use order, on a worker thread.</summary>
    private void Prefetch(OnDemandPrograms? onDemand)
    {
        var folder = _pipelineFolders.Cache;

        if (onDemand is null || folder is null)
        {
            return;
        }

        Task.Run(() =>
        {
            var queued = onDemand.Prefetch(PipelineJournalFile.ShaderPrefixes(Path.Combine(folder, PipelineJournal.FileName)));
            _log.Info($"  [d3d12_xs] programs on demand: {queued} shaders of earlier boots' pipelines queued");
        });
    }

    /// <summary>The journal of the pipelines the game asks for, beside the disk cache; null without a cache folder.</summary>
    private PipelineJournal? OpenJournal()
    {
        var folder = _pipelineFolders.Cache;

        if (folder is null)
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(folder);

            return PipelineJournal.Open(folder);
        }
        catch (IOException exception)
        {
            _log.Warn($"[d3d12_xs] pipeline journal off: {exception.Message}");

            return null;
        }
    }

    /// <summary>The pipeline disk cache of --pipeline-cache, saving in the background; null without one.</summary>
    private PipelineLibraryCache? OpenCache(nint device2)
    {
        var folder = _pipelineFolders.Cache;

        if (folder is null)
        {
            return null;
        }

        var cache = PipelineLibraryCache.Open(folder, device2, _log);
        cache?.StartSaving();

        return cache;
    }

    /// <summary>
    /// Takes the program index of --shader-dir: the one started at loader start (<see cref="PipelineFolders.Index"/>),
    /// else one built now. The loader calls it before the game starts, because the game's hang watchdog also counts
    /// the time its device creation spends (docs/loader.md); device creation then finds the index ready.
    /// </summary>
    public void WaitForProgramIndex()
    {
        string? programs = _pipelineFolders.Programs;

        if (_programIndex is not null || programs is null || !_pipelineFolders.Enabled)
        {
            return;
        }

        if (_pipelineFolders.OnDemand is not null)
        {
            var identity = _pipelineFolders.OnDemand.Identity(out var hashed);
            _log.Info($"  [d3d12_xs] program cache identity {identity}, waited {hashed.TotalMilliseconds:F0} ms for it");
        }

        var load = _pipelineFolders.Index;
        var waited = TimeSpan.Zero;

        if (load is null)
        {
            _programIndex = ProgramIndex.Load(programs);
        }
        else
        {
            _programIndex = load.Wait(out waited);
        }

        string source = _programIndex.Saved ? "saved index" : "read from the programs";
        _log.Info($"  [d3d12_xs] program index: {_programIndex.Count} programs ({source}), waited {waited.TotalMilliseconds:F0} ms for it");
    }

    /// <summary>Reads back the presents <paramref name="capture"/> names; with <paramref name="traceDraws"/> also traces draws.</summary>
    public void EnableCapture(FrameCapture capture, bool traceDraws, string? tracedPipelines = null)
    {
        Capture = capture;
        Presenter.Capture = capture;

        if (traceDraws)
        {
            DrawTrace = new DrawTrace(capture, MemorySource, this, _log) { PipelineFilter = tracedPipelines };
            Presenter.Trace = DrawTrace;
        }
    }

    public void Dispose()
    {
        _log.Info($"  [d3d12_xs] {ListSubmissions.Summary}");
        _log.Info($"  [d3d12_xs] {Bindings.Layouts.Summary}");

        if (DebugLayer.PoolPageWatch.Started)
        {
            _log.Info($"  {DebugLayer.PoolPageWatch.Summary}");
        }

        if (DebugLayer.FourGiBLineWatch.Started)
        {
            _log.Info($"  {DebugLayer.FourGiBLineWatch.Summary}");
        }

        var waits = MemoryWaits.Counts;
        _log.Info($"  [memory wait] {waits.Recorded} recorded: {waits.Held} met at submission, {waits.Deferred} waited for on the GPU, {waits.TimedOut} released by the timeout, {waits.ReleasedForCpu} released for a CPU wait, {waits.Dropped} not kept; {ListSplits.Splits} lists split");
        Pipelines?.Cache?.Dispose();

        Presenter.Dispose();
        Fences.Dispose();
        Textures.Dispose();
        Capture?.Dispose();
        Debugging.Dispose();
    }

    private void BindDeviceSlots()
    {
        delegate* unmanaged<XboxObject*, void*, Guid*, nint*, int> queue = &XboxDeviceCreation.CreateCommandQueue;
        delegate* unmanaged<XboxObject*, int, Guid*, nint*, int> allocator = &XboxDeviceCreation.CreateCommandAllocator;
        delegate* unmanaged<XboxObject*, uint, int, nint, nint, Guid*, nint*, int> list = &XboxDeviceCreation.CreateCommandList;
        delegate* unmanaged<XboxObject*, DescriptorHeapDescription*, Guid*, nint*, int> descriptorHeap = &XboxDeviceCreation.CreateDescriptorHeap;

        delegate* unmanaged<XboxObject*, void*, int, ResourceDescription*, int, void*, Guid*, nint*, int> committed = &XboxDeviceCreation.CreateCommittedResource;
        delegate* unmanaged<XboxObject*, void*, Guid*, nint*, int> heap = &XboxDeviceCreation.CreateHeap;
        delegate* unmanaged<XboxObject*, nint, ulong, ResourceDescription*, int, void*, Guid*, nint*, int> placed = &XboxDeviceCreation.CreatePlacedResource;
        delegate* unmanaged<XboxObject*, ResourceDescription*, int, void*, Guid*, nint*, int> reserved = &XboxDeviceCreation.CreateReservedResource;

        delegate* unmanaged<XboxObject*, uint, nint*, int> residency = &XboxDeviceCreation.Residency;
        delegate* unmanaged<XboxObject*, ulong, int, Guid*, nint*, int> fence = &XboxDeviceCreation.CreateFence;
        delegate* unmanaged<XboxObject*, void*, Guid*, nint*, int> queryHeap = &XboxDeviceCreation.CreateQueryHeap;
        delegate* unmanaged<XboxObject*, CommandSignatureDescription*, nint, Guid*, nint*, int> signature = &XboxDeviceCreation.CreateCommandSignature;

        BindDevice(XboxDeviceCreation.CreateCommandQueueSlot, (nint)queue);
        BindDevice(XboxDeviceCreation.CreateCommandAllocatorSlot, (nint)allocator);
        BindDevice(XboxDeviceCreation.CreateCommandListSlot, (nint)list);
        BindDevice(XboxDeviceCreation.CreateDescriptorHeapSlot, (nint)descriptorHeap);
        BindDevice(XboxDeviceCreation.CreateCommittedResourceSlot, (nint)committed);
        BindDevice(XboxDeviceCreation.CreateHeapSlot, (nint)heap);

        BindDevice(XboxDeviceCreation.CreatePlacedResourceSlot, (nint)placed);
        BindDevice(XboxDeviceCreation.CreateReservedResourceSlot, (nint)reserved);
        BindDevice(XboxDeviceCreation.MakeResidentSlot, (nint)residency);
        BindDevice(XboxDeviceCreation.EvictSlot, (nint)residency);
        BindDevice(XboxDeviceCreation.CreateFenceSlot, (nint)fence);
        BindDevice(XboxDeviceCreation.CreateQueryHeapSlot, (nint)queryHeap);

        BindDevice(XboxDeviceCreation.CreateCommandSignatureSlot, (nint)signature);
        BindResourceQueries();
        BindDescriptorSlots();
        BindXboxDeviceSlots();
    }

    /// <summary>The queries that take resource descs, which may carry console flags and layouts.</summary>
    private void BindResourceQueries()
    {
        delegate* unmanaged<ResourceAllocationInfo*, XboxObject*, uint, uint, ResourceDescription*, ResourceAllocationInfo*> allocationInfo = &XboxDeviceCreation.GetResourceAllocationInfo;
        delegate* unmanaged<XboxObject*, ResourceDescription*, uint, uint, ulong, void*, uint*, ulong*, ulong*, void> footprints = &XboxDeviceCreation.GetCopyableFootprints;
        BindDevice(XboxDeviceCreation.GetResourceAllocationInfoSlot, (nint)allocationInfo);
        BindDevice(XboxDeviceCreation.GetCopyableFootprintsSlot, (nint)footprints);

        delegate* unmanaged<ResourceAllocationInfo*, XboxObject*, uint, uint, ResourceDescription*, void*, ResourceAllocationInfo*> allocationInfo1 = &XboxDeviceCreation.GetResourceAllocationInfo1;
        BindDevice(XboxDeviceCreation.XboxAllocationInfo1Slot, (nint)allocationInfo1);
    }

    /// <summary>View creation and descriptor copies, whose CPU handles may be title memory (<see cref="XboxDescriptorMethods"/>).</summary>
    private void BindDescriptorSlots()
    {
        delegate* unmanaged<XboxObject*, ConstantBufferViewDescription*, nint, void> constantBuffer = &XboxDescriptorMethods.CreateConstantBufferView;
        delegate* unmanaged<XboxObject*, nint, void*, nint, void> shaderResource = &XboxDescriptorMethods.CreateShaderResourceView;
        delegate* unmanaged<XboxObject*, nint, nint, void*, nint, void> unorderedAccess = &XboxDescriptorMethods.CreateUnorderedAccessView;
        delegate* unmanaged<XboxObject*, nint, void*, nint, void> renderTarget = &XboxDescriptorMethods.CreateRenderTargetView;
        delegate* unmanaged<XboxObject*, nint, void*, nint, void> depthStencil = &XboxDescriptorMethods.CreateDepthStencilView;
        delegate* unmanaged<XboxObject*, void*, nint, void> sampler = &XboxDescriptorMethods.CreateSampler;

        delegate* unmanaged<XboxObject*, uint, nint*, uint*, uint, nint*, uint*, int, void> copy = &XboxDescriptorMethods.CopyDescriptors;
        delegate* unmanaged<XboxObject*, uint, nint, nint, int, void> copySimple = &XboxDescriptorMethods.CopyDescriptorsSimple;
        delegate* unmanaged<XboxObject*, uint, uint> increment = &XboxDescriptorMethods.GetDescriptorHandleIncrementSize;
        BindDevice(XboxDescriptorMethods.IncrementSlot, (nint)increment);

        BindDevice(XboxDescriptorMethods.CreateConstantBufferViewSlot, (nint)constantBuffer);
        BindDevice(XboxDescriptorMethods.CreateShaderResourceViewSlot, (nint)shaderResource);
        BindDevice(XboxDescriptorMethods.CreateUnorderedAccessViewSlot, (nint)unorderedAccess);
        BindDevice(XboxDescriptorMethods.CreateRenderTargetViewSlot, (nint)renderTarget);

        BindDevice(XboxDescriptorMethods.CreateDepthStencilViewSlot, (nint)depthStencil);
        BindDevice(XboxDescriptorMethods.CreateSamplerSlot, (nint)sampler);
        BindDevice(XboxDescriptorMethods.CopyDescriptorsSlot, (nint)copy);
        BindDevice(XboxDescriptorMethods.CopyDescriptorsSimpleSlot, (nint)copySimple);
    }

    /// <summary>The X-only device slots with a desktop translation (<see cref="XboxDeviceMethods"/>).</summary>
    private void BindXboxDeviceSlots()
    {
        delegate* unmanaged<XboxObject*, CommandQueueDescription*, Guid*, nint*, int> createQueue = &XboxDeviceMethods.CreateCommandQueue;
        delegate* unmanaged<XboxObject*, CommandListDescription*, nint, nint, Guid*, nint*, int> createList = &XboxDeviceMethods.CreateCommandList;
        delegate* unmanaged<XboxObject*, ulong, ResourceDescription*, uint, void*, Guid*, nint*, int> placed = &XboxDeviceMethods.CreatePlacedResource;

        BindDevice(D3D12XLayout.CreateCommandQueueSlot, (nint)createQueue);
        BindDevice(D3D12XLayout.CreateCommandListSlot, (nint)createList);
        BindDevice(CreatePlacedResourceSlot, (nint)placed);

        delegate* unmanaged<XboxObject*, void*, int, ResourceDescription*, int, void*, nint, uint, void*, Guid*, nint*, int> committed = &XboxDeviceMethods.CreateCommittedResource;
        BindDevice(CreateCommittedResourceSlot, (nint)committed);
        BindAddressSlots();
    }

    /// <summary>Slots 61, 70 and 74, which describe memory by address (<see cref="XboxAddressDescriptors"/>).</summary>
    private void BindAddressSlots()
    {
        delegate* unmanaged<XboxObject*, ResourceDescription*, AddressViewDescription*, nint, void> view = &XboxAddressDescriptors.CreateShaderResourceViewAtAddress;
        delegate* unmanaged<XboxObject*, ConsoleSamplerDescription*, nint, void> sampler = &XboxAddressDescriptors.CreateSamplerX;
        delegate* unmanaged<XboxObject*, nint, uint, ulong*, int> range = &XboxDeviceMethods.RegisterGpuRange;

        BindDevice(XboxAddressDescriptors.CreateShaderResourceViewAtAddressSlot, (nint)view);
        BindDevice(XboxAddressDescriptors.CreateSamplerXSlot, (nint)sampler);
        BindDevice(RegisterGpuRangeSlot, (nint)range);
    }

    private void BindObjectSlots()
    {
        delegate* unmanaged<XboxObject*, uint, nint*, nint, uint, int> executeX = &XboxQueueMethods.ExecuteCommandListsX;
        delegate* unmanaged<XboxObject*, uint, nint*, void> execute = &XboxQueueMethods.ExecuteCommandLists;
        delegate* unmanaged<XboxResourceObject*, ulong> gpuAddress = &XboxObjectMethods.GetGpuVirtualAddress;
        delegate* unmanaged<XboxDescriptorHeapObject*, nint> cpuStart = &XboxObjectMethods.GetCpuHandleForHeapStart;
        delegate* unmanaged<XboxDescriptorHeapObject*, nint> gpuStart = &XboxObjectMethods.GetGpuHandleForHeapStart;

        Objects.SetHandler(XboxInterfaceKind.CommandQueue, XboxQueueMethods.ExecuteCommandListsXSlot, (nint)executeX);
        Objects.SetHandler(XboxInterfaceKind.CommandQueue, XboxQueueMethods.ExecuteCommandListsSlot, (nint)execute);
        Objects.SetHandler(XboxInterfaceKind.Resource, ResourceGpuAddressSlot, (nint)gpuAddress);

        delegate* unmanaged<XboxResourceObject*, uint, void*, void**, int> map = &XboxObjectMethods.Map;
        delegate* unmanaged<XboxResourceObject*, uint, void*, void> unmap = &XboxObjectMethods.Unmap;
        Objects.SetHandler(XboxInterfaceKind.Resource, XboxObjectMethods.MapSlot, (nint)map);
        Objects.SetHandler(XboxInterfaceKind.Resource, XboxObjectMethods.UnmapSlot, (nint)unmap);

        delegate* unmanaged<XboxObject*, nint, ulong, nint, ulong, ulong, void> copyBuffer = &BufferCopies.CopyBufferRegion;
        Objects.SetHandler(XboxInterfaceKind.CommandList, BufferCopies.CopyBufferRegionSlot, (nint)copyBuffer);

        delegate* unmanaged<XboxObject*, int> resetAllocator = &XboxObjectMethods.ResetAllocator;
        Objects.SetHandler(XboxInterfaceKind.CommandAllocator, XboxObjectMethods.AllocatorResetSlot, (nint)resetAllocator);

        delegate* unmanaged<XboxFenceObject*, ulong*> fenceValue = &FenceValueMirror.CompletedValueAddress;
        delegate* unmanaged<XboxFenceObject*, uint> releaseFence = &FenceValueMirror.ReleaseFence;
        delegate* unmanaged<XboxObject*, nint, ulong, int> signal = &FenceValueMirror.QueueSignal;
        Objects.SetHandler(XboxInterfaceKind.CommandQueue, FenceValueMirror.QueueSignalSlot, (nint)signal);

        delegate* unmanaged<XboxObject*, nint, ulong, int> wait = &FenceValueMirror.QueueWait;
        Objects.SetHandler(XboxInterfaceKind.CommandQueue, FenceValueMirror.QueueWaitSlot, (nint)wait);
        QueuePageMappings.Bind(Objects);
        Objects.SetHandler(XboxInterfaceKind.Fence, FenceValueMirror.CompletedValueAddressSlot, (nint)fenceValue);
        Objects.SetHandler(XboxInterfaceKind.Fence, ComObject.ReleaseSlot, (nint)releaseFence);

        delegate* unmanaged<XboxResourceObject*, uint> releaseResource = &XboxCommittedResources.ReleaseResource;
        Objects.SetHandler(XboxInterfaceKind.Resource, ComObject.ReleaseSlot, (nint)releaseResource);
        Objects.SetHandler(XboxInterfaceKind.DescriptorHeap, HeapCpuStartSlot, (nint)cpuStart);
        Objects.SetHandler(XboxInterfaceKind.DescriptorHeap, HeapGpuStartSlot, (nint)gpuStart);

        delegate* unmanaged<XboxObject*, uint, void> topology = &PrimitiveTopologies.SetPrimitiveTopology;
        Objects.SetHandler(XboxInterfaceKind.CommandList, PrimitiveTopologies.SetPrimitiveTopologySlot, (nint)topology);
        BindInputBuffers();
        RectangleListSlots.Register(Objects);
        PresentSlots.Bind(Objects);

        ResourceBarrierSlots.Bind(Objects);
        CacheFlushSlots.Bind(Objects);
        EnhancedBarrierSlots.Bind(Objects);
        TextureCopies.Bind(Objects);

        UploadAllocations.Bind(Objects);
        BottomOfPipeWrites.Bind(Objects);
        MemoryWaits.Bind(Objects, _log);
        ListStateSlots.Register(Objects);
    }

    private void BindInputBuffers()
    {
        delegate* unmanaged<XboxObject*, VertexBufferView*, void> indexBuffer = &InputBuffers.SetIndexBuffer;
        delegate* unmanaged<XboxObject*, uint, uint, VertexBufferView*, void> vertexBuffers = &InputBuffers.SetVertexBuffers;
        Objects.SetHandler(XboxInterfaceKind.CommandList, InputBuffers.SetIndexBufferSlot, (nint)indexBuffer);
        Objects.SetHandler(XboxInterfaceKind.CommandList, InputBuffers.SetVertexBuffersSlot, (nint)vertexBuffers);
    }

    private void BindDevice(int slot, nint handler)
    {
        Objects.SetHandler(XboxInterfaceKind.Device, slot, handler);
    }

    private void LogParameters(nint adapter, byte* parameters, Guid* iid)
    {
        if (!_log.DetailEnabled)
        {
            return;
        }

        var text = new StringBuilder($"  [d3d12_xs] D3D12XboxCreateDevice(adapter=0x{adapter:x}");

        if (iid != null)
        {
            text.Append($", iid={*iid}");
        }

        if (parameters != null)
        {
            text.Append(", parameters=");

            for (int offset = 0; offset < D3D12XLayout.ParametersSize; offset += sizeof(uint))
            {
                text.Append($" {*(uint*)(parameters + offset):x8}");
            }
        }

        _log.Detail(text.Append(')').ToString());
    }
}
