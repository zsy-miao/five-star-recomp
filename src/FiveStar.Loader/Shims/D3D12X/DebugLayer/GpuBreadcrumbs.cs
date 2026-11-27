// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using FiveStar.Loader.Diagnostics;
using FiveStar.Loader.Native;
using Vortice.Direct3D12;

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>
/// <c>--d3d12-breadcrumbs</c>: before every draw, dispatch, ExecuteIndirect, buffer copy and barrier, and at Close, a
/// command list writes its recording's number and the operation's index to that recording's dword of a readback
/// buffer, with WriteBufferImmediate(MARKER_OUT), which waits for the work before it. Each recording (Reset to Close)
/// takes the next of <see cref="Recordings"/> slots and keeps what it recorded, and each submission notes the queue
/// fence value its recordings end at, so a list recorded again while its last submission still runs keeps both. When
/// <see cref="QueueProgress"/> reports an execution the GPU has not finished, each of its recordings names the
/// operation it stopped at (docs/d3d12x-shim.md#queue-progress). Unlike DRED it needs no debug layer, and it covers
/// the game's own command lists only.
/// </summary>
internal static unsafe class GpuBreadcrumbs
{
    /// <summary>Recordings followed at once, a ring; each takes one dword. A power of two that divides 2^16.</summary>
    private const int Recordings = 1 << 13;

    /// <summary>Operations kept per recording: chunks of <see cref="ChunkOperations"/>, made as a recording reaches them (w79's lists stopped at operations 520 to 3229, past the 512 kept before).</summary>
    private const int ChunkOperations = 512;
    private const int MaxChunks = 128;
    private const int KeptOperations = ChunkOperations * MaxChunks;

    /// <summary>The root tables noted per operation: their window offsets.</summary>
    private const int NotedTables = MarkedOperation.MaxTables;

    /// <summary>A marker is the recording number (low 16 bits) over the operation index.</summary>
    private const int SerialShift = 16;
    private const uint IndexMask = 0xFFFF;

    /// <summary>The index written at Close: the list finished.</summary>
    private const uint Finished = 0xFFFF;
    private const uint LastIndex = Finished - 1;

    /// <summary>ID3D12GraphicsCommandList2::WriteBufferImmediate and D3D12_WRITEBUFFERIMMEDIATE_MODE_MARKER_OUT (d3d12.h).</summary>
    private const int WriteBufferImmediateSlot = 66;
    private const int MarkerOut = 2;

    /// <summary>What a list stopped part way says (<see cref="Describe"/>).</summary>
    public const string StoppedMark = "STOPPED at operation";

    private static bool _enabled;
    private static ID3D12Resource? _buffer;
    private static uint* _markers;
    private static Recording* _recordings;
    private static ulong _address;
    private static int _serial;

    [StructLayout(LayoutKind.Sequential)]
    private struct WriteParameter
    {
        public ulong Destination;
        public uint Value;
    }

    /// <summary>One recording: its list and number, where it was last submitted, and what it recorded.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Recording
    {
        public nint List;
        public nint Fence;
        public ulong Value;
        public uint Serial;
        public int Position;
        public int Operations;
        public MarkedOperation** Chunks;
    }

    /// <summary>Turns the markers on (or leaves them off) for the device created next.</summary>
    public static void Enable(bool enabled)
    {
        _enabled = enabled;
    }

    /// <summary>After device creation: the marker buffer and recording ring, when enabled.</summary>
    public static void Attach(nint device, LoaderLog log)
    {
        if (!_enabled)
        {
            return;
        }

        Marshal.AddRef(device);
        using var desktop = new ID3D12Device(device);
        var buffer = desktop.CreateCommittedResource(new HeapProperties(HeapType.Readback), HeapFlags.None, Vortice.Direct3D12.ResourceDescription.Buffer(Recordings * sizeof(uint)), ResourceStates.CopyDest);
        void* data;
        buffer.Map(0, null, &data).CheckError();

        _recordings = (Recording*)NativeMemory.AllocZeroed((nuint)(Recordings * sizeof(Recording)));
        _buffer = buffer;
        _address = buffer.GPUVirtualAddress;
        _markers = (uint*)data;
        log.Info($"  [gpu] breadcrumbs on: a marker before every operation of the last {Recordings} command-list recordings");
    }

    /// <summary>Turns the markers off and drops the buffer (tests: another device's lists must not write into it).</summary>
    public static void Detach()
    {
        _markers = null;
        _buffer?.Dispose();
        _buffer = null;
        _enabled = false;
    }

    /// <summary>Operation <paramref name="index"/> of recording <paramref name="serial"/> as the stall reports name it; null when not kept.</summary>
    public static string? OperationText(uint serial, int index, Func<nint, string> pipelineName)
    {
        if (_recordings == null)
        {
            return null;
        }

        var recording = _recordings + (serial & (Recordings - 1));
        var kept = recording->Serial == serial ? Kept(recording, index) : null;

        if (kept == null)
        {
            return null;
        }

        return Operation(kept, pipelineName);
    }

    /// <summary>At Reset: the list's next recording takes a slot of its own; 0 when the markers are off.</summary>
    [HotPath]
    public static uint Begin(XboxObject* list)
    {
        if (_markers == null)
        {
            return 0;
        }

        uint serial;

        // Serial 0 in the low 16 bits would match a dword never written.
        do
        {
            serial = (uint)Interlocked.Increment(ref _serial);
        }
        while ((serial & IndexMask) == 0);

        var recording = _recordings + (serial & (Recordings - 1));

        if (recording->Chunks == null)
        {
            recording->Chunks = (MarkedOperation**)NativeMemory.AllocZeroed((nuint)(MaxChunks * sizeof(nint)));
        }

        recording->List = (nint)list;
        recording->Fence = 0;
        recording->Value = 0;
        recording->Serial = serial;
        recording->Operations = 0;

        return serial;
    }

    /// <summary>After ExecuteCommandLists: the list's recording ends at <paramref name="value"/> on <paramref name="fence"/>.</summary>
    [HotPath]
    public static void Submitted(nint list, nint fence, ulong value, int position)
    {
        var recording = Current((XboxObject*)list);

        if (recording != null)
        {
            recording->Fence = fence;
            recording->Value = value;
            recording->Position = position;
        }
    }

    /// <summary>Before a draw or dispatch: notes it with its pipeline and counts and marks that the work before it is done.</summary>
    [HotPath]
    public static void Before(XboxObject* list, MarkedKind kind, uint x, uint y, uint z)
    {
        var operation = new MarkedOperation { Kind = kind, X = x, Y = y, Z = z };
        Before(list, &operation);
    }

    /// <summary>Before an ExecuteIndirect: also notes its signature and the console address of its arguments.</summary>
    [HotPath]
    public static void BeforeIndirect(XboxObject* list, nint signature, uint maxCount, ulong arguments, uint argumentOffset)
    {
        var operation = new MarkedOperation { Kind = MarkedKind.Indirect, X = maxCount, Signature = signature, Address = arguments, Y = argumentOffset };
        Before(list, &operation);
    }

    /// <summary>Before a copy or barrier: notes it as <paramref name="command"/> in place of a pipeline and marks the work before it done.</summary>
    [HotPath]
    public static void Before(XboxObject* list, RecordedCommand command)
    {
        var operation = new MarkedOperation { Kind = MarkedKind.Command, Pipeline = (nint)command };
        Before(list, &operation);
    }

    /// <summary>A command noted in place of a pipeline, by its kind.</summary>
    public static string CommandName(nint pipeline)
    {
        return ((RecordedCommand)pipeline).ToString();
    }

    /// <summary>At Close: marks the list finished once its work is.</summary>
    [HotPath]
    public static void Close(XboxObject* list)
    {
        var recording = Current(list);

        if (recording != null)
        {
            Write(list, recording, Finished);
        }
    }

    /// <summary>Where each recording of the execution that ends at <paramref name="value"/> on <paramref name="fence"/> stopped.</summary>
    public static void Describe(StringBuilder text, nint fence, ulong value, Func<nint, string> pipelineName)
    {
        if (_buffer is null || _markers == null)
        {
            return;
        }

        var lines = new SortedList<int, string>();

        for (int slot = 0; slot < Recordings; slot++)
        {
            var recording = _recordings + slot;

            if (recording->List != 0 && recording->Fence == fence && recording->Value == value)
            {
                lines[recording->Position] = Where(recording, Volatile.Read(ref _markers[slot]), pipelineName);
            }
        }

        foreach (var (position, line) in lines)
        {
            text.Append(CultureInfo.InvariantCulture, $"\n        #{position} ").Append(line);
        }
    }

    private static string Where(Recording* recording, uint marker, Func<nint, string> pipelineName)
    {
        uint index = marker & IndexMask;
        string name = string.Create(CultureInfo.InvariantCulture, $"list 0x{recording->List:x} ({recording->Operations} operations)");

        if (marker >> SerialShift != (recording->Serial & IndexMask))
        {
            return $"{name}: not started";
        }

        if (index == Finished)
        {
            return $"{name}: finished";
        }

        string operation = "?";

        var kept = Kept(recording, (int)index);

        if (kept != null)
        {
            operation = Operation(kept, pipelineName);
        }

        return string.Create(CultureInfo.InvariantCulture, $"{name}: {StoppedMark} {index} (the work before it is done), {operation}");
    }

    /// <summary>The list's current recording, begun here when the list was created recording (no Reset); null when off.</summary>
    [HotPath]
    private static Recording* Current(XboxObject* list)
    {
        if (_markers == null)
        {
            return null;
        }

        var activity = ListActivity.Of(list);

        if (activity->Serial == 0)
        {
            activity->Serial = Begin(list);
        }

        var recording = _recordings + (activity->Serial & (Recordings - 1));

        if (recording->Serial != activity->Serial)
        {
            return null;
        }

        return recording;
    }

    [HotPath]
    private static void Before(XboxObject* list, MarkedOperation* operation)
    {
        if (operation->Kind == MarkedKind.Command)
        {
            AftermathCrashDumps.Mark(list, operation->Kind, operation->Pipeline);
        }
        else
        {
            AftermathCrashDumps.Mark(list, operation->Kind, ListActivity.Of(list)->Pipeline);
        }

        var recording = Current(list);

        if (recording == null)
        {
            return;
        }

        int index = recording->Operations++;

        if (operation->Kind != MarkedKind.Command)
        {
            operation->Pipeline = ListActivity.Of(list)->Pipeline;
            NoteTables(list, operation);
            TransientPoolHits.Note(list, recording->Serial, index, operation->Kind, operation->Pipeline);
        }

        var slot = Slot(recording, index);

        if (slot != null)
        {
            *slot = *operation;
        }

        uint marker = LastIndex;

        if (index < LastIndex)
        {
            marker = (uint)index;
        }

        Write(list, recording, marker);
    }

    /// <summary>The kept operation at <paramref name="index"/>, its chunk made when first reached; null past <see cref="KeptOperations"/>.</summary>
    [HotPath]
    private static MarkedOperation* Slot(Recording* recording, int index)
    {
        if (index >= KeptOperations || recording->Chunks == null)
        {
            return null;
        }

        var chunk = recording->Chunks[index / ChunkOperations];

        if (chunk == null)
        {
            chunk = (MarkedOperation*)NativeMemory.AllocZeroed((nuint)(ChunkOperations * sizeof(MarkedOperation)));
            recording->Chunks[index / ChunkOperations] = chunk;
        }

        return chunk + (index % ChunkOperations);
    }

    /// <summary>The kept operation at <paramref name="index"/> of a recording; null when none is kept there.</summary>
    private static MarkedOperation* Kept(Recording* recording, int index)
    {
        if (index < 0 || index >= KeptOperations || index >= recording->Operations || recording->Chunks == null)
        {
            return null;
        }

        var chunk = recording->Chunks[index / ChunkOperations];

        if (chunk == null)
        {
            return null;
        }

        return chunk + (index % ChunkOperations);
    }

    /// <summary>The window offsets of the first root tables the operation's root signature binds (graphics for draws, compute for dispatches).</summary>
    [HotPath]
    private static void NoteTables(XboxObject* list, MarkedOperation* operation)
    {
        bool compute = operation->Kind == MarkedKind.Dispatch
            || (operation->Kind == MarkedKind.Indirect && CommandSignatures.Clampable(operation->Signature).Kind == IndirectClamp.DispatchKind);
        operation->Compute = compute;
        var bindings = Binding.CommandListBindings.Of(list);
        var layout = compute ? bindings->ComputeLayout : bindings->GraphicsLayout;

        if (layout == null)
        {
            return;
        }

        uint* data = compute ? bindings->Compute : bindings->Graphics;
        uint first = compute ? Binding.UserData.ComputeBase : Binding.UserData.GraphicsBase;

        for (uint parameter = 0; parameter < (uint)layout->Count && operation->TableCount < NotedTables; parameter++)
        {
            uint sgpr = first + layout->Offsets[parameter];

            if (layout->Kind(parameter) == Binding.RootParameterKind.Table && sgpr < Binding.CommandListBindings.UserDataDwords)
            {
                operation->Tables[operation->TableCount++] = data[sgpr];
            }
        }
    }

    /// <summary>A kept operation as text: its kind and counts, an ExecuteIndirect's signature and current arguments.</summary>
    private static string Operation(MarkedOperation* operation, Func<nint, string> pipelineName)
    {
        string pipeline = pipelineName(operation->Pipeline);
        string tables = Tables(operation);

        switch (operation->Kind)
        {
            case MarkedKind.Draw:
                return string.Create(CultureInfo.InvariantCulture, $"draw ({operation->X} x {operation->Y}), pipeline {pipeline}{tables}");
            case MarkedKind.Dispatch:
                return string.Create(CultureInfo.InvariantCulture, $"dispatch ({operation->X}, {operation->Y}, {operation->Z}), pipeline {pipeline}{tables}");
            case MarkedKind.Indirect:
                string signature = CommandSignatures.Describe(operation->Signature);
                string arguments = IndirectArguments(operation->Address, CommandSignatures.Stride(operation->Signature), operation->X);

                return string.Create(CultureInfo.InvariantCulture, $"ExecuteIndirect (max {operation->X}, signature {signature}, argument buffer offset 0x{operation->Y:x}, arguments at 0x{operation->Address:x}: {arguments}), pipeline {pipeline}{tables}");
            default:
                return pipeline;
        }
    }

    /// <summary>"; graphics tables at window 0x.., 0x.." for the root tables noted with the operation; empty for none.</summary>
    public static string Tables(MarkedOperation* operation)
    {
        if (operation->TableCount == 0)
        {
            return string.Empty;
        }

        var offsets = new string[operation->TableCount];

        for (int i = 0; i < offsets.Length; i++)
        {
            offsets[i] = string.Create(CultureInfo.InvariantCulture, $"0x{operation->Tables[i]:x}");
        }

        string kind = "graphics";

        if (operation->Compute)
        {
            kind = "compute";
        }

        return $"; {kind} tables at window {string.Join(", ", offsets)}";
    }

    /// <summary>The argument dwords as they are in title memory now (a buffer the GPU shares with the game).</summary>
    private static string IndirectArguments(ulong address, uint stride, uint count)
    {
        const int MaxDwords = 32;

        if (address == 0)
        {
            return "not in title memory";
        }

        ulong bytes = Math.Min((ulong)stride * Math.Max(count, 1u), MaxDwords * sizeof(uint));

        if (bytes == 0 || !Readable(address, bytes))
        {
            return "unreadable";
        }

        var text = new StringBuilder();

        for (ulong offset = 0; offset < bytes; offset += sizeof(uint))
        {
            text.Append(CultureInfo.InvariantCulture, $"{*(uint*)(address + offset):x} ");
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>Whether this process can read <paramref name="bytes"/> at <paramref name="address"/> (one committed, accessible region).</summary>
    internal static bool Readable(ulong address, ulong bytes)
    {
        MemoryBasicInformation info;

        if (Kernel32.VirtualQueryEx(Kernel32.GetCurrentProcess(), (nint)address, &info, (nuint)sizeof(MemoryBasicInformation)) == 0)
        {
            return false;
        }

        bool blocked = (info.Protect & (Win32Constants.PageNoAccess | Win32Constants.PageGuard)) != 0;
        ulong end = (ulong)info.BaseAddress + (ulong)info.RegionSize;

        return info.State == Win32Constants.MemCommit && !blocked && address + bytes <= end;
    }

    [HotPath]
    private static void Write(XboxObject* list, Recording* recording, uint index)
    {
        long slot = recording - _recordings;
        var parameter = new WriteParameter
        {
            Destination = _address + ((ulong)slot * sizeof(uint)),
            Value = ((recording->Serial & IndexMask) << SerialShift) | index,
        };
        int mode = MarkerOut;
        nint inner = list->Inner;
        ((delegate* unmanaged<nint, uint, WriteParameter*, int*, void>)ComObject.Slot(inner, WriteBufferImmediateSlot))(inner, 1, &parameter, &mode);
    }

    /// <summary>
    /// The submission of recording <paramref name="serial"/>: the queue fence and value it ends at (0 when not submitted
    /// yet); false when the recording's slot was taken by a newer one.
    /// </summary>
    public static bool TryRecording(uint serial, out nint fence, out ulong value)
    {
        fence = 0;
        value = 0;

        if (_recordings == null || serial == 0)
        {
            return false;
        }

        var recording = _recordings + (serial & (Recordings - 1));

        if (recording->Serial != serial)
        {
            return false;
        }

        fence = recording->Fence;
        value = recording->Value;

        return true;
    }

    /// <summary>Whether a device has the markers.</summary>
    public static bool Active => _markers != null;

    /// <summary>The number of markers, the length of a <see cref="Snapshot"/>.</summary>
    public static int Slots => Recordings;

    /// <summary>Copies every recording's marker into <paramref name="markers"/>.</summary>
    public static void Snapshot(uint[] markers)
    {
        if (_markers == null)
        {
            return;
        }

        for (int slot = 0; slot < Recordings; slot++)
        {
            markers[slot] = Volatile.Read(ref _markers[slot]);
        }
    }

    /// <summary>
    /// Appends each operation that ended between snapshots <paramref name="before"/> and <paramref name="after"/>, and
    /// the one each such recording runs at <paramref name="after"/>; adds their (serial, index) to
    /// <paramref name="operations"/>. Returns how many it appended.
    /// </summary>
    public static int DescribeWindow(StringBuilder text, uint[] before, uint[] after, HashSet<(uint, int)> operations, int limit, Func<nint, string> pipelineName)
    {
        int appended = 0;

        for (int slot = 0; slot < Recordings && appended < limit; slot++)
        {
            var recording = _recordings + slot;
            uint serial = recording->Serial & IndexMask;

            if (before[slot] == after[slot] || recording->List == 0 || after[slot] >> SerialShift != serial)
            {
                continue;
            }

            (int first, int last, bool running) = Range(recording, before[slot], after[slot]);

            for (int index = first; index <= last && appended < limit; index++)
            {
                operations.Add((recording->Serial, index));
                string state = running && index == last ? "running" : "done";
                string operation = "?";

                var kept = Kept(recording, index);

                if (kept != null)
                {
                    operation = Operation(kept, pipelineName);
                }

                text.Append(CultureInfo.InvariantCulture, $"\n    recording {recording->Serial} (list 0x{recording->List:x}) operation {index} {state}: {operation}");
                appended++;
            }
        }

        return appended;
    }

    /// <summary>
    /// Adds to <paramref name="pipelines"/> the pipeline (or command) of every operation that ran between snapshots
    /// <paramref name="before"/> and <paramref name="after"/>: those a marker passed, and the one each unfinished
    /// recording is in, also when its marker did not move (a long dispatch).
    /// </summary>
    public static void CollectWindow(uint[] before, uint[] after, HashSet<nint> pipelines)
    {
        if (_recordings == null)
        {
            return;
        }

        for (int slot = 0; slot < Recordings; slot++)
        {
            var recording = _recordings + slot;

            if (recording->List == 0 || after[slot] >> SerialShift != (recording->Serial & IndexMask))
            {
                continue;
            }

            if (before[slot] == after[slot] && (after[slot] & IndexMask) == Finished)
            {
                continue;
            }

            (int first, int last, _) = Range(recording, before[slot], after[slot]);

            for (int index = first; index <= last; index++)
            {
                var kept = Kept(recording, index);

                if (kept != null)
                {
                    pipelines.Add(kept->Pipeline);
                }
            }
        }
    }

    /// <summary>The operations a recording went through between two of its markers; the last one may still run.</summary>
    private static (int First, int Last, bool Running) Range(Recording* recording, uint before, uint after)
    {
        uint serial = recording->Serial & IndexMask;
        int first = 0;

        if (before >> SerialShift == serial)
        {
            first = (int)(before & IndexMask);
        }

        if ((after & IndexMask) == Finished)
        {
            return (first, recording->Operations - 1, false);
        }

        return (first, (int)(after & IndexMask), true);
    }
}
