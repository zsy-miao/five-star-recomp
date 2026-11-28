# Physical memory

The console lets a title own physical pages and map them into address space it reserved: `xmem.dll`
`XMemVirtualAlloc`, `XMemAllocatePhysicalPages`, `XMemMapPhysicalPages`, `XMemMapPhysicalPagesScatter` and
`XMemFreePhysicalPages`. The loader reproduces this with Windows placeholders and pagefile-backed sections, so a page
mapped at two addresses is one page and a remapped page keeps its contents. Code: `src/FiveStar.Loader/Shims/XMem*.cs`.

## Signatures

Source: **GDK** is the public GDK reference, **winnt.h** the Windows SDK header, **call sites** the game executable.

| Function | Signature | Source |
|---|---|---|
| `XMemVirtualAlloc` | `PVOID (PVOID address, SIZE_T size, DWORD allocationType, uint64 xmemFlags, DWORD protect)` | GDK overview: VirtualAlloc's parameters plus an XMem flags field; order and widths from 23 call sites |
| `XMemAllocatePhysicalPages` | `BOOL (DWORD flags, ULONG_PTR* count, ULONG_PTR* frames)`; `*count` returns the number allocated | call sites (the game frees and fails when `*count` comes back short) |
| `XMemFreePhysicalPages` | `BOOL (ULONG_PTR count, ULONG_PTR* frames)` | call sites |
| `XMemMapPhysicalPages` | `PVOID (PVOID address, ULONG_PTR count, PULONG_PTR frames)`; NULL on failure; a NULL array unmaps and keeps the reservation; mapped pages are replaced | GDK |
| `XMemMapPhysicalPagesScatter` | `BOOL (PVOID* addresses, ULONG_PTR count, PULONG_PTR frames)`; a NULL array unmaps all, a 0 entry unmaps one | GDK; the game breaks into the debugger on FALSE |
| `XMemGetWorkingSetStatistics` | `BOOL (DWORD workingSetFlags, XMEM_WORKING_SET_STATISTICS* statistics)` | GDK |

A physical page is 64 KiB (GDK). Entries of a frame array count 4 KiB frames: the game turns one into a page index
with bits 4..35 and stores indices in 21-bit fields, so the loader hands out multiples of 16, never 0, below 2^25.

## Flags seen in the executable

`allocationType` and `protect` bits are named in winnt.h: MEM_64K_PAGES is MEM_LARGE_PAGES | MEM_PHYSICAL
(0x20400000); PAGE_GRAPHICS_NOACCESS 0x800, _READONLY 0x1000, _READWRITE 0x2000, _EXECUTE_READ 0x8000, _COHERENT 0x20000 and
_NOCACHE 0x40000 are the GPU's view; PAGE_WRITECOMBINE is 0x400. `xmemFlags` and the page-allocation `flags` are not public.

| Site | allocationType | xmemFlags | protect | Then |
|---|---|---|---|---|
| 0x141E86400 | 0x42000 MEM_RESERVE \| MEM_RESERVE_PLACEHOLDER | 0x7 | 0x1 | splits it with `VirtualFree(MEM_RELEASE \| MEM_PRESERVE_PLACEHOLDER)` into five pieces |
| same | 0x20406000 MEM_RESERVE \| MEM_REPLACE_PLACEHOLDER \| MEM_64K_PAGES | 0x27 | 0x1004, 0x1404, 0x8404, 0x2404, 0x2004 | allocates pages (flags 0x280) and maps each piece |
| 0x141E6C110 | 0x20402000 MEM_RESERVE \| MEM_64K_PAGES | 0x21, 0x3, 0x23 | 0x4, 0x801, 0x2004 | pages with flags 0x380; the 0x21 range is filled by `XMemMapPhysicalPagesScatter` at 0x140F2CB60 |
| 0x141E1CFE0 | 0x20402000 | 0x21, 0x3 | 0x4, 0x801 | as 0x141E6C110 |
| 0x141D6E600, 0x145BA6C10 | 0x20403000 MEM_RESERVE \| MEM_COMMIT \| MEM_64K_PAGES | 0x181 | 0x4 | used directly |
| 0x142478640 | 0x20401000 MEM_COMMIT \| MEM_64K_PAGES, address 0 | 0x283 | 0x42404 | used directly |
| 0x14193E1A0, 0x141E1D168..0x141E1D4BD | 0x20003000 MEM_RESERVE \| MEM_COMMIT \| MEM_LARGE_PAGES | 0x283, 0x183 | 0x2004, 0x2404, 0x22004 | used directly |

## Mapping

```mermaid
flowchart LR
    A[XMemAllocatePhysicalPages] --> S[(64 MiB pagefile sections)]
    R[XMemVirtualAlloc MEM_64K_PAGES] --> P[placeholder range]
    M[map frame at VA] --> P
    P -->|split 64 KiB, MapViewOfFile3 MEM_REPLACE_PLACEHOLDER| V[view of the page's section]
    V -->|UnmapViewOfFile2 MEM_PRESERVE_PLACEHOLDER| P
```

- **Pages** (`XMemPageStore`): sections of 1024 pages (or a multiple, for a larger request) made with
  `CreateFileMapping2(SEC_COMMIT)` as pages are first needed. A request for one page takes a freed page first; a
  request for several takes fresh consecutive pages of one section (a group, `XMemPageGroup`), and which page backs
  which of its frames is decided at the frame's first mapping: the page at the mapped address's offset from the
  origin of the group's first mapping, the start of the game placeholder the reservation was split from. So the GPU
  pool the game maps from one allocation, later piece first (0x141E86400), lies in address order in one section,
  and the D3D12 shim places one buffer over it. The store keeps a read-write view of each whole section; freed pages
  are zeroed through it.
- **Reservations** (`XMemReservation`): MEM_64K_PAGES without MEM_COMMIT is a placeholder from
  `VirtualAlloc2(MEM_RESERVE_PLACEHOLDER)`, or the game's own placeholder piece when MEM_REPLACE_PLACEHOLDER is set.
  Mapping splits the 64 KiB slot off (`VirtualFree(MEM_RELEASE | MEM_PRESERVE_PLACEHOLDER)`) and replaces it with a
  view; a remap unmaps the old view first. Unmapping a whole reservation through `XMemMapPhysicalPages` merges it back
  into one placeholder (`MEM_COALESCE_PLACEHOLDERS`).
- **GPU aliases**: a GPU-only reservation (PAGE_NOACCESS plus PAGE_GRAPHICS_* bits) of the same size as the
  CPU-accessible mappable reservation made just before it is that range's GPU alias: the game maps pages only into
  the CPU range and addresses the GPU at the same offset in the alias ([gpu-resources.md](gpu-resources.md)).
  `TryLocate` and `CpuAddress` resolve alias addresses to the CPU range.
- **Other types**: MEM_RESERVE_PLACEHOLDER makes a placeholder for the game to split; everything else goes to
  `VirtualAlloc` (or `VirtualAlloc2` with MEM_REPLACE_PLACEHOLDER) with MEM_RESERVE/MEM_COMMIT and the low protection
  byte.
- **4 GiB lines**: a reservation or allocation whose address the game leaves to the system starts on a 4 GiB line
  (`AlignedReservations`: VirtualAlloc2 with MEM_ADDRESS_REQUIREMENTS), as on the console. The game rebuilds addresses in
  that memory as the base's high dword with a 32-bit offset: with a base off the line (0x1ce1f4c0000) the address of base +
  0xc000 became 0x1ce0000c000, another allocation, and three world loads faulted reading or writing there (w62, w70c, w75).
- **XMemAlloc alignment**: the alignment code of XALLOC_ATTRIBUTES (bits 24..28, 1 << (code - 1), up to 1 GiB) is
  honoured; a block aligned past 64 KiB is a VirtualAlloc2 range of its own on that alignment.
- **Protection**: a view is read-only when the reservation's CPU protection is PAGE_READONLY and read-write
  otherwise, including GPU-only ranges (0x801), which the D3D12 shim reaches through the CPU. Cache attributes cannot
  differ between views of a section and are dropped; the console also refuses aliases with differing cache attributes.
- **Threads**: allocating and freeing pages lock; mapping does not lock or allocate. Reservation lookup reads a sorted
  array that is replaced on each new reservation.

The game frees reservations with its own `VirtualFree` import, which the loader does not see; a new reservation drops
every table entry it overlaps.

## The streaming pool

A boot allocates pages three times: 11,360 and 2,960 pages with flags 0x280 (the GPU pool of 0x141E86400) and 99,776
pages with flags 0x380 (6.1 GiB) right before a reservation of exactly that size (flags 0x23), into which the pool is
mapped first, so its frames bind in address order. The game then maps pool pages again, one call per page
(`XMemMapPhysicalPages`, about 57,000 calls in the ten seconds after the pad pairs), into a second 3.5 GiB reservation
where its resources live. There a resource's pages are whichever pool pages the game picked: consecutive frames on
the console are not needed, since its GPU translates every 64 KiB page. A slot-133 buffer there whose pages are not
consecutive in the section is several desktop buffers ([d3d12x-shim.md](d3d12x-shim.md#game-memory-on-the-gpu)); no
binding of frames to section pages can make both reservations contiguous, and the desktop refuses tile mappings onto
these heaps.

DirectStorage writes into the streaming heap by page list: a request with option bit 0x200000 (0x144ffd6bc) has as
destination a pointer into the game's page table, one frame number per 64 KiB page (0x144ffd8c0), and the byte
offset into the first page in the 16-bit field at request offset 0x10. `PhysicalPageSink` copies the data into those
frames through the store (`XMemPhysicalMemory.WriteFrames`).

**CopyPageMappingsBatchX (queue slot 31).** The game also remaps pages through the queue:
(count, batches, pool, pool pages, flags), call at 0x140f2cc7f. VIctory named the method and the layouts. A batch is
(destination, range count, ranges), and a range is (kind, start page, count) in 64 KiB pages:

| Kind | Mapping |
|---|---|
| 0 | pool pages from start, in order |
| 1 | pool page start at every page |
| 2 | unmapped |
| 3 | unchanged |

On the console the remap is queue work, after the queue's earlier work. `QueuePageMappings` waits until the desktop
queue has finished everything submitted so far (its shim fence), then remaps on the CPU
(`XMemPhysicalMemory.CopyPageMappings`) before the call returns. Until then the slot was a stub, and the destinations
kept their old pages.

The address map that translated programs reach game memory through follows the page tables, as the console GPU's
do: every map, remap and unmap (this one, `XMemMapPhysicalPages` and the scatter form) points the page, where the GPU
addresses it (its GPU-only alias when it has one, else its CPU address), into views over the section that holds its
frame now, or at the null views ([shader-translation.md](shader-translation.md#resources)). Programs therefore read
and write the frame the CPU sees. That includes pages under a slot-133 buffer that had no mapped page when it was made
(a committed buffer of its own): once the game maps pages there, programs reach them while desktop views and copies
on the resource keep its committed memory.

GPU limit: desktop buffers placed over the pages when a slot-133 resource was created keep the pages of that moment,
and so do the desktop views, copies and indirect arguments made on them. A later remap is seen by the CPU, by the
address map and by resources created after it, not by those. Once the old frame is mapped again at another address,
GPU writes through such a buffer change the memory there: a copy into the buffer reaches the other address and not
the buffer's own (RTX 5070 and the loader tests). The loader reports both (`StaleGpuPages`, always on): a remap or
unmap under a buffer of the address table as `[gpu pages]` with the GPU and CPU addresses and both frames, and the old
frame mapped again with the address the buffer's writes now land at; the first 16 of each in full, then totals at
every power of two. Such writes corrupt the new owner of the frame but cannot fault: the section heap stays open, so
the frame is still valid memory.

A slot-133 buffer placed over pages another one covers replaces that range in the address table (w170: 33 times).
The old range leaves the address map first: its views become null views and each of its pages follows its frame
again (`GameMemoryViews.Remove`). Its desktop buffer retires (`RetiredAddressBuffers`) and is released once
`RetirePresents` (8) presents have passed and every queue has completed what it had submitted by then. Before this
it was released at once, while lists in flight could still write through it or through its views (a GPU page fault,
device removed). Logged as `[d3d12_xs] address map: N replaced ranges left the map ...` and `[d3d12_xs] address
table: N replaced buffers released once the queues were past them`, at the first and then every power of two.

## Working set statistics

The structure is 0x78 bytes (callers zero that much). One caller (flags 0x20000001) takes qword 0 minus qword 1 and
compares it with 100 MiB, so the pair is total and used. The loader writes total = resident set + free physical
memory and used = resident set; everything else is zero. The other caller (flags 0x2000000A, reached only when
`XMemIsToolsMemAvailable` returns non-zero) reads qwords 2 and 3 and dwords +0x48 and +0x4C.

## GPU visibility

On the console a mapped page has the same address for the GPU and the CPU. The D3D12.X shim cannot keep that on PC
but can give the GPU the same memory:

- Open the store's whole view of each page section as a heap with `ID3D12Device3::OpenExistingHeapFromAddress`
  (`OpenExistingHeapFromFileMapping` rejects the pagefile sections). The view of a section over 1 GiB is refused
  (E_INVALIDARG for the 6 GiB pool), so such a section takes a heap per 1 GiB window, each through its own view
  (`SectionHeaps`, shared by slot-133 buffers and the address map); a buffer or view stays inside one window.
- Budget: these heaps live in the GPU's system-memory segment (DXGI non-local), whose budget is about half the
  machine's RAM whatever the card has: about 8 GB on a 16 GB machine, which the 2.2 GiB game heap and the 6.1 GiB
  pool exceed (E_OUTOFMEMORY, issue #13). A window opens only while it fits the budget less the process's use and
  768 MiB of headroom (and `--section-heap-budget <GB>`); the address map's requests leave 1 GiB of that to slot-133
  buffers. A section that does not fit whole is backed lazily in 64 MiB windows, opened by the first map or buffer
  request there. Pages without a heap read as zeros and wait: when a window opens (a buffer placed there), the address
  map points them at it. A buffer without a heap gets a committed buffer that is not shared with the game. Heaps are
  never released: GPU work in flight and descriptors may still reach them.
- `--evict-windows` (prototype, off by default; programs made on demand): on a 16 GB machine the budget is 6.7 GiB,
  less than the 6.1 GiB pool plus the 2.2 GiB game heap, so the pool's windows become a cache. The CPU cannot know
  which windows a frame reads (programs follow V#s and pointers through the address map), so the GPU says: a waiting
  page's map entry holds the null views and `0x80000000 | window id`, and `gpu_page` (FIVESTAR_WINDOW_FEEDBACK) sets
  that window's flag in the descriptor window. At the first submission of each present `WindowResidency` reads the
  flags, opens up to 8 asked-for windows that fit, and otherwise releases the open windows asked for least recently
  (not within 30 presents; never one a slot-133 buffer pins): their pages wait again at once, and their views and
  heap go 8 presents later, when the frame throttle has the GPU past them. A window opens one frame after it was
  first read, so that frame reads zeros. The game's own allocations open by the 64 MiB window around the first
  address the GPU is given (`OpenExistingHeapFromAddress1`, ID3D12Device13; whole without it); those windows hold
  addresses recorded into command lists, so they are not released.
- Translate a game address with `XMemPhysicalMemory.TryLocate` to (section, offset), then to the GPU address of a
  buffer placed over that heap (`CreatePlacedResource`) plus the offset. Contiguity ends at each 64 KiB page unless
  the frames are consecutive in one section.
- A reserved resource with `UpdateTileMappings` onto these heaps is refused (the runtime: no tile mappings on
  cross-adapter heaps), so scattered pages cannot be stitched into one GPU range this way.
- Limits: heaps opened from a file mapping are system-memory (CPU-visible) heaps read over PCIe; textures in them
  are restricted to row-major layouts, so tiled console textures need a copy into a default heap.

### What the GPU pays for system memory

One compute dispatch of 67 million 4-byte accesses to a 256 MiB buffer on an RTX 4090 (driver 32.0.16.2124), GPU ms
by timestamp queries, nothing else on the GPU (a scratch program, not in the repository):

| Buffer in | load, in order | load, random | load, random in 1 MiB | store, random | atomic add, random |
|---|---|---|---|---|---|
| imported VirtualAlloc memory or section view (the heaps above) | 22 | 224 | 131 | 242 to 274 | 283 to 321 |
| driver heap in system memory (CUSTOM, L0) | 17 | 222 | 1.1 | 241 | 274 |
| video memory, DEFAULT or GPU_UPLOAD (through the BAR) | 0.3 | 8 | 0.8 | 9 | 18 |

Video memory is 25 to 280 times faster at first reads, stores and atomics. Memory the driver owns is cached by the
GPU once read, imported memory is not; so the loader's own tables (the descriptor window, the address map, the
user-data ring: driver heaps) gain little from video memory: `--gpu-upload-heaps` gave 204 against 212 ms of GPU
work a frame in the same cutscene (w324, w323; samples vary by 3 %). The CPU writes a GPU_UPLOAD mapping at
21 GB/s and reads it at 14 MB/s. lane-drvheap holds the full table and a prototype that allocates the game's
graphics heaps as driver heaps (not booting yet).

What is left is the game's memory the GPU reads for the first time each frame: geometry and streamed data in the
6.1 GiB pool and constants in the GPU pool, all imported. The frame's GPU work (240 ms of queue #1 in the world) is
led by the G-buffer and shadow mesh-shader draws, which read them through the address map.

### The ceiling: twins that are never synced

`--vram-twin-ceiling <presents>[,pool][,<MiB>]` (a measurement, not for play) gives buffers over game memory a
twin in video memory that programs read (their SRVs), copied from game memory when made and every so many presents,
otherwise never synced; stores still go to game memory. Twins stop at 5120 MiB (or the MiB given) and where the
device's local budget would keep less than 1 GiB free: the world uses 16.1 of an RTX 4090's 22.8 GiB without them.
`,pool` twins only the streaming pool's 1 GiB section windows; without it, the GPU pool's window and every buffer of
1 MiB or more get one too.

Same build, input and command line, RTX 4090, scenes matched by their top passes (queue #1 GPU ms per frame):

| Scene | No twins (w392) | Pool twins, 5 of 6.1 GiB (w393) |
|---|---|---|
| spotlight cutscene, two samples | 149.7 | 161.6 |
| yard, hair and position-only passes leading | 303.9 | 291.8 |

The pool's reads cost the frame nothing measurable: each pass is within the spread between boots (w371, the same
command line a build earlier, ran the cutscene at 165.1). The full form (w394: 5.6 GiB, the GPU pool's window, a
174 MiB, a 15 MiB and two 1 MiB buffers) lost the device (DXGI_ERROR_DEVICE_HUNG) 30 s after its first world frame:
programs read counts and arguments the GPU wrote as they were up to 60 presents earlier. What the GPU pool and the
game's buffers cost to read can only be measured once GPU writes reach the twins, which is the write-mark step below.

The programs that lead the frame's pass list do not pay for system memory either. Captured in w395
(`--capture-dispatch`) and replayed alone on the same RTX 4090 with their game memory in video memory and in imported
system memory (`fivestar shader replay --game-memory video|system`, median of 10 runs):

| Program | In the frame (w392, same scene) | Replay, video memory | Replay, system memory |
|---|---|---|---|
| hair_resolve CS_HairTileClassify (1 dispatch, 160 x 90 groups) | 33 to 38 ms | 0.011 ms | 0.011 ms |
| ped_skinning_cs CS_PedStandardSkinning_8 (268 dispatches) | 15 to 17 ms, 0.06 a dispatch | 0.005 ms | 0.005 ms |
| compute_graph_ops CS_MatMulAlignedOperation (45 dispatches) | 12 to 20 ms, 0.3 a dispatch | 0.004 ms | 0.005 ms |
| CS_ScreenProbes_GenerateShaderBins | 7.5 to 9 ms | 0.011 ms | 0.012 ms |
| CS_ProjDecalClassify | 5 to 17 ms | 0.010 ms | 0.012 ms |

A pass's time in `--gpu-timings` runs from its timestamp to the next, so it holds whatever the queue waited for
there (the drain of earlier work at a barrier), not what the program costs: the frame is bound by how its work is
serialized. `--indirect-batch`, which replaces about 5 million argument clamps (each with three transitions) by
340 thousand batch passes over a boot, took the same two scenes from 303.9 to 241.7 and from 149.7 to 119.9 ms of
queue #1 a frame (w395 against w392), with every program's pass time unchanged. Both of w395's screen captures show
a grid of half-tiles over the whole frame that no capture without it shows; that is not yet tied to the batch.
So the next speed-up is in the barriers around indirect work, not a video-memory image; the write marks below wait
until a frame shows reads of the GPU pool to cost something.

### Video memory images of game memory (design notes)

`--gpu-memory-vram` (branch lane-vram, parked) was not faster for two reasons its logs show:

- It images little of what is read. It moves slot-133 buffers of 1 MiB or more on XMem pages: 21 to 32 buffers,
  162 to 379 MiB (w154 to w166). The 480 to 540 buffers in the game's graphics heap (457 to 592 MiB), the pool
  the address map reads for mesh shaders and every address a program computes stay in system memory.
- Where the game says the GPU may write (PAGE_GRAPHICS_READWRITE, nearly all of it), nothing tells which pages
  the GPU did write, so any CPU access to such a page brings 512 KiB down after waiting for every queue: 36,835 MiB
  read back and 113,775 ms waited in w166. An earlier form compared instead (w154: 271,640 ms syncing).

VIctory (PR #3102, GPL-3.0; facts from its code and docs) images title buffers in GPU upload heaps:

- **CPU writes:** pages are made read-only (64 KiB units) and a vectored handler marks them; write watch only
  tells which pools are tracked. Writes by the kernel and the runtime (file reads, DirectStorage) are reported by
  hand. A page mapped at several addresses is guarded at each, and a write at one marks every copy. A submission
  refreshes the ranges it binds, and for programs with computed addresses every copy its page table names.
- **GPU writes:** translated programs set a bit per 64 KiB page at every store through a view (write marks); after
  the submission only marked pages come back from video memory (one GPU copy into a system-memory mirror, 0.05 ms
  plus the bytes) and are compared with title memory. Ranges written without marks are snapshot before and
  compared after, which costs more than it saves when large.
- **Measured** (RTX 5070 Ti, synthetic submissions; no figure from a real frame is published): GPU time 44 to
  16 ms with read-only copies in video memory and to 11 ms with written ones too, of which 9 ms is marking,
  because the mark bitmap sits in system memory.

XCL's shadow of its coherent window (XCL_UMA_SHADOW, for GPUs without OpenExistingHeapFromAddress) uploads the CPU's
writes found by write watch before each submission and reads GPU writes back only at Map and after the lists'
memory writes: it does not find program stores either.

For this loader:

- Write watch does not apply: the pool and the GPU pool are views of sections (MEM_WRITE_WATCH is for VirtualAlloc
  memory). Page guards do (`VramPages` on lane-vram), at every address a frame is mapped at, with DirectStorage's
  writes through the store reported (`ObserveWrites`).
- The game's flags prove little: of the GPU pool only the first 38 MiB are GPU read-only (0x1004, 0x1404,
  0x8404); constants are in its 47 MiB write-combined read-write piece (0x2404), buffer-view data in the 27 MiB
  write-combined read-only one (0x8404), and the streaming pool is all read-write.
- So the step that makes an image pay is the same as VIctory's: **write marks from the translator** (a bit per
  64 KiB page at every store through the address map's UAVs, in a video-memory bitmap read back with the fence).
  With them the image of the section windows can be the GPU's only copy (SRV and UAV of each page), pages come
  down only where marked, and CPU reads of unmarked pages need no fault. It is a FiveStar.Shaders change and
  belongs in a translator batch.
- Without the translator, two pieces are safe: the write-combined pieces of the GPU pool as GPU upload memory
  mapped at the game's address (no sync at all: the game declared it does not read them; needs XMem to back a
  reservation with a mapping that is not a section, and the address steering of lane-drvheap, not tried on a BAR
  mapping), and images of the pages the flags make GPU read-only.

The game calls the device right after mapping (vtable +0x250 with the address and page count at 0x141E6C2BB,
+0x428 after the five pieces at 0x141E8684C); these are the hooks for registering the memory with the GPU.

## Unknown

- The meaning of `xmemFlags` and of the page-allocation flags (0x180, 0x280, 0x380); whether pages must be physically
  contiguous; mapping into 2 MiB (MEM_LARGE_PAGES) reservations, which take one frame per 32-page cluster and are
  not supported here.
- The working-set structure beyond its first two qwords, and the 0x20000000 flag bit.
- Alignments the console may give that the loader does not: frame runs aligned to a large page for those page
  flags (their meaning is not known, so runs are only consecutive when possible), and 4 MiB alignment for an XMemAlloc
  block with XALLOC_PAGESIZE_4MB (only its alignment code is honoured; a block on 4 MB pages need not start on one).
  Neither has shown up in a fault; both stay as they are until one does.
