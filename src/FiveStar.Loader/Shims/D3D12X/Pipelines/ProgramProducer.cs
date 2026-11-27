// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using FiveStar.Graphics.Programs;
using FiveStar.Shaders.Analysis;
using FiveStar.Shaders.Cache;
using FiveStar.Shaders.Compilation;
using FiveStar.Shaders.Containers;
using FiveStar.Shaders.Disassembly;
using FiveStar.Shaders.Translation;

namespace FiveStar.Loader.Shims.D3D12X.Pipelines;

/// <summary>
/// Makes the DXIL of one extracted program for this adapter, as <c>fivestar shader translate</c> would: translates the
/// FSSM program with the default translator choices, picks the form the adapter runs (<see cref="ProgramVariantPicker"/>:
/// native, wave32 variant or mesh superstep form) and compiles only that one into <c>&lt;folder&gt;/&lt;name&gt;.dxil</c>.
/// A mesh fallback is not compiled: pipelines cannot run it. Sources over the large-source length, or whose optimized
/// compile was slow before (<see cref="SlowCompiles"/>), compile with -Od. A failure that would repeat (the translator
/// refuses the program, no form loads, the compiler reports a source error) is kept as <c>&lt;name&gt;.fail</c>.
/// </summary>
internal sealed class ProgramProducer
{
    private const string ProgramExtension = ".fssm";
    private const string DxilExtension = ".dxil";
    private const string HlslExtension = ".hlsl";
    private const string FailExtension = ".fail";
    private const string VariantDxilSuffix = ".w32.dxil";
    private const string NoForm = "no form: none loads on this adapter";
    private const string NoProgram = "missing: no extracted program file";
    private const string NoProbe = "probe: not a mesh program the probe can take";
    private const string DxilReplacement = "override: a DXIL replacement has no source to link or trim";

    // A compile failure with a source or validation diagnostic repeats; a crash or exhausted memory may not.
    private const string SourceError = ": error: ";
    private const string ValidationError = "error: validation errors";

    private readonly string _programs;
    private readonly string _folder;
    private readonly ProgramVariantPicker _picker;
    private readonly IShaderCompiler _compiler;
    private readonly SlowCompiles _slow;
    private readonly ProgramLoader _loader = new(new RdnaDecoder());
    private int _restored;

    /// <param name="programs">The extracted FSSM programs (--shader-dir), their containers in the sibling dxbc folder.</param>
    /// <param name="folder">Where the DXIL goes: the folder of this translator, compiler and adapter.</param>
    /// <param name="picker">Picks the form for the adapter.</param>
    /// <param name="compiler">Compiles the picked form.</param>
    /// <param name="slow">Sources compiled with -Od from the start.</param>
    public ProgramProducer(string programs, string folder, ProgramVariantPicker picker, IShaderCompiler compiler, SlowCompiles slow)
    {
        _programs = programs;
        _folder = folder;

        // Programs that trace rays are known by their markers here, or read from their console code.
        RayTracingPrograms.Folder = RayTracingPrograms.MarkerFolder(folder);
        RayTracingPrograms.Scanner = TracesRays;
        _picker = picker;
        _compiler = compiler;
        _slow = slow;
    }

    /// <summary>
    /// The defines every program starts with: this process's modes (<see cref="ModeHeader"/>), or in the compile host
    /// those the loader sent with the request.
    /// </summary>
    public string Header { get; init; } = ModeHeader();

    /// <summary>The defines of the modes this process runs in: --user-data-constants, --evict-windows, the window guard and BVH misses.</summary>
    public static string ModeHeader()
    {
        var header = new StringBuilder(Binding.DescriptorWindow.Header);

        if (Binding.UserDataConstants.Enabled)
        {
            header.Append(Binding.UserDataConstants.Header);
        }

        if (Binding.WindowFeedback.Enabled)
        {
            header.Append(Binding.WindowFeedback.Header);
        }

        if (Binding.WindowGuard.Enabled)
        {
            header.Append(Binding.WindowGuard.Header);
        }

        if (Binding.BvhMiss.Enabled)
        {
            header.Append(Binding.BvhMiss.Header);
        }

        return header.ToString();
    }

    /// <summary>
    /// Compiled programs by what they were compiled from (<see cref="ProgramContentStore"/>): a program a new
    /// translator build translates to the same HLSL is copied from there, not compiled again; null keeps none.
    /// </summary>
    public ProgramContentStore? ContentStore { get; init; }

    /// <summary>Programs made by copying their DXIL from <see cref="ContentStore"/> (here or in the compile host) rather than compiling them.</summary>
    public int Restored => Volatile.Read(ref _restored);

    /// <summary>The folder the DXIL goes to.</summary>
    public string Folder => _folder;

    /// <summary>Whether a program's console code holds an image_bvh instruction (it traces rays); null when it cannot be read.</summary>
    public bool? TracesRays(string name)
    {
        var path = Path.Combine(_programs, DebugLayer.MeshProbes.Source(name) + ProgramExtension);

        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return _loader.Load(path, null).Program?.Instructions.Any(instruction => instruction.Text.StartsWith("image_bvh", StringComparison.Ordinal));
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>Whether the program's DXIL is in the folder.</summary>
    public bool Exists(string name)
    {
        return File.Exists(DxilPath(name));
    }

    /// <summary>The failure kept for the program, or null.</summary>
    public string? KeptFailure(string name)
    {
        var path = Path.Combine(_folder, Stem(name) + FailExtension);

        if (!File.Exists(path))
        {
            return null;
        }

        return File.ReadAllText(path);
    }

    /// <summary>
    /// The hand-written programs that replace translations (<c>--shader-overrides</c>); null for none. A replaced
    /// program's files are named by <see cref="Stem"/>.
    /// </summary>
    public ShaderOverrides? Overrides { get; init; }

    /// <summary>The program's file stem here: its name, or for a replaced one the name with the replacement's hash.</summary>
    public string Stem(string name)
    {
        return Overrides?.Stem(name) ?? name;
    }

    /// <summary>Translates the program and picks its form (its replacement's, for a replaced one); a refusal is kept.</summary>
    public ProgramForm Prepare(string name)
    {
        var path = Path.Combine(_programs, DebugLayer.MeshProbes.Source(name) + ProgramExtension);

        if (!File.Exists(path))
        {
            return new ProgramForm(name, null, NoProgram);
        }

        if (Overrides?.DxilFile(name) is not null)
        {
            return new ProgramForm(name, null, DxilReplacement);
        }

        var program = CorpusTranslator.TranslateFile(_loader, path, null, OnDemandStart.Translation);

        if (program.Hlsl is null || program.Stage is null)
        {
            return Refused(name, $"{program.Status ?? ProgramStatus.Crash}: {program.Reason}");
        }

        var profile = ShaderStages.ProfileName(program.Stage.Value);
        var replacement = Overrides?.Hlsl(name);
        var picked = replacement is null ? Picked(name, profile, program) : Replacement(name, profile, replacement);

        if (picked is null)
        {
            return Refused(name, NoForm);
        }

        var (hlsl, source) = picked.Value;

        if (DebugLayer.PoolStoreTrace.Traces(name))
        {
            hlsl = DebugLayer.PoolStoreTrace.Header(name) + DebugLayer.PoolStoreTrace.Instrument(hlsl);
        }

        RayTracingPrograms.Note(name, hlsl, RayTracingPrograms.Folder ?? _folder);
        hlsl = Probed(name, hlsl);
        NoteCost(name, hlsl);
        StaticSampler.Keep(_folder, hlsl);

        if (hlsl.Length == 0)
        {
            return Refused(name, NoProbe);
        }

        hlsl = Header + Binding.WindowGuard.ProgramHeader(Header, name) + hlsl;
        var slow = hlsl.Length > _compiler.Options.LargeSourceLength || _slow.Contains(source);

        return new ProgramForm(name, new CompileRequest(Path.Combine(_folder, source), hlsl, profile, slow), null);
    }

    /// <summary>The translation's form the adapter runs (native or wave32 variant) and its source file; null when none loads.</summary>
    private (string Hlsl, string Source)? Picked(string name, string profile, FiveStar.Shaders.Compilation.TranslatedProgram program)
    {
        var facts = WaveFacts.Read(program.Hlsl!);
        string? variant = null;

        if (program.GroupWave?.Hlsl is not null)
        {
            variant = name + VariantDxilSuffix;
        }

        var forms = new ProgramVariants(name, profile, name + DxilExtension, facts.ProgramWave, facts.Floor, facts.WavePin, variant, null);
        var file = _picker.Pick(forms);

        if (file is null)
        {
            return null;
        }

        if (file == variant)
        {
            return (program.GroupWave!.Hlsl!, name + CorpusTranslator.GroupWaveSuffix);
        }

        return (program.Hlsl!, name + HlslExtension);
    }

    /// <summary>A replacement as the one form, when the adapter runs its waves, its source named by <see cref="Stem"/>; null when it does not.</summary>
    private (string Hlsl, string Source)? Replacement(string name, string profile, string hlsl)
    {
        var facts = WaveFacts.Read(hlsl);
        var forms = new ProgramVariants(name, profile, name + DxilExtension, facts.ProgramWave, facts.Floor, facts.WavePin, null, null);

        if (_picker.Pick(forms) is null)
        {
            return null;
        }

        return (hlsl, Stem(name) + HlslExtension);
    }

    /// <summary>
    /// The compile host that makes programs whole, translation included (<see cref="MakeInHost"/>), the backend compiler
    /// whose counts it keeps and the adapter; null makes them in this process.
    /// </summary>
    public ProgramHosting? Hosting { get; init; }

    /// <summary>
    /// Makes the program in the compile host: translated, its form picked and compiled there, so the translator's
    /// allocations stay out of this process too. See <see cref="HostedMake"/> for what comes back.
    /// </summary>
    public HostedMake MakeInHost(string name)
    {
        // A replaced program is compiled here: the compile host reads no replacements.
        if (Hosting is null || DebugLayer.PoolStoreTrace.Traces(name) || DebugLayer.MeshProbes.IsProbed(name) || (Overrides?.Has(name) ?? false))
        {
            return HostedMake.Here;
        }

        var answer = Hosting.Host.Make(new MakeRequest(name, _programs, _folder, Hosting.Adapter, Header), out bool hostEnded);

        if (answer is null || answer.Kind == HostAnswerKind.Failed)
        {
            return new HostedMake(false, hostEnded, null, null);
        }

        if (answer.Kind == HostAnswerKind.Refused)
        {
            return new HostedMake(true, false, answer.Text, null);
        }

        Hosting.Backend.Count((BackendVerdict)answer.Kind);

        if (answer.Kind == HostAnswerKind.Compiled)
        {
            if (answer.WasRestored)
            {
                Interlocked.Increment(ref _restored);
            }

            return new HostedMake(true, false, null, null);
        }

        var slow = answer.Hlsl.Length > _compiler.Options.LargeSourceLength || _slow.Contains(answer.Source);
        var request = new CompileRequest(Path.Combine(_folder, answer.Source), answer.Hlsl, answer.Profile, slow);

        return new HostedMake(true, false, null, new ProgramForm(name, request, null));
    }

    /// <summary>Compiles a prepared form into <c>&lt;name&gt;.dxil</c>; null when it did, else why not.</summary>
    public string? Compile(ProgramForm form)
    {
        return Compile(form, dxcOnly: false);
    }

    /// <summary>The same; with <paramref name="dxcOnly"/> the hosted backend's fallback (dxc) compiles it, the backend having refused it or ended on it.</summary>
    public string? Compile(ProgramForm form, bool dxcOnly)
    {
        var request = form.Request!;

        if (ContentStore is not null && ContentStore.TryRestore(request))
        {
            Interlocked.Increment(ref _restored);
        }
        else
        {
            var failure = CompileNow(form, request, dxcOnly);

            if (failure is not null)
            {
                return failure;
            }

            ContentStore?.Keep(request);
        }

        var target = DxilPath(form.Name);

        if (!string.Equals(request.DxilPath, target, StringComparison.OrdinalIgnoreCase))
        {
            File.Move(request.DxilPath, target, overwrite: true);
        }

        return null;
    }

    /// <summary>Runs the compiler on <paramref name="request"/>; null when it compiled, else why not (a source or validation error is kept).</summary>
    private string? CompileNow(ProgramForm form, CompileRequest request, bool dxcOnly)
    {
        CompileOutcome outcome;

        if (dxcOnly && Hosting is not null)
        {
            outcome = Hosting.Backend.Fallback(request, _compiler.Options.CompileTimeout, "dxc only");
        }
        else
        {
            outcome = _compiler.Compile(request, _compiler.Options.CompileTimeout);
        }

        if (outcome.Succeeded)
        {
            return null;
        }

        var failure = $"{ProgramStatus.CompileFailure}: {DxcCompiler.FirstError(outcome.Error)}";

        if (outcome.Error.Contains(SourceError, StringComparison.Ordinal) || outcome.Error.Contains(ValidationError, StringComparison.Ordinal))
        {
            Keep(form.Name, failure);
        }

        return failure;
    }

    /// <summary>
    /// Compiles the program's form with its outputs laid out for a pixel shader's <paramref name="inputs"/>
    /// (<see cref="StageLinker.Link(string, IReadOnlyList{PsvElement}, bool)"/>; <paramref name="dropUnread"/> for a mesh program) into <paramref name="dxilPath"/>; null when it did, else why not (kept beside it).
    /// </summary>
    public string? Link(string name, IReadOnlyList<PsvElement> inputs, string dxilPath, bool dropUnread)
    {
        return Remake(name, hlsl => StageLinker.Link(hlsl, inputs, dropUnread), dxilPath);
    }

    /// <summary>Compiles a pixel shader without the inputs it never reads (<see cref="StageLinker.TrimInputs"/>), as <see cref="Link"/>.</summary>
    public string? Trim(string name, string dxilPath)
    {
        return Remake(name, StageLinker.TrimInputs, dxilPath);
    }

    private string? Remake(string name, Func<string, string> rewrite, string dxilPath)
    {
        var failurePath = Path.ChangeExtension(dxilPath, FailExtension);

        if (File.Exists(failurePath))
        {
            return File.ReadAllText(failurePath);
        }

        var path = Path.Combine(_programs, DebugLayer.MeshProbes.Source(name) + ProgramExtension);

        if (!File.Exists(path))
        {
            return NoProgram;
        }

        var form = Prepare(name);
        string? failure;

        if (form.Request is null)
        {
            failure = form.Failure;
        }
        else
        {
            failure = CompileRewritten(form.Request, rewrite, dxilPath);
        }

        if (failure is not null)
        {
            File.WriteAllText(failurePath, failure, Encoding.UTF8);
        }

        return failure;
    }

    private string? CompileRewritten(CompileRequest request, Func<string, string> rewrite, string dxilPath)
    {
        string linked;

        try
        {
            linked = rewrite(request.Hlsl);
        }
        catch (UnsupportedProgramException exception)
        {
            return $"link: {exception.Message}";
        }

        var linkedRequest = request with { HlslPath = Path.ChangeExtension(dxilPath, HlslExtension), Hlsl = linked };

        if (ContentStore is not null && ContentStore.TryRestore(linkedRequest))
        {
            return null;
        }

        var outcome = _compiler.Compile(linkedRequest, _compiler.Options.CompileTimeout);

        if (outcome.Succeeded)
        {
            ContentStore?.Keep(linkedRequest);

            return null;
        }

        // The source and the compiler's whole report stay beside the failure, for a look at why.
        File.WriteAllText(linkedRequest.HlslPath, linked, Encoding.UTF8);
        File.WriteAllText(Path.ChangeExtension(dxilPath, ".log"), outcome.Error, Encoding.UTF8);

        return $"link {ProgramStatus.CompileFailure}: {DxcCompiler.FirstError(outcome.Error)}";
    }

    /// <summary>
    /// A superstep program's cost, once: its wave-operation stops and its round bound (SuperstepWriter's comment). A round
    /// is a group barrier and a run of the dispatch switch, so stops x rounds tells a program that may outlast the
    /// driver's timeout (w33: MSNC_VS_LinearDepthSkin_f1f2249c, 1,218 stops, DEVICE_HUNG on its first dispatch). A program
    /// --fast-waves runs approximately is noted too, with why the exact analysis refused it.
    /// </summary>
    private static void NoteCost(string name, string hlsl)
    {
        int fast = hlsl.IndexOf(FiveStar.Shaders.Translation.TranslationOptions.FastWavesComment, StringComparison.Ordinal);

        if (fast >= 0)
        {
            // --fast-waves: the program runs natively on narrower waves, approximately.
            Note($"fast-waves-{name}", $"[d3d12_xs] fast waves {name}: {hlsl[fast..hlsl.IndexOf('\n', fast)]}");
        }

        int at = hlsl.IndexOf(FiveStar.Shaders.Translation.TranslationOptions.SuperstepCostComment, StringComparison.Ordinal);

        if (at < 0)
        {
            return;
        }

        int start = at + FiveStar.Shaders.Translation.TranslationOptions.SuperstepCostComment.Length;
        int end = hlsl.IndexOf('\n', start);
        Note($"superstep-{name}", $"[d3d12_xs] superstep program {name}: {hlsl[start..end]}");
    }

    /// <summary>
    /// A note once per key through the runtime's warnings, or the log in the warm-up, which has no runtime: asking for
    /// one threw, and every superstep program a cold warm-up made failed with that.
    /// </summary>
    private static void Note(string key, string message)
    {
        var runtime = ShimContext.ActiveRuntime;

        if (runtime is null)
        {
            ShimContext.Log?.Info($"  {message}");

            return;
        }

        runtime.D3D12X.Warnings.Warn(key, message);
    }

    /// <summary>A --probe-pipeline program's source with the probe code (empty when it cannot take it); any other as it is.</summary>
    private string Probed(string name, string hlsl)
    {
        if (!DebugLayer.MeshProbes.IsProbed(name))
        {
            return hlsl;
        }

        try
        {
            return DebugLayer.MeshProbes.Instrument(name, hlsl, _folder);
        }
        catch (UnsupportedProgramException)
        {
            return string.Empty;
        }
    }

    /// <summary>The program's DXIL: a DXIL replacement's own file, else <c>&lt;folder&gt;/&lt;stem&gt;.dxil</c>.</summary>
    public string DxilPath(string name)
    {
        return Overrides?.DxilFile(name) ?? FiveStar.Shaders.Cache.PlainFileName.Inside(_folder, Stem(name) + DxilExtension);
    }

    private ProgramForm Refused(string name, string failure)
    {
        Keep(name, failure);

        return new ProgramForm(name, null, failure);
    }

    private void Keep(string name, string failure)
    {
        var path = FiveStar.Shaders.Cache.PlainFileName.Inside(_folder, Stem(name) + FailExtension);
        File.WriteAllText(path + ".tmp", failure, Encoding.UTF8);
        File.Move(path + ".tmp", path, overwrite: true);
    }
}
