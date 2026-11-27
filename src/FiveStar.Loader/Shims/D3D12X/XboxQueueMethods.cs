// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using FiveStar.Loader.Diagnostics;
using FiveStar.Loader.Native;
using FiveStar.Loader.Shims.D3D12X.DebugLayer;
using FiveStar.Loader.Shims.D3D12X.Present;
using FiveStar.Loader.Shims.D3D12X.Resources;

namespace FiveStar.Loader.Shims.D3D12X;

/// <summary>Command-queue slots that take an array of wrapped lists: per-frame, so allocation-free.</summary>
internal static unsafe class XboxQueueMethods
{
    /// <summary>X slot 35, read as ExecuteCommandListsX: (count, lists, frame object, flags 0 or 3).</summary>
    public const int ExecuteCommandListsXSlot = 35;

    /// <summary>X slot 12: desktop ExecuteCommandLists two slots later, like the queue's other desktop methods.</summary>
    public const int ExecuteCommandListsSlot = 12;

    /// <summary>ID3D12CommandQueue::ExecuteCommandLists in the desktop vtable (d3d12.h).</summary>
    private const int DesktopExecuteSlot = 10;

    /// <summary>Lists unwrapped per desktop call; the game submits at most 256 at once (0x142481e90).</summary>
    private const int Batch = 256;

    /// <summary>
    /// Slot 35. The frame object and the flags have no desktop counterpart: the lists are submitted in order and the
    /// call succeeds.
    /// </summary>
    [HotPath]
    [UnmanagedCallersOnly]
    public static int ExecuteCommandListsX(XboxObject* self, uint count, nint* lists, nint frame, uint flags)
    {
        try
        {
            if (QueueTrace.Enabled)
            {
                QueueTrace.Execute(self, count, frame, flags);
            }

            Execute(self, count, lists);

            return HResult.Ok;
        }
        catch (Exception exception)
        {
            UnmanagedGuard.Caught(exception, "XboxQueueMethods.ExecuteCommandListsX");

            return HResult.Fail;
        }
    }

    /// <summary>Slot 12, desktop ExecuteCommandLists(count, lists).</summary>
    [HotPath]
    [UnmanagedCallersOnly]
    public static void ExecuteCommandLists(XboxObject* self, uint count, nint* lists)
    {
        try
        {
            if (QueueTrace.Enabled)
            {
                QueueTrace.Execute(self, count, 0, 0);
            }

            // After a removal the work would only pile up against a dead device (DeviceLoss ends the process).
            if (DeviceLoss.Lost)
            {
                return;
            }

            Execute(self, count, lists);
        }
        catch (Exception exception)
        {
            UnmanagedGuard.Caught(exception, "XboxQueueMethods.ExecuteCommandLists");
        }
    }

    [HotPath]
    private static void Execute(XboxObject* self, uint count, nint* lists)
    {
        if (lists == null)
        {
            return;
        }

        long start = FrameLog.Start();
        var d3d12 = ShimContext.Runtime.D3D12X;
        var objects = d3d12.Objects;
        Present.GpuKeepAwake.Sent(objects.DesktopDevice);

        d3d12.Textures.RefreshWatched(objects.DesktopDevice, d3d12.Presenter.Presents);
        d3d12.Textures.RefreshWritten(objects.DesktopDevice);
        FrameLog.AddRefresh(start);

        d3d12.Residency?.Poll(objects.DesktopDevice, d3d12.Bindings.Window.WindowFlags, d3d12.Presenter.Presents);
        d3d12.PlacedResources.Addresses.Advance(d3d12.Presenter.Presents);

        nint inner = self->Inner;
        d3d12.Textures.Uploader.OrderBefore(inner);

        if (DepthTwinSides.Any)
        {
            ExecuteInTwinOrder(self, count, lists);
        }
        else
        {
            ExecuteRange(self, count, lists);
        }

        FrameLog.AddSubmit(start);
    }

    /// <summary>
    /// <c>--depth-twins</c>: the lists in order, each after the copies between depth twins' sides it needs as the lists
    /// before it left them (<see cref="DepthTwinSides.Plan"/>, run by <see cref="DepthTwinPrelude"/>).
    /// </summary>
    private static void ExecuteInTwinOrder(XboxObject* self, uint count, nint* lists)
    {
        var copies = new List<DepthTwinSides.Copy>();
        nint device = ShimContext.Runtime.D3D12X.Objects.DesktopDevice;
        uint start = 0;

        for (uint i = 0; i < count; i++)
        {
            copies.Clear();
            DepthTwinSides.Plan(lists[i], copies);

            if (copies.Count == 0)
            {
                continue;
            }

            ExecuteRange(self, i - start, lists + start);
            long mark = FrameLog.Start();
            DepthTwinPrelude.Run(device, self->Inner, copies);
            FrameLog.AddPrelude(mark);
            start = i;
        }

        ExecuteRange(self, count - start, lists + start);
    }

    /// <summary>The lists in batches of <see cref="Batch"/>, after the waits and segments they carry.</summary>
    [HotPath]
    private static void ExecuteRange(XboxObject* self, uint count, nint* lists)
    {
        var d3d12 = ShimContext.Runtime.D3D12X;
        var objects = d3d12.Objects;
        var execute = (delegate* unmanaged<nint, uint, nint*, void>)ComObject.Slot(self->Inner, DesktopExecuteSlot);
        nint* desktop = stackalloc nint[Batch];

        for (uint first = 0; first < count; first += Batch)
        {
            uint size = count - first;

            if (size > Batch)
            {
                size = Batch;
            }

            for (uint i = 0; i < size; i++)
            {
                nint list = lists[first + i];
                desktop[i] = objects.Unwrap(list);

                if (desktop[i] != list)
                {
                    ListSubmissions.WaitIfRunning(list, (XboxQueueObject*)self);
                    MemoryWaitWriters.Submitted(list, (XboxQueueObject*)self);
                }
            }

            RetireWaits.BeforeExecute((XboxQueueObject*)self, lists + first, size);
            Submit(self, lists + first, desktop, size, execute);
            d3d12.DrawTrace?.Submitted(((XboxQueueObject*)self)->Number, lists + first, size);
        }
    }

    /// <summary>
    /// Runs the lists in order. A list with memory waits or segments (<see cref="MemoryWaits"/>, <see cref="ListSplits"/>)
    /// or gathered indirect records (<see cref="IndirectGather"/>) goes alone after the lists before it: each of its
    /// segments runs after the waits that precede it and the prelude that clamps its records.
    /// </summary>
    [HotPath]
    private static void Submit(XboxObject* self, nint* lists, nint* desktop, uint count, delegate* unmanaged<nint, uint, nint*, void> execute)
    {
        var objects = ShimContext.Runtime.D3D12X.Objects;
        var queue = (XboxQueueObject*)self;
        nint inner = self->Inner;
        uint start = 0;

        for (uint i = 0; i <= count; i++)
        {
            bool waits = i < count && desktop[i] != lists[i] && (MemoryWaits.HasWaits(lists[i]) || IndirectGather.Has(lists[i]));

            if (i < count && !waits)
            {
                continue;
            }

            if (i > start)
            {
                execute(inner, i - start, desktop + start);
                ListSubmissions.Submitted(queue, lists + start, i - start, objects);
            }

            if (waits)
            {
                SubmitSegments(queue, lists[i], desktop[i], execute);
                ListSubmissions.Submitted(queue, lists + i, 1, objects);
            }

            start = i + 1;
        }
    }

    /// <summary>The segments of <paramref name="list"/> (a wrapper whose desktop list is <paramref name="last"/>), each after its waits.</summary>
    [HotPath]
    private static void SubmitSegments(XboxQueueObject* queue, nint list, nint last, delegate* unmanaged<nint, uint, nint*, void> execute)
    {
        var segments = ListSegments.Of((XboxObject*)list);
        nint inner = queue->Header.Inner;

        for (int s = 0; s <= segments->Count; s++)
        {
            MemoryWaits.Before(queue, list, s);
            IndirectGather.Before(queue, list, s);
            nint segment = segments->Segment(s, last);
            execute(inner, 1, &segment);
        }
    }
}
