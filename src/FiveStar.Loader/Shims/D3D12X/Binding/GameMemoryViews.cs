// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Diagnostics;
using FiveStar.Loader.Native;
using FiveStar.Loader.Shims.D3D12X.Pipelines;
using FiveStar.Shaders.Translation;

namespace FiveStar.Loader.Shims.D3D12X.Binding;

/// <summary>
/// The address map through which translated programs read game memory at console GPU addresses (root descriptors,
/// V#s and pointers the game builds; rdna_prelude <c>gpu_page</c>, <see cref="AddressMap"/>). Every desktop buffer
/// the address table gains gets a raw SRV and UAV per 256 MiB of it, each view covering up to 512 MiB (the most a
/// raw view's element count reaches), and the map names for each 64 KiB page the views of its chunk. XMem pages follow
/// the page tables as the console GPU's do: every map, remap or unmap (<see cref="XMemPhysicalMemory.ObservePages"/>)
/// points the page where the GPU addresses it (its GPU-only alias when it has one, else its CPU address) into views
/// over the section that holds its frame now, chunk by chunk of its heap window, or at the null views; a page whose
/// window has no heap (<see cref="SectionHeaps"/>: over budget, or refused) waits for one there. Buffers placed over pages
/// keep their frames; the map does not. A range the table replaces leaves the map before its buffer retires
/// (<see cref="Remove"/>). The map lives in write-combined system memory read at heap slot
/// <see cref="DescriptorWindow.AddressMapSlot"/>; the views take whole remap pages of the desktop heap. Ranges and
/// pages mapped before the game's view heap exists wait for it.
/// </summary>
internal sealed unsafe class GameMemoryViews
{
    /// <summary>4 GiB spans the map can hold: the boot's game memory spans about 20.</summary>
    private const int MapSpans = 64;

    /// <summary>Remap pages for the views: 16,384 descriptors, 8,192 SRV/UAV pairs.</summary>
    private const uint DescriptorPages = 4;

    private const ulong ChunkBytes = 256UL << 20;
    private const ulong ViewBytes = 512UL << 20;
    private const uint DwordBytes = 4;
    private const uint PairSize = 2;
    private const int CommonState = 0;

    /// <summary>ID3D12Resource::GetDesc and D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS (d3d12.h).</summary>
    private const int GetDescSlot = 10;
    private const int AllowUnorderedAccess = 0x4;

    private const ulong PageMask = XMemPhysicalMemory.PageSize - 1;

    /// <summary>The window id in a waiting page's offset field (<see cref="WindowFeedback.WaitTag"/>).</summary>
    private const uint WindowIdMask = 0xFFFF;

    /// <summary>Pages <see cref="Maps"/> checks one by one.</summary>
    private const ulong PagesChecked = 256;

    /// <summary>D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS | ALLOW_CROSS_ADAPTER, which a buffer on a section heap needs.</summary>
    private const int SectionBufferFlags = 0x14;

    private readonly object _gate = new();
    private readonly List<AddressRange> _pending = [];
    private readonly Dictionary<(nint Section, ulong Window, ulong Chunk), uint> _sectionViews = [];
    private readonly Dictionary<(nint Section, ulong Start), HashSet<ulong>> _waiting = [];
    private readonly Queue<(nint Section, ulong Start)> _opened = new();
    private readonly Dictionary<(nint Section, ulong Window, ulong Chunk), uint> _pairs = [];
    private readonly Dictionary<(nint Section, ulong Window, ulong Chunk), nint> _buffers = [];
    private readonly Dictionary<(nint Section, ulong Start), HashSet<ulong>> _resident = [];
    private readonly Dictionary<ulong, (nint Section, ulong Start)> _pageWindow = [];
    private readonly Dictionary<AddressRange, List<uint>> _rangePairs = [];
    private readonly Dictionary<(nint Section, ulong Start), nint> _twinSources = [];

    /// <summary>The desktop buffer and byte offset of each view pair's byte 0 (<see cref="Locate"/>).</summary>
    private readonly Dictionary<uint, (nint Buffer, ulong Offset)> _viewSources = [];
    private readonly XMemPhysicalMemory _memory;
    private readonly SectionHeaps _heaps;
    private readonly AddressMap _map = new(MapSpans, DescriptorWindow.UnmappedSlot);
    private readonly OnceLog _warnings;
    private readonly LoaderLog _log;
    private nint _device;
    private nint _cpuStart;
    private uint _increment;
    private byte* _data;
    private uint _next;
    private uint _end;
    private bool _following;
    private long _replacedRanges;
    private ulong _replacedPages;

    public GameMemoryViews(XMemPhysicalMemory memory, SectionHeaps heaps, OnceLog warnings, LoaderLog log)
    {
        _memory = memory;
        _heaps = heaps;
        _warnings = warnings;
        _log = log;
    }

    /// <summary>Asks for a window by id (<see cref="WindowResidency.Ask"/>); null without --evict-windows.</summary>
    public Action<int>? Ask { get; set; }

    /// <summary>The map entry programs read for <paramref name="address"/>: view index and offset field; (0, 0) before the map exists.</summary>
    public (uint View, uint Field) Entry(ulong address)
    {
        lock (_gate)
        {
            if (_data == null)
            {
                return (0, 0);
            }

            return AddressMap.Read(new ReadOnlySpan<byte>(_data, _map.Bytes), address);
        }
    }

    /// <summary>
    /// Whether every page of [<paramref name="start"/>, + <paramref name="bytes"/>) has a view in the map now, as
    /// programs read through it (gpu_page); false before the map exists. Past <see cref="PagesChecked"/> pages only the
    /// first and the last are looked at.
    /// </summary>
    public bool Maps(ulong start, ulong bytes)
    {
        ulong first = start & ~PageMask;
        ulong last = (start + Math.Max(bytes, 1) - 1) & ~PageMask;
        ulong pages = ((last - first) / XMemPhysicalMemory.PageSize) + 1;

        lock (_gate)
        {
            if (_data == null)
            {
                return false;
            }

            var map = new ReadOnlySpan<byte>(_data, _map.Bytes);

            if (pages > PagesChecked)
            {
                return Viewed(map, first) && Viewed(map, last);
            }

            for (ulong page = first; page <= last; page += XMemPhysicalMemory.PageSize)
            {
                if (!Viewed(map, page))
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// Whether the page has a view. One that waits for its window's heap (<see cref="WindowFeedback"/>) asks for the
    /// window (<see cref="Ask"/>): a view over it is about to be read, and while the window is closed the view keeps its
    /// heap index, so no program would read the page through the map and ask itself.
    /// </summary>
    private bool Viewed(ReadOnlySpan<byte> map, ulong page)
    {
        var (view, field) = AddressMap.Read(map, page);

        if (view == DescriptorWindow.UnmappedSlot && (field & WindowFeedback.WaitTag) != 0)
        {
            Ask?.Invoke((int)(field & WindowIdMask));
        }

        return view != 0 && view != DescriptorWindow.UnmappedSlot;
    }

    /// <summary>Maps the pages of a range the address table gained (<see cref="AddressTable.Observe"/>).</summary>
    public void Add(AddressRange range)
    {
        if (range.Resource == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_data == null)
            {
                _pending.Add(range);

                return;
            }

            Place(range);
        }
    }

    /// <summary>
    /// Creates the map for the desktop view heap at <paramref name="cpuStart"/>, takes its views' descriptors from
    /// <paramref name="remap"/> and maps the ranges added so far.
    /// </summary>
    public void Attach(nint device, nint cpuStart, uint increment, DescriptorRemap remap)
    {
        lock (_gate)
        {
            int state = CommonState;
            var properties = GpuUploadHeaps.For(device, new HeapProperties { Type = HeapProperties.Custom, CpuPageProperty = HeapProperties.WriteCombine, MemoryPool = HeapProperties.SystemPool }, ref state);
            nint buffer = DesktopCalls.CreateBuffer(device, properties, (ulong)_map.Bytes, 0, state);
            byte* data = buffer == 0 ? null : DesktopCalls.Map(buffer);
            uint first = remap.Reserve(DescriptorPages);

            if (data == null || first == DescriptorRemap.Unmapped)
            {
                _log.Warn("[d3d12_xs] address map: no buffer or descriptors; programs read game memory at console addresses as zeros");

                return;
            }

            _map.Clear(new Span<byte>(data, _map.Bytes));
            var view = BufferViewDescription.RawShaderResource((uint)_map.Bytes / DwordBytes);
            DesktopCalls.ShaderResourceView(device, buffer, &view, cpuStart + (nint)(DescriptorWindow.AddressMapSlot * increment));

            _device = device;
            _cpuStart = cpuStart;
            _increment = increment;
            _data = data;

            _next = first;
            _end = first + (DescriptorPages * DescriptorRemap.PageSize);
            _log.Detail($"  [d3d12_xs] address map: 0x{_map.Bytes:x} bytes for {MapSpans} spans, views from desktop index {first}, {_pending.Count} ranges waiting");

            foreach (var range in _pending)
            {
                Place(range);
            }

            _pending.Clear();
            int pages = 0;
            _memory.VisitMapped((address, _) =>
            {
                Follow(address);
                pages++;
            });
            _log.Detail($"  [d3d12_xs] address map: {pages} mapped XMem pages follow their frames");
        }
    }

    /// <summary><see cref="XMemPhysicalMemory.ObservePages"/>: points the page at <paramref name="address"/> at its new frame.</summary>
    public void PageChanged(nint address, ulong previous, ulong frame)
    {
        lock (_gate)
        {
            if (_data != null)
            {
                Follow(address);
            }
        }
    }

    /// <summary>
    /// The map entry of the page the CPU maps at <paramref name="address"/>, where the GPU addresses it: the game maps
    /// pages only into the CPU range of a pair and gives the GPU the alias (docs/physical-memory.md).
    /// </summary>
    private void Follow(nint address)
    {
        nint page = (nint)((ulong)address & ~PageMask);
        nint alias = _memory.GpuAlias(page);
        ulong gpuPage = (ulong)page;

        if (alias != 0)
        {
            gpuPage = (ulong)alias;
        }

        // A window this page opens (or another thread opened) maps its waiting pages after this one.
        bool outer = !_following;
        _following = true;
        MapPage(gpuPage);

        if (outer)
        {
            FollowOpened();
            _following = false;
        }
    }

    /// <summary>
    /// <see cref="SectionHeaps.WindowOpened"/>: the pages that waited for a heap in that window (a section backed
    /// lazily, or a placed buffer that opened it) now get their entries; from the window's thread, or after the page
    /// being mapped when the map opened it itself.
    /// </summary>
    public void WindowOpened(nint section, ulong start, ulong bytes)
    {
        lock (_gate)
        {
            if (_data == null)
            {
                return;
            }

            _opened.Enqueue((section, start));

            if (!_following)
            {
                _following = true;
                FollowOpened();
                _following = false;
            }
        }
    }

    /// <summary>Maps the pages that waited for the windows opened meanwhile, at the frames mapped there now.</summary>
    private void FollowOpened()
    {
        while (_opened.TryDequeue(out var window))
        {
            if (!_waiting.Remove(window, out var pages))
            {
                continue;
            }

            foreach (ulong page in pages)
            {
                MapPage(page);
            }
        }
    }

    /// <summary>One page's entry: the section view of the frame mapped there now, else the null views.</summary>
    private void MapPage(ulong page)
    {
        var map = new Span<byte>(_data, _map.Bytes);
        uint pair = DescriptorWindow.UnmappedSlot;
        ulong viewStart = page;
        uint tag = 0;

        if (_memory.TryLocate((nint)page, out var chunk, out ulong offset))
        {
            pair = SectionView(page, chunk, offset, out ulong inView, out tag);
            viewStart = page - inView;
        }

        if (pair == DescriptorWindow.UnmappedSlot)
        {
            viewStart = page;
            Untrack(page);
        }

        bool mapped = tag == 0 ? _map.Map(map, page, XMemPhysicalMemory.PageSize, pair, viewStart) : _map.Mark(map, page, pair, tag);

        if (!mapped)
        {
            _warnings.Warn("address-map-spans", $"[d3d12_xs] address map: no page table left for 0x{page:x}; it reads as zeros");
        }
    }

    /// <summary>
    /// The view pair over the chunk of the section heap window that holds byte <paramref name="offset"/> of
    /// <paramref name="chunk"/>'s section, made on first use, and the byte's offset in it. The null views when the
    /// window has no heap: <paramref name="page"/> then waits for one (<see cref="WindowOpened"/>), and with
    /// <see cref="WindowFeedback"/> its entry carries <paramref name="tag"/>, the window's wait tag.
    /// </summary>
    private uint SectionView(ulong page, XMemPageChunk chunk, ulong offset, out ulong inView, out uint tag)
    {
        inView = 0;
        tag = 0;
        nint heap = _heaps.Heap(_device, chunk, offset, SectionHeapUse.Map, out ulong heapOffset, out ulong heapBytes);
        (nint Section, ulong Start) window = (chunk.Section, offset - heapOffset);

        if (heap == 0)
        {
            Wait(window, page);
            tag = WaitTag(window);
            _warnings.Warn("address-map-sections", $"[d3d12_xs] address map: no heap over part of section 0x{chunk.Section:x}; its pages read as zeros until one opens");

            return DescriptorWindow.UnmappedSlot;
        }

        // A view starts at a chunk of its heap and ends with the heap.
        ulong index = heapOffset / ChunkBytes;
        ulong viewOffset = index * ChunkBytes;
        inView = heapOffset - viewOffset;
        var key = (window.Section, window.Start, index);

        if (_sectionViews.TryGetValue(key, out uint pair))
        {
            Track(page, window, pair);

            return pair;
        }

        ulong bytes = Math.Min(ViewBytes, heapBytes - viewOffset);
        nint buffer = DesktopCalls.CreatePlacedBuffer(_device, heap, viewOffset, bytes, SectionBufferFlags, CommonState);
        pair = DescriptorWindow.UnmappedSlot;

        if (buffer == 0 || (!_pairs.ContainsKey(key) && _next + PairSize > _end))
        {
            _warnings.Warn("address-map-section-views", $"[d3d12_xs] address map: no view over section 0x{chunk.Section:x}; its pages read as zeros");
        }
        else
        {
            // A window's pair outlives its buffer: a window released (WindowResidency) and opened again writes it anew.
            if (!_pairs.TryGetValue(key, out pair))
            {
                pair = _next;
                _next += PairSize;
                _pairs[key] = pair;
            }

            _buffers[key] = buffer;
            WriteViews(buffer, 0, bytes, pair, true);
            ReadTwin(window, heap, heapBytes, viewOffset, bytes, pair);
        }

        _sectionViews[key] = pair;
        Track(page, window, pair);

        return pair;
    }

    /// <summary><c>--vram-twin-ceiling</c>: the pair's SRV reads the twin of its heap window in video memory (<see cref="VramTwins"/>).</summary>
    private void ReadTwin((nint Section, ulong Start) window, nint heap, ulong heapBytes, ulong viewOffset, ulong bytes, uint pair)
    {
        if (!VramTwins.Enabled || (VramTwins.PoolOnly && heapBytes == VramTwins.GpuPoolWindow))
        {
            return;
        }

        if (!_twinSources.TryGetValue(window, out nint source))
        {
            source = DesktopCalls.CreatePlacedBuffer(_device, heap, 0, heapBytes, SectionBufferFlags, CommonState);
            _twinSources[window] = source;
        }

        nint twin = VramTwins.Of(_device, source, heapBytes);

        if (twin == 0)
        {
            return;
        }

        var read = BufferViewDescription.RawShaderResource((uint)(bytes / DwordBytes), viewOffset / DwordBytes);
        DesktopCalls.ShaderResourceView(_device, twin, &read, _cpuStart + (nint)(pair * _increment));
    }

    private void Wait((nint Section, ulong Start) window, ulong page)
    {
        if (!_waiting.TryGetValue(window, out var pages))
        {
            pages = [];
            _waiting[window] = pages;
        }

        pages.Add(page);
    }

    /// <summary>The offset field of a page waiting for <paramref name="window"/>: its id and <see cref="WindowFeedback.WaitTag"/>; 0 without feedback.</summary>
    private uint WaitTag((nint Section, ulong Start) window)
    {
        if (!WindowFeedback.Enabled)
        {
            return 0;
        }

        var record = _heaps.Find(window.Section, window.Start);

        if (record is null || record.Id == 0)
        {
            return 0;
        }

        return WindowFeedback.WaitTag | (uint)record.Id;
    }

    /// <summary>With <see cref="WindowFeedback"/>: <paramref name="page"/> reads through a view of <paramref name="window"/> now.</summary>
    private void Track(ulong page, (nint Section, ulong Start) window, uint pair)
    {
        if (!WindowFeedback.Enabled)
        {
            return;
        }

        if (pair == DescriptorWindow.UnmappedSlot)
        {
            Untrack(page);

            return;
        }

        if (_pageWindow.TryGetValue(page, out var previous) && previous != window)
        {
            _resident[previous].Remove(page);
        }

        _pageWindow[page] = window;

        if (!_resident.TryGetValue(window, out var pages))
        {
            pages = [];
            _resident[window] = pages;
        }

        pages.Add(page);
    }

    private void Untrack(ulong page)
    {
        if (WindowFeedback.Enabled && _pageWindow.Remove(page, out var window))
        {
            _resident[window].Remove(page);
        }
    }

    /// <summary>
    /// <see cref="WindowResidency"/> releases <paramref name="window"/>: its pages wait with its tag from now on (reading
    /// zeros), and its view buffers come back, to be released with its heap once the GPU is past them.
    /// </summary>
    public List<nint> Close(SectionWindow window)
    {
        lock (_gate)
        {
            (nint Section, ulong Start) key = (window.Chunk.Section, window.Start);
            var buffers = new List<nint>();

            foreach (var view in _sectionViews.Keys.Where(view => view.Section == key.Section && view.Window == key.Start).ToList())
            {
                _sectionViews.Remove(view);

                if (_buffers.Remove(view, out nint buffer))
                {
                    buffers.Add(buffer);
                }

                if (_pairs.TryGetValue(view, out uint pair))
                {
                    _viewSources.Remove(pair);
                }
            }

            // --vram-twin-ceiling: the twin stops copying from the heap, and its source buffer goes with the views.
            if (_twinSources.Remove(key, out nint twinSource))
            {
                VramTwins.Forget(twinSource);
                buffers.Add(twinSource);
            }

            if (_data == null || !_resident.Remove(key, out var pages))
            {
                return buffers;
            }

            var map = new Span<byte>(_data, _map.Bytes);
            uint tag = WaitTag(key);

            foreach (ulong page in pages)
            {
                _pageWindow.Remove(page);
                Wait(key, page);
                _map.Mark(map, page, DescriptorWindow.UnmappedSlot, tag);
            }

            return buffers;
        }
    }

    /// <summary>Views over each chunk of the range's buffer and the map entries of its pages.</summary>
    private void Place(AddressRange range)
    {
        bool writable = (DesktopFlags(range.Resource) & AllowUnorderedAccess) != 0;
        var map = new Span<byte>(_data, _map.Bytes);

        // Read-only buffers too: what the CPU wrote reads stale, as everywhere with twins.
        if (range.Size >= VramTwins.SmallestRange)
        {
            VramTwins.Whole(_device, range.Resource, range.Size);
        }

        for (ulong start = 0; start < range.Size; start += ChunkBytes)
        {
            if (_next + PairSize > _end)
            {
                _warnings.Warn("address-map-views", "[d3d12_xs] address map: no descriptors left; later game memory reads as zeros");

                return;
            }

            uint pair = _next;
            _next += PairSize;
            WriteViews(range.Resource, start, Math.Min(ViewBytes, range.Size - start), pair, writable);
            RangePairs(range).Add(pair);

            if (!_map.Map(map, range.GameAddress + start, Math.Min(ChunkBytes, range.Size - start), pair, range.GameAddress + start))
            {
                _warnings.Warn("address-map-spans", $"[d3d12_xs] address map: no page table left for 0x{range.GameAddress + start:x}; it reads as zeros");
            }
        }
    }

    private List<uint> RangePairs(AddressRange range)
    {
        if (!_rangePairs.TryGetValue(range, out var pairs))
        {
            pairs = [];
            _rangePairs[range] = pairs;
        }

        return pairs;
    }

    /// <summary>
    /// <see cref="AddressTable"/> replaced <paramref name="range"/> (the game placed a new resource over its pages): its
    /// views become null views and each of its pages follows its frame again, or names the null views, before its buffer
    /// retires (<see cref="RetiredAddressBuffers"/>). The range replacing it maps its own pages after this. Left as they
    /// were, pages outside the new range named views of a buffer about to be released.
    /// </summary>
    public void Remove(AddressRange range)
    {
        if (range.Resource == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_data == null)
            {
                _pending.Remove(range);

                return;
            }

            VramTwins.Forget(range.Resource);

            if (_rangePairs.Remove(range, out var pairs))
            {
                foreach (uint pair in pairs)
                {
                    WriteViews(0, 0, 0, pair, false);
                }
            }

            ulong first = range.GameAddress & ~PageMask;
            bool outer = !_following;
            _following = true;

            for (ulong page = first; page < range.GameAddress + range.Size; page += XMemPhysicalMemory.PageSize)
            {
                MapPage(page);
            }

            if (outer)
            {
                FollowOpened();
                _following = false;
            }

            Unmapped(range, (range.GameAddress + range.Size - first + PageMask) / XMemPhysicalMemory.PageSize);
        }
    }

    /// <summary>The first replaced range, and then each power of two, logs the counts.</summary>
    private void Unmapped(AddressRange range, ulong pages)
    {
        _replacedRanges++;
        _replacedPages += pages;

        if ((_replacedRanges & (_replacedRanges - 1)) == 0)
        {
            _log.Info($"[d3d12_xs] address map: {_replacedRanges} replaced ranges left the map before their buffers retire ({_replacedPages} pages pointed at their frames or the null views; last 0x{range.GameAddress:x}, 0x{range.Size:x} bytes)");
        }
    }

    /// <summary>
    /// The desktop buffer the map reads <paramref name="address"/> from and the byte's offset in it, as a program reads
    /// it now (<see cref="DebugLayer.DispatchCaptures"/> copies game memory with it); false for a page without a view.
    /// </summary>
    public bool Locate(ulong address, out nint buffer, out ulong offset)
    {
        buffer = 0;
        offset = 0;

        lock (_gate)
        {
            if (_data == null)
            {
                return false;
            }

            var (view, field) = AddressMap.Read(new ReadOnlySpan<byte>(_data, _map.Bytes), address);

            if (!_viewSources.TryGetValue(view, out var source) || source.Buffer == 0)
            {
                return false;
            }

            buffer = source.Buffer;
            offset = source.Offset + field + (address & PageMask);

            return true;
        }
    }

    private void WriteViews(nint resource, ulong offset, ulong bytes, uint pair, bool writable)
    {
        _viewSources[pair] = (resource, offset);
        uint dwords = (uint)(bytes / DwordBytes);
        var read = BufferViewDescription.RawShaderResource(dwords, offset / DwordBytes);
        var write = BufferViewDescription.RawUnorderedAccess(dwords, offset / DwordBytes);
        nint handle = _cpuStart + (nint)(pair * _increment);
        DesktopCalls.ShaderResourceView(_device, resource, &read, handle);

        if (!writable)
        {
            // An upload buffer (list slot 148) takes no UAV: writes there go nowhere.
            write = BufferViewDescription.RawUnorderedAccess(0);
            resource = 0;
        }

        DesktopCalls.UnorderedAccessView(_device, resource, &write, handle + (nint)_increment);
    }

    /// <summary>The desktop resource's D3D12_RESOURCE_FLAGS.</summary>
    private static int DesktopFlags(nint resource)
    {
        ResourceDescription desc;
        _ = ((delegate* unmanaged<nint, ResourceDescription*, ResourceDescription*>)ComObject.Slot(resource, GetDescSlot))(resource, &desc);

        return desc.Flags;
    }
}
