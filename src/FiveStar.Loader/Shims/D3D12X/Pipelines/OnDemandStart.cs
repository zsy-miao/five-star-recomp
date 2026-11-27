// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using FiveStar.Graphics;
using FiveStar.Graphics.Pipelines;
using FiveStar.Graphics.Programs;
using FiveStar.Loader.Diagnostics;
using FiveStar.Shaders.Cache;
using FiveStar.Shaders.Compilation;
using FiveStar.Shaders.Disassembly;
using FiveStar.Shaders.Translation;

namespace FiveStar.Loader.Shims.D3D12X.Pipelines;

/// <summary>
/// Programs made on demand, from loader start: the compiler and the identity of this translator and compiler build,
/// hashed on a worker thread (the game's device creation only picks it up). Programs go to
/// <c>&lt;program cache&gt;/&lt;identity&gt;/&lt;adapter&gt;/</c>, so a new translator, compiler or GPU starts a new folder.
/// </summary>
internal sealed class OnDemandStart
{
    /// <summary>The program cache folder's name in the work folder.</summary>
    public const string FolderName = "program-cache";

    private const string DxcVariable = "FIVESTAR_DXC";
    private const string SdkFolder = @"C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64";
    private const string ShadersAssembly = "FiveStar.Shaders.dll";
    private const int IdentityLength = 16;
    private const int ProcessorsPerJob = 4;
    private const int HostProcessorsPerJob = 2;
    private const string HlslExtension = ".hlsl";
    private const string GeometryProfile = "gs";

    private const string WarmUpSource = """
        #define VW_WAVE 64
        #define VW_EMULATE 1
        #include "rdna_prelude.hlsli"
        [numthreads(64, 1, 1)]
        void main(uint3 id : SV_DispatchThreadID) {
          uint v0 = WaveActiveSum(id.x);
          if (v0 > 3u) { v0 = v0 * 2u; }
        }
        """;

    private const string ComputeProfile = "cs";
    private const string WarmUpFile = "warm-up.hlsl";

    private readonly Task<string> _identity;
    private readonly IShaderCompiler _compiler;
    private readonly DxilBackendCompiler? _backend;
    private readonly IBackendRunner? _runner;

    private OnDemandStart(string root, IShaderCompiler compiler, DxilBackendCompiler? backend, IBackendRunner? runner, int jobs)
    {
        Root = root;
        Jobs = jobs;
        _compiler = compiler;
        _backend = backend;
        _runner = runner;
        _identity = Task.Run(() => Identity(compiler));

        if (runner is not null)
        {
            _ = Task.Run(WarmUp);
        }
    }

    /// <summary>
    /// Compiles a small program, so the compile host is running and the backend's code jitted before a game thread
    /// compiles a program inline.
    /// </summary>
    private void WarmUp()
    {
        Directory.CreateDirectory(Root);
        _runner!.Run(new CompileRequest(Path.Combine(Root, WarmUpFile), WarmUpSource, ComputeProfile, DisableOptimizations: false));
    }

    /// <summary>
    /// The translator and compiler build's identity; programs that trace their stores (--trace-pool-stores), read
    /// their user data from root constants (--user-data-constants) or report the windows they want (--evict-windows)
    /// have their own.
    /// </summary>
    private static string Identity(IShaderCompiler compiler)
    {
        string identity = ContentHash.OfText(ShadersIdentity(), nameof(RdnaDecoder), Translation.ToString(), compiler.Identity, Binding.DescriptorWindow.Header);

        if (DebugLayer.PoolStoreTrace.Enabled)
        {
            identity = ContentHash.OfText(identity, "store-trace");
        }

        if (Binding.UserDataConstants.Enabled)
        {
            identity = ContentHash.OfText(identity, BindlessRootSignature.UserConstantsDefine);
        }

        if (Binding.WindowFeedback.Enabled)
        {
            identity = ContentHash.OfText(identity, Binding.WindowFeedback.Header);
        }

        if (Binding.WindowGuard.Enabled)
        {
            identity = ContentHash.OfText(identity, Binding.WindowGuard.Header);
        }

        if (Binding.BvhMiss.Enabled)
        {
            identity = ContentHash.OfText(identity, Binding.BvhMiss.Header);
        }

        return identity;
    }

    /// <summary>
    /// The translator choices of programs made on demand (<c>--loop-caps</c> sets their loop caps); part of the
    /// identity, so set before <see cref="Begin"/>.
    /// </summary>
    public static TranslationOptions Translation { get; set; } = CorpusOptions.DefaultTranslation;

    /// <summary>
    /// The hand-written programs that replace translations (<c>--shader-overrides</c>); null for none. Not part of the
    /// identity: a replaced program's files hold its replacement's hash (<see cref="ShaderOverrides.Stem"/>).
    /// </summary>
    public static ShaderOverrides? Overrides { get; set; }

    /// <summary>The program cache folder.</summary>
    public string Root { get; }

    /// <summary>Threads that make programs.</summary>
    public int Jobs { get; }

    /// <summary>Compiles a rectangle-list geometry shader source to a DXIL path (<see cref="RectanglePipelines.Compiler"/>).</summary>
    public Func<string, string, byte[]?> RectangleCompiler => (source, path) =>
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var request = new CompileRequest(Path.ChangeExtension(path, HlslExtension), source, GeometryProfile, DisableOptimizations: false);

        if (!_compiler.Compile(request, _compiler.Options.CompileTimeout).Succeeded)
        {
            return null;
        }

        return File.ReadAllBytes(request.DxilPath);
    };

    /// <summary>Programs the backend compiled and handed to dxc, for the summary; empty with dxc alone.</summary>
    public string BackendCounts
    {
        get
        {
            if (_backend is null)
            {
                return string.Empty;
            }

            if (_runner is CompileHost host)
            {
                return $", {_backend.Summary}, {host.Summary}";
            }

            return ", " + _backend.Summary;
        }
    }

    /// <summary>
    /// Starts the identity hash; null with a message when no dxcompiler.dll is found. Programs compile with the DXIL
    /// backend, validated by dxil.dll, and with dxc when it cannot lower them or the validator rejects them; with
    /// <paramref name="dxcOnly"/> with dxc alone.
    /// </summary>
    public static OnDemandStart? Begin(string root, string? dxc, bool dxcOnly, int? jobs, out string message)
    {
        var library = FindLibrary(dxc);

        if (library is null)
        {
            message = $"no {DxcLibraryCompiler.LibraryFile} (--dxc, {DxcVariable}, beside the loader or in the Windows SDK): programs are not made on demand";

            return null;
        }

        var dxcCompiler = new DxcLibraryCompiler(DxcOptions.Defaults(library, retry: false));
        string launch = MeshLaunch.Prepare(dxcCompiler, root);

        if (dxcOnly)
        {
            message = $"programs made on demand with {library} into {root}{launch}";

            return new OnDemandStart(root, dxcCompiler, null, null, jobs ?? Math.Max(1, Environment.ProcessorCount / ProcessorsPerJob));
        }

        // A worker waits for the compile host about 80% of the time (36 ms translating, 169 ms waiting per program), so
        // the backend gets twice the workers: 432 programs offline, 8 workers 21-23 programs/s, 16 workers 27.
        jobs ??= Math.Max(1, Environment.ProcessorCount / HostProcessorsPerJob);

        var validator = DxilBackendCompiler.ValidatorBeside(library);
        var runner = Runner(validator);
        var backend = new DxilBackendCompiler(dxcCompiler.Options, dxcCompiler, runner);
        message = $"programs made on demand with the DXIL backend in a compile host (validated by {validator ?? "nothing: no dxil.dll"}), {library} for the rest, into {root}{launch}";

        return new OnDemandStart(root, backend, backend, runner, jobs.Value);
    }

    /// <summary>The backend in a compile host process (the loader itself), or in this process when the loader's path is unknown.</summary>
    private static IBackendRunner Runner(string? validator)
    {
        var executable = Environment.ProcessPath;

        if (executable is null)
        {
            return new InProcessBackend(validator, null);
        }

        var host = new CompileHost(executable, validator, CompileHost.AnswerLimit);
        CompileBudget.HelperPrivateBytes = () => host.PrivateBytes;

        return host;
    }

    /// <summary>The identity, waiting for it; <paramref name="waited"/> is the time spent waiting.</summary>
    public string Identity(out TimeSpan waited)
    {
        long start = Stopwatch.GetTimestamp();
        using var wait = Diagnostics.BlockingWaits.Enter("program cache identity");
        var identity = _identity.GetAwaiter().GetResult();
        waited = Stopwatch.GetElapsedTime(start);

        return identity;
    }

    /// <summary>
    /// The on-demand library for the adapter of <paramref name="device"/>, over the ahead-of-time translation when there
    /// is one, with its workers started.
    /// </summary>
    public OnDemandPrograms Open(ProgramIndex index, string programs, string? translated, nint device, OnceLog warnings, LoaderLog log)
    {
        var adapter = DesktopAdapter.Describe(device);
        var folder = Path.Combine(Root, Identity(out _)[..IdentityLength], AdapterFolder(adapter));
        Directory.CreateDirectory(folder);
        WriteIncludes(folder);
        DebugLayer.MeshProbes.Folder = folder;
        Binding.WindowGuard.Index = index;

        var ownLog = Path.Combine(Root, TimedCompiler.LogName);
        List<string> logs = [ownLog, .. TimedCompiler.Logs(Root).Where(path => path != ownLog)];
        TranslatedProgramLibrary? ahead = null;

        // The ahead-of-time translation reads its user data from a root CBV.
        if (translated is not null && Directory.Exists(translated) && !Binding.UserDataConstants.Enabled)
        {
            ahead = TranslatedProgramLibrary.Open(index, translated, device, warnings, log);
            logs.Add(Path.Combine(translated, TimedCompiler.LogName));
        }

        var slow = SlowCompiles.Read(logs, _compiler.Options.CompileTimeout);
        var compiler = new TimedCompiler(_compiler, ownLog, TimeProvider.System, Environment.ProcessId);
        var picker = new ProgramVariantPicker(new ShaderManifest([]), adapter, warnings);
        var workers = new ProgramWorkers(
            Jobs,
            running => WarmMemoryGuard.RoomForPrograms(running) && CompileBudget.RoomForPrograms(running),
            running => WarmMemoryGuard.RoomForHeavyBuild(running) && CompileBudget.RoomForPrograms(running));
        workers.Start();
        log.Detail($"  [d3d12_xs] programs on demand: {folder}, {Jobs} threads and one for heavy compiles, {slow.Count} sources slow at -O3");

        ShimContext.ActiveRuntime?.D3D12X.Bindings.Window.StaticSamplers.AddFolder(folder);

        ProgramHosting? hosting = null;

        if (_runner is CompileHost host && _backend is not null)
        {
            hosting = new ProgramHosting(host, _backend, adapter);
        }

        var producer = new ProgramProducer(programs, folder, picker, compiler, slow)
        {
            Hosting = hosting,
            Overrides = Overrides,
            ContentStore = new FiveStar.Shaders.Cache.ProgramContentStore(FiveStar.Shaders.Cache.ProgramContentStore.FolderBeside(folder), FiveStar.Shaders.Cache.ProgramContentStore.CompilerIdentity(compiler)),
        };

        return new OnDemandPrograms(index, ahead, producer, workers, warnings, () => BackendCounts);
    }

    /// <summary>The adapter facts that pick a program's form: shader model, wave lane range, mesh-shader tier and mesh wave.</summary>
    private static string AdapterFolder(AdapterInfo adapter)
    {
        var folder = $"sm{adapter.ShaderModel}-w{adapter.WaveLaneCountMin}-{adapter.WaveLaneCountMax}-{adapter.MeshShaderTier}";

        if (adapter.MeshWaveLanes is not null)
        {
            folder += $"-ms{adapter.MeshWaveLanes}";
        }

        return folder;
    }

    /// <summary>The runtime headers the sources include; another process may be writing them too.</summary>
    private static void WriteIncludes(string folder)
    {
        if (RuntimeIncludes.Names.All(name => File.Exists(Path.Combine(folder, name))))
        {
            return;
        }

        try
        {
            RuntimeIncludes.WriteTo(folder);
        }
        catch (IOException)
        {
            // Written by the other process; the identity covers their text.
        }
    }

    /// <summary>The translator build: FiveStar.Shaders.dll beside the loader, or the loader itself when published as one file.</summary>
    private static string ShadersIdentity()
    {
        var assembly = Path.Combine(AppContext.BaseDirectory, ShadersAssembly);

        if (File.Exists(assembly))
        {
            return ContentHash.OfFiles([assembly]);
        }

        return ContentHash.OfFiles([Environment.ProcessPath ?? string.Empty]);
    }

    private static string? FindLibrary(string? dxc)
    {
        string?[] candidates = [dxc, Environment.GetEnvironmentVariable(DxcVariable), AppContext.BaseDirectory, SdkFolder];

        return candidates
            .Where(candidate => !string.IsNullOrEmpty(candidate))
            .Select(candidate => DxcLibraryCompiler.LibraryPath(candidate!))
            .FirstOrDefault(File.Exists);
    }
}
