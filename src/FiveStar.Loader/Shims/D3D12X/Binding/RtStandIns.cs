// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using FiveStar.Loader.Native;
using FiveStar.Loader.Shims.D3D12X.Present;
using FiveStar.Loader.Shims.D3D12X.Resources;
using FiveStar.Loader.Shims.D3D12X.States;

namespace FiveStar.Loader.Shims.D3D12X.Binding;

/// <summary>
/// Stand-in outputs for the ray-tracing passes held without <c>--raytracing</c> (default on; <c>--no-rt-stand-ins</c>
/// turns them off): when such a pass's dispatch is skipped, the texture UAVs its programs store through
/// (<see cref="TableReaches.StoredOfNames"/>; the root signature's UAV ranges without --shader-dir) get a constant
/// instead of what a frame ago or nothing left there (<see cref="UavFill"/>). Ray-traced shadow masks read fully lit,
/// planar reflections black; the screen-probe light, whose rays are held, is overwritten after
/// CS_ScreenProbes_TemporalResolve runs (or at its skip) with a modest grey, as VIctory's ambient stand-in does
/// (ZJOU-VICTORY 1fc58f3 after Belobug 4f45ab8; vitutu1 c3faf2a adds shadow-mask and exposure stand-ins; facts only).
/// Raw-buffer writers (the screen-probe rays, the deferred gather) are left alone. A rule's first fill that writes
/// nothing is logged with what the tables held.
/// </summary>
internal static unsafe class RtStandIns
{
    /// <summary>ID3D12Device::GetDescriptorHandleIncrementSize and the CBV_SRV_UAV heap type (d3d12.h).</summary>
    private const int IncrementSlot = 15;
    private const int ViewHeapType = 0;

    /// <summary>DXGI_FORMAT_R32_UINT.</summary>
    private const int R32Uint = 42;

    /// <summary>
    /// DXGI R32G32B32A32_FLOAT, R16G16B16A16_FLOAT and R11G11B10_FLOAT: the formats a colour-only rule fills. The
    /// screen-probe resolve also writes probe metadata through its tables (octahedral normals as x * 0.5 + 0.5, packed
    /// bit fields), which a grey would corrupt.
    /// </summary>
    private static readonly int[] ColourFormats = [2, 10, 26];

    private static readonly (string Name, FillValue Value, bool AfterRun, bool ColourOnly, string Stands)[] Rules =
    [
        ("shadowmask~CS_TraceShadowRays", new FillValue(1, 1, 1, uint.MaxValue), false, false, "ray-traced shadow mask written fully lit"),
        ("rtdirectionalshadows~", new FillValue(1, 1, 1, uint.MaxValue), false, false, "ray-traced directional shadows written fully lit"),
        ("CS_PPR_Raytrace", new FillValue(0, 0, 0, 0), false, false, "planar reflections written black"),
        ("CS_ScreenProbes_TemporalResolve", FillValue.Shared(AmbientLevel), true, true, "screen-probe light overwritten after it runs (a share of the scene's own light, or a grey: AmbientLight)"),
    ];

    /// <summary>
    /// Without the programs' reach, the records of a UAV range a shadow or reflection rule fills, the first 8 (those
    /// fill every texture view); a colour-only rule takes the whole range, its formats choosing what it writes.
    /// </summary>
    private const uint MaxFilledRecords = 8;

    /// <summary>The desktop pipelines whose outputs are overwritten after they run, with their rule (<see cref="Named"/>).</summary>
    private static readonly ConcurrentDictionary<nint, int> AfterRunPipelines = new();
    private static readonly long[] FilledByRule = new long[Rules.Length];

    /// <summary>Whether a rule's fill that wrote nothing was logged (once each).</summary>
    private static readonly int[] EmptyLogged = new int[Rules.Length];

    /// <summary>The left-alone UAVs last logged for each held pass, and how many such lines the log may hold (<see cref="NoteLeft"/>).</summary>
    private static readonly ConcurrentDictionary<string, string> LeftNoted = new(StringComparer.Ordinal);
    private const int MaxLeftLogs = 48;
    private static int _leftLogs;
    private static long _filled;

    /// <summary>Filled sources copied into their R9G9B9E5 shadows so far.</summary>
    private static long _shadowCopies;

    /// <summary>The grey of the screen-probe light stand-in (VIctory's 0.6).</summary>
    public const float AmbientLevel = 0.6f;

    /// <summary>Whether held passes get stand-in outputs (default on; off with <c>--no-rt-stand-ins</c>).</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>
    /// Whether the screen-probe light is overwritten with grey after its resolve runs (default on; off with
    /// <c>--rt-traversal</c>, whose probes trace their own light). The rules applied at a held pass's skip stay.
    /// </summary>
    public static bool ScreenProbeLight { get; set; } = true;

    /// <summary>
    /// <c>--rt-stand-ins-reach</c>: a held pass's fill writes every texture UAV in its programs' reach, as it did before
    /// it took only the records they store through. That reach holds other passes' textures: the tiled sun-shadow
    /// traces read the global table past the cascade shadow textures, which the fill then set fully lit after the
    /// cascade passes had written them (bars of sunlight on shadowed walls, w357).
    /// </summary>
    public static bool WholeReach { get; set; }

    /// <summary>UAVs filled so far.</summary>
    public static long Filled => Interlocked.Read(ref _filled);

    /// <summary>The sample line: UAVs each rule filled so far, or that none was.</summary>
    public static string Summary
    {
        get
        {
            var used = Enumerable.Range(0, Rules.Length).Where(rule => Interlocked.Read(ref FilledByRule[rule]) != 0).ToList();

            if (used.Count == 0)
            {
                return "ray-tracing stand-ins: none filled";
            }

            return "ray-tracing stand-ins (approximate): " + string.Join(", ", used.Select(rule => $"{Rules[rule].Name} {Interlocked.Read(ref FilledByRule[rule])} UAVs")) + $", {Interlocked.Read(ref _shadowCopies)} of them copied into their R9G9B9E5 shadows, {AmbientLight.Summary}, {ShadowMaskTiles.Summary};{SharedExponentAliases.Summary}";
        }
    }

    /// <summary>The rule for a pipeline name, or -1.</summary>
    public static int RuleOf(string name)
    {
        for (int rule = 0; rule < Rules.Length; rule++)
        {
            if (name.Contains(Rules[rule].Name, StringComparison.Ordinal))
            {
                return rule;
            }
        }

        return -1;
    }

    /// <summary>A desktop pipeline was built from <paramref name="names"/>: an after-run rule is kept for it (a reused address loses an old one).</summary>
    public static void Named(nint pipeline, string names)
    {
        AmbientLight.Named(pipeline, names);
        ShadowMaskTiles.Named(pipeline, names);
        int rule = RuleOf(names);

        if (Enabled && ScreenProbeLight && rule >= 0 && Rules[rule].AfterRun)
        {
            AfterRunPipelines[pipeline] = rule;

            return;
        }

        AfterRunPipelines.TryRemove(pipeline, out _);
    }

    /// <summary>A dispatch of a held pipeline named <paramref name="name"/> was skipped: its outputs get the rule's value.</summary>
    public static void AtSkip(XboxObject* list, string name)
    {
        ShadowMaskTiles.Skipped(name);
        int rule = RuleOf(name);

        if (rule >= 0)
        {
            Fill(list, rule, name);
        }
    }

    /// <summary>After a dispatch that ran: a pass whose rule writes after it (the screen-probe light) gets its outputs overwritten.</summary>
    [HotPath]
    public static void AfterDispatch(XboxObject* list, nint pipeline)
    {
        if (!AfterRunPipelines.IsEmpty && AfterRunPipelines.TryGetValue(pipeline, out int rule))
        {
            Fill(list, rule, QueueProgress.PipelineName(pipeline));
        }

        AmbientLight.AfterDispatch(list, pipeline);
        ShadowMaskTiles.AfterDispatch(list, pipeline);
    }

    /// <summary>
    /// After an indirect dispatch that ran: the shadow-mask denoisers are ExecuteIndirects (w119's watch), so their
    /// tables of tiles are gone over here (<see cref="ShadowMaskTiles"/>); the other rules act at plain dispatches.
    /// </summary>
    [HotPath]
    public static void AfterIndirectDispatch(XboxObject* list, nint pipeline)
    {
        ShadowMaskTiles.AfterDispatch(list, pipeline);
    }

    /// <summary>
    /// Writes rule <paramref name="rule"/>'s value over the texture UAVs of the list's compute tables, then sets the
    /// list's compute state again. The first fill of each rule is logged with the pipeline <paramref name="name"/>.
    /// </summary>
    private static void Fill(XboxObject* list, int rule, string name)
    {
        var runtime = ShimContext.Runtime.D3D12X;
        var bindings = CommandListBindings.Of(list);
        var layout = bindings->ComputeLayout;
        var window = runtime.Bindings.Window;

        var misses = default(FillMisses);

        // Without the game's compute root signature set there is nothing to restore after the fill's own.
        if (bindings->ComputeRoot == 0 || layout == null || window.Data == null || window.ViewHeap == 0)
        {
            NoteEmpty(rule, name, misses);

            return;
        }

        nint inner = list->Inner;
        int filled = FillTables(inner, bindings, window, rule, name, ref misses);

        if (filled == 0)
        {
            NoteEmpty(rule, name, misses);

            return;
        }

        Barrier(inner);
        CopyToShadows(list, ref misses);
        Interlocked.Add(ref _filled, filled);

        if (Interlocked.Add(ref FilledByRule[rule], filled) == filled)
        {
            ShimContext.Log?.Warn($"[d3d12_xs] ray-tracing stand-in (approximate, not the pass's result; --no-rt-stand-ins turns it off): {name}: {Rules[rule].Stands}");
        }

        CommandListBindingMethods.RestoreCompute(list);
    }

    /// <summary>
    /// A held pass's fill takes the records its programs store textures through (<see cref="TableReaches.StoredOfNames"/>):
    /// its own outputs. With <see cref="WholeReach"/>, and for the rule that writes after a pass that ran, every record
    /// the programs read (<see cref="FillReach"/>); without the programs, the root signature's UAV ranges.
    /// </summary>
    private static int FillTables(nint inner, CommandListBindings* bindings, DescriptorWindow window, int rule, string name, ref FillMisses misses)
    {
        var reach = TableReaches.OfNames(name);

        if (reach.Length == 0)
        {
            return FillRanges(inner, bindings, window, rule, ref misses);
        }

        if (WholeReach || Rules[rule].AfterRun)
        {
            return FillReach(inner, bindings, window, reach, rule, ref misses);
        }

        var stored = TableReaches.StoredOfNames(name);
        NoteLeft(bindings, window, reach, stored, name);

        return FillStored(inner, bindings, window, stored, rule, ref misses);
    }

    /// <summary>The records the pass's programs store through, each alone.</summary>
    private static int FillStored(nint inner, CommandListBindings* bindings, DescriptorWindow window, (int Sgpr, uint Record)[] stored, int rule, ref FillMisses misses)
    {
        int filled = 0;

        foreach (var (sgpr, record) in stored)
        {
            if (sgpr >= CommandListBindings.UserDataDwords || record >= DescriptorWindow.MaxTableRecords)
            {
                continue;
            }

            uint pointer = bindings->Compute[sgpr];
            ShimContext.Runtime.D3D12X.DescriptorHeaps.MirrorTable(pointer, record + 1);
            filled += FillRecords(inner, window, pointer, record, record + 1, rule, filled == 0, ref misses);
        }

        return filled;
    }

    /// <summary>
    /// Logs the texture UAVs in a pass's reach that its fill leaves alone, which a whole-reach fill wrote: other passes'
    /// textures (the cascade shadows, records 62 and 63 of the table in s4). Logged when the set changes (the tables
    /// fill as the world loads), <see cref="MaxLeftLogs"/> times in all.
    /// </summary>
    private static void NoteLeft(CommandListBindings* bindings, DescriptorWindow window, TableReachEntry[] reach, (int Sgpr, uint Record)[] stored, string name)
    {
        if (Volatile.Read(ref _leftLogs) >= MaxLeftLogs)
        {
            return;
        }

        var left = new List<string>();

        foreach (var entry in reach)
        {
            if (entry.Sgpr >= CommandListBindings.UserDataDwords || entry.Records >= DescriptorWindow.MaxTableRecords)
            {
                continue;
            }

            uint pointer = bindings->Compute[entry.Sgpr];
            ShimContext.Runtime.D3D12X.DescriptorHeaps.MirrorTable(pointer, entry.Records);

            for (uint record = 0; record < entry.Records; record++)
            {
                if (!stored.Contains((entry.Sgpr, record)) && TryView(window, pointer, record, out var view) && Takes(view, colourOnly: false))
                {
                    left.Add($"s{entry.Sgpr}:{record} {view.Width}x{view.Height} format {view.Format}");
                }
            }
        }

        string text = string.Join(", ", left);

        if (LeftNoted.TryGetValue(name, out var last) && last == text)
        {
            return;
        }

        LeftNoted[name] = text;
        Interlocked.Increment(ref _leftLogs);
        string outputs = string.Join(", ", stored.Select(record => $"s{record.Sgpr}:{record.Record}"));
        ShimContext.Log?.Info($"[d3d12_xs] ray-tracing stand-in of {name} fills the records it stores through ({outputs}) and leaves {left.Count} texture UAVs in its reach alone (--rt-stand-ins-reach fills them too): {text}");
    }

    /// <summary>The texture UAV record <paramref name="record"/> of the table at window offset <paramref name="pointer"/> names; false for none.</summary>
    private static bool TryView(DescriptorWindow window, uint pointer, uint record, out UavView view)
    {
        ulong offset = pointer + ((ulong)record * DescriptorWindow.ViewRecordBytes);
        view = default;

        if (offset + DescriptorWindow.ViewRecordBytes > DescriptorWindow.NullTableBase)
        {
            return false;
        }

        nint device = ShimContext.Runtime.D3D12X.Objects.DesktopDevice;
        uint increment = ((delegate* unmanaged<nint, int, uint>)ComObject.Slot(device, IncrementSlot))(device, ViewHeapType);
        uint index = ((uint*)(window.Data + offset))[DescriptorWindow.MirroredDesktopDword];

        return UavViews.TryFind(DesktopCalls.HeapCpuStart(window.ViewHeap) + (nint)((ulong)index * increment), out view);
    }

    /// <summary>
    /// The records the pass's programs read from each table (<see cref="TableReaches.OfNames"/>): the game copies its
    /// descriptors to its binding entries' offsets, past the root signature's ranges, whose records name nothing.
    /// </summary>
    private static int FillReach(nint inner, CommandListBindings* bindings, DescriptorWindow window, TableReachEntry[] reach, int rule, ref FillMisses misses)
    {
        int filled = 0;

        foreach (var entry in reach)
        {
            // A reach of the whole table is an index the decoder could not bound (RTDistantShadowTrace32's s6): not the pass's own outputs.
            if (entry.Sgpr >= CommandListBindings.UserDataDwords || entry.Records >= DescriptorWindow.MaxTableRecords)
            {
                continue;
            }

            uint pointer = bindings->Compute[entry.Sgpr];
            ShimContext.Runtime.D3D12X.DescriptorHeaps.MirrorTable(pointer, entry.Records);
            filled += FillRecords(inner, window, pointer, 0, entry.Records, rule, filled == 0, ref misses);
        }

        return filled;
    }

    /// <summary>Without the programs' reach: the root signature's UAV ranges.</summary>
    private static int FillRanges(nint inner, CommandListBindings* bindings, DescriptorWindow window, int rule, ref FillMisses misses)
    {
        var layout = bindings->ComputeLayout;
        int filled = 0;

        for (int range = 0; range < layout->UavRanges; range++)
        {
            uint pointer = bindings->Compute[UserData.ComputeBase + layout->Offsets[layout->UavParameters[range]]];
            uint count = Rules[rule].ColourOnly ? layout->UavCounts[range] : Math.Min(layout->UavCounts[range], MaxFilledRecords);
            uint end = layout->UavStarts[range] + count;
            ShimContext.Runtime.D3D12X.DescriptorHeaps.MirrorTable(pointer, end);
            filled += FillRecords(inner, window, pointer, layout->UavStarts[range], end, rule, filled == 0, ref misses);
        }

        return filled;
    }

    /// <summary>Fills the texture UAVs of records <paramref name="first"/> to <paramref name="end"/> of the table at window offset <paramref name="pointer"/>; how many.</summary>
    private static int FillRecords(nint list, DescriptorWindow window, uint pointer, uint first, uint end, int rule, bool barrierFirst, ref FillMisses misses)
    {
        var value = Rules[rule].Value;

        // The screen-probe light's rule writes what the scene measured, or the grey.
        if (Rules[rule].AfterRun)
        {
            value = AmbientLight.Value;
        }

        nint device = ShimContext.Runtime.D3D12X.Objects.DesktopDevice;
        nint cpuStart = DesktopCalls.HeapCpuStart(window.ViewHeap);
        ulong gpuStart = DesktopCalls.HeapGpuStart(window.ViewHeap);
        uint increment = ((delegate* unmanaged<nint, int, uint>)ComObject.Slot(device, IncrementSlot))(device, ViewHeapType);
        int filled = 0;

        for (uint record = first; record < end; record++)
        {
            ulong offset = pointer + ((ulong)record * DescriptorWindow.ViewRecordBytes);

            if (offset + DescriptorWindow.ViewRecordBytes > DescriptorWindow.NullTableBase)
            {
                break;
            }

            uint index = ((uint*)(window.Data + offset))[DescriptorWindow.MirroredDesktopDword];
            misses.Records++;

            if (!UavViews.TryFind(cpuStart + (nint)((ulong)index * increment), out var view))
            {
                misses.Unknown++;

                continue;
            }

            if (!Takes(view, Rules[rule].ColourOnly))
            {
                misses.Refused++;
                misses.Format = view.Format;

                continue;
            }

            if (barrierFirst && filled == 0)
            {
                Barrier(list);
            }

            if (UavFill.Record(device, list, gpuStart, index, view, value))
            {
                filled++;
                misses.Note(view.Resource);
            }
        }

        return filled;
    }

    /// <summary>
    /// The filled textures that are R32_UINT sources of R9G9B9E5 shadows get their texels copied into the shadow on the
    /// same list (<see cref="SharedExponentAliases.Written"/>). The lighting reads the screen-probe light through
    /// R9G9B9E5 views, so through the shadows; a shadow is otherwise copied at a list's first read and after a barrier
    /// on its source, and the fill is a write no barrier announces: in w352 and w353 the sources held the grey and
    /// nothing of it was in the lit image.
    /// </summary>
    private static void CopyToShadows(XboxObject* list, ref FillMisses misses)
    {
        if (!SharedExponentAliases.Any)
        {
            return;
        }

        var states = ShimContext.Runtime.D3D12X.ListStates.For(list);

        for (int source = 0; source < misses.Sources; source++)
        {
            if (SharedExponentAliases.Written(list->Inner, states, (nint)misses.Filled[source], "a stand-in's fill"))
            {
                Interlocked.Increment(ref _shadowCopies);
            }
        }
    }

    /// <summary>The first fill of a rule that wrote nothing is logged with what its tables held, so a stand-in that never reaches its pass shows why.</summary>
    private static void NoteEmpty(int rule, string name, FillMisses misses)
    {
        if (Interlocked.Read(ref FilledByRule[rule]) != 0 || Interlocked.Exchange(ref EmptyLogged[rule], 1) != 0)
        {
            return;
        }

        ShimContext.Log?.Warn($"[d3d12_xs] ray-tracing stand-in for {name} wrote nothing: {misses.Records} UAV records, {misses.Unknown} without a noted texture view, {misses.Refused} refused by format (last DXGI format {misses.Format}); no compute root or UAV range when all are 0");
    }

    /// <summary>What a fill's UAV records held when it did not write them.</summary>
    private struct FillMisses
    {
        /// <summary>The most filled textures remembered for <see cref="CopyToShadows"/> (the resolve fills 7).</summary>
        private const int MaxSources = 16;

        public int Records;
        public int Unknown;
        public int Refused;
        public int Format;

        /// <summary>The desktop textures filled, the first <see cref="MaxSources"/>.</summary>
        public fixed long Filled[MaxSources];
        public int Sources;

        public void Note(nint resource)
        {
            if (Sources < MaxSources)
            {
                Filled[Sources++] = resource;
            }
        }
    }

    /// <summary>
    /// Whether a rule writes <paramref name="view"/>: never an integer view (tile lists, counters, packed fields) but,
    /// for a colour-only rule, an R32_UINT view of a texture the game also reads as R9G9B9E5 (its shared-exponent
    /// light, packed by <see cref="UavFill"/>); for a colour-only rule otherwise a float colour format alone.
    /// </summary>
    public static bool Takes(UavView view, bool colourOnly)
    {
        if (UavFill.Integer(view.Format))
        {
            return colourOnly && view.Format == R32Uint && UavViews.SharedExponent(view.Resource);
        }

        return !colourOnly || Array.IndexOf(ColourFormats, view.Format) >= 0;
    }

    /// <summary>A UAV barrier on every resource: the fill after the pass's writes, the readers after the fill.</summary>
    internal static void Barrier(nint list)
    {
        var barrier = new TransitionBarrier { Type = TransitionBarrier.UavType, Resource = 0 };
        BarrierWriter.Write(list, &barrier);
    }
}
