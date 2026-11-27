// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using FiveStar.Loader.Diagnostics;
using FiveStar.Loader.Native;
using FiveStar.Loader.Shims.D3D12X.Binding;
using FiveStar.Loader.Shims.D3D12X.DebugLayer;
using FiveStar.Loader.Shims.D3D12X.Resources;

namespace FiveStar.Loader.Shims.D3D12X;

/// <summary>
/// Device slots 17 to 24 (view creation and descriptor copies, desktop order): a destination handle in a shader-visible
/// heap becomes the desktop handle behind it with its record written
/// (<see cref="XboxDescriptorHeaps.Destination(nint, BufferRecord)"/>; a buffer view's record keeps its V# fields), a
/// source handle the desktop descriptor its record names, and wrapped resources desktop resources. Copies inside one
/// shader-visible heap copy records, as the console copies descriptor bytes. Views are written while streaming, so
/// these stay allocation-free.
/// </summary>
internal static unsafe class XboxDescriptorMethods
{
    /// <summary>D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV (d3d12.h).</summary>
    private const int ViewHeapType = 0;

    public const int CreateConstantBufferViewSlot = 17;
    public const int CreateShaderResourceViewSlot = 18;
    public const int CreateUnorderedAccessViewSlot = 19;
    public const int CreateRenderTargetViewSlot = 20;
    public const int CreateDepthStencilViewSlot = 21;
    public const int CreateSamplerSlot = 22;
    public const int CopyDescriptorsSlot = 23;
    public const int CopyDescriptorsSimpleSlot = 24;

    /// <summary>ID3D12Device::GetDescriptorHandleIncrementSize (d3d12.h).</summary>
    public const int IncrementSlot = 15;

    /// <summary>D3D12_DESCRIPTOR_HEAP_TYPE_NUM_TYPES: the desktop heap types are 0 (CBV_SRV_UAV) to 3 (DSV) (d3d12.h).</summary>
    private const uint DesktopHeapTypes = 4;

    /// <summary>D3D12_RESOURCE_FLAG_ALLOW_DEPTH_STENCIL (d3d12.h).</summary>
    private const int AllowDepthStencil = 0x2;

    /// <summary>ID3D12Resource::GetDesc (desktop order: object, then the return buffer).</summary>
    private const int ResourceGetDescSlot = 10;

    private static XboxDeviceFactory D3D12X => ShimContext.Runtime.D3D12X;

    /// <summary>
    /// Slot 15. The game asks for types 0 to 4 once at start-up (0x142478e3d) and keeps only type 0's answer; type 4 is
    /// console-only and answered 0 without the desktop call, which refuses it.
    /// </summary>
    [HotPath]
    [UnmanagedCallersOnly]
    public static uint GetDescriptorHandleIncrementSize(XboxObject* self, uint type)
    {
        try
        {
            if (type >= DesktopHeapTypes)
            {
                return 0;
            }

            return Increment(self->Inner, (int)type);
        }
        catch (Exception exception)
        {
            UnmanagedGuard.Caught(exception, "XboxDescriptorMethods.GetDescriptorHandleIncrementSize");

            return default;
        }
    }

    [HotPath]
    [UnmanagedCallersOnly]
    public static void CreateConstantBufferView(XboxObject* self, ConstantBufferViewDescription* description, nint handle)
    {
        try
        {
            nint inner = self->Inner;
            ulong location = 0;

            if (description != null)
            {
                location = ConstantSource(description->Location, description->Size);
            }

            // In the game's own heaps a CBV is a V# the programs read as a ByteAddressBuffer: a raw SRV over the same bytes.
            if (location != 0 && D3D12X.DescriptorHeaps.Find(handle, out _) is not null
                && D3D12X.AddressBuffers.CreateRaw(inner, location, description->Size, D3D12X.DescriptorHeaps.Destination(handle, BufferRecords.Based(BufferRecords.ConstantBuffer(description->Size), location, 0, 0, ViewKind.ConstantBuffer))))
            {
                D3D12X.DrawTrace?.ConstantBufferView(D3D12X.DescriptorHeaps.Source(handle), description->Location, description->Size);

                return;
            }

            if (WroteNullRaw(inner, description, handle))
            {
                return;
            }

            ConstantBufferViewDescription desktop = default;
            ConstantBufferViewDescription* view = null;

            // The console address becomes a desktop one and the size grows to the desktop's 256-byte granularity.
            if (description != null)
            {
                desktop = *description;
                desktop.Location = location;
                desktop.Size = (desktop.Size + ConstantBufferViewDescription.Alignment - 1) & ~(ConstantBufferViewDescription.Alignment - 1);
                view = &desktop;

                if (desktop.Location != 0 && !D3D12X.GameMemory.TryTranslate(desktop.Location, out desktop.Location))
                {
                    WarnUnknownConstantBuffer(description->Location, description->Size);
                    view = null;
                }
                else if ((desktop.Location & (ConstantBufferViewDescription.Alignment - 1)) != 0)
                {
                    // The console takes constant buffers at 16-byte boundaries; the desktop removes the device for these.
                    D3D12X.Warnings.Warn("cbv-alignment", "[d3d12_xs] CreateConstantBufferView: an address off the desktop's 256-byte alignment, null view written");
                    view = null;
                }
            }

            ((delegate* unmanaged<nint, ConstantBufferViewDescription*, nint, void>)ComObject.Slot(inner, CreateConstantBufferViewSlot))(inner, view, Destination(handle));
        }
        catch (Exception exception)
        {
            UnmanagedGuard.Caught(exception, "XboxDescriptorMethods.CreateConstantBufferView");
        }
    }

    /// <summary>
    /// The address a constant-buffer view reads: the game's own where a desktop buffer covers it; else, for game memory
    /// the CPU can read (XMem pages of the game's constant pools, which it remaps under any buffer thousands of times
    /// in the story load), a copy made now in the upload ring (<see cref="UploadArena"/>), which the address table
    /// covers. The game writes the constants before it creates the view.
    /// </summary>
    [HotPath]
    private static ulong ConstantSource(ulong address, uint size)
    {
        if (address == 0 || size == 0 || D3D12X.GameMemory.TryFind(address, out _))
        {
            return address;
        }

        byte* source = D3D12X.MemorySource.Find(address, size);
        ulong rounded = (size + ConstantBufferViewDescription.Alignment - 1) & ~(ulong)(ConstantBufferViewDescription.Alignment - 1);
        byte* copy = null;

        if (source != null)
        {
            copy = D3D12X.Uploads.Allocate(rounded, ConstantBufferViewDescription.Alignment);
        }

        if (copy == null)
        {
            return address;
        }

        Buffer.MemoryCopy(source, copy, rounded, size);

        return (ulong)copy;
    }

    /// <summary>
    /// Programs read the slot of a record without V# fields as a ByteAddressBuffer: a constant-buffer view in a game heap
    /// that got no raw view over its bytes (noted as a record without V# fields) becomes a null raw SRV, not a CBV
    /// (<see cref="RawBufferViews"/>); false outside a game heap or when off.
    /// </summary>
    [HotPath]
    private static bool WroteNullRaw(nint inner, ConstantBufferViewDescription* description, nint handle)
    {
        bool gameHeap = D3D12X.DescriptorHeaps.Find(handle, out _) is not null;

        if (description != null && BufferRecords.Noting(BufferRecords.FieldlessConstantBuffer) && gameHeap)
        {
            NoteConstantBuffer(description);
        }

        if (!gameHeap || !RawBufferViews.Enabled)
        {
            return false;
        }

        var none = RawBufferViews.NullConstantBuffer();
        ((delegate* unmanaged<nint, nint, BufferViewDescription*, nint, void>)ComObject.Slot(inner, CreateShaderResourceViewSlot))(inner, 0, &none, Destination(handle));

        return true;
    }

    private static void WarnUnknownConstantBuffer(ulong address, uint size)
    {
        MemoryBasicInformation info = default;
        Kernel32.VirtualQueryEx(Kernel32.GetCurrentProcess(), (nint)address, &info, (nuint)sizeof(MemoryBasicInformation));
        D3D12X.Warnings.Warn("cbv", $"[d3d12_xs] CreateConstantBufferView: GPU address 0x{address:x} ({size} bytes) outside known game memory, null view written (allocation 0x{info.AllocationBase:x}, region 0x{info.BaseAddress:x} + 0x{info.RegionSize:x}, state 0x{info.State:x}, type 0x{info.Type:x}, protect 0x{info.Protect:x}, reserved by XMem {ShimContext.Runtime.XMemPhysical.IsReserved((nint)address)})");
    }

    [HotPath]
    [UnmanagedCallersOnly]
    public static void CreateShaderResourceView(XboxObject* self, nint resource, void* description, nint handle)
    {
        try
        {
            nint desktop = D3D12X.Objects.Unwrap(resource);
            nint traced = desktop;
            var record = ImageViewClasses.With(BufferRecords.Based(BufferRecords.ShaderResource(description), BufferRecords.GameAddress(resource), BufferRecords.FirstElement(description, unordered: false), desktop, ViewKind.ShaderResource), description);
            NoteFieldless(description, record, unordered: false);

            byte* copy = stackalloc byte[DepthViewDescriptions.ShaderResourceViewSize];
            desktop = BufferViewRanges.Fit(resource, desktop, description, copy, false, out void* fitted);
            byte* raw = stackalloc byte[DepthViewDescriptions.ShaderResourceViewSize];
            fitted = RawBufferViews.View(fitted, raw, false, record, ref desktop);
            void* view = TargetViewDescriptions.ShaderResourceView(desktop, fitted, copy);

            nint destination = D3D12X.DescriptorHeaps.Destination(handle, record);

            void* written = DepthViewDescriptions.ShaderResourceView(desktop, view, copy);

            // Traced at the handle the view is written to: a second Destination call takes another ring slot.
            D3D12X.DrawTrace?.ShaderResourceView(traced, destination, resource, written);
            CreateDesktopView(self, CreateShaderResourceViewSlot, desktop, written, destination);
            Written(destination, resource, desktop, written, unordered: false);
        }
        catch (Exception exception)
        {
            UnmanagedGuard.Caught(exception, "XboxDescriptorMethods.CreateShaderResourceView");
        }
    }

    [HotPath]
    [UnmanagedCallersOnly]
    public static void CreateRenderTargetView(XboxObject* self, nint resource, void* description, nint handle)
    {
        try
        {
            nint desktop = D3D12X.Objects.Unwrap(resource);
            byte* copy = stackalloc byte[TargetViewDescriptions.RenderTargetViewSize];
            void* view = TargetViewDescriptions.RenderTargetView(desktop, description, copy);
            nint destination = Destination(handle);
            D3D12X.DrawTrace?.RenderTargetView(resource, destination, Format(description), Format(view), (int*)view);

            CreateDesktopView(self, CreateRenderTargetViewSlot, desktop, view, destination);
            D3D12X.RenderTargets.Note(D3D12X.Objects, destination, resource);

            if (DebugLayer.TargetClears.Any)
            {
                DebugLayer.TargetClears.NoteView(destination, desktop, TargetShape(desktop));
            }
        }
        catch (Exception exception)
        {
            UnmanagedGuard.Caught(exception, "XboxDescriptorMethods.CreateRenderTargetView");
        }
    }

    [HotPath]
    [UnmanagedCallersOnly]
    public static void CreateDepthStencilView(XboxObject* self, nint resource, void* description, nint handle)
    {
        try
        {
            D3D12X.DrawTrace?.RenderTargetView(resource, Destination(handle));
            nint desktop = D3D12X.Objects.Unwrap(resource);
            byte* copy = stackalloc byte[DepthViewDescriptions.DepthStencilViewSize];
            void* view = DepthViewDescriptions.DepthStencilView(desktop, description, copy);
            CreateDesktopView(self, CreateDepthStencilViewSlot, desktop, view, Destination(handle));
            D3D12X.RenderTargets.NoteDepth(D3D12X.Objects, Destination(handle), resource, view);

            if (DebugLayer.TargetClears.Census)
            {
                DebugLayer.ReplacedTargets.NoteDepthView(Destination(handle), desktop, TargetShape(desktop));
            }

            if (DepthTwins.Enabled && view != null)
            {
                DepthTwins.NoteDepthView(Destination(handle), desktop, *(int*)view);
            }
        }
        catch (Exception exception)
        {
            UnmanagedGuard.Caught(exception, "XboxDescriptorMethods.CreateDepthStencilView");
        }
    }

    [HotPath]
    [UnmanagedCallersOnly]
    public static void CreateUnorderedAccessView(XboxObject* self, nint resource, nint counter, void* description, nint handle)
    {
        try
        {
            nint inner = self->Inner;
            var objects = D3D12X.Objects;
            nint desktop = objects.Unwrap(resource);
            byte* copy = stackalloc byte[TargetViewDescriptions.UnorderedAccessViewSize];
            var create = (delegate* unmanaged<nint, nint, nint, void*, nint, void>)ComObject.Slot(inner, CreateUnorderedAccessViewSlot);

            if (!TargetViewDescriptions.TakesUnorderedAccess(desktop, out var desc))
            {
                if (DepthTwins.Enabled && TwinView(inner, resource, desktop, desc, description, handle))
                {
                    return;
                }

                WarnNullUnorderedAccessView(desc, resource, description, handle);
                NullUnorderedAccessView(create, inner, description, copy, handle);

                return;
            }

            var record = ImageViewClasses.With(BufferRecords.Based(BufferRecords.UnorderedAccess(description), BufferRecords.GameAddress(resource), BufferRecords.FirstElement(description, unordered: true), desktop, ViewKind.UnorderedAccess), description);
            NoteFieldless(description, record, unordered: true);
            desktop = BufferViewRanges.Fit(resource, desktop, description, copy, true, out void* fitted);
            byte* raw = stackalloc byte[TargetViewDescriptions.UnorderedAccessViewSize];
            void* typed = fitted;
            fitted = RawBufferViews.View(fitted, raw, true, record, ref desktop);

            // A raw UAV takes no counter.
            if (fitted != typed)
            {
                counter = 0;
            }

            var destination = D3D12X.DescriptorHeaps.Destination(handle, record);

            if (TransientPoolViews.Enabled)
            {
                NoteUnorderedAccess(resource, description, handle, record);
            }

            void* view = TargetViewDescriptions.UnorderedAccessView(desktop, fitted, copy);

            if (DebugLayer.LineViewScan.Enabled)
            {
                BufferViewRanges.Check(resource, desktop, description, view, record.Base != 0);
            }

            if (RtStandIns.Enabled)
            {
                UavViews.Note(destination, desktop, desc, view);
            }

            create(inner, desktop, objects.Unwrap(counter), view, destination);
            Written(destination, resource, desktop, view, unordered: true);
        }
        catch (Exception exception)
        {
            UnmanagedGuard.Caught(exception, "XboxDescriptorMethods.CreateUnorderedAccessView");
        }
    }

    /// <summary>
    /// The buffer fields of the desktop-heap views copied in, into their records (<see cref="DesktopRecords"/>); how
    /// many carried fields.
    /// </summary>
    [HotPath]
    private static uint CopyFields(ShaderVisibleHeap heap, uint index, uint count, nint source, uint increment)
    {
        uint carried = 0;

        if (!DesktopRecords.Any)
        {
            return carried;
        }

        for (uint i = 0; i < count; i++)
        {
            if (DesktopRecords.TryFind(source + (nint)((ulong)i * increment), out var buffer))
            {
                heap.SetFields(index + i, buffer);
                DesktopRecords.NoteCarried(index + i, buffer);
                carried++;
            }
        }

        return carried;
    }

    /// <summary>A buffer view whose desc leaves its record without V# fields (<see cref="BufferRecords.NoteFieldless"/>).</summary>
    private static void NoteFieldless(void* description, BufferRecord record, bool unordered)
    {
        var view = (BufferViewDescription*)description;

        if (view == null || view->Dimension != BufferViewDescription.BufferDimension || record.IsBuffer || !BufferRecords.Noting(BufferRecords.FieldlessView))
        {
            return;
        }

        if (unordered)
        {
            NoteView("UAV", view->Format, view->UavFlags, view->UavElements, view->UavStride);

            return;
        }

        NoteView("SRV", view->Format, view->SrvFlags, view->SrvElements, view->SrvStride);
    }

    private static void NoteView(string kind, int format, int flags, uint elements, uint stride)
    {
        BufferRecords.NoteFieldless(BufferRecords.FieldlessView, $"a buffer {kind} of format {format}, flags 0x{flags:x}, {elements} elements of stride {stride}");
    }

    private static void NoteConstantBuffer(ConstantBufferViewDescription* description)
    {
        BufferRecords.NoteFieldless(BufferRecords.FieldlessConstantBuffer, $"a desktop CBV in a game heap (address 0x{description->Location:x}, 0x{description->Size:x} bytes)");
    }

    private static void NoteCopy(uint run, uint index, uint carried)
    {
        BufferRecords.NoteFieldless(BufferRecords.FieldlessCopy, $"{run} descriptors copied in from a desktop heap at game index {index}, {carried} carrying buffer fields");
    }

    /// <summary>With --watch-transient: the game range of a buffer UAV, by the desktop index its record names.</summary>
    private static void NoteUnorderedAccess(nint resource, void* description, nint handle, BufferRecord record)
    {
        var buffer = D3D12X.Objects.AsResource(resource);

        if (description == null || D3D12X.DescriptorHeaps.Find(handle, out _) is null)
        {
            return;
        }

        ulong game = 0;

        if (buffer != null && record.Records != 0)
        {
            game = buffer->GameAddress;
        }

        TransientPoolViews.Note(handle, game, ((BufferViewDescription*)description)->UavFirstElement, record);
    }

    /// <summary>
    /// <c>--depth-twins</c>: a UAV of a depth texture's depth or stencil plane goes to that plane's colour twin instead
    /// of a null view (<see cref="DepthTwins"/>); false when the view's format takes no twin or the twin cannot be made.
    /// </summary>
    private static bool TwinView(nint device, nint resource, nint depth, ResourceDescription desc, void* description, nint handle)
    {
        int viewFormat = description == null ? 0 : *(int*)description;

        if ((desc.Flags & AllowDepthStencil) == 0 || !DepthTwins.Twinned(viewFormat) || D3D12X.DescriptorHeaps.Find(handle, out uint index) is null)
        {
            return false;
        }

        var twin = DepthTwins.For(device, depth, desc, viewFormat);
        var game = D3D12X.Objects.AsResource(resource);

        if (twin is null || game == null)
        {
            return false;
        }

        twin.Home = game->HomeState;

        byte* view = stackalloc byte[TargetViewDescriptions.UnorderedAccessViewSize];
        Buffer.MemoryCopy(description, view, TargetViewDescriptions.UnorderedAccessViewSize, TargetViewDescriptions.UnorderedAccessViewSize);
        *(int*)view = twin.Format;
        *(int*)(view + DepthTwins.PlaneSliceOffset) = 0;

        // A stencil twin is R8_UINT: under --typed-image-views its record carries the uint class its stores need.
        nint destination = D3D12X.DescriptorHeaps.Destination(handle, ImageViewClasses.With(BufferRecord.None, view));
        ((delegate* unmanaged<nint, nint, nint, void*, nint, void>)ComObject.Slot(device, CreateUnorderedAccessViewSlot))(device, twin.Twin, 0, view, destination);
        DepthTwins.NoteView(twin, destination);

        return true;
    }

    /// <summary>A view desc's format (its first field), 0 without a desc.</summary>
    /// <summary>"WxH format F" of a desktop texture, for <see cref="DebugLayer.TargetClears"/>.</summary>
    private static string TargetShape(nint resource)
    {
        if (resource == 0)
        {
            return "null";
        }

        ResourceDescription read;
        _ = ((delegate* unmanaged<nint, ResourceDescription*, ResourceDescription*>)ComObject.Slot(resource, ResourceGetDescSlot))(resource, &read);

        return $"{read.Width}x{read.Height} format {read.Format}";
    }

    private static int Format(void* description)
    {
        if (description == null)
        {
            return 0;
        }

        return *(int*)description;
    }

    /// <summary>Warns once; a depth texture's UAV is also noted by resource and heap index (<see cref="DepthUavs"/>).</summary>
    /// <summary>Writes a null UAV in place of one the desktop resource cannot take, traced at the handle it went to.</summary>
    private static void NullUnorderedAccessView(delegate* unmanaged<nint, nint, nint, void*, nint, void> create, nint inner, void* description, byte* copy, nint handle)
    {
        nint destination = Destination(handle);
        create(inner, 0, 0, TargetViewDescriptions.NullUnorderedAccessView(description, copy), destination);
        D3D12X.DrawTrace?.UnorderedAccessView(0, destination);
    }

    private static void WarnNullUnorderedAccessView(ResourceDescription desc, nint resource, void* description, nint handle)
    {
        var game = D3D12X.Objects.AsResource(resource);

        if ((desc.Flags & AllowDepthStencil) != 0 && game != null && D3D12X.DescriptorHeaps.Find(handle, out uint index) is not null)
        {
            int viewFormat = description == null ? 0 : *(int*)description;
            DepthUavs.Note(game->GameAddress, index, $"depth texture at 0x{game->GameAddress:x} ({desc.Width}x{desc.Height}, format {desc.Format}, view format {viewFormat})");
        }

        D3D12X.Warnings.Warn("uav-no-flag", $"[d3d12_xs] CreateUnorderedAccessView: a texture made without ALLOW_UNORDERED_ACCESS ({desc.Width}x{desc.Height}, format {desc.Format}, flags 0x{desc.Flags:x}); null view written");
    }

    [HotPath]
    [UnmanagedCallersOnly]
    public static void CreateSampler(XboxObject* self, void* description, nint handle)
    {
        try
        {
            nint inner = self->Inner;
            var destination = D3D12X.DescriptorHeaps.Destination(handle, SamplerRecords.Of((DesktopSamplerDescription*)description));
            ((delegate* unmanaged<nint, void*, nint, void>)ComObject.Slot(inner, CreateSamplerSlot))(inner, description, destination);
        }
        catch (Exception exception)
        {
            UnmanagedGuard.Caught(exception, "XboxDescriptorMethods.CreateSampler");
        }
    }

    [HotPath]
    [UnmanagedCallersOnly]
    public static void CopyDescriptorsSimple(XboxObject* self, uint count, nint destination, nint source, int type)
    {
        try
        {
            nint inner = self->Inner;
            Copy(inner, count, destination, source, Increment(inner, type), type);
        }
        catch (Exception exception)
        {
            UnmanagedGuard.Caught(exception, "XboxDescriptorMethods.CopyDescriptorsSimple");
        }
    }

    /// <summary>
    /// CopyDescriptors(dest range count, dest starts, dest sizes, source range count, source starts, source sizes,
    /// type); null sizes mean ranges of one. The ranges are walked together and copied in runs both sides share.
    /// </summary>
    [HotPath]
    [UnmanagedCallersOnly]
    public static void CopyDescriptors(XboxObject* self, uint destinationCount, nint* destinations, uint* destinationSizes, uint sourceCount, nint* sources, uint* sourceSizes, int type)
    {
        try
        {
            nint inner = self->Inner;
            uint increment = Increment(inner, type);
            uint destination = 0;
            uint source = 0;
            uint destinationOffset = 0;
            uint sourceOffset = 0;

            while (destination < destinationCount && source < sourceCount)
            {
                uint destinationLeft = RangeSize(destinationSizes, destination) - destinationOffset;
                uint sourceLeft = RangeSize(sourceSizes, source) - sourceOffset;
                uint run = Least(destinationLeft, sourceLeft);
                Copy(inner, run, destinations[destination] + (nint)((ulong)destinationOffset * increment), sources[source] + (nint)((ulong)sourceOffset * increment), increment, type);
                destinationOffset += run;
                sourceOffset += run;

                if (run == destinationLeft)
                {
                    destination++;
                    destinationOffset = 0;
                }

                if (run == sourceLeft)
                {
                    source++;
                    sourceOffset = 0;
                }
            }
        }
        catch (Exception exception)
        {
            UnmanagedGuard.Caught(exception, "XboxDescriptorMethods.CopyDescriptors");
        }
    }

    /// <summary>
    /// One run: records copied inside a shader-visible heap; into one from elsewhere, desktop copies per desktop run
    /// with the records written; out of one, a desktop copy per descriptor from what its record names.
    /// </summary>
    [HotPath]
    private static void Copy(nint device, uint count, nint destination, nint source, uint increment, int type)
    {
        var heaps = D3D12X.DescriptorHeaps;
        var destinationHeap = heaps.Find(destination, out uint destinationIndex);
        var sourceHeap = heaps.Find(source, out uint sourceIndex);

        if (destinationHeap is not null && destinationHeap == sourceHeap)
        {
            uint inside = Least(destinationHeap.Count - destinationIndex, destinationHeap.Count - sourceIndex);
            destinationHeap.CopyRecords(destinationIndex, sourceIndex, Least(count, inside));

            return;
        }

        // Between the CPU-only view heaps and the shader-visible one: records name the same desktop heap.
        if (destinationHeap is not null && sourceHeap is not null && destinationHeap.Increment == sourceHeap.Increment && !(destinationHeap.Mirrored && sourceHeap.Mirrored))
        {
            uint inside = Least(destinationHeap.Count - destinationIndex, sourceHeap.Count - sourceIndex);
            destinationHeap.CopyRecords(destinationIndex, sourceHeap, sourceIndex, Least(count, inside));

            return;
        }

        if (destinationHeap is not null)
        {
            CopyInto(device, destinationHeap, destinationIndex, count, source, increment, type);

            return;
        }

        if (sourceHeap is null)
        {
            DesktopCopy(device, count, destination, source, type);
            CopiedViews(destination, source, count, increment, type);

            return;
        }

        for (uint i = 0; i < count; i++)
        {
            DesktopCopy(device, 1, destination + (nint)((ulong)i * increment), sourceHeap.Source(sourceIndex + i), type);
            CopiedViews(destination + (nint)((ulong)i * increment), sourceHeap.Source(sourceIndex + i), 1, increment, type);
        }
    }

    [HotPath]
    private static void CopyInto(nint device, ShaderVisibleHeap heap, uint index, uint count, nint source, uint increment, int type)
    {
        uint done = 0;
        count = Least(count, heap.Count - index);

        while (done < count)
        {
            uint run = Least(count - done, heap.RunLength(index + done));
            nint desktop = heap.Destination(index + done, run, BufferRecord.None);
            nint from = source + (nint)((ulong)done * increment);
            uint carried = 0;

            if (desktop == 0)
            {
                desktop = D3D12X.DescriptorHeaps.Sink();
                run = 1;
                DesktopCopy(device, run, desktop, from, type);
            }
            else
            {
                DesktopCopy(device, run, desktop, from, type);
                CopiedViews(desktop, from, run, increment, type);
                carried = CopyFields(heap, index + done, run, from, increment);

                if (RtStandIns.Enabled)
                {
                    UavViews.Carry(from, desktop, run, increment);
                }
            }

            if (carried < run && BufferRecords.Noting(BufferRecords.FieldlessCopy))
            {
                NoteCopy(run, index + done, carried);
            }

            done += run;
        }
    }

    /// <summary>The view just written at <paramref name="destination"/>: its dimension checked, a UAV traced, and with <c>--check-view-liveness</c> noted.</summary>
    [HotPath]
    private static void Written(nint destination, nint resource, nint desktop, void* view, bool unordered)
    {
        ViewDimensions.Check(desktop, view, unordered);

        if (unordered)
        {
            D3D12X.DrawTrace?.UnorderedAccessView(desktop, destination);
        }

        NoteTextureView(destination, resource, desktop, view, unordered);

        if (ViewLiveness.Noting)
        {
            var game = D3D12X.Objects.AsResource(resource);
            int home = 0;

            if (game != null)
            {
                home = game->HomeState;
            }

            ViewLiveness.Written(destination, desktop, ViewLiveness.Shape(desktop, view, unordered), home, view);
        }
    }

    /// <summary>
    /// An SRV or UAV of a registered texture (<see cref="TextureViewHandles"/>): nulled or written again before the
    /// texture is released. GPU-written textures' views were left out, and a released render target's UAV stayed.
    /// </summary>
    [HotPath]
    private static void NoteTextureView(nint destination, nint resource, nint desktop, void* view, bool unordered)
    {
        var game = D3D12X.Objects.AsResource(resource);

        if (game == null || desktop == 0)
        {
            return;
        }

        var texture = D3D12X.Textures.Registry.Find(game->GameAddress);

        if (texture is null || texture.Desktop != desktop)
        {
            return;
        }

        D3D12X.Textures.Views.Written(destination, texture, desktop, view, unordered);
    }

    /// <summary>With <c>--check-view-liveness</c>: view descriptors copied carry what was noted of them.</summary>
    [HotPath]
    private static void CopiedViews(nint destination, nint source, uint count, uint increment, int type)
    {
        if (type != ViewHeapType)
        {
            return;
        }

        if (ViewLiveness.Noting)
        {
            ViewLiveness.Copied(destination, source, count, increment);
        }

        D3D12X.Textures.Views.Copied(destination, source, count, increment);
    }

    [HotPath]
    private static void DesktopCopy(nint device, uint count, nint destination, nint source, int type)
    {
        var copy = (delegate* unmanaged<nint, uint, nint, nint, int, void>)ComObject.Slot(device, CopyDescriptorsSimpleSlot);
        copy(device, count, destination, source, type);
    }

    [HotPath]
    private static uint RangeSize(uint* sizes, uint range)
    {
        if (sizes == null)
        {
            return 1;
        }

        return sizes[range];
    }

    [HotPath]
    private static uint Least(uint first, uint second)
    {
        if (first < second)
        {
            return first;
        }

        return second;
    }

    [HotPath]
    private static uint Increment(nint device, int type)
    {
        return ((delegate* unmanaged<nint, int, uint>)ComObject.Slot(device, IncrementSlot))(device, type);
    }

    [HotPath]
    private static void CreateDesktopView(XboxObject* self, int slot, nint resource, void* description, nint destination)
    {
        nint inner = self->Inner;
        var create = (delegate* unmanaged<nint, nint, void*, nint, void>)ComObject.Slot(inner, slot);
        create(inner, resource, description, destination);
    }

    [HotPath]
    private static nint Destination(nint handle)
    {
        return D3D12X.DescriptorHeaps.Destination(handle);
    }
}
