// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

// Clamps batched between the points where arguments may change follow the 7900 XTX fork of VIctory (commit 0375962,
// crates/victory-gpu/src/indirect/batch.rs and draw.rs, VICTORY_INDIRECT_BATCH); GPL-3.0-or-later. Ported as facts
// and written anew here.

using FiveStar.Loader.Native;
using FiveStar.Loader.Shims.D3D12X.Binding;
using FiveStar.Loader.Shims.D3D12X.DebugLayer;

namespace FiveStar.Loader.Shims.D3D12X;

/// <summary>
/// <c>--indirect-batch</c> (with <c>--indirect-gather</c>): an ExecuteIndirect that is not gathered joins its list's
/// open clamp batch instead of a clamp pass and three transitions of its own. The first such ExecuteIndirect after a
/// point where arguments may change opens the batch there: one pass of <see cref="IndirectBatchProgram"/> over a job
/// table in the user-data ring (<see cref="UserDataRing.Reserve"/>), then one barrier into the argument fetch. Later
/// ExecuteIndirects add their records to the table on the CPU while the list is recorded (it is complete before the
/// GPU runs the list) and execute from their slots in the batch's scratch piece. The batch ends at a barrier or cache
/// flush after work that may write memory (as gathering does, <see cref="IndirectGather"/>, but counted from the last
/// barrier, not the segment's start), at any memory wait and at a new segment or recording; a memory write of a known
/// range recorded while it is open holds back only an ExecuteIndirect whose arguments may overlap it. An
/// ExecuteIndirect opens a new batch at its own place when the table or the piece is full, or when its argument or
/// count buffer is none of the pass's four sources. An ExecuteIndirect with a count buffer is clamped alone (the program
/// still zeroes records past a count it is given). The
/// budget is the recording's counter of <see cref="IndirectClamp"/>, unchanged.
/// </summary>
internal static unsafe class IndirectBatch
{
    /// <summary>Jobs (records) in one table: 16 bytes of header and 255 jobs fill 4 KiB, eight ring blocks.</summary>
    public const uint MaxJobs = 255;

    /// <summary>Bytes of a batch's piece of the scratch: 255 records of up to 32 bytes.</summary>
    public const ulong PieceBytes = 8 << 10;

    private const uint TableBlocks = (IndirectBatchProgram.HeaderBytes + (MaxJobs * IndirectBatchProgram.JobBytes)) / UserDataRing.BlockBytes;
    private const ulong SlotAlignment = 16;
    private const uint WordBytes = 4;
    private const uint WordsPerJob = IndirectBatchProgram.JobBytes / WordBytes;
    private const uint MaxStrideWords = ushort.MaxValue;

    /// <summary>A job's words: its head, its offset in the source, its slot's offset in the scratch, the count's offset.</summary>
    private const int HeadWord = 0;
    private const int FromWord = 1;
    private const int SlotWord = 2;
    private const int CountWord = 3;

    /// <summary>ID3D12Resource::GetDesc, a struct returned through a pointer.</summary>
    private const int ResourceGetDescSlot = 10;

    /// <summary>ID3D12GraphicsCommandList: Dispatch, SetPipelineState, SetComputeRootSignature, SetComputeRoot32BitConstants, SetComputeRootShaderResourceView, SetComputeRootUnorderedAccessView.</summary>
    private const int DispatchSlot = 14;
    private const int SetPipelineSlot = 25;
    private const int SetRootSignatureSlot = 29;
    private const int SetConstantsSlot = 35;
    private const int SetShaderResourceSlot = 39;
    private const int SetUnorderedAccessSlot = 41;

    private static readonly long[] Opens = new long[Enum.GetValues<IndirectBatchOpen>().Length];
    private static long _batched;
    private static long _jobs;
    private static long _countedAlone;

    /// <summary>Whether <c>--indirect-batch</c> is on.</summary>
    public static bool Enabled { get; set; }

    /// <summary>Batch passes recorded so far.</summary>
    public static long Passes => Opens.Sum();

    /// <summary>"; [indirect batch] E ExecuteIndirects (R records) in P passes, opened by ..." for the thread sample; empty when off.</summary>
    public static string Summary
    {
        get
        {
            if (!Enabled)
            {
                return string.Empty;
            }

            var reasons = Enum.GetValues<IndirectBatchOpen>().Select(reason => $"{reason} {Interlocked.Read(ref Opens[(int)reason])}");

            return $"; [indirect batch] {Interlocked.Read(ref _batched)} ExecuteIndirects of those not gathered ({Interlocked.Read(ref _jobs)} records) clamped in {Passes} batch passes, opened by {string.Join(", ", reasons)}; {Interlocked.Read(ref _countedAlone)} with a count buffer clamped alone";
        }
    }

    /// <summary>
    /// Adds the records of the ExecuteIndirect of <paramref name="call"/> (desktop <paramref name="arguments"/> at
    /// <paramref name="offset"/>, count buffer <paramref name="count"/> at <paramref name="countOffset"/> or none) to the
    /// list's batch, opening one here when needed (<paramref name="opened"/>: the pass replaced the list's compute root
    /// signature and pipeline); the scratch and slot to execute from. False when it cannot be batched (it is clamped alone).
    /// </summary>
    [HotPath]
    public static bool Try(XboxObject* self, IndirectCall call, nint arguments, ulong offset, nint count, ulong countOffset, out nint buffer, out ulong slot, out bool opened)
    {
        buffer = 0;
        slot = 0;
        opened = false;

        if (!Enabled || !Clampable(call, arguments, offset, count, countOffset))
        {
            return false;
        }

        // A count buffer is clamped alone, right before its execution, as gathering never takes one: the GPU writes
        // counts (culling, appends), and with the count read at the batch's pass the bodies of w400 ran empty.
        if (count != 0)
        {
            Interlocked.Increment(ref _countedAlone);

            return false;
        }

        var batch = ListIndirectBatch.Of(self);

        if (!Fits(batch, call, arguments, count, out var reason))
        {
            if (!Open(self, call.Kind == IndirectClamp.DispatchKind, arguments, count, reason))
            {
                return false;
            }

            opened = true;
        }

        slot = Append(batch, call, arguments, offset, count, countOffset);
        buffer = batch->Buffer;
        Interlocked.Increment(ref _batched);

        return true;
    }

    /// <summary>Ends the list's open batch: its piece goes back to what the next clamp's barriers expect (<see cref="ClampBarriers.AfterExecute"/>).</summary>
    [HotPath]
    public static void End(XboxObject* self)
    {
        var batch = ListIndirectBatch.Of(self);

        if (batch->Open == 0)
        {
            return;
        }

        batch->Open = 0;
        ClampBarriers.AfterExecute(self->Inner, batch->Buffer);
    }

    /// <summary>A new recording or segment, a desktop list of its own: the open batch is dropped without a barrier.</summary>
    [HotPath]
    public static void Drop(XboxObject* self)
    {
        ListIndirectBatch.Of(self)->Open = 0;
    }

    /// <summary>
    /// A memory write of <paramref name="bytes"/> at console address <paramref name="address"/> while a batch is open: an
    /// ExecuteIndirect whose arguments may overlap it opens a new batch after it; past
    /// <see cref="ListIndirectBatch.MaxWrites"/> writes, or one at an unknown address, the batch ends.
    /// </summary>
    [HotPath]
    public static void WroteMemory(XboxObject* self, ulong address, ulong bytes)
    {
        var batch = ListIndirectBatch.Of(self);

        if (!Enabled || batch->Open == 0)
        {
            return;
        }

        if (address == 0 || bytes == 0 || batch->WriteCount >= ListIndirectBatch.MaxWrites)
        {
            End(self);

            return;
        }

        batch->WriteStarts[batch->WriteCount] = address;
        batch->WriteEnds[batch->WriteCount] = address + bytes;
        batch->WriteCount++;
    }

    /// <summary>Whether the ExecuteIndirect fits the pass's job format and bounds: a kind the clamp takes, at most 128 records, offsets in 4 GB.</summary>
    [HotPath]
    private static bool Clampable(IndirectCall call, nint arguments, ulong offset, nint count, ulong countOffset)
    {
        ulong bytes = (ulong)call.Stride * call.MaxCount;

        if (arguments == 0 || call.Kind < 0 || call.MaxCount == 0 || call.MaxCount > IndirectBatchProgram.MaxIndex + 1 || call.MaxCount > MaxJobs)
        {
            return false;
        }

        if (call.Stride == 0 || call.Stride % WordBytes != 0 || call.Stride / WordBytes > MaxStrideWords || bytes > PieceBytes)
        {
            return false;
        }

        if (offset + bytes > uint.MaxValue || offset + bytes > Width(arguments))
        {
            return false;
        }

        return count == 0 || (countOffset + WordBytes <= uint.MaxValue && countOffset + WordBytes <= Width(count));
    }

    /// <summary>Whether the ExecuteIndirect can join the open batch; else why a new one opens (<paramref name="reason"/>).</summary>
    [HotPath]
    private static bool Fits(ListIndirectBatch* batch, IndirectCall call, nint arguments, nint count, out IndirectBatchOpen reason)
    {
        reason = IndirectBatchOpen.Stretch;

        if (batch->Open == 0)
        {
            return false;
        }

        if (batch->Jobs + call.MaxCount > MaxJobs)
        {
            reason = IndirectBatchOpen.TableFull;

            return false;
        }

        if (batch->PieceUsed + ((ulong)call.Stride * call.MaxCount) > PieceBytes)
        {
            reason = IndirectBatchOpen.PieceFull;

            return false;
        }

        if (SourceSlot(batch, arguments) < 0 || (count != 0 && SourceSlot(batch, count) < 0))
        {
            reason = IndirectBatchOpen.Source;

            return false;
        }

        reason = IndirectBatchOpen.Write;

        return !Overlaps(batch, call.ArgumentsAt, (ulong)call.Stride * call.MaxCount, count != 0);
    }

    /// <summary>Whether arguments at console address <paramref name="at"/> may overlap a memory write since the batch opened; unknown addresses and count buffers overlap any.</summary>
    [HotPath]
    private static bool Overlaps(ListIndirectBatch* batch, ulong at, ulong bytes, bool counted)
    {
        if (batch->WriteCount == 0)
        {
            return false;
        }

        if (at == 0 || counted)
        {
            return true;
        }

        for (int i = 0; i < batch->WriteCount; i++)
        {
            if (at < batch->WriteEnds[i] && batch->WriteStarts[i] < at + bytes)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Opens a batch at the list's current place: ends the open one, takes a scratch piece and a ring table, makes the
    /// argument and count buffers sources and records the pass. False when the program, the piece or the ring is missing.
    /// </summary>
    [HotPath]
    private static bool Open(XboxObject* self, bool dispatch, nint arguments, nint count, IndirectBatchOpen reason)
    {
        End(self);

        var ring = ShimContext.Runtime.D3D12X.Bindings.Ring;
        nint device = ShimContext.Runtime.D3D12X.Objects.DesktopDevice;

        if (ring is null || !IndirectBatchProgram.Ready(device, out nint root, out nint pipeline))
        {
            return false;
        }

        if (!IndirectClamp.Piece(self, PieceBytes, out nint scratch, out ulong piece, out ulong scratchBytes, out ulong budget, out bool zero) || scratchBytes > uint.MaxValue)
        {
            return false;
        }

        ulong table = ring.Reserve(TableBlocks, out byte* data);
        *(uint*)data = 0;

        var batch = ListIndirectBatch.Of(self);
        (batch->Open, batch->Jobs, batch->WriteCount) = (1, 0, 0);
        (batch->Buffer, batch->BufferBytes, batch->Piece, batch->PieceUsed) = (scratch, scratchBytes, piece, 0);
        batch->Table = (nint)data;
        int kept = Bind(batch, arguments, -1);

        if (count != 0)
        {
            Bind(batch, count, kept);
        }

        Record(self, root, pipeline, table, budget, zero);
        GpuTimings.Work((nint)self, self->Inner, dispatch, ListActivity.Of(self)->Pipeline);
        Interlocked.Increment(ref Opens[(int)reason]);

        return true;
    }

    /// <summary>
    /// The pass: the scratch made writable, our root signature and pipeline (the list's own go back after it,
    /// <see cref="CommandListBindings.NoRoot"/>), the budget's zero pass for the buffer's first clamp, one dispatch as
    /// wide as the table, then the barrier into the argument fetch.
    /// </summary>
    [HotPath]
    private static void Record(XboxObject* self, nint root, nint pipeline, ulong table, ulong budget, bool zero)
    {
        var batch = ListIndirectBatch.Of(self);
        nint list = self->Inner;

        GpuTimings.Work((nint)self, list, compute: true, GpuTimings.IndirectClampPipeline);
        ClampBarriers.BeforeClamp(list, batch->Buffer, zero);
        ((delegate* unmanaged<nint, nint, void>)ComObject.Slot(list, SetRootSignatureSlot))(list, root);
        CommandListBindings.Of(self)->ComputeDesktopRoot = CommandListBindings.NoRoot;
        ((delegate* unmanaged<nint, nint, void>)ComObject.Slot(list, SetPipelineSlot))(list, pipeline);

        uint* constants = stackalloc uint[(int)IndirectBatchProgram.Constants];
        Views(list, batch, table, budget, constants);
        constants[IndirectBatchProgram.JobsConstant] = MaxJobs;
        constants[IndirectBatchProgram.ScratchBytesConstant] = (uint)batch->BufferBytes;

        if (zero)
        {
            constants[IndirectBatchProgram.ZeroConstant] = 1;
            Dispatch(list, constants, 1);
            ClampBarriers.AfterZero(list, batch->Buffer);
            constants[IndirectBatchProgram.ZeroConstant] = 0;
        }

        Dispatch(list, constants, IndirectBatchProgram.Groups(MaxJobs));
        ClampBarriers.AfterClamp(list, batch->Buffer);
    }

    /// <summary>The root views: each source (the table where a slot has none) with its size in <paramref name="constants"/>, the table, the scratch, the budget.</summary>
    [HotPath]
    private static void Views(nint list, ListIndirectBatch* batch, ulong table, ulong budget, uint* constants)
    {
        var view = (delegate* unmanaged<nint, uint, ulong, void>)ComObject.Slot(list, SetShaderResourceSlot);

        for (int i = 0; i < ListIndirectBatch.Sources; i++)
        {
            nint source = (nint)batch->SourceBuffers[i];
            ulong address = table;
            ulong bytes = 0;

            if (source != 0)
            {
                address = DesktopCalls.GpuAddress(source);
                bytes = Width(source);
            }

            view(list, IndirectBatchProgram.FirstSourceParameter + (uint)i, address);
            constants[IndirectBatchProgram.SourceBytesConstant + i] = Words(bytes);
        }

        view(list, IndirectBatchProgram.TableParameter, table);
        var writable = (delegate* unmanaged<nint, uint, ulong, void>)ComObject.Slot(list, SetUnorderedAccessSlot);
        writable(list, IndirectBatchProgram.ScratchParameter, DesktopCalls.GpuAddress(batch->Buffer));
        writable(list, IndirectBatchProgram.BudgetParameter, budget);
    }

    [HotPath]
    private static void Dispatch(nint list, uint* constants, uint groups)
    {
        ((delegate* unmanaged<nint, uint, uint, uint*, uint, void>)ComObject.Slot(list, SetConstantsSlot))(list, IndirectBatchProgram.ConstantsParameter, IndirectBatchProgram.Constants, constants, 0);
        ((delegate* unmanaged<nint, uint, uint, uint, void>)ComObject.Slot(list, DispatchSlot))(list, groups, 1, 1);
    }

    /// <summary>The ExecuteIndirect's records as jobs of the table (written only: write-combined memory); its first slot.</summary>
    [HotPath]
    private static ulong Append(ListIndirectBatch* batch, IndirectCall call, nint arguments, ulong offset, nint count, ulong countOffset)
    {
        uint head = (uint)SourceSlot(batch, arguments) | ((uint)call.Kind << IndirectBatchProgram.KindShift) | ((call.Stride / WordBytes) << IndirectBatchProgram.StrideShift);

        if (count != 0)
        {
            head |= ((uint)SourceSlot(batch, count) << IndirectBatchProgram.CountSourceShift) | (1u << IndirectBatchProgram.CountedShift);
        }

        ulong first = batch->Piece + batch->PieceUsed;
        uint* jobs = (uint*)((byte*)batch->Table + IndirectBatchProgram.HeaderBytes) + ((uint)batch->Jobs * WordsPerJob);

        for (uint i = 0; i < call.MaxCount; i++)
        {
            ulong record = (ulong)i * call.Stride;
            uint* job = jobs + (i * WordsPerJob);
            job[HeadWord] = head | (i << IndirectBatchProgram.IndexShift);
            job[FromWord] = (uint)(offset + record);
            job[SlotWord] = (uint)(first + record);
            job[CountWord] = (uint)countOffset;
        }

        batch->Jobs += (int)call.MaxCount;
        *(uint*)batch->Table = (uint)batch->Jobs;
        batch->PieceUsed = (batch->PieceUsed + ((ulong)call.Stride * call.MaxCount) + SlotAlignment - 1) & ~(SlotAlignment - 1);
        Interlocked.Add(ref _jobs, call.MaxCount);

        return first;
    }

    /// <summary>The slot of <paramref name="buffer"/> among the batch's sources; -1 for none.</summary>
    [HotPath]
    private static int SourceSlot(ListIndirectBatch* batch, nint buffer)
    {
        for (int i = 0; i < ListIndirectBatch.Sources; i++)
        {
            if (batch->SourceBuffers[i] == buffer)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Makes <paramref name="buffer"/> a source, in the next slot round unless it is one (never slot <paramref name="kept"/>); its slot.</summary>
    [HotPath]
    private static int Bind(ListIndirectBatch* batch, nint buffer, int kept)
    {
        int slot = SourceSlot(batch, buffer);

        if (slot >= 0)
        {
            return slot;
        }

        if (batch->NextSource == kept)
        {
            batch->NextSource = (batch->NextSource + 1) % ListIndirectBatch.Sources;
        }

        slot = batch->NextSource;
        batch->SourceBuffers[slot] = buffer;
        batch->NextSource = (slot + 1) % ListIndirectBatch.Sources;

        return slot;
    }

    /// <summary>A size as the program's 32-bit bound: past 4 GB, 4 GB less a word.</summary>
    [HotPath]
    private static uint Words(ulong bytes)
    {
        if (bytes > uint.MaxValue)
        {
            return uint.MaxValue - WordBytes + 1;
        }

        return (uint)bytes;
    }

    /// <summary>The byte size of desktop buffer <paramref name="resource"/>.</summary>
    [HotPath]
    private static ulong Width(nint resource)
    {
        ResourceDescription read;
        _ = ((delegate* unmanaged<nint, ResourceDescription*, ResourceDescription*>)ComObject.Slot(resource, ResourceGetDescSlot))(resource, &read);

        return read.Width;
    }
}
