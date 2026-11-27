// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using FiveStar.Graphics.Pipelines;
using FiveStar.Loader.Diagnostics;
using FiveStar.Loader.Native;
using FiveStar.Loader.Shims.D3D12X.DebugLayer;
using FiveStar.Loader.Shims.D3D12X.Resources;

namespace FiveStar.Loader.Shims.D3D12X.Binding;

/// <summary>
/// The command-list slots that bind (docs/d3d12x-shim.md, Binding). Slots 33 to 46 are the desktop root methods four
/// slots later; slot 32 is SetDescriptorHeaps (call at 0x144e4cac2 with the main view and sampler heaps). Root
/// arguments go to the list's user data and reach the GPU as one root CBV (or root constants, <see cref="UserDataConstants"/>)
/// per draw or dispatch that follows a change.
/// </summary>
internal static unsafe class CommandListBindingMethods
{
    public const int CreateRootSignatureSlot = 16;
    public const int ResetSlot = 10;
    public const int DrawInstancedSlot = 12;
    public const int DrawIndexedInstancedSlot = 13;
    public const int DispatchSlot = 14;
    public const int SetDescriptorHeapsSlot = 32;
    public const int SetComputeRootSignatureSlot = 33;
    public const int SetGraphicsRootSignatureSlot = 34;
    public const int SetComputeRootDescriptorTableSlot = 35;
    public const int SetComputeRoot32BitConstantSlot = 37;
    public const int SetComputeRoot32BitConstantsSlot = 39;
    public const int FirstRootAddressSlot = 41;
    public const int LastRootAddressSlot = 45;
    public const int ExecuteIndirectSlot = 61;

    /// <summary>Command-list slot 139: Reset with a third argument (0x142474cce).</summary>
    public const int ResetXSlot = 139;

    /// <summary>ID3D12GraphicsCommandList in the desktop vtable (d3d12.h).</summary>
    private const int DesktopReset = 10;
    private const int DesktopDrawInstanced = 12;
    private const int DesktopDrawIndexedInstanced = 13;
    private const int DesktopDispatch = 14;
    private const int DesktopSetDescriptorHeaps = 28;
    private const int DesktopSetComputeRootSignature = 29;
    private const int DesktopSetGraphicsRootSignature = 30;
    private const int DesktopSetComputeRootDescriptorTable = 31;
    private const int DesktopSetGraphicsRootDescriptorTable = 32;
    private const int DesktopSetComputeRoot32BitConstants = 35;
    private const int DesktopSetGraphicsRoot32BitConstants = 36;
    private const int DesktopSetComputeRootConstantBufferView = 37;
    private const int DesktopSetGraphicsRootConstantBufferView = 38;
    private const int DesktopExecuteIndirect = 59;
    private const int DesktopSetPipelineState = 25;

    /// <summary>Root parameters of BindlessRootSignature: the user data and the mesh fallback table.</summary>
    private const uint UserDataParameter = BindlessRootSignature.UserDataParameter;
    private const uint FallbackTableParameter = BindlessRootSignature.FallbackTableParameter;

    /// <summary>The game binds at most a view and a sampler heap.</summary>
    private const int MaxHeaps = 2;

    private static XboxBindings Bindings => ShimContext.Runtime.D3D12X.Bindings;

    /// <summary>Device slot 16: the desktop root signature, whose layout is kept for the lists.</summary>
    [UnmanagedCallersOnly]
    public static int CreateRootSignature(XboxObject* self, uint nodeMask, void* blob, nuint size, Guid* iid, nint* rootSignature)
    {
        try
        {
            nint inner = self->Inner;
            var create = (delegate* unmanaged<nint, uint, void*, nuint, Guid*, nint*, int>)ComObject.Slot(inner, CreateRootSignatureSlot);
            int hr = create(inner, nodeMask, blob, size, iid, rootSignature);

            if (!HResult.Failed(hr) && rootSignature != null && blob != null && !Bindings.Layouts.Register(*rootSignature, new ReadOnlySpan<byte>(blob, (int)size)))
            {
                Bindings.Warnings.Warn("root-layout", "[d3d12_xs] CreateRootSignature: a blob without a readable RTS0 part; its root arguments are dropped");
            }

            return hr;
        }
        catch (Exception exception)
        {
            UnmanagedGuard.Caught(exception, "CommandListBindingMethods.CreateRootSignature");

            return HResult.Fail;
        }
    }

    [HotPath]
    [UnmanagedCallersOnly]
    public static int Reset(XboxObject* self, nint allocator, nint pipelineState)
    {
        try
        {
            return ResetList(self, allocator, pipelineState);
        }
        catch (Exception exception)
        {
            UnmanagedGuard.Caught(exception, "CommandListBindingMethods.Reset");

            return HResult.Fail;
        }
    }

    [HotPath]
    [UnmanagedCallersOnly]
    public static int ResetX(XboxObject* self, nint allocator, nint pipelineState, uint flags)
    {
        try
        {
            return ResetList(self, allocator, pipelineState);
        }
        catch (Exception exception)
        {
            UnmanagedGuard.Caught(exception, "CommandListBindingMethods.ResetX");

            return HResult.Fail;
        }
    }

    /// <summary>Slot 32: the desktop heaps behind the game's heaps.</summary>
    [HotPath]
    [UnmanagedCallersOnly]
    public static void SetDescriptorHeaps(XboxObject* self, uint count, nint* heaps)
    {
        try
        {
            var objects = ShimContext.Runtime.D3D12X.Objects;
            nint* desktop = stackalloc nint[MaxHeaps];
            uint used = count;

            if (used > MaxHeaps)
            {
                used = MaxHeaps;
            }

            for (uint i = 0; i < used; i++)
            {
                desktop[i] = objects.Unwrap(heaps[i]);
            }

            BindHeaps(self, used, desktop);
        }
        catch (Exception exception)
        {
            UnmanagedGuard.Caught(exception, "CommandListBindingMethods.SetDescriptorHeaps");
        }
    }

    [HotPath]
    [UnmanagedCallersOnly]
    public static void SetComputeRootSignature(XboxObject* self, nint rootSignature)
    {
        try
        {
            SetRootSignature(self, rootSignature, true);
        }
        catch (Exception exception)
        {
            UnmanagedGuard.Caught(exception, "CommandListBindingMethods.SetComputeRootSignature");
        }
    }

    [HotPath]
    [UnmanagedCallersOnly]
    public static void SetGraphicsRootSignature(XboxObject* self, nint rootSignature)
    {
        try
        {
            SetRootSignature(self, rootSignature, false);
        }
        catch (Exception exception)
        {
            UnmanagedGuard.Caught(exception, "CommandListBindingMethods.SetGraphicsRootSignature");
        }
    }

    [HotPath]
    [UnmanagedCallersOnly]
    public static void SetComputeRootDescriptorTable(XboxObject* self, uint parameter, ulong handle)
    {
        try
        {
            var bindings = CommandListBindings.Of(self);
            UserData.Write(bindings, true, parameter, 0, TableAddress(bindings, true, parameter, handle));
        }
        catch (Exception exception)
        {
            UnmanagedGuard.Caught(exception, "CommandListBindingMethods.SetComputeRootDescriptorTable");
        }
    }

    [HotPath]
    [UnmanagedCallersOnly]
    public static void SetGraphicsRootDescriptorTable(XboxObject* self, uint parameter, ulong handle)
    {
        try
        {
            var bindings = CommandListBindings.Of(self);
            UserData.Write(bindings, false, parameter, 0, TableAddress(bindings, false, parameter, handle));
        }
        catch (Exception exception)
        {
            UnmanagedGuard.Caught(exception, "CommandListBindingMethods.SetGraphicsRootDescriptorTable");
        }
    }

    [HotPath]
    [UnmanagedCallersOnly]
    public static void SetComputeRoot32BitConstant(XboxObject* self, uint parameter, uint value, uint offset)
    {
        try
        {
            UserData.Write(CommandListBindings.Of(self), true, parameter, offset, value);
        }
        catch (Exception exception)
        {
            UnmanagedGuard.Caught(exception, "CommandListBindingMethods.SetComputeRoot32BitConstant");
        }
    }

    [HotPath]
    [UnmanagedCallersOnly]
    public static void SetGraphicsRoot32BitConstant(XboxObject* self, uint parameter, uint value, uint offset)
    {
        try
        {
            UserData.Write(CommandListBindings.Of(self), false, parameter, offset, value);
        }
        catch (Exception exception)
        {
            UnmanagedGuard.Caught(exception, "CommandListBindingMethods.SetGraphicsRoot32BitConstant");
        }
    }

    [HotPath]
    [UnmanagedCallersOnly]
    public static void SetComputeRoot32BitConstants(XboxObject* self, uint parameter, uint count, uint* values, uint offset)
    {
        try
        {
            WriteConstants(CommandListBindings.Of(self), true, parameter, count, values, offset);
        }
        catch (Exception exception)
        {
            UnmanagedGuard.Caught(exception, "CommandListBindingMethods.SetComputeRoot32BitConstants");
        }
    }

    [HotPath]
    [UnmanagedCallersOnly]
    public static void SetGraphicsRoot32BitConstants(XboxObject* self, uint parameter, uint count, uint* values, uint offset)
    {
        try
        {
            WriteConstants(CommandListBindings.Of(self), false, parameter, count, values, offset);
        }
        catch (Exception exception)
        {
            UnmanagedGuard.Caught(exception, "CommandListBindingMethods.SetGraphicsRoot32BitConstants");
        }
    }

    /// <summary>
    /// Root CBV, SRV or UAV: the console GPU address goes to the parameter's SGPR pair as the game passes it, and the
    /// memory behind it gets a desktop buffer if it has none, so the address map holds it (<see cref="GameMemoryViews"/>).
    /// </summary>
    [HotPath]
    [UnmanagedCallersOnly]
    public static void SetComputeRootAddress(XboxObject* self, uint parameter, ulong address)
    {
        try
        {
            ShimContext.Runtime.D3D12X.GameMemory.TryFind(address, out _);
            TransientPoolWatch.Note(address, ListActivity.Of(self)->Pipeline, true);
            UserData.WriteAddress(CommandListBindings.Of(self), true, parameter, address);
        }
        catch (Exception exception)
        {
            UnmanagedGuard.Caught(exception, "CommandListBindingMethods.SetComputeRootAddress");
        }
    }

    [HotPath]
    [UnmanagedCallersOnly]
    public static void SetGraphicsRootAddress(XboxObject* self, uint parameter, ulong address)
    {
        try
        {
            ShimContext.Runtime.D3D12X.GameMemory.TryFind(address, out _);
            TransientPoolWatch.Note(address, ListActivity.Of(self)->Pipeline, false);
            UserData.WriteAddress(CommandListBindings.Of(self), false, parameter, address);
        }
        catch (Exception exception)
        {
            UnmanagedGuard.Caught(exception, "CommandListBindingMethods.SetGraphicsRootAddress");
        }
    }

    /// <summary>
    /// Before a draw, dispatch or ExecuteIndirect: its pass's timestamp (<c>--gpu-timings</c>; an ExecuteIndirect takes it
    /// after its argument clamp, <paramref name="timed"/> false) and the bound tables' views (<c>--check-view-liveness</c>).
    /// </summary>
    [HotPath]
    private static void BeforeWork(XboxObject* self, bool compute, bool timed = true)
    {
        if (timed)
        {
            GpuTimings.Work((nint)self, self->Inner, compute, ListActivity.Of(self)->Pipeline);
        }

        if (NanScan.Enabled)
        {
            NanScan.Before(self, compute);
        }

        if (ViewTrace.Enabled)
        {
            ViewTrace.Before(self, compute);
        }

        if (ViewLiveness.Enabled)
        {
            ViewLiveness.Check(self, compute);
        }
    }

    /// <summary>An unclamped ExecuteIndirect's pass timestamp, right before it runs (a clamped one's: <see cref="IndirectClamp.Record"/>).</summary>
    [HotPath]
    private static void TimeIndirect(XboxObject* self, int kind)
    {
        GpuTimings.Work((nint)self, self->Inner, kind == IndirectClamp.DispatchKind, ListActivity.Of(self)->Pipeline);
    }

    [HotPath]
    [UnmanagedCallersOnly]
    public static void DrawInstanced(XboxObject* self, uint vertices, uint instances, uint firstVertex, uint firstInstance)
    {
        try
        {
            if (PipelinePending(self, draw: true))
            {
                return;
            }

            // A rectangle-list draw whose variant still builds is skipped; the variant is bound after CopyTwin.
            if (ShimContext.Runtime.D3D12X.Rectangles.Building(self))
            {
                return;
            }

            if (DepthTwins.Any)
            {
                CopyTwin(self);
            }

            Flush(self, false);
            ListActivity.Of(self)->Draws++;
            GpuBreadcrumbs.Before(self, MarkedKind.Draw, vertices, instances, 0);
            BeforeWork(self, false);

            ShimContext.Runtime.D3D12X.Rectangles.BeforeDraw(self);
            ShimContext.Runtime.D3D12X.DrawTrace?.Draw(self, vertices, instances);
            nint inner = self->Inner;

            bool bounds = Pipelines.ClassDepthBounds.Before(self, RectangleListState.Of(self)->Pipeline);
            ((delegate* unmanaged<nint, uint, uint, uint, uint, void>)ComObject.Slot(inner, DesktopDrawInstanced))(inner, vertices, instances, firstVertex, firstInstance);
            AfterBounds(self, bounds);
            ShimContext.Runtime.D3D12X.DrawTrace?.AfterDraw(self);
            IndirectGather.AfterWork(self, false, RectangleListState.Of(self)->Pipeline);
        }
        catch (Exception exception)
        {
            UnmanagedGuard.Caught(exception, "CommandListBindingMethods.DrawInstanced");
        }
    }

    [HotPath]
    [UnmanagedCallersOnly]
    public static void DrawIndexedInstanced(XboxObject* self, uint indices, uint instances, uint firstIndex, int baseVertex, uint firstInstance)
    {
        try
        {
            if (PipelinePending(self, draw: true))
            {
                return;
            }

            // A rectangle-list draw whose variant still builds is skipped; the variant is bound after CopyTwin.
            if (ShimContext.Runtime.D3D12X.Rectangles.Building(self))
            {
                return;
            }

            if (DepthTwins.Any)
            {
                CopyTwin(self);
            }

            Flush(self, false);
            ListActivity.Of(self)->Draws++;
            GpuBreadcrumbs.Before(self, MarkedKind.Draw, indices, instances, 0);
            BeforeWork(self, false);

            ShimContext.Runtime.D3D12X.Rectangles.BeforeDraw(self);
            ShimContext.Runtime.D3D12X.DrawTrace?.Draw(self, indices, instances);
            nint inner = self->Inner;

            bool bounds = Pipelines.ClassDepthBounds.Before(self, RectangleListState.Of(self)->Pipeline);
            ((delegate* unmanaged<nint, uint, uint, uint, int, uint, void>)ComObject.Slot(inner, DesktopDrawIndexedInstanced))(inner, indices, instances, firstIndex, baseVertex, firstInstance);
            AfterBounds(self, bounds);
            ShimContext.Runtime.D3D12X.DrawTrace?.AfterDraw(self);
            IndirectGather.AfterWork(self, false, RectangleListState.Of(self)->Pipeline);
        }
        catch (Exception exception)
        {
            UnmanagedGuard.Caught(exception, "CommandListBindingMethods.DrawIndexedInstanced");
        }
    }

    [HotPath]
    [UnmanagedCallersOnly]
    public static void Dispatch(XboxObject* self, uint x, uint y, uint z)
    {
        try
        {
            if (ComputeChainBroken(self, producer: true))
            {
                return;
            }

            Flush(self, true);
            ListActivity.Of(self)->Dispatches++;
            GpuBreadcrumbs.Before(self, MarkedKind.Dispatch, x, y, z);
            BeforeWork(self, true);
            WorkShapes.Dispatch(ListActivity.Of(self)->Pipeline, x, y, z);

            var trace = ShimContext.Runtime.D3D12X.DrawTrace;
            trace?.Dispatch(self, x, y, z);

            if (DispatchCaptures.Enabled)
            {
                DispatchCaptures.Before(self, x, y, z);
            }

            nint inner = self->Inner;
            ((delegate* unmanaged<nint, uint, uint, uint, void>)ComObject.Slot(inner, DesktopDispatch))(inner, x, y, z);
            trace?.AfterDispatch(self);

            if (DispatchCaptures.Enabled)
            {
                DispatchCaptures.After(self);
            }

            IndirectGather.Dispatched(self, RectangleListState.Of(self)->Pipeline);

            if (RtStandIns.Enabled)
            {
                RtStandIns.AfterDispatch(self, RectangleListState.Of(self)->Pipeline);
            }
        }
        catch (Exception exception)
        {
            UnmanagedGuard.Caught(exception, "CommandListBindingMethods.Dispatch");
        }
    }

    /// <summary>Slot 61: (signature, max count, argument buffer, offset, count buffer, offset) with wrapped objects.</summary>
    [HotPath]
    [UnmanagedCallersOnly]
    public static void ExecuteIndirect(XboxObject* self, nint signature, uint maxCount, nint arguments, ulong argumentOffset, nint count, ulong countOffset)
    {
        try
        {
            bool consumer = EmptyIndirectAfterSkip && CommandListBindings.Of(self)->ProducerSkipped != 0;

            // The signature's kind and stride, looked up once (a dictionary read each time it was asked, six a call).
            var (kind, stride) = CommandSignatures.Clampable(signature);
            bool dispatch = kind == IndirectClamp.DispatchKind;

            if (ComputeChainBroken(self, dispatch))
            {
                return;
            }

            // Its arguments, counts and offsets may come from the skipped pass: stale, so it runs nothing.
            if (consumer)
            {
                ShimContext.Runtime.D3D12X.Pipelines?.StandIns.SkippedAfterChain();

                return;
            }

            FlushForIndirect(self, dispatch);
            ListActivity.Of(self)->Indirect++;
            NoteIndirect(self, signature, maxCount, arguments, argumentOffset, stride, count != 0, dispatch);

            var indirect = new IndirectCall(signature, kind, stride, maxCount, ArgumentAddress(arguments, argumentOffset));

            if (dispatch && DispatchCaptures.Enabled)
            {
                DispatchCaptures.BeforeIndirect(self, arguments, argumentOffset, stride, maxCount, count, countOffset);
            }

            bool bounds = !dispatch && Pipelines.ClassDepthBounds.Before(self, RectangleListState.Of(self)->Pipeline);
            RunIndirect(self, indirect, BufferCopies.Desktop(arguments, ref argumentOffset), argumentOffset, BufferCopies.Desktop(count, ref countOffset), countOffset);
            AfterBounds(self, bounds);
            ShimContext.Runtime.D3D12X.DrawTrace?.AfterIndirect(self);

            if (dispatch && DispatchCaptures.Enabled)
            {
                DispatchCaptures.After(self);
            }

            IndirectGather.AfterWork(self, dispatch, RectangleListState.Of(self)->Pipeline);
        }
        catch (Exception exception)
        {
            UnmanagedGuard.Caught(exception, "CommandListBindingMethods.ExecuteIndirect");
        }
    }

    /// <summary>After a draw that <see cref="Pipelines.ClassDepthBounds"/> set depth bounds for: the game's bounds again.</summary>
    [HotPath]
    private static void AfterBounds(XboxObject* self, bool bounds)
    {
        if (bounds)
        {
            Pipelines.ClassDepthBounds.After(self);
        }
    }

    /// <summary>Before an ExecuteIndirect: its breadcrumb, the checks before work and the draw trace, each only when on.</summary>
    [HotPath]
    private static void NoteIndirect(XboxObject* self, nint signature, uint maxCount, nint arguments, ulong argumentOffset, uint stride, bool counted, bool dispatch)
    {
        var trace = ShimContext.Runtime.D3D12X.DrawTrace;
        ulong address = 0;

        if (GpuBreadcrumbs.Active || trace is not null)
        {
            address = ArgumentAddress(arguments, argumentOffset);
        }

        GpuBreadcrumbs.BeforeIndirect(self, signature, maxCount, address, (uint)argumentOffset);
        BeforeWork(self, dispatch, timed: false);
        trace?.Indirect(self, maxCount, address, stride, counted, dispatch);
    }

    /// <summary>
    /// An ExecuteIndirect over its desktop argument and count buffers: dropped by the null-address check, launched by the
    /// mesh launch stage when the bound pipeline has it (<see cref="MeshLaunch"/>), or run on the clamped copy of its
    /// records (<see cref="IndirectClamp"/>), or as it is when the clamp cannot run.
    /// </summary>
    [HotPath]
    private static void RunIndirect(XboxObject* self, IndirectCall call, nint argumentBuffer, ulong argumentOffset, nint countBuffer, ulong countOffset)
    {
        if (NullAddressCheck.Enabled && IndirectUnsafe(self, call.Signature, call.MaxCount, argumentBuffer, argumentOffset, countBuffer, countOffset))
        {
            return;
        }

        if (ListActivity.Of(self)->Launches != 0 && call.Kind == IndirectClamp.MeshKind)
        {
            Launch(self, call, argumentBuffer, argumentOffset, countBuffer, countOffset);

            return;
        }

        nint signature = ShimContext.Runtime.D3D12X.Objects.Unwrap(call.Signature);

        // --indirect-gather: records nothing earlier in the segment can have written are clamped before it runs.
        if (IndirectGather.Try(self, call, argumentBuffer, argumentOffset, countBuffer, out nint gathered, out ulong slot))
        {
            TimeIndirect(self, call.Kind);
            Shapes(self, call, 0, 0);
            Execute(self->Inner, signature, call.MaxCount, gathered, slot, 0, 0);

            return;
        }

        // --indirect-batch: the others join the clamp batch opened since arguments last may have changed.
        if (IndirectBatch.Try(self, call, argumentBuffer, argumentOffset, countBuffer, countOffset, out nint batched, out ulong batchSlot, out bool opened))
        {
            Batched(self, call, opened);
            Execute(self->Inner, signature, call.MaxCount, batched, batchSlot, countBuffer, countOffset);

            return;
        }

        if (IndirectClamp.Record(self, call.Signature, call.MaxCount, argumentBuffer, argumentOffset, countBuffer, countOffset, out nint scratch, out ulong scratchOffset))
        {
            AfterClamp(self, call.Kind == IndirectClamp.DispatchKind);
            Execute(self->Inner, signature, call.MaxCount, scratch, scratchOffset, countBuffer, countOffset);
            IndirectClamp.Done(self, scratch);
            Shapes(self, call, scratch, scratchOffset);

            return;
        }

        TimeIndirect(self, call.Kind);
        Shapes(self, call, 0, 0);
        Execute(self->Inner, signature, call.MaxCount, argumentBuffer, argumentOffset, countBuffer, countOffset);
    }

    [HotPath]
    private static void Execute(nint inner, nint signature, uint maxCount, nint arguments, ulong argumentOffset, nint count, ulong countOffset)
    {
        var execute = (delegate* unmanaged<nint, nint, uint, nint, ulong, nint, ulong, void>)ComObject.Slot(inner, DesktopExecuteIndirect);
        execute(inner, signature, maxCount, arguments, argumentOffset, count, countOffset);
    }

    /// <summary>
    /// After the argument clamp's dispatch: a dispatch's execution needs the list's compute state now; a draw's needs
    /// only the list's pipeline, and the compute root signature goes back before the list's next compute work
    /// (<see cref="Flush"/>), so a run of indirect draws keeps the clamp's bound (<see cref="IndirectClamp"/>).
    /// </summary>
    [HotPath]
    private static void AfterClamp(XboxObject* self, bool dispatch)
    {
        if (dispatch)
        {
            RestoreCompute(self);

            return;
        }

        RestorePipeline(self);
    }

    /// <summary>
    /// Before a batched ExecuteIndirect runs: after the batch's pass (<paramref name="opened"/>) the list's state goes back
    /// as after a clamp, else only its pass timestamp; its work shape without a clamped copy to read.
    /// </summary>
    [HotPath]
    private static void Batched(XboxObject* self, IndirectCall call, bool opened)
    {
        if (opened)
        {
            AfterClamp(self, call.Kind == IndirectClamp.DispatchKind);
        }
        else
        {
            TimeIndirect(self, call.Kind);
        }

        Shapes(self, call, 0, 0);
    }

    /// <summary>With <c>--gpu-timings</c>: an ExecuteIndirect's work for <see cref="WorkShapes"/> (its clamped copy, when there is one).</summary>
    [HotPath]
    private static void Shapes(XboxObject* self, IndirectCall call, nint scratch, ulong scratchOffset)
    {
        if (GpuTimings.Active)
        {
            long frame = ShimContext.Runtime.D3D12X.Presenter.Presents;
            WorkShapes.Indirect(self->Inner, ListActivity.Of(self)->Pipeline, call.Kind, call.MaxCount, scratch, scratchOffset, frame);
        }
    }

    /// <summary>
    /// A mesh ExecuteIndirect on a pipeline with the mesh launch stage, which an ExecuteIndirect would launch wrongly (its
    /// records would count stage groups): one record without a count buffer is read by the stage where the game wrote it;
    /// any other call is clamped and its clamped records launched one by one. Dropped (counted) when the clamp cannot run.
    /// </summary>
    [HotPath]
    private static void Launch(XboxObject* self, IndirectCall call, nint argumentBuffer, ulong argumentOffset, nint countBuffer, ulong countOffset)
    {
        if (call.MaxCount == 1 && countBuffer == 0 && argumentBuffer != 0)
        {
            ulong budget = IndirectClamp.LaunchBudget(self);

            if (budget != 0)
            {
                TimeIndirect(self, call.Kind);
                MeshLaunch.Record(self->Inner, MeshLaunch.ChargedRecord, DesktopCalls.GpuAddress(argumentBuffer) + argumentOffset, budget);

                return;
            }
        }

        if (!IndirectClamp.Record(self, call.Signature, call.MaxCount, argumentBuffer, argumentOffset, countBuffer, countOffset, out nint scratch, out ulong scratchOffset))
        {
            MeshLaunch.Drop();

            return;
        }

        RestoreCompute(self);
        IndirectClamp.LaunchClamped(self, scratch, scratchOffset, call.MaxCount, call.Stride);
        IndirectClamp.Done(self, scratch);
    }

    /// <summary>
    /// <c>--check-null-addresses</c>: whether an ExecuteIndirect's desktop argument or count buffer is missing, at a low
    /// address or read past its end (<see cref="NullAddressCheck"/>); true drops it.
    /// </summary>
    private static bool IndirectUnsafe(XboxObject* self, nint signature, uint maxCount, nint arguments, ulong argumentOffset, nint count, ulong countOffset)
    {
        ulong bytes = (ulong)CommandSignatures.Clampable(signature).Stride * maxCount;
        string? problem = NullAddressCheck.Indirect(arguments, argumentOffset, bytes, count, countOffset);

        return problem is not null && NullAddressCheck.Drop(NullAddressKind.IndirectBuffer, self, $"ExecuteIndirect of {maxCount}: {problem}");
    }

    /// <summary>
    /// Before an ExecuteIndirect: a depth twin's copy for draws (<see cref="CopyTwin"/>), then the user data of the bind
    /// point it runs on; the other's goes out before that bind point's next work, as for a direct draw or dispatch.
    /// </summary>
    [HotPath]
    private static void FlushForIndirect(XboxObject* self, bool dispatch)
    {
        if (DepthTwins.Any && !dispatch)
        {
            CopyTwin(self);
        }

        Flush(self, dispatch);
    }

    /// <summary>
    /// <c>--depth-twins</c>, before a draw: the bound depth-stencil view's texture is a side the list uses
    /// (<see cref="DepthTwinSides.UsePlane"/>); when the list wrote its depth or stencil twin since, the twin's values go
    /// into that plane here (<see cref="DepthTwinCopy"/>), then the list's graphics state is set again (heaps, input,
    /// targets, viewports, root signature, user data, pipeline). What lists submitted before it wrote is copied at
    /// submission (<see cref="DepthTwinPrelude"/>).
    /// </summary>
    private static void CopyTwin(XboxObject* self)
    {
        var replay = ListReplayState.Of(self);

        if ((replay->Set & ListReplayState.DepthStencilBit) == 0)
        {
            return;
        }

        nint view = (nint)replay->DepthStencil;
        var depth = DepthTwins.OfView(view, out int format, out var stencil);
        nint list = self->Inner;
        bool copied = CopyPlane(self, depth, view, format);

        if (!(CopyPlane(self, stencil, view, format) | copied))
        {
            return;
        }

        ListSplits.RestoreGraphics(replay, list);

        if (CommandListBindings.Of(self)->GraphicsRoot != 0)
        {
            SetBindlessRoot(self, false);
        }

        var rectangles = RectangleListState.Of(self);
        rectangles->VariantBound = 0;

        if (CommandListBindings.Of(self)->PipelinePending == 0 && rectangles->Pipeline != 0)
        {
            ((delegate* unmanaged<nint, nint, void>)ComObject.Slot(list, DesktopSetPipelineState))(list, rectangles->Pipeline);
        }
    }

    /// <summary>
    /// Notes the plane's use; records the copy of <paramref name="twin"/> into its plane when the list wrote the twin
    /// since. The copy reads the twin through the bound view heap: a list without one set has nothing to read it by.
    /// Whether it was recorded.
    /// </summary>
    private static bool CopyPlane(XboxObject* self, DepthTwin? twin, nint view, int format)
    {
        if (twin is null || !DepthTwinSides.UsePlane((nint)self, twin, format))
        {
            return false;
        }

        if ((ListReplayState.Of(self)->Set & ListReplayState.HeapsBit) == 0)
        {
            DepthTwinSides.PlaneLeftBehind((nint)self, twin);

            return false;
        }

        if (twin.ViewHandle == 0)
        {
            return CopyThroughOwnView(self, twin, format);
        }

        var d3d12x = ShimContext.Runtime.D3D12X;
        nint heap = d3d12x.Bindings.Window.ViewHeap;
        ulong table = DesktopCalls.HeapGpuStart(heap) + (ulong)(twin.ViewHandle - DesktopCalls.HeapCpuStart(heap));
        int state = d3d12x.ListStates.For(self).Current(twin.Depth, twin.Home);

        if (DepthTwinCopy.Record(d3d12x.Objects.DesktopDevice, self->Inner, twin, view, format, state, table))
        {
            DepthTwinLog.InList(true, twin);

            return true;
        }

        DepthTwinSides.PlaneLeftBehind((nint)self, twin);

        return false;
    }

    /// <summary>
    /// The in-list copy of a twin whose UAV the game wrote over in its heap since: through a view of the loader's own
    /// heap (<see cref="DepthTwinPrelude.InList"/>), then the list's heaps again; both root signatures are bound anew
    /// before their next work, their tables having named the other heap.
    /// </summary>
    private static bool CopyThroughOwnView(XboxObject* self, DepthTwin twin, int format)
    {
        var d3d12x = ShimContext.Runtime.D3D12X;
        int state = d3d12x.ListStates.For(self).Current(twin.Depth, twin.Home);
        nint inner = self->Inner;
        bool copied = DepthTwinPrelude.InList(d3d12x.Objects.DesktopDevice, inner, twin, format, state);

        var replay = ListReplayState.Of(self);
        ((delegate* unmanaged<nint, uint, long*, void>)ComObject.Slot(inner, DesktopSetDescriptorHeaps))(inner, replay->HeapCount, replay->Heaps);
        var bindings = CommandListBindings.Of(self);
        bindings->GraphicsDesktopRoot = CommandListBindings.NoRoot;
        bindings->ComputeDesktopRoot = CommandListBindings.NoRoot;

        if (!copied)
        {
            DepthTwinSides.PlaneLeftBehind((nint)self, twin);

            if (bindings->GraphicsRoot != 0)
            {
                SetBindlessRoot(self, false);
            }

            return false;
        }

        DepthTwinLog.InList(true, twin);

        return true;
    }

    /// <summary>After the argument clamp's dispatch: the list's compute root signature, user data and pipeline again.</summary>
    [HotPath]
    internal static void RestoreCompute(XboxObject* self)
    {
        var bindings = CommandListBindings.Of(self);

        if (bindings->ComputeRoot != 0)
        {
            SetBindlessRoot(self, true);
            Flush(self, true);
        }
        else
        {
            // Ours (the clamp's, a fill's) stays bound and the list has none of its own to go back to.
            bindings->ComputeDesktopRoot = CommandListBindings.NoRoot;
        }

        RestorePipeline(self);
    }

    /// <summary>The list's own pipeline again after ours ran (and a rectangle list's variant, for a draw).</summary>
    [HotPath]
    private static void RestorePipeline(XboxObject* self)
    {
        var rectangles = RectangleListState.Of(self);
        rectangles->VariantBound = 0;

        if (CommandListBindings.Of(self)->PipelinePending == 0 && rectangles->Pipeline != 0)
        {
            nint inner = self->Inner;
            ((delegate* unmanaged<nint, nint, void>)ComObject.Slot(inner, DesktopSetPipelineState))(inner, rectangles->Pipeline);
        }

        if (rectangles->Topology == PrimitiveTopologies.RectangleList)
        {
            ShimContext.Runtime.D3D12X.Rectangles.BeforeDraw(self);
        }
    }

    /// <summary>The console address of an ExecuteIndirect argument, 0 for a buffer without one.</summary>
    [HotPath]
    private static ulong ArgumentAddress(nint arguments, ulong offset)
    {
        var resource = ShimContext.Runtime.D3D12X.Objects.AsResource(arguments);

        if (resource == null || resource->GameAddress == 0)
        {
            return 0;
        }

        return resource->GameAddress + offset;
    }

    /// <summary>
    /// True while the list's pipeline is a stand-in the driver has not built: its draw or dispatch is skipped and
    /// counted; otherwise it is counted as recorded.
    /// </summary>
    [HotPath]
    private static bool PipelinePending(XboxObject* self, bool draw)
    {
        var standIns = ShimContext.Runtime.D3D12X.Pipelines?.StandIns;
        var activity = ListActivity.Of(self);
        var trace = ShimContext.Runtime.D3D12X.DrawTrace;

        if (CommandListBindings.Of(self)->PipelinePending == 0)
        {
            standIns?.Recorded(activity->Skipped);
            activity->Skipped = 0;
            trace?.Census(self, RectangleListState.Of(self)->Pipeline, skipped: false);

            // --hold-passes / --stub-passes: recorded work of a held or stubbed pass is skipped all the same.
            return PassControls.Rules.Enabled && PassControls.Intercept(self, RectangleListState.Of(self)->Pipeline, draw);
        }

        trace?.Census(self, activity->StandIn, skipped: true);

        if (PassControls.Rules.Enabled && standIns is not null)
        {
            PassControls.Skipped(standIns.Name(activity->StandIn));
        }

        standIns?.Skipped(activity->StandIn);
        activity->Skipped = activity->StandIn;

        return true;
    }

    /// <summary>With <c>--skip-compute-chains</c>: a skipped dispatch skips the rest of its recording's compute (off by default).</summary>
    public static bool SkipComputeChains { get; set; }

    /// <summary>
    /// With <c>--empty-indirect-after-skip</c>: once a compute dispatch of a recording is skipped for a stand-in, the
    /// recording's later ExecuteIndirects run nothing (off by default). Unlike <see cref="SkipComputeChains"/> direct
    /// dispatches and draws still run: only work whose size comes from GPU-written arguments, which the skipped pass
    /// may have been meant to write (culling, argument building), is dropped.
    /// </summary>
    public static bool EmptyIndirectAfterSkip { get; set; }

    /// <summary>
    /// Whether a dispatch or ExecuteIndirect is skipped: its pipeline is a stand-in, or, with
    /// <see cref="SkipComputeChains"/>, an earlier one of the recording was. The game's compute chains (culling, argument building) prepare counters and arguments for the passes after
    /// them, and the whole 174 MB transient pool is one raw UAV, so a pass that runs after a skipped initialiser can
    /// write records anywhere in it; skipping the rest of the chain for a frame is harmless.
    /// </summary>
    [HotPath]
    private static bool ComputeChainBroken(XboxObject* self, bool producer)
    {
        var bindings = CommandListBindings.Of(self);

        if (bindings->ComputeSkipped != 0)
        {
            ShimContext.Runtime.D3D12X.Pipelines?.StandIns.SkippedAfterChain();

            return true;
        }

        if (!PipelinePending(self, draw: false))
        {
            return false;
        }

        var standIns = ShimContext.Runtime.D3D12X.Pipelines?.StandIns;

        if (RtStandIns.Enabled && standIns is not null)
        {
            RtStandIns.AtSkip(self, standIns.Name(ListActivity.Of(self)->StandIn));
        }

        if (SkipComputeChains)
        {
            bindings->ComputeSkipped = 1;
        }

        if (producer)
        {
            bindings->ProducerSkipped = 1;
        }

        return true;
    }

    [HotPath]
    private static int ResetList(XboxObject* self, nint allocator, nint pipelineState)
    {
        *CommandListBindings.Of(self) = default;
        ListMemoryWaits.Of(self)->Reset();

        ListSegments.Of(self)->Reset(ShimContext.Runtime.D3D12X.Objects.Unwrap(allocator));
        ListReplayState.Of(self)->Set = 0;
        ListReplayState.Of(self)->VertexMask = 0;

        ListIndirectScratch.Of(self)->Used = 0;
        ListIndirectScratch.Of(self)->Budget = 0;
        ListIndirectScratch.Of(self)->LaunchSegment = 0;
        ListActivity.Reset(self);
        IndirectGather.Reset(self);
        ShimContext.Runtime.D3D12X.DrawTrace?.Reset(self);

        if (DepthTwinSides.Any)
        {
            DepthTwinSides.Forget((nint)self);
        }

        var objects = ShimContext.Runtime.D3D12X.Objects;
        nint inner = self->Inner;
        var reset = (delegate* unmanaged<nint, nint, nint, int>)ComObject.Slot(inner, DesktopReset);
        nint pipeline = objects.Unwrap(pipelineState);
        var standIns = ShimContext.Runtime.D3D12X.Pipelines?.StandIns;

        if (standIns is not null)
        {
            pipeline = standIns.Desktop(pipeline, out _);
        }

        ShimContext.Runtime.D3D12X.Rectangles.Reset(self, pipeline);
        ListActivity.Of(self)->Launches = MeshLaunch.Enabled && MeshLaunch.Has(pipeline) ? 1 : 0;
        nint desktop = objects.Unwrap(allocator);
        int hr = reset(inner, desktop, pipeline);

        // The allocator backs another recording list, which the console allows: record on a spare (SpareAllocators).
        if (hr == HResult.InvalidArgument && desktop != 0)
        {
            hr = ResetOnSpare(self, desktop, pipeline, reset);
        }

        // A new recording: its submission need not wait on the CPU for the last run (ListSubmissions).
        if (!HResult.Failed(hr))
        {
            ListSubmission.Of((nint)self)->ResetSince = 1;
        }

        return hr;
    }

    /// <summary>The list's Reset on a spare of its allocator, or on a new desktop list when the desktop list is dead.</summary>
    private static int ResetOnSpare(XboxObject* self, nint desktop, nint pipeline, delegate* unmanaged<nint, nint, nint, int> reset)
    {
        nint inner = self->Inner;
        var spares = ShimContext.Runtime.D3D12X.SpareAllocators;
        int hr = spares.Reset(inner, desktop, pipeline, reset);

        // Refused on a fresh allocator too: the list's last Close failed, and the desktop list is dead.
        if (hr == HResult.InvalidArgument)
        {
            nint replacement = spares.Replace(inner, desktop, pipeline);

            if (replacement != 0)
            {
                self->Inner = replacement;
                hr = HResult.Ok;
            }
        }

        return hr;
    }

    [HotPath]
    private static void BindHeaps(XboxObject* self, uint count, nint* heaps)
    {
        var bindings = CommandListBindings.Of(self);
        nint inner = self->Inner;
        ((delegate* unmanaged<nint, uint, nint*, void>)ComObject.Slot(inner, DesktopSetDescriptorHeaps))(inner, count, heaps);
        bindings->HeapsBound = 1;
        KeepHeaps(self, count, heaps);

        // The bindless root signature indexes the heaps directly: it is set again after a heap change.
        if (bindings->GraphicsRoot != 0)
        {
            SetBindlessRoot(self, false);
        }

        if (bindings->ComputeRoot != 0)
        {
            SetBindlessRoot(self, true);
        }
    }

    [HotPath]
    private static void SetRootSignature(XboxObject* self, nint rootSignature, bool compute)
    {
        var bindings = CommandListBindings.Of(self);
        var layout = Bindings.Layouts.Find(rootSignature);
        bool same;

        if (compute)
        {
            same = bindings->ComputeLayout == layout && bindings->ComputeDesktopRoot == CommandListBindings.BindlessRoot;
            bindings->ComputeLayout = layout;
            bindings->ComputeRoot = 1;
        }
        else
        {
            same = bindings->GraphicsLayout == layout && bindings->GraphicsDesktopRoot == CommandListBindings.BindlessRoot;
            bindings->GraphicsLayout = layout;
            bindings->GraphicsRoot = 1;
        }

        if (bindings->HeapsBound == 0)
        {
            BindDefaultHeaps(self);
        }

        // The game's root signature again, its layout known and the same: the bindless one is still bound and the
        // user data already laid out for it, so neither is set again (each set made every draw upload and bind anew).
        if (same && layout != null)
        {
            return;
        }

        SetBindlessRoot(self, compute);
    }

    /// <summary>A list that sets a root signature before any heap gets the game's main heaps (they are the only shader-visible ones).</summary>
    [HotPath]
    private static void BindDefaultHeaps(XboxObject* self)
    {
        var window = Bindings.Window;
        nint* heaps = stackalloc nint[MaxHeaps];
        uint count = 0;

        if (window.ViewHeap != 0)
        {
            heaps[count++] = window.ViewHeap;
        }

        if (window.SamplerHeap != 0)
        {
            heaps[count++] = window.SamplerHeap;
        }

        if (count != 0)
        {
            nint inner = self->Inner;
            ((delegate* unmanaged<nint, uint, nint*, void>)ComObject.Slot(inner, DesktopSetDescriptorHeaps))(inner, count, heaps);
            CommandListBindings.Of(self)->HeapsBound = 1;
            KeepHeaps(self, count, heaps);
        }
    }

    /// <summary>The desktop heaps the list set, for a split list (<see cref="ListReplayState"/>).</summary>
    [HotPath]
    private static void KeepHeaps(XboxObject* self, uint count, nint* heaps)
    {
        var state = ListReplayState.Of(self);
        state->Set &= ~ListReplayState.HeapsBit;

        if (count > ListReplayState.MaxHeaps || (count != 0 && heaps == null))
        {
            return;
        }

        for (uint i = 0; i < count; i++)
        {
            state->Heaps[i] = heaps[i];
        }

        state->HeapCount = count;
        state->Set |= ListReplayState.HeapsBit;
    }

    /// <summary>
    /// After a split (<see cref="ListSplits"/>): sets the list's heaps again on its new desktop list, then the bindless
    /// root signature of each bind point the list had set; the user data goes out again at the next draw or dispatch.
    /// </summary>
    public static void Rebind(XboxObject* self)
    {
        var state = ListReplayState.Of(self);
        var bindings = CommandListBindings.Of(self);

        // A new desktop list: no root signature bound on it yet.
        bindings->GraphicsDesktopRoot = CommandListBindings.NoRoot;
        bindings->ComputeDesktopRoot = CommandListBindings.NoRoot;

        if ((state->Set & ListReplayState.HeapsBit) != 0)
        {
            nint inner = self->Inner;
            ((delegate* unmanaged<nint, uint, long*, void>)ComObject.Slot(inner, DesktopSetDescriptorHeaps))(inner, state->HeapCount, state->Heaps);
        }

        if (bindings->GraphicsRoot != 0)
        {
            SetBindlessRoot(self, false);
        }

        if (bindings->ComputeRoot != 0)
        {
            SetBindlessRoot(self, true);
        }
    }

    /// <summary>The bindless root signature with the fallback table; the user data goes out again at the next draw.</summary>
    [HotPath]
    private static void SetBindlessRoot(XboxObject* self, bool compute)
    {
        var bindings = CommandListBindings.Of(self);
        nint root = Bindings.Root;
        nint inner = self->Inner;

        if (root == 0)
        {
            return;
        }

        int setRoot = compute ? DesktopSetComputeRootSignature : DesktopSetGraphicsRootSignature;
        int setTable = compute ? DesktopSetComputeRootDescriptorTable : DesktopSetGraphicsRootDescriptorTable;
        ((delegate* unmanaged<nint, nint, void>)ComObject.Slot(inner, setRoot))(inner, root);

        if (Bindings.Window.ViewHeap != 0)
        {
            ((delegate* unmanaged<nint, uint, ulong, void>)ComObject.Slot(inner, setTable))(inner, FallbackTableParameter, Bindings.Window.FallbackTable);
        }

        if (compute)
        {
            bindings->ComputeDirty = 1;
            bindings->ComputeDesktopRoot = CommandListBindings.BindlessRoot;
        }
        else
        {
            bindings->GraphicsDirty = 1;
            bindings->GraphicsDesktopRoot = CommandListBindings.BindlessRoot;
        }
    }

    /// <summary>
    /// Sets the bind point's user data when it changed: as root constants (<see cref="UserDataConstants"/>), or uploaded
    /// to the ring with root parameter 0 pointed at it.
    /// </summary>
    [HotPath]
    private static void Flush(XboxObject* self, bool compute)
    {
        var bindings = CommandListBindings.Of(self);

        // The clamp's root signature left in place after an indirect draw (AfterClamp): the list's own goes back first.
        if (compute && bindings->ComputeRoot != 0 && bindings->ComputeDesktopRoot != CommandListBindings.BindlessRoot)
        {
            SetBindlessRoot(self, true);
        }

        MirrorReach(self, bindings, compute);
        uint* userData = compute ? bindings->Compute : bindings->Graphics;
        SharedExponentSamplers.Check(RectangleListState.Of(self)->Pipeline, userData, ShimContext.Runtime.D3D12X.Bindings.Window.Data);

        var ring = Bindings.Ring;
        int root = compute ? bindings->ComputeRoot : bindings->GraphicsRoot;
        int dirty = compute ? bindings->ComputeDirty : bindings->GraphicsDirty;
        bool constants = UserDataConstants.Enabled;

        if ((ring is null && !constants) || root == 0 || dirty == 0 || Bindings.Root == 0)
        {
            return;
        }

        uint* data = compute ? bindings->Compute : bindings->Graphics;
        uint used = UserData.Used(bindings, compute);
        nint inner = self->Inner;

        if (constants)
        {
            SetUserConstants(inner, compute, data, used);
        }
        else
        {
            ulong address = ring!.Upload(data, used);

            if (NullAddressCheck.DropsRing(self, address))
            {
                return;
            }

            int slot = compute ? DesktopSetComputeRootConstantBufferView : DesktopSetGraphicsRootConstantBufferView;
            ((delegate* unmanaged<nint, uint, ulong, void>)ComObject.Slot(inner, slot))(inner, UserDataParameter, address);
        }

        if (compute)
        {
            bindings->ComputeDirty = 0;
        }
        else
        {
            bindings->GraphicsDirty = 0;
        }
    }

    /// <summary>
    /// Before a draw or dispatch: the tables the bound pipeline's programs read past what their binds mirrored, mirrored
    /// that far (<see cref="TableReaches"/>), as the game may have copied descriptors there since.
    /// </summary>
    [HotPath]
    private static void MirrorReach(XboxObject* self, CommandListBindings* bindings, bool compute)
    {
        nint pipeline = RectangleListState.Of(self)->Pipeline;
        uint* data = compute ? bindings->Compute : bindings->Graphics;

        if (!compute && TargetClears.Census)
        {
            TargetClears.Drew(self->Inner, pipeline);
        }

        // Every table the programs read, also one a bind mirrored whole (the tiled lighting's material-mask pass binds
        // its depth twins' UAVs in a table of 8 records).
        if (DepthTwins.Any)
        {
            DepthTwins.MarkWriters(self, data, ShimContext.Runtime.D3D12X.Bindings.Window.Records, TableReaches.AllOf(pipeline));
        }

        var entries = TableReaches.Of(pipeline);

        if (entries.Length == 0)
        {
            return;
        }

        if (DepthUavs.Any)
        {
            DepthUavs.CheckFirstUse(pipeline, data, ShimContext.Runtime.D3D12X.Bindings.Window.Data);
        }

        if (SharedExponentAliases.Any)
        {
            SharedExponentAliases.BeforeWork(self->Inner, ShimContext.Runtime.D3D12X.ListStates.For(self), data, ShimContext.Runtime.D3D12X.Bindings.Window.Records, entries);
        }

        var heaps = ShimContext.Runtime.D3D12X.DescriptorHeaps;

        foreach (var entry in entries)
        {
            if (entry.Sgpr < CommandListBindings.UserDataDwords)
            {
                heaps.MirrorTable(data[entry.Sgpr], entry.Records);
            }
        }
    }

    /// <summary>
    /// The used dwords as root constants. Past <see cref="BindlessRootSignature.UserConstants"/> (a layout the shim could
    /// not read, which uploads all 128) is nothing a program reads: RDNA preloads no user SGPR past s31.
    /// </summary>
    [HotPath]
    private static void SetUserConstants(nint inner, bool compute, uint* data, uint used)
    {
        int slot = compute ? DesktopSetComputeRoot32BitConstants : DesktopSetGraphicsRoot32BitConstants;

        if (used > BindlessRootSignature.UserConstants)
        {
            used = BindlessRootSignature.UserConstants;
        }

        ((delegate* unmanaged<nint, uint, uint, uint*, uint, void>)ComObject.Slot(inner, slot))(inner, UserDataParameter, used, data, 0);
    }

    [HotPath]
    private static void WriteConstants(CommandListBindings* bindings, bool compute, uint parameter, uint count, uint* values, uint offset)
    {
        for (uint i = 0; i < count; i++)
        {
            UserData.Write(bindings, compute, parameter, offset + i, values[i]);
        }
    }

    /// <summary>The window pointer of a table, with as many records mirrored as the bound root signature's table holds.</summary>
    [HotPath]
    private static uint TableAddress(CommandListBindings* bindings, bool compute, uint parameter, ulong handle)
    {
        RootLayout* layout = compute ? bindings->ComputeLayout : bindings->GraphicsLayout;
        uint extent = DescriptorWindow.MaxTableRecords;

        if (layout != null && parameter < (uint)layout->Count)
        {
            extent = layout->Extents[parameter];
        }

        return ShimContext.Runtime.D3D12X.DescriptorHeaps.TableAddress(handle, DescriptorWindow.MirroredRecords(extent));
    }
}
