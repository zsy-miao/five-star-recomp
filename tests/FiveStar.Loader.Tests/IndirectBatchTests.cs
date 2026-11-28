// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Shims;
using FiveStar.Loader.Shims.D3D12X;
using FiveStar.Loader.Shims.D3D12X.Binding;
using FiveStar.Loader.Tests.Support;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>
/// --indirect-batch: ExecuteIndirects that are not gathered share one clamp pass over a job table filled while the list
/// is recorded, with the clamp's bounds, on the default adapter; a barrier after work that may write, a memory wait, an
/// overlapping memory write or a new source buffer opens the next batch.
/// </summary>
public sealed unsafe class IndirectBatchTests
{
    private const int CloseSlot = 9;
    private const int CopyBufferRegionSlot = 15;
    private const int MapSlot = 8;
    private const uint Stride = 12;
    private const uint Records = 3;
    private const ulong ArgumentsAt = 0x2_0000_1000;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Executions_after_the_first_join_its_pass_and_all_records_are_clamped(bool enhanced)
    {
        using var test = TestRuntime.Create();
        Enable(true);
        bool before = ClampBarriers.Enhanced;

        try
        {
            var device = new XboxDevice(test);
            var objects = ShimContext.Runtime.D3D12X.Objects;

            // Enhanced barriers where the adapter has them (--clamp-enhanced-barriers), else the legacy transitions again.
            ClampBarriers.Requested = enhanced;
            ClampBarriers.Attach(objects.DesktopDevice);

            nint upload = device.Buffer(XboxDevice.UploadHeap, 256, XboxDevice.GenericRead);
            uint[] asked = [4, 5, 6, uint.MaxValue, uint.MaxValue, uint.MaxValue, 70000, 2, 1, 7, 8, 9, 1, 1, 1, 2, 2, 2, 1];
            asked.CopyTo(new Span<uint>(Map(upload), asked.Length));
            nint source = objects.Unwrap(upload);

            var list = (XboxObject*)device.DirectList();
            IndirectGather.Reset(list);
            var call = new IndirectCall(0x5180, IndirectClamp.DispatchKind, Stride, Records);
            Assert.True(IndirectBatch.Try(list, call, source, 0, 0, 0, out nint first, out ulong firstSlot, out bool opened));
            Assert.True(opened);

            // The second reads the third record and two more; one with a count buffer is clamped alone (w400's bodies).
            Assert.True(IndirectBatch.Try(list, call, source, Stride * 2, 0, 0, out nint second, out ulong secondSlot, out opened));
            Assert.False(opened);
            Assert.False(IndirectBatch.Try(list, call with { MaxCount = 2 }, source, Stride * 4, source, Stride * 6, out _, out _, out _));

            Assert.Equal(first, second);
            IndirectBatch.End(list);
            ClampBarriers.BeforeCopy(list->Inner, first);

            nint readback = device.Buffer(XboxDevice.ReadbackHeap, 256, XboxDevice.CopyDest);
            Copy(list, objects.Unwrap(readback), 0, first, firstSlot, Stride * Records);
            Copy(list, objects.Unwrap(readback), Stride * Records, first, secondSlot, Stride * Records);
            Assert.Equal(0, ((delegate* unmanaged<nint, int>)XboxDevice.Vtable((nint)list)[CloseSlot])((nint)list));
            device.Run((nint)list);

            uint* clamped = Map(readback);
            Assert.Equal([4u, 5u, 6u, 0u, 0u, 0u, 65535u, 2u, 1u], new Span<uint>(clamped, 9).ToArray());
            Assert.Equal([65535u, 2u, 1u, 7u, 8u, 9u, 1u, 1u, 1u], new Span<uint>(clamped + 9, 9).ToArray());
        }
        finally
        {
            Enable(false);
            ClampBarriers.Requested = false;
            ClampBarriers.Enhanced = before;
        }
    }

    [Fact]
    public void A_barrier_after_writes_a_wait_an_overlapping_write_or_a_new_source_opens_the_next_batch()
    {
        using var test = TestRuntime.Create();
        Enable(true);

        try
        {
            var device = new XboxDevice(test);
            var objects = ShimContext.Runtime.D3D12X.Objects;
            nint source = objects.Unwrap(device.Buffer(XboxDevice.UploadHeap, 256, XboxDevice.GenericRead));
            nint other = objects.Unwrap(device.Buffer(XboxDevice.UploadHeap, 256, XboxDevice.GenericRead));

            var list = (XboxObject*)device.DirectList();
            IndirectGather.Reset(list);
            var call = new IndirectCall(0x5181, IndirectClamp.DispatchKind, Stride, 1, ArgumentsAt);

            Assert.True(Opens(list, call, source));

            // A barrier with no write since the last one keeps the batch; one after a write ends it.
            IndirectGather.Synced(list, IndirectGatherCloser.UavBarrier);
            Assert.False(Opens(list, call, source));
            IndirectGather.Wrote(list, IndirectGatherMiss.AfterCopy);
            IndirectGather.Synced(list, IndirectGatherCloser.UavBarrier);
            Assert.Equal(0, ListIndirectBatch.Of(list)->Open);
            Assert.True(Opens(list, call, source));

            // A memory write holds back only arguments it overlaps.
            IndirectGather.WroteMemory(list, IndirectGatherMiss.AfterMemoryWrite, ArgumentsAt + 0x100, sizeof(uint));
            Assert.False(Opens(list, call, source));
            IndirectGather.WroteMemory(list, IndirectGatherMiss.AfterMemoryWrite, ArgumentsAt + sizeof(uint), sizeof(uint));
            Assert.True(Opens(list, call, source));

            // A buffer none of the sources is opens a batch; one already a source joins.
            Assert.True(Opens(list, call, other));
            Assert.False(Opens(list, call, source));

            IndirectGather.WaitRecorded(list);
            Assert.Equal(0, ListIndirectBatch.Of(list)->Open);
            Assert.True(Opens(list, call, source));
            IndirectBatch.End(list);
        }
        finally
        {
            Enable(false);
        }
    }

    private static bool Opens(XboxObject* list, IndirectCall call, nint source)
    {
        Assert.True(IndirectBatch.Try(list, call, source, 0, 0, 0, out _, out _, out bool opened));

        return opened;
    }

    private static void Enable(bool on)
    {
        IndirectGather.Enabled = on;
        IndirectBatch.Enabled = on;
    }

    private static void Copy(XboxObject* list, nint destination, ulong destinationOffset, nint source, ulong sourceOffset, ulong bytes)
    {
        nint inner = list->Inner;
        ((delegate* unmanaged<nint, nint, ulong, nint, ulong, ulong, void>)XboxDevice.Vtable(inner)[CopyBufferRegionSlot])(inner, destination, destinationOffset, source, sourceOffset, bytes);
    }

    private static uint* Map(nint resource)
    {
        void* data = null;
        Assert.Equal(0, ((delegate* unmanaged<nint, uint, void*, void**, int>)XboxDevice.Vtable(resource)[MapSlot])(resource, 0, null, &data));

        return (uint*)data;
    }
}
