// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Text;
using FiveStar.Loader.Diagnostics;
using FiveStar.Loader.Shims.D3D12X.Binding;
using FiveStar.Shaders.Cache;
using FiveStar.Shaders.Translation;

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>
/// <c>--probe-pipeline &lt;text,...&gt;</c>: the mesh, pixel and compute programs whose name holds one of the texts are
/// made on demand with probe code
/// (<see cref="MeshProbe"/>) under their name plus <see cref="Suffix"/> (the program index renames them), so their DXIL,
/// links and pipelines are cache entries of their own and every other program stays warm. Each group of such a program
/// appends a record to a ring in the descriptor window past the window flags; at every captured present the records
/// written since the last are summed up per program: groups, the counts they gave SetMeshOutputCounts, culled
/// primitives, vertex 0's positions, and per load site how many groups saw its range check fail, an unmapped page or a
/// record naming no descriptor, with the site's text (kept beside the DXIL as <c>&lt;name&gt;.sites.txt</c>).
/// </summary>
internal static unsafe class MeshProbes
{
    /// <summary>The ring's window offset, past the window flags of <see cref="WindowFeedback"/>.</summary>
    public const uint Base = WindowFeedback.Bytes;

    /// <summary>Window bytes the ring needs.</summary>
    public const uint Bytes = Base + MeshProbe.RingBytes;

    private const string SitesExtension = ".sites.txt";
    private const string Prefix = "  [mesh probe] ";
    private const int ValuesShown = 4;

    /// <summary>A non-finite lane's pixel: x in bits 0-15, y from 16.</summary>
    private const uint PixelMask = 0xFFFF;
    private const int PixelYShift = 16;
    private const int PositionsShown = 4;
    private const int MaskBits = 32;
    private const int SiteFields = 2;
    private const uint PastViews = 0x80000000;

    private static readonly ConcurrentDictionary<uint, string> Names = new();
    private static uint _reported;

    /// <summary>The name a probed program is made under: its own plus this, which names the ring offset its code writes to.</summary>
    public static string Suffix { get; } = $".probe-{Base:x}";

    /// <summary>The text probed program names hold; null without <c>--probe-pipeline</c>.</summary>
    public static string? Filter { get; set; }

    public static bool Enabled => Filter is not null;

    /// <summary>The program cache folder of this boot's programs, where the site lists are.</summary>
    public static string? Folder { get; set; }

    /// <summary>The index with the probed programs renamed, each registered by its record id.</summary>
    public static ProgramIndex Rename(ProgramIndex index)
    {
        if (Filter is null)
        {
            return index;
        }

        return index.Renamed(Matches, Suffix);
    }

    /// <summary>Whether <paramref name="name"/> is a probed program's name (<see cref="Suffix"/>).</summary>
    public static bool IsProbed(string name)
    {
        return Enabled && name.EndsWith(Suffix, StringComparison.Ordinal);
    }

    /// <summary>The extracted program a name stands for: a probed program's without <see cref="Suffix"/>.</summary>
    public static string Source(string name)
    {
        if (IsProbed(name))
        {
            return name[..^Suffix.Length];
        }

        return name;
    }

    /// <summary>A probed program's source with the probe code; its site list goes beside its DXIL in <paramref name="folder"/>.</summary>
    public static string Instrument(string name, string hlsl, string folder)
    {
        var (probed, sites) = MeshProbe.Instrument(hlsl, MeshProbe.Id(Source(name)), Base);
        var lines = sites.Select((text, site) => $"{site}\t{text}");
        File.WriteAllLines(Path.Combine(folder, name + SitesExtension), lines, Encoding.UTF8);

        return probed;
    }

    /// <summary>Logs the records written since the last report; the GPU finished everything submitted.</summary>
    public static void Report(long present, LoaderLog log)
    {
        byte* window = ShimContext.Runtime.D3D12X.Bindings.Window.Data;

        if (!Enabled || window == null)
        {
            return;
        }

        uint* ring = (uint*)(window + Base);
        uint written = Volatile.Read(ref *ring);
        uint fresh = Math.Min(written - _reported, MeshProbe.Records);
        _reported = written;
        var records = new List<MeshProbeRecord>((int)fresh);

        for (uint i = written - fresh; i != written; i++)
        {
            var at = (uint*)((byte*)ring + MeshProbe.RecordsOffset + ((i % MeshProbe.Records) * MeshProbe.RecordBytes));
            records.Add(MeshProbeRecord.Read(new ReadOnlySpan<uint>(at, MeshProbeRecord.Dwords)));
        }

        int broken = records.Count(record => !record.Whole);
        log.Info($"{Prefix}present {present}: {written} groups probed so far, the last {records.Count}, {broken} not whole (stray writes into the ring; left out):");

        foreach (var program in records.Where(record => record.Whole).GroupBy(record => record.Id))
        {
            Summarize(program.Key, [.. program], log);
        }
    }

    private static bool Matches(string name)
    {
        if (!Filter!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(text => name.Contains(text, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        Names[MeshProbe.Id(name)] = name;

        return true;
    }

    private static void Summarize(uint id, List<MeshProbeRecord> groups, LoaderLog log)
    {
        string name = Names.GetValueOrDefault(id, $"program 0x{id:x8}");

        if (groups.All(group => group.Stage))
        {
            SummarizeStage(name, groups, log);

            return;
        }

        int empty = groups.Count(group => group.Vertices == 0 || group.Primitives == 0);
        long primitives = groups.Sum(group => (long)group.Primitives);
        long culled = groups.Sum(group => (long)group.Culled);

        var positions = groups.Where(group => group.Position is not null).Select(group => group.Position!.Value).ToList();
        int degenerate = positions.Count(position => position.W == 0 || float.IsNaN(position.X) || (position.X == 0 && position.Y == 0 && position.Z == 0));
        string shown = string.Join(" ", positions.Take(PositionsShown).Select(p => $"({p.X:G4}, {p.Y:G4}, {p.Z:G4}, {p.W:G4})"));

        log.Info($"{Prefix}{name}: {groups.Count} groups, {empty} with no vertices or primitives; vertices {groups.Min(g => g.Vertices)} to {groups.Max(g => g.Vertices)}, primitives {groups.Min(g => g.Primitives)} to {groups.Max(g => g.Primitives)}, {culled} of {primitives} primitives culled");
        log.Info($"{Prefix}{name}: vertex 0 written by {positions.Count} groups, {degenerate} at w 0, NaN or the origin; first {shown}");
        log.Info($"{Prefix}{name}: m0 at GS_ALLOC_REQ (primitives << 12 | vertices): {Values(groups.Select(group => group.Allocation))}");
        uint window = groups.Aggregate(0u, (mask, group) => mask | group.WindowLoads);
        log.Info($"{Prefix}{name}: sites loading through a 32-bit pointer (the descriptor window) 0x{window:x8}; first window address thread 0 read: {Values(groups.Select(group => group.WindowAddress))} (view records from 0x{DescriptorWindow.ViewBase:x}, 32 bytes each)");
        LogSites(name, groups, log);
    }

    /// <summary>
    /// A pixel or compute program's waves: where they ran (pixels or groups), the colours a pixel program returned (how
    /// many black, NaN or infinite), then its sites as a mesh program's.
    /// </summary>
    private static void SummarizeStage(string name, List<MeshProbeRecord> waves, LoaderLog log)
    {
        var colours = waves.Where(wave => wave.Position is not null).Select(wave => wave.Position!.Value).ToList();
        int black = colours.Count(c => c.X == 0 && c.Y == 0 && c.Z == 0);
        int invalid = colours.Count(c => !float.IsFinite(c.X) || !float.IsFinite(c.Y) || !float.IsFinite(c.Z) || !float.IsFinite(c.W));
        string shown = string.Join(" ", colours.Take(PositionsShown).Select(c => $"({c.X:G4}, {c.Y:G4}, {c.Z:G4}, {c.W:G4})"));
        string at = string.Join(" ", waves.Take(PositionsShown).Select(wave => $"({wave.Group.X}, {wave.Group.Y}, {wave.Group.Z})"));

        log.Info($"{Prefix}{name}: {waves.Count} waves (pixel or compute), first lanes at {at}");

        if (colours.Count > 0)
        {
            log.Info($"{Prefix}{name}: returned colours: {black} of {colours.Count} black, {invalid} NaN or infinite; first {shown}");
        }

        uint window = waves.Aggregate(0u, (mask, wave) => mask | wave.WindowLoads);
        log.Info($"{Prefix}{name}: sites loading through a 32-bit pointer (the descriptor window) 0x{window:x8}");
        LogNonFinite(name, waves, log);
        LogSites(name, waves, log);
    }

    /// <summary>
    /// The waves where a lane's value came out NaN or infinite (NanProbe): the earliest site with its value, the pixel of
    /// a lane that hit it and the site's text, then the first sites by how many waves stopped there first.
    /// </summary>
    private static void LogNonFinite(string name, List<MeshProbeRecord> waves, LoaderLog log)
    {
        var hit = waves.Where(wave => wave.NonFiniteSite is not null).ToList();

        if (hit.Count == 0)
        {
            log.Info($"{Prefix}{name}: no NaN or infinite value at any float site");

            return;
        }

        var (_, texts) = SiteTexts(name);
        var first = hit.MinBy(wave => wave.NonFiniteSite!.Value);
        int site = (int)first.NonFiniteSite!.Value;
        float value = BitConverter.UInt32BitsToSingle(first.NonFiniteValue);
        var counts = hit.GroupBy(wave => wave.NonFiniteSite!.Value).OrderByDescending(group => group.Count()).Take(PositionsShown);

        log.Info($"{Prefix}{name}: {hit.Count} of {waves.Count} waves had a NaN or infinite value ({hit.Sum(wave => (long)wave.NonFiniteLanes)} lanes); earliest at site {site}, 0x{first.NonFiniteValue:x8} ({value}), pixel ({first.NonFiniteAt & PixelMask}, {first.NonFiniteAt >> PixelYShift}): {texts.GetValueOrDefault(site, "?")}");
        log.Info($"{Prefix}{name}: first non-finite site by waves: {string.Join(", ", counts.Select(group => $"{group.Key} x{group.Count()}"))}");
    }

    /// <summary>
    /// The program's sites as its records count them, then per site of 0 to 31 with a failure or a noted value: the
    /// groups whose range check failed, that read an unmapped page or a null record, and the values thread 0 loaded.
    /// </summary>
    private static void LogSites(string name, List<MeshProbeRecord> groups, LoaderLog log)
    {
        var (path, texts) = SiteTexts(name);
        string listing = texts.Count > 0 ? $"texts in {path}" : $"no site texts at {path}";
        log.Info($"{Prefix}{name}: {groups.Max(group => group.Sites)} load sites ({listing}; past site 31 they count as 31 and keep no value)");

        for (int site = 0; site < MaskBits; site++)
        {
            uint bit = 1u << site;
            int range = groups.Count(group => (group.RangeFailed & bit) != 0);
            int unmapped = groups.Count(group => (group.Unmapped & bit) != 0);
            int empty = groups.Count(group => (group.NullRecord & bit) != 0);
            var values = groups.Select(group => group.Values.ElementAtOrDefault(site)).ToList();
            var windows = groups.Select(group => WindowIndex(group.WindowAddresses.ElementAtOrDefault(site))).ToList();

            if (range + unmapped + empty == 0 && values.All(value => value is null) && windows.All(index => index is null))
            {
                continue;
            }

            string window = string.Empty;

            if (windows.Any(index => index is not null))
            {
                window = $"; window record index {Values(windows)}";
            }

            log.Info($"{Prefix}{name}: site {site}: range {range}, unmapped {unmapped}, null record {empty} of {groups.Count} groups; loaded {Values(values)}{window}: {texts.GetValueOrDefault(site, "?")}");
        }
    }

    /// <summary>
    /// The view record a window address falls in (its game descriptor index, the index copies and writes name), or the
    /// address itself with bit 31 set when it lies past the view records (the null table, samplers).
    /// </summary>
    private static uint? WindowIndex(uint? address)
    {
        if (address is not uint at)
        {
            return null;
        }

        if (at >= DescriptorWindow.NullTableBase)
        {
            return at | PastViews;
        }

        return (at - DescriptorWindow.ViewBase) / DescriptorWindow.ViewRecordBytes;
    }

    /// <summary>The distinct values with how many groups had each, most first; "none" when no group noted one.</summary>
    private static string Values(IEnumerable<uint?> values)
    {
        var counted = values.Where(value => value is not null).GroupBy(value => value!.Value).OrderByDescending(group => group.Count()).ToList();

        if (counted.Count == 0)
        {
            return "none";
        }

        string shown = string.Join(", ", counted.Take(ValuesShown).Select(group => $"0x{group.Key:x} x{group.Count()}"));

        if (counted.Count > ValuesShown)
        {
            shown += $" (+{counted.Count - ValuesShown} more)";
        }

        return shown;
    }

    /// <summary>The site list's path and its texts by site; none when it is missing.</summary>
    private static (string Path, Dictionary<int, string> Texts) SiteTexts(string name)
    {
        var texts = new Dictionary<int, string>();
        var path = Path.Combine(Folder ?? string.Empty, name + Suffix + SitesExtension);

        if (!File.Exists(path))
        {
            return (path, texts);
        }

        foreach (var line in File.ReadLines(path))
        {
            var fields = line.Split('\t', SiteFields);

            if (fields.Length == SiteFields && int.TryParse(fields[0], out int site))
            {
                texts[site] = fields[1];
            }
        }

        return (path, texts);
    }
}
