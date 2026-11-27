// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using FiveStar.Shaders.Disassembly;
using FiveStar.Shaders.Hardware;
using FiveStar.Shaders.Translation;

namespace FiveStar.Loader.Shims.D3D12X.Binding;

/// <summary>
/// How many records each pipeline's programs read past the table pointers in their user SGPRs (<see cref="TableReach"/>),
/// so a draw or dispatch mirrors that much of each bound table before it runs, beyond what the table's bind mirrored
/// (<see cref="DescriptorWindow.MirroredRecords"/>). The game copies descriptors into its tables itself, past the
/// ranges its root signatures declare: TL_PS_PBR_MATERIAL_STANDARD reads record 180 of its table 0, which the 64
/// records a bind mirrors leave stale. Each program is decoded once, from its FSSM file.
/// </summary>
internal static class TableReaches
{
    private const string ProgramExtension = ".fssm";
    private const string WrappedSuffix = "_Wrapped";
    private const string Separator = " + ";
    private const int Wave64 = 64;

    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<int, uint>> Programs = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<nint, TableReachEntry[]> Pipelines = new();
    private static readonly ConcurrentDictionary<nint, TableReachEntry[]> Whole = new();
    private static readonly ConcurrentDictionary<string, TableReachEntry[]> ByNames = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, (int Sgpr, uint Record)[]> StoredByNames = new(StringComparer.Ordinal);

    /// <summary>The folder of the extracted FSSM programs (--shader-dir); null reads no reach.</summary>
    public static string? ProgramFolder { get; set; }

    /// <summary>
    /// <c>--mirror-table-records reach</c>: draws mirror their programs' reach. Off by default while mesh programs hang
    /// on the real data past record 64 (w44, w47).
    /// </summary>
    public static bool Enabled { get; set; }

    /// <summary>The entries of a pipeline; empty when its programs read no table past what binds mirror.</summary>
    public static TableReachEntry[] Of(nint pipeline)
    {
        return Pipelines.GetValueOrDefault(pipeline, []);
    }

    /// <summary>
    /// Every table a pipeline's programs read, with the records they reach, also those a bind mirrors whole (which
    /// <see cref="Of"/> leaves out): the depth twins' writers are found in them (<see cref="Resources.DepthTwins.MarkWriters"/>).
    /// </summary>
    public static TableReachEntry[] AllOf(nint pipeline)
    {
        return Whole.GetValueOrDefault(pipeline, []);
    }

    /// <summary>Records <paramref name="pipeline"/>'s reach from the programs <paramref name="names"/> (" + "-joined) it was built from.</summary>
    public static void Register(nint pipeline, string names)
    {
        if (pipeline == 0 || ProgramFolder is null || !Enabled)
        {
            return;
        }

        var reach = Reach(names);
        Pipelines[pipeline] = Entries(reach, DescriptorWindow.MinTableRecords);
        Whole[pipeline] = Entries(reach, 0);
    }

    /// <summary>
    /// Every table the programs <paramref name="names"/> read, with the records they reach, whether or not a pipeline
    /// was built from them (a held pass stays a stand-in; <see cref="RtStandIns"/>); empty without a program folder.
    /// </summary>
    public static TableReachEntry[] OfNames(string names)
    {
        if (ProgramFolder is null)
        {
            return [];
        }

        return ByNames.GetOrAdd(names, key => Entries(Reach(key), 0));
    }

    /// <summary>
    /// The records the programs <paramref name="names"/> store textures through (<see cref="StoredRecords"/>): a held
    /// pass's own outputs, which its stand-in fills; empty without a program folder.
    /// </summary>
    public static (int Sgpr, uint Record)[] StoredOfNames(string names)
    {
        if (ProgramFolder is null)
        {
            return [];
        }

        return StoredByNames.GetOrAdd(names, key => [.. key.Split(Separator, StringSplitOptions.RemoveEmptyEntries).SelectMany(Stored).Distinct()]);
    }

    /// <summary>A program's stored records, from its FSSM file (empty when the file is missing or does not decode).</summary>
    private static IReadOnlyList<(int Sgpr, uint Record)> Stored(string name)
    {
        var instructions = Instructions(Source(name));

        if (instructions is null)
        {
            return [];
        }

        return StoredRecords.Find(instructions);
    }

    /// <summary>The records each SGPR's table is read to, over the programs <paramref name="names"/>.</summary>
    private static Dictionary<int, uint> Reach(string names)
    {
        var reach = new Dictionary<int, uint>();

        foreach (var name in names.Split(Separator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var (sgpr, bytes) in Program(name))
            {
                reach[sgpr] = Math.Max(reach.GetValueOrDefault(sgpr), bytes);
            }
        }

        return reach;
    }

    /// <summary>The SGPRs whose reach passes <paramref name="mirrored"/> records, with the records they reach.</summary>
    public static TableReachEntry[] Entries(IReadOnlyDictionary<int, uint> reach, uint mirrored)
    {
        return [.. reach.Select(pair => new TableReachEntry(pair.Key, (pair.Value + DescriptorWindow.ViewRecordBytes - 1) / DescriptorWindow.ViewRecordBytes))
            .Where(entry => entry.Records > mirrored)];
    }

    /// <summary>A program's reach by SGPR, from its FSSM file (empty when the file is missing or does not decode).</summary>
    private static IReadOnlyDictionary<int, uint> Program(string name)
    {
        return Programs.GetOrAdd(Source(name), source =>
        {
            var instructions = Instructions(source);

            if (instructions is null)
            {
                return new Dictionary<int, uint>();
            }

            return TableReach.Find(instructions);
        });
    }

    /// <summary>The extracted program <paramref name="source"/> disassembled; null when its file is missing or does not decode.</summary>
    private static IReadOnlyList<Instruction>? Instructions(string source)
    {
        var path = Path.Combine(ProgramFolder!, source + ProgramExtension);

        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var bytes = File.ReadAllBytes(path);
            var config = FssmHeader.Decode(bytes);

            return new RdnaDecoder().Disassemble(FssmHeader.ProgramCode(bytes), config.WaveSize ?? Wave64);
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The extracted program a translated one comes from: its name up to <c>_Wrapped</c> (no link, trim or probe suffix).</summary>
    public static string Source(string name)
    {
        int at = name.IndexOf(WrappedSuffix, StringComparison.Ordinal);

        if (at < 0)
        {
            return name;
        }

        return name[..(at + WrappedSuffix.Length)];
    }
}
