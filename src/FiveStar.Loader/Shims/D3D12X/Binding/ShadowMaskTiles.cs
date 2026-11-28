// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using FiveStar.Loader.Native;

namespace FiveStar.Loader.Shims.D3D12X.Binding;

/// <summary>
/// The stand-in for the shadow masks of lamps whose shadows are ray traced (<see cref="RtStandIns"/>). The lighting
/// (TL_PS_*) looks each shadowed light up per 8x8 tile in a table of tiles, an R32_UINT image with one screen of tiles
/// per light slot (7680x90 at 1280x720, 48 slots): a value up to 0xff is the tile's visibility, over 15 when bit 0 of
/// the lighting's flags is set (4-bit packed masks) and over 255 when not; a larger one is 0x100 plus the tile's place
/// in the mask atlas. The flags are dword 0x88 of the constant buffer at record 178 (byte 0x1640) of the shared table,
/// which TL_PS and the denoisers both read. The table is cleared to 0xffffffff; the denoiser
/// (shadowmask~CS_DenoiseShadowMask*, an ExecuteIndirect) reads it and writes the atlas. With the trace held
/// (shadowmask~CS_TraceShadowRays_*) nearly no tile is given a place: boots w352, w353, w362 and w380 had 0xffffffff in
/// over 99.9% of it, which the lighting takes as a place far outside the atlas, reads 0 there, and so every such lamp
/// was dark. After a packed denoiser runs, the tiles still at 0xffffffff are set to "lit" on the flags' scale (0xf or
/// 0xff); the others stay. Only while a trace is held: a trace skipped in the last <see cref="HeldWindowSeconds"/> s
/// (<see cref="Skipped"/>); when the traces run, their table is left alone. Without --shader-dir (no programs' reach)
/// or when the flags cannot be read, nothing is replaced.
/// </summary>
internal static unsafe class ShadowMaskTiles
{
    private const string Denoiser = "shadowmask~CS_DenoiseShadowMask";
    private const string Packed = "Packed";
    private const string Trace = "shadowmask~CS_TraceShadowRays";

    /// <summary>What the cleared table holds.</summary>
    private const uint Uncovered = 0xFFFFFFFF;

    /// <summary>A tile's full visibility with 4-bit packed masks and without.</summary>
    private const uint LitPacked = 0xF;
    private const uint LitUnpacked = 0xFF;

    /// <summary>The lighting's constant buffer: its record in the shared table, its flags' byte offset, the packing bit.</summary>
    private const uint ConstantsRecord = 0x1640 / DescriptorWindow.ViewRecordBytes;
    private const ulong FlagsOffset = 0x88;
    private const uint PackedFlag = 1;

    /// <summary>V# dword 1: base_hi in bits 0-15.</summary>
    private const uint BaseHighMask = 0xFFFF;
    private const int BaseHighShift = 32;

    /// <summary>DXGI_FORMAT_R32_UINT.</summary>
    private const int R32Uint = 42;

    /// <summary>ID3D12Device::GetDescriptorHandleIncrementSize and the CBV_SRV_UAV heap type (d3d12.h).</summary>
    private const int IncrementSlot = 15;
    private const int ViewHeapType = 0;

    /// <summary>The denoiser's own table is short (4 records, the table of tiles among them); the shared table is not searched.</summary>
    private const uint MaxRecords = 16;

    /// <summary>The table of tiles is many screens of tiles side by side: at least this many times as wide as high.</summary>
    private const uint WideBy = 8;

    /// <summary>How recently a trace must have been held for a denoiser's table to be gone over (traces run every frame).</summary>
    private const double HeldWindowSeconds = 2;

    private static readonly ConcurrentDictionary<nint, byte> Denoisers = new();
    private static long _replaced;
    private static long _unpacked;
    private static long _flagsUnread;
    private static long _lastHeld;
    private static int _logged;

    /// <summary>"tables of tiles gone over N times (M on the 8-bit scale), K skipped with the flags unread": for the sample line.</summary>
    public static string Summary => $"shadow-mask tables' uncovered tiles set lit {Interlocked.Read(ref _replaced)} times ({Interlocked.Read(ref _unpacked)} on the 8-bit scale), {Interlocked.Read(ref _flagsUnread)} left with the lighting's flags unread";

    /// <summary>A desktop pipeline was built from <paramref name="names"/>: a packed denoiser's table is gone over after it runs.</summary>
    public static void Named(nint pipeline, string names)
    {
        if (RtStandIns.Enabled && IsPackedDenoiser(names))
        {
            Denoisers[pipeline] = 0;

            return;
        }

        Denoisers.TryRemove(pipeline, out _);
    }

    /// <summary>Whether <paramref name="names"/> is a packed denoiser of the shadow masks.</summary>
    public static bool IsPackedDenoiser(string names)
    {
        return names.Contains(Denoiser, StringComparison.Ordinal) && names.Contains(Packed, StringComparison.Ordinal);
    }

    /// <summary>A held pass's dispatch was skipped: a shadow-mask trace's skip lets the next denoisers' tables be gone over.</summary>
    public static void Skipped(string name)
    {
        if (name.Contains(Trace, StringComparison.Ordinal))
        {
            Interlocked.Exchange(ref _lastHeld, Stopwatch.GetTimestamp());
        }
    }

    /// <summary>After a dispatch or indirect dispatch that ran: a packed denoiser's table of tiles has its uncovered tiles set lit while a trace is held.</summary>
    [HotPath]
    public static void AfterDispatch(XboxObject* list, nint pipeline)
    {
        if (!Denoisers.IsEmpty && Denoisers.ContainsKey(pipeline) && TraceHeld(Interlocked.Read(ref _lastHeld), Stopwatch.GetTimestamp()))
        {
            Replace(list, QueueProgress.PipelineName(pipeline));
        }
    }

    /// <summary>Whether a trace skipped at timestamp <paramref name="lastHeld"/> (0 for never) is recent at <paramref name="now"/>.</summary>
    public static bool TraceHeld(long lastHeld, long now)
    {
        return lastHeld != 0 && now - lastHeld <= (long)(HeldWindowSeconds * Stopwatch.Frequency);
    }

    /// <summary>Whether a UAV is the table of tiles: a 2D R32_UINT image far wider than high that is not shared-exponent light.</summary>
    public static bool IsTable(UavView view)
    {
        return view.Format == R32Uint && view.Dimension == UavViews.Texture2D && view.Width >= WideBy * view.Height && !UavViews.SharedExponent(view.Resource);
    }

    /// <summary>A tile's full visibility for the lighting's <paramref name="flags"/>: 0xf with 4-bit packing (bit 0), else 0xff.</summary>
    public static uint LitFor(uint flags)
    {
        if ((flags & PackedFlag) != 0)
        {
            return LitPacked;
        }

        return LitUnpacked;
    }

    /// <summary>The console address a buffer record's V# dwords 0 and 1 hold; 0 when base_hi is 0 (a desktop heap index, <see cref="BufferRecord"/>).</summary>
    public static ulong RecordBase(uint word0, uint word1)
    {
        ulong high = word1 & BaseHighMask;

        if (high == 0)
        {
            return 0;
        }

        return (high << BaseHighShift) | word0;
    }

    /// <summary>
    /// Goes over the tables of tiles in the short tables the denoiser's programs read (<see cref="TableReaches.OfNames"/>),
    /// with "lit" on the scale of the flags read through the shared table, then sets the list's compute state again.
    /// </summary>
    private static void Replace(XboxObject* list, string name)
    {
        var runtime = ShimContext.Runtime.D3D12X;
        var bindings = CommandListBindings.Of(list);
        var window = runtime.Bindings.Window;
        var reach = TableReaches.OfNames(name);

        // Without the game's compute root signature set there is nothing to restore after the replacement's own.
        if (bindings->ComputeRoot == 0 || bindings->ComputeLayout == null || window.Data == null || window.ViewHeap == 0 || reach.Length == 0)
        {
            return;
        }

        if (!TryFlags(bindings, window, reach, out uint flags))
        {
            Interlocked.Increment(ref _flagsUnread);

            return;
        }

        uint lit = LitFor(flags);
        int done = 0;

        // The game copies descriptors past its root signatures' ranges, whose tables are declared 1,024 records long:
        // the programs' reach is what finds the denoiser's own table (w362 went over none by the root signature's).
        foreach (var entry in reach)
        {
            if (entry.Sgpr < CommandListBindings.UserDataDwords && entry.Records <= MaxRecords)
            {
                runtime.DescriptorHeaps.MirrorTable(bindings->Compute[entry.Sgpr], entry.Records);
                done += ReplaceIn(list->Inner, window, bindings->Compute[entry.Sgpr], entry.Records, lit, done == 0);
            }
        }

        if (done == 0)
        {
            return;
        }

        RtStandIns.Barrier(list->Inner);
        CommandListBindingMethods.RestoreCompute(list);

        if (lit == LitUnpacked)
        {
            Interlocked.Add(ref _unpacked, done);
        }

        if (Interlocked.Add(ref _replaced, done) == done && Interlocked.Exchange(ref _logged, 1) == 0)
        {
            ShimContext.Log?.Warn($"[d3d12_xs] ray-tracing stand-in (approximate, not the pass's result; --no-rt-stand-ins turns it off): after {name} the tiles of the shadow-mask table no pass covered (0xffffffff, which the lighting reads as shadowed) are set lit (0x{lit:x}, the lighting's flags 0x{flags:x}): lamps with ray-traced shadows shine unshadowed instead of not at all");
        }
    }

    /// <summary>
    /// The lighting's flags: dword <see cref="FlagsOffset"/> of the constant buffer at record <see cref="ConstantsRecord"/>
    /// of the table the programs read past it, read from game memory through the kernel (an unmapped page fails, not faults).
    /// </summary>
    private static bool TryFlags(CommandListBindings* bindings, DescriptorWindow window, TableReachEntry[] reach, out uint flags)
    {
        flags = 0;

        foreach (var entry in reach)
        {
            if (entry.Sgpr >= CommandListBindings.UserDataDwords || entry.Records <= ConstantsRecord || entry.Records >= DescriptorWindow.MaxTableRecords)
            {
                continue;
            }

            uint pointer = bindings->Compute[entry.Sgpr];
            ulong offset = pointer + ((ulong)ConstantsRecord * DescriptorWindow.ViewRecordBytes);

            if (offset + DescriptorWindow.ViewRecordBytes > DescriptorWindow.NullTableBase)
            {
                continue;
            }

            ShimContext.Runtime.D3D12X.DescriptorHeaps.MirrorTable(pointer, ConstantsRecord + 1);
            uint* record = (uint*)(window.Data + offset);
            ulong address = RecordBase(record[0], record[1]);

            if (address == 0)
            {
                continue;
            }

            nint cpu = ShimContext.Runtime.XMemPhysical.CpuAddress((nint)(address + FlagsOffset));
            uint value = 0;
            nuint read = 0;

            if (Kernel32.ReadProcessMemory(Kernel32.GetCurrentProcess(), cpu, &value, sizeof(uint), &read) != 0 && read == sizeof(uint))
            {
                flags = value;

                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Records the replacement over each table of tiles among the UAVs of the table at window offset
    /// <paramref name="pointer"/>, after a barrier on the denoiser's writes when <paramref name="barrierFirst"/>; how many.
    /// </summary>
    private static int ReplaceIn(nint list, DescriptorWindow window, uint pointer, uint records, uint lit, bool barrierFirst)
    {
        nint device = ShimContext.Runtime.D3D12X.Objects.DesktopDevice;
        nint cpuStart = DesktopCalls.HeapCpuStart(window.ViewHeap);
        uint increment = ((delegate* unmanaged<nint, int, uint>)ComObject.Slot(device, IncrementSlot))(device, ViewHeapType);
        int done = 0;

        for (uint record = 0; record < records; record++)
        {
            ulong offset = pointer + ((ulong)record * DescriptorWindow.ViewRecordBytes);

            if (offset + DescriptorWindow.ViewRecordBytes > DescriptorWindow.NullTableBase)
            {
                break;
            }

            uint index = ((uint*)(window.Data + offset))[DescriptorWindow.MirroredDesktopDword];

            if (!UavViews.TryFind(cpuStart + (nint)((ulong)index * increment), out var view) || !IsTable(view))
            {
                continue;
            }

            // The denoiser's writes before the replacement reads them.
            if (barrierFirst && done == 0)
            {
                RtStandIns.Barrier(list);
            }

            if (UavFill.Replace(device, list, DesktopCalls.HeapGpuStart(window.ViewHeap), index, view, Uncovered, lit))
            {
                done++;
            }
        }

        return done;
    }
}
