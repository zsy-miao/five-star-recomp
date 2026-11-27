// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using FiveStar.Loader.Diagnostics;
using FiveStar.Loader.Shims.D3D12X.Binding;

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>
/// <c>--trace-pool-stores</c>: translated programs made on demand define FIVESTAR_STORE_TRACE as their id, and their
/// global stores and atomics into up to 8 address ranges, and buffer stores and atomics through up to 8 views, append
/// {program id, address or offset, address high dword or view index, value} to a ring at the end of the GPU-memory
/// window (rdna_prelude.hlsli). The ranges are the transient pool and the game heap region its block records live in;
/// the views are the buffer UAVs over the pool. A transient report lists the entries that hit the corrupted block,
/// and entries outside the pool are logged as they appear (the first <see cref="OutsideLogged"/>).
/// </summary>
internal static unsafe class PoolStoreTrace
{
    /// <summary>The ring's window offset: past the room for view records and the window guard (rdna_prelude TRACE_BASE).</summary>
    public const uint TraceBase = WindowGuard.Bytes;

    private const uint HeaderBytes = 256;
    private const uint Entries = 65536;
    private const uint EntryBytes = 16;
    private const uint RangesAt = 16;
    private const uint RangeBytes = 12;
    private const uint ViewsAt = 112;
    private const int MaxSlots = 8;
    private const uint ViewKind = 0x80000000;
    private const uint GuardKind = 0x40000000;
    private const uint ConstantKind = 0x20000000;
    private const uint KindMask = 0xE0000000;

    /// <summary>Loop numbers a program can have, and the sizes an s_buffer_load reads (one to sixteen dwords).</summary>
    private const uint MaxGuard = 4096;

    /// <summary>The largest loop cap a stage has (LoopGuard).</summary>
    private const uint MaxCap = 1u << 16;
    private const uint MinConstantBytes = 4;
    private const uint MaxConstantBytes = 64;
    private const int HighShift = 32;
    private const int OutsideLogged = 64;
    private const int PerProgram = 3;

    /// <summary>Where the descriptor size sits in the key of <see cref="Constants"/>, above the program id.</summary>
    private const int DescriptorSizeShift = 32;
    private const long GuardHitsLoggedAgain = 1000;

    /// <summary>Entry dwords: program id, address low or offset, address high or view | kind, value; a range's size.</summary>
    private const int LowWord = 1;
    private const int HighWord = 2;
    private const int ValueWord = 3;
    private const int SizeWord = 2;

    /// <summary>Window bytes the ring needs.</summary>
    public const uint Bytes = TraceBase + HeaderBytes + (Entries * EntryBytes);

    private static readonly ConcurrentDictionary<uint, string> Programs = new();
    private static readonly ConcurrentDictionary<uint, int> Outside = new();
    private static readonly ConcurrentDictionary<uint, long> Guards = new();
    private static readonly ConcurrentDictionary<ulong, int> Constants = new();
    private static readonly uint[] Views = new uint[MaxSlots];
    private static readonly (ulong Start, ulong Bytes)[] Ranges = new (ulong, ulong)[MaxSlots];
    private static int _nextId;
    private static long _outsideSeen;
    private static long _malformed;
    private static int _outsideLogged;
    private static long _storeEntries;
    private static long _otherKinds;

    /// <summary>The ring's entries <see cref="Collect"/> read so far: stores, and loop-guard and constant-load notes.</summary>
    public static string Kinds => $"{Interlocked.Read(ref _storeEntries)} stores and {Interlocked.Read(ref _otherKinds)} loop-guard and constant-load notes read from the ring";

    /// <summary>Whether programs made on demand trace their stores.</summary>
    public static bool Enabled { get; set; }

    /// <summary>
    /// <c>--trace-stores-of &lt;text,...&gt;</c>: only the programs whose name holds one of the texts trace their stores,
    /// made under their name plus <see cref="Suffix"/> so every other program stays warm; null traces them all.
    /// </summary>
    public static string? Filter { get; set; }

    /// <summary>The name a traced program is made under with <see cref="Filter"/>: its own plus this.</summary>
    public const string Suffix = ".stores2";

    /// <summary>Whether every program made on demand traces its stores (<c>--trace-pool-stores</c>): they have a cache identity of their own.</summary>
    public static bool AllPrograms => Enabled && Filter is null;

    /// <summary>Whether the program <paramref name="name"/> traces its stores.</summary>
    public static bool Traces(string name)
    {
        return Enabled && (Filter is null || name.Contains(Suffix, StringComparison.Ordinal));
    }

    /// <summary>
    /// A traced program's source with its formatted stores traced too: the prelude's FmtStore has no trace hook, so
    /// each call goes through a wrapper defined after the prelude that notes a store into the first 64 KiB of its view
    /// (a map view over a game-memory buffer, or a kept heap index) as a view entry, then stores.
    /// BatchCullMeshlets' visible-meshlet appends are such stores.
    /// </summary>
    public static string Instrument(string hlsl)
    {
        const string Include = "#include \"rdna_prelude.hlsli\"";
        const string Call = "FmtStore(";
        int at = hlsl.IndexOf(Include, StringComparison.Ordinal);

        if (at < 0 || !hlsl.Contains(Call, StringComparison.Ordinal))
        {
            return hlsl;
        }

        string body = hlsl[(at + Include.Length)..].Replace(Call, "FsTracedFmtStore(", StringComparison.Ordinal);
        const string Wrapper = "\nvoid FsTracedFmtStore(uint h, uint a, uint c, uint4 v, uint n) { if ((a & ~3u) < 0x10000u) trace_entry(a & ~3u, h | 0x80000000u, v.x); FmtStore(h, a, c, v, n); }\n";

        return hlsl[..(at + Include.Length)] + Wrapper + body;
    }

    /// <summary>The extracted program a name stands for: without <see cref="Suffix"/>.</summary>
    public static string Source(string name)
    {
        if (Filter is null)
        {
            return name;
        }

        return name.Replace(Suffix, string.Empty, StringComparison.Ordinal);
    }

    /// <summary>The index with the programs <see cref="Filter"/> names renamed (<see cref="Suffix"/>).</summary>
    public static FiveStar.Shaders.Cache.ProgramIndex Rename(FiveStar.Shaders.Cache.ProgramIndex index)
    {
        if (Filter is null)
        {
            return index;
        }

        string[] texts = Filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return index.Renamed(name => Renames(name, texts), Suffix);
    }

    private static bool Renames(string name, string[] texts)
    {
        if (!texts.Any(text => name.Contains(text, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        Programs[FiveStar.Shaders.Translation.MeshProbe.Id(name)] = name;

        return true;
    }

    /// <summary>The define a program made on demand starts with; registers its id.</summary>
    public static string Header(string program)
    {
        // With a filter the id is the name's hash: a traced program's DXIL is kept between boots under its name.
        uint id = Filter is null ? (uint)Interlocked.Increment(ref _nextId) : FiveStar.Shaders.Translation.MeshProbe.Id(Source(program));
        Programs[id] = program;

        return $"#define FIVESTAR_STORE_TRACE {id}u\n";
    }

    /// <summary>Sets address range <paramref name="slot"/> (clipped to the 4 GiB span of its start).</summary>
    public static void SetRange(int slot, ulong start, ulong bytes)
    {
        byte* header = Header();

        if (header == null || slot >= MaxSlots)
        {
            return;
        }

        ulong spanEnd = (start | uint.MaxValue) + 1;
        bytes = Math.Min(bytes, spanEnd - start);
        Ranges[slot] = (start, bytes);

        uint* range = (uint*)(header + RangesAt + ((uint)slot * RangeBytes));
        range[0] = (uint)start;
        range[1] = (uint)(start >> HighShift);
        range[SizeWord] = (uint)bytes;
    }

    /// <summary>Range <paramref name="slot"/> as every address whose high dword is in [<paramref name="first"/>, <paramref name="last"/>].</summary>
    public static void SetSpan(int slot, uint first, uint last)
    {
        byte* header = Header();

        if (header == null || slot >= MaxSlots)
        {
            return;
        }

        uint* range = (uint*)(header + RangesAt + ((uint)slot * RangeBytes));
        range[0] = first;
        range[1] = last;
        range[SizeWord] = uint.MaxValue;
    }

    /// <summary>A buffer UAV over the pool: its stores are traced (up to 8 views).</summary>
    public static void AddView(uint desktop)
    {
        byte* header = Header();

        if (header == null || desktop == 0)
        {
            return;
        }

        lock (Views)
        {
            for (int i = 0; i < MaxSlots; i++)
            {
                if (Views[i] == desktop)
                {
                    return;
                }

                if (Views[i] == 0)
                {
                    Views[i] = desktop;
                    ((uint*)(header + ViewsAt))[i] = desktop;

                    return;
                }
            }
        }
    }

    /// <summary>The newest entries whose address is in [<paramref name="start"/>, +<paramref name="bytes"/>).</summary>
    public static void Describe(StringBuilder text, ulong start, ulong bytes, int limit)
    {
        byte* header = Header();

        if (header == null)
        {
            return;
        }

        uint count = Volatile.Read(ref *(uint*)header);
        int printed = 0;
        text.Append(CultureInfo.InvariantCulture, $"\n  traced stores so far: {count}; into this block, newest first:");

        for (uint n = 0; n < Math.Min(count, Entries) && printed < limit; n++)
        {
            uint* entry = Entry(header, count - 1 - n);
            ulong address = Address(entry);

            if (address >= start && address < start + bytes)
            {
                text.Append(CultureInfo.InvariantCulture, $"\n    {Describe(entry, address)}");
                printed++;
            }
        }

        if (printed == 0)
        {
            text.Append("\n    none");
        }
    }

    /// <summary>
    /// Counts, per program, the ring's entries since <paramref name="seen"/> whose address lies in the first
    /// <paramref name="bytes"/> after one of <paramref name="starts"/> (0: unused), with the last address and value
    /// of each; called often enough (<see cref="FourGiBLineWatch"/>, every 10 ms) the ring does not lap it.
    /// </summary>
    public static void Collect(ReadOnlySpan<ulong> starts, ulong bytes, Dictionary<string, (long Stores, ulong Address, uint Value)> into, ref long seen)
    {
        byte* header = Header();

        if (header == null)
        {
            return;
        }

        long count = Volatile.Read(ref *(uint*)header);

        if (count < seen)
        {
            seen = 0;
        }

        for (long n = Math.Max(seen, count - Entries); n < count; n++)
        {
            uint* entry = Entry(header, (uint)n);

            if ((entry[HighWord] & KindMask & ~ViewKind) != 0)
            {
                Interlocked.Increment(ref _otherKinds);

                continue;
            }

            ulong address = Address(entry);
            Interlocked.Increment(ref _storeEntries);

            foreach (ulong start in starts)
            {
                if (start != 0 && address >= start && address - start < bytes)
                {
                    string program = Programs.GetValueOrDefault(entry[0], $"program {entry[0]}");
                    into[program] = (into.GetValueOrDefault(program).Stores + 1, address, entry[ValueWord]);

                    break;
                }
            }
        }

        seen = count;
    }

    /// <summary>
    /// Logs new global stores outside range 0 (the pool): writes into the game heap, the first
    /// <see cref="PerProgram"/> of each program. Stores through a view stay inside it (the desktop view bounds them).
    /// </summary>
    public static void LogOutside(LoaderLog log)
    {
        byte* header = Header();

        if (header == null)
        {
            return;
        }

        long count = Volatile.Read(ref *(uint*)header);
        long first = Math.Max(_outsideSeen, count - Entries);

        for (long n = first; n < count; n++)
        {
            uint* entry = Entry(header, (uint)n);
            ulong address = Address(entry);
            var pool = Ranges[0];

            if (!WellFormed(entry))
            {
                LogMalformed(log);

                continue;
            }

            if ((entry[HighWord] & KindMask) == GuardKind)
            {
                LogGuard(log, entry);

                continue;
            }

            if ((entry[HighWord] & KindMask) == ConstantKind)
            {
                LogConstant(log, entry);

                continue;
            }

            bool global = (entry[HighWord] & ViewKind) == 0;

            if (global && _outsideLogged < OutsideLogged && address != 0 && (address < pool.Start || address >= pool.Start + pool.Bytes) && Outside.AddOrUpdate(entry[0], 1, static (_, seen) => seen + 1) <= PerProgram)
            {
                _outsideLogged++;
                log.Warn($"[store trace] a GPU global store outside the transient pool: {Describe(entry, address)}");
            }
        }

        _outsideSeen = count;
    }

    /// <summary>
    /// Whether an entry has the shape its kind's writer gives it (rdna_prelude trace_entry): a guard has no address, a
    /// loop number below <see cref="MaxGuard"/> and a cap of at most <see cref="MaxCap"/>, a constant load reads 4 to 64 bytes. The ring lies in the
    /// GPU-memory window, where programs storing through 32-bit pointers also write: such words are skipped, not read
    /// as loops in the millions or loads of 500 MB.
    /// </summary>
    private static bool WellFormed(uint* entry)
    {
        uint kind = entry[HighWord] & KindMask;
        uint field = entry[HighWord] & ~KindMask;

        if (kind == GuardKind)
        {
            return entry[LowWord] == 0 && field < MaxGuard && entry[ValueWord] <= MaxCap;
        }

        if (kind == ConstantKind)
        {
            return field is >= MinConstantBytes and <= MaxConstantBytes && (field & (field - 1)) == 0;
        }

        return true;
    }

    /// <summary>Counts entries no trace writer made; logged at every power of two.</summary>
    private static void LogMalformed(LoaderLog log)
    {
        long malformed = Interlocked.Increment(ref _malformed);

        if ((malformed & (malformed - 1)) == 0)
        {
            log.Warn($"[store trace] {malformed} ring entries no traced program wrote (other stores into the GPU-memory window) skipped");
        }
    }

    /// <summary>
    /// A loop that spent its invocation's loop cap (the stage's, in the value word; LoopGuard): logged on the first hit
    /// per program, counted after.
    /// </summary>
    private static void LogGuard(LoaderLog log, uint* entry)
    {
        long hits = Guards.AddOrUpdate(entry[0], 1, static (_, seen) => seen + 1);

        if (hits == 1 || hits == GuardHitsLoggedAgain)
        {
            string program = Programs.GetValueOrDefault(entry[0], $"program {entry[0]}");
            log.Warn($"[store trace] loop {entry[HighWord] & ~KindMask} reached the loop cap of {entry[ValueWord]} iterations in {program} ({hits} hits)");
        }
    }

    /// <summary>An s_buffer_load past its descriptor's range (it reads 0): the first <see cref="PerProgram"/> per program and descriptor size.</summary>
    private static void LogConstant(LoaderLog log, uint* entry)
    {
        if (Constants.AddOrUpdate(((ulong)entry[ValueWord] << DescriptorSizeShift) | entry[0], 1, static (_, seen) => seen + 1) > PerProgram)
        {
            return;
        }

        string program = Programs.GetValueOrDefault(entry[0], $"program {entry[0]}");
        log.Warn($"[store trace] s_buffer_load of {entry[HighWord] & ~KindMask} bytes at offset 0x{entry[LowWord]:x} past the descriptor's 0x{entry[ValueWord]:x} bytes reads 0 in {program}");
    }

    private static uint* Entry(byte* header, uint index)
    {
        return (uint*)(header + HeaderBytes + ((index % Entries) * EntryBytes));
    }

    /// <summary>An entry's game address: the address of a global access, or the view's start plus the offset.</summary>
    private static ulong Address(uint* entry)
    {
        if ((entry[HighWord] & ViewKind) == 0)
        {
            return entry[LowWord] | ((ulong)entry[HighWord] << HighShift);
        }

        ulong start = TransientPoolViews.StartOf(entry[HighWord] & ~ViewKind);

        if (start == 0)
        {
            start = LineViewScan.StartOf(entry[HighWord] & ~ViewKind);
        }

        if (start == 0)
        {
            start = ShimContext.Runtime.D3D12X.MemoryViews.Start(entry[HighWord] & ~ViewKind);
        }

        if (start == 0)
        {
            return 0;
        }

        return start + entry[LowWord];
    }

    private static string Describe(uint* entry, ulong address)
    {
        string program = Programs.GetValueOrDefault(entry[0], $"program {entry[0]}");
        string how = "global";

        if ((entry[HighWord] & ViewKind) != 0)
        {
            how = $"view {entry[HighWord] & ~ViewKind} + 0x{entry[LowWord]:x}";
        }

        return $"0x{address:x} = 0x{entry[ValueWord]:x} ({how}) by {program}";
    }

    private static byte* Header()
    {
        if (!Enabled)
        {
            return null;
        }

        byte* window = ShimContext.Runtime.D3D12X.Bindings.Window.Data;

        if (window == null)
        {
            return null;
        }

        return window + TraceBase;
    }
}
