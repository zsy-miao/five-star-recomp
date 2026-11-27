// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace FiveStar.Loader.Shims.D3D12X.Binding;

/// <summary>
/// One of the game's shader-visible heaps, or one of its CPU-only view heaps. Its descriptors live in title memory as
/// on the console, one record per descriptor: a buffer view's V# as the hardware lays it out, its console base address
/// in dword 0 and dword 1 bits 0-15 (<see cref="BufferRecord"/>), else the index in the desktop shader-visible heap
/// standing behind it in dword 0. A record with a base address keeps that desktop index in dword 4 (the shim's copies to
/// desktop heaps read it); the window mirror holds it in dword 4 for every record, for the debug readers. The game copies these
/// records itself (its per-draw tables, 0x144d9efc0, also from CPU-only heaps), so a table in title memory names
/// desktop descriptors wherever the game put it. For a shader-visible heap the <see cref="DescriptorWindow"/> holds a
/// mirror of dwords 0 to 3 of every record, which programs read through a table's pointer; a record is mirrored when
/// the shim writes it and again when a table that covers it is bound. A CPU-only heap takes fresh desktop descriptors
/// from a <see cref="DescriptorRing"/> on every write and is never mirrored.
/// </summary>
internal sealed unsafe class ShaderVisibleHeap
{
    /// <summary>A shadow value no title record has been mirrored from yet.</summary>
    private static readonly UInt128 NotMirrored = UInt128.MaxValue;

    /// <summary>The record dwords holding the buffer fields, in title memory and in the window.</summary>
    private const int StrideDword = 1;
    private const int RecordsDword = 2;
    private const int FormatDword = 3;

    /// <summary>The desktop index of every record, past the V#, in records of 32 bytes (view heaps).</summary>
    private const int DesktopDword = DescriptorWindow.MirroredDesktopDword;
    private const uint HighMask = 0xFFFF;

    private readonly byte* _title;
    private readonly nint _desktopStart;
    private readonly DescriptorRemap? _remap;
    private readonly DescriptorRing? _ring;
    private readonly DescriptorWindow _window;
    private readonly HeapRecords _layout;

    /// <summary>The dwords 0 to 3 last mirrored to the window, per record.</summary>
    private readonly UInt128[] _shadow;

    /// <param name="title">The title memory of the heap (its CPU and GPU start for the game).</param>
    /// <param name="count">Descriptors in the game heap.</param>
    /// <param name="increment">Bytes per descriptor, in title memory as on the desktop.</param>
    /// <param name="desktopStart">The desktop CPU handle of desktop index 0.</param>
    /// <param name="layout">Where the records of the heap lie in the window and which desktop indices they may name.</param>
    /// <param name="remap">Game to desktop indices; null when they are equal.</param>
    /// <param name="window">The window the records are mirrored to.</param>
    /// <param name="ring">For a CPU-only heap, where its writes take desktop descriptors; null for a shader-visible heap.</param>
    public ShaderVisibleHeap(nint title, uint count, uint increment, nint desktopStart, HeapRecords layout, DescriptorRemap? remap, DescriptorWindow window, DescriptorRing? ring)
    {
        _ring = ring;
        _title = (byte*)title;
        Count = count;
        Increment = increment;
        _desktopStart = desktopStart;

        _remap = remap;
        _window = window;
        _layout = layout;

        _shadow = new UInt128[count];
        Array.Fill(_shadow, NotMirrored);
    }

    public uint Count { get; }

    public uint Increment { get; }

    public nint Title => (nint)_title;

    /// <summary>Whether programs read the heap through the window (a shader-visible heap).</summary>
    public bool Mirrored => _ring is null;

    /// <summary>The game index of a handle (CPU or GPU, both title memory addresses); false outside the heap.</summary>
    [HotPath]
    public bool TryIndex(ulong handle, out uint index)
    {
        ulong offset = handle - (ulong)_title;
        index = 0;

        if (offset >= (ulong)Count * Increment)
        {
            return false;
        }

        index = (uint)(offset / Increment);

        return true;
    }

    /// <summary>
    /// A new descriptor at <paramref name="index"/> and the <paramref name="count"/> - 1 after it (one desktop run): the
    /// desktop CPU handle to write it to, with its records written (<paramref name="buffer"/>'s fields for a buffer
    /// view, else none); 0 when no desktop descriptor is left for it.
    /// </summary>
    [HotPath]
    public nint Destination(uint index, uint count, BufferRecord buffer)
    {
        uint desktop = _ring is null ? DesktopIndex(index) : _ring.Next(count);

        if (desktop == DescriptorRemap.Unmapped)
        {
            return 0;
        }

        for (uint i = 0; i < count; i++)
        {
            WriteRecord(index + i, desktop + i, buffer);
        }

        if (Resources.DepthTwins.Any)
        {
            Resources.DepthTwins.Overwritten(desktop, count);
        }

        nint start = _desktopStart + (nint)((ulong)desktop * Increment);

        // The desktop descriptors are about to be rewritten: a stand-in fill must not take one for the UAV it was.
        if (RtStandIns.Enabled)
        {
            UavViews.Forget(start, count, Increment);
        }

        return start;
    }

    /// <summary>The desktop CPU handle of the descriptor the record at <paramref name="index"/> names.</summary>
    [HotPath]
    public nint Source(uint index)
    {
        return _desktopStart + (nint)((ulong)Valid(Desktop(Record(index))) * Increment);
    }

    /// <summary>Dword <paramref name="dword"/> of the record at <paramref name="index"/> (<see cref="DebugLayer.ViewLiveness"/>).</summary>
    public uint RecordDword(uint index, int dword)
    {
        return Record(index)[dword];
    }

    /// <summary>
    /// Writes <paramref name="buffer"/>'s V# fields into the record at <paramref name="index"/>, keeping the desktop
    /// descriptor it names (a copy from a desktop heap, <see cref="DesktopRecords"/>).
    /// </summary>
    [HotPath]
    public void SetFields(uint index, BufferRecord buffer)
    {
        if (index < Count)
        {
            WriteRecord(index, Desktop(Record(index)), buffer);
        }
    }

    /// <summary>Descriptors from <paramref name="index"/> that one desktop run holds.</summary>
    [HotPath]
    public uint RunLength(uint index)
    {
        if (_remap is null || _ring is not null)
        {
            return Count - index;
        }

        return DescriptorRemap.RunLength(index);
    }

    /// <summary>Copies records inside the heap, as the console copies descriptor bytes.</summary>
    [HotPath]
    public void CopyRecords(uint destination, uint source, uint count)
    {
        Buffer.MemoryCopy(Record(source), Record(destination), (ulong)(Count - destination) * Increment, (ulong)count * Increment);
        Mirror(destination, count);
    }

    /// <summary>Copies records from another title-memory heap of the same kind: both name descriptors of the desktop shader-visible heap.</summary>
    [HotPath]
    public void CopyRecords(uint destination, ShaderVisibleHeap source, uint sourceIndex, uint count)
    {
        Buffer.MemoryCopy(source.Record(sourceIndex), Record(destination), (ulong)(Count - destination) * Increment, (ulong)count * Increment);
        Mirror(destination, count);
    }

    /// <summary>
    /// A table at <paramref name="index"/> of up to <paramref name="extent"/> descriptors: its records mirrored, its
    /// pointer returned, relative to its heap as on the console (the view records start at window offset 0; a program
    /// reads an S# through a 32-bit pointer from the sampler records, prelude SamplerPtrLoad).
    /// </summary>
    [HotPath]
    public uint Table(uint index, uint extent)
    {
        uint left = Count - index;

        if (extent > left)
        {
            extent = left;
        }

        Mirror(index, extent);

        return index * _layout.RecordBytes;
    }

    [HotPath]
    private uint DesktopIndex(uint index)
    {
        if (_remap is not null)
        {
            return _remap.Map(index);
        }

        if (index >= _layout.DesktopCount)
        {
            return DescriptorRemap.Unmapped;
        }

        return index;
    }

    [HotPath]
    private void WriteRecord(uint index, uint desktop, BufferRecord buffer)
    {
        uint* record = Record(index);
        new Span<byte>(record, (int)Increment).Clear();
        record[0] = buffer.Word0(desktop);

        if (HasDesktopDword)
        {
            record[DesktopDword] = desktop;
        }

        record[StrideDword] = buffer.Word1;
        record[RecordsDword] = buffer.Records;
        record[FormatDword] = buffer.Word3;
        MirrorOne(index);

        if (DebugLayer.LineViewScan.Enabled)
        {
            DebugLayer.LineViewScan.Wrote(desktop, buffer.IsBuffer && buffer.Base == 0);
        }
    }

    /// <summary>
    /// Mirrors the records that changed since they were last mirrored. Rebinding a large table is the common case and
    /// most of its records are unchanged, so the loop only compares each record's 16 bytes with its shadow (one vector
    /// compare, no bounds checks): 1,024 unchanged records took 1.14 µs before, through <see cref="MirrorOne"/> each.
    /// </summary>
    [HotPath]
    private void Mirror(uint index, uint count)
    {
        byte* window = _window.Data;

        if (_ring is not null || window == null || index >= Count)
        {
            return;
        }

        if (count > Count - index)
        {
            count = Count - index;
        }

        ref UInt128 shadow = ref MemoryMarshal.GetArrayDataReference(_shadow);
        byte* record = (byte*)Record(index);

        for (uint i = index; i < index + count; i++, record += Increment)
        {
            ref UInt128 seen = ref Unsafe.Add(ref shadow, i);

            if (Vector128.Load(record) != Vector128.LoadUnsafe(ref Unsafe.As<UInt128, byte>(ref seen)))
            {
                Write(i, (uint*)record, ref seen, window);
            }
        }
    }

    [HotPath]
    private void Write(uint index, uint* record, ref UInt128 seen, byte* window)
    {
        seen = *(UInt128*)record;
        MirrorTo(window, index, record);
    }

    /// <summary>
    /// A record's V# into its window mirror: dword 0 as it is for a record holding a console base address (base_hi in
    /// dword 1, the desktop index then in dword 4), else the desktop index it names (the null descriptor for none); and
    /// the desktop index after the V#.
    /// </summary>
    [HotPath]
    private void MirrorTo(byte* window, uint index, uint* record)
    {
        ulong offset = _layout.Base + ((ulong)index * _layout.RecordBytes);
        uint* mirror = (uint*)(window + offset);
        uint desktop = Valid(Desktop(record));
        uint first = desktop;

        if ((record[StrideDword] & HighMask) != 0)
        {
            first = record[0];
        }

        mirror[0] = first;
        mirror[StrideDword] = record[StrideDword];
        mirror[RecordsDword] = record[RecordsDword];
        mirror[FormatDword] = record[FormatDword];

        if (_layout.RecordBytes > DesktopDword * sizeof(uint))
        {
            mirror[DesktopDword] = desktop;
        }

        // The window is write-combined: what its view records name is kept in cached memory for the per-draw scans.
        if (_layout.RecordBytes == DescriptorWindow.ViewRecordBytes)
        {
            _window.Records.Written((uint)(offset / DescriptorWindow.ViewRecordBytes), first, record[StrideDword], record[RecordsDword], record[FormatDword]);
        }
    }

    /// <summary>The desktop index a record names: dword 4 of one holding a console base address, else dword 0.</summary>
    [HotPath]
    private uint Desktop(uint* record)
    {
        if (HasDesktopDword && (record[StrideDword] & HighMask) != 0)
        {
            return record[DesktopDword];
        }

        return record[0];
    }

    private bool HasDesktopDword => Increment > DesktopDword * sizeof(uint);

    [HotPath]
    private void MirrorOne(uint index)
    {
        byte* window = _window.Data;
        uint* record = Record(index);
        var value = *(UInt128*)record;

        if (_ring is not null || _shadow[index] == value || window == null)
        {
            return;
        }

        _shadow[index] = value;
        MirrorTo(window, index, record);
    }

    /// <summary>A record's desktop index, or the null descriptor for one that names none (a null or unwritten descriptor).</summary>
    [HotPath]
    private uint Valid(uint desktop)
    {
        if (desktop < _layout.FirstDesktop || desktop >= _layout.DesktopCount)
        {
            return _layout.NullIndex;
        }

        return desktop;
    }

    [HotPath]
    private uint* Record(uint index)
    {
        return (uint*)(_title + ((ulong)index * Increment));
    }
}
