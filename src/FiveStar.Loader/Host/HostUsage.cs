// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Host;

/// <summary>The usage text of fivestar-loader.</summary>
internal static class HostUsage
{
    public const string Text = """
        fivestar-loader - manual PE loader and Xbox API shims

          fivestar-loader --load <dll> [--load <dll> ...] [--call <export>|all] [options]

        options:
          --call <name>          call an inspected export (see --list-safe-calls); 'all' = every applicable one
          --run                  run the main executable's entry point; reports faults in mapped code with the
                                 last calls through shims and stubs
          --run-timeout <s>      with --run: after s seconds report the main thread's stack and exit (code 3)
          --cpu <host|intel>     intel: report SSE4a, CLZERO, MONITORX/MWAITX and CLWB as missing and check their
                                 emulation through the exception handler (self-test code, not the game's)
          --trace-system         with --run: the call trace also records System32 imports
          --input-script <file>  timed key presses ("<s> <key> [hold <s>] [repeat <n> every <s>]", "<s> capture", "<s> learn <screen>" per line; "screen <name>" starts a block timed from when that screen shows; reloaded when the file changes)
          --allow-real-input     with --input-script, the real keyboard and mouse reach the game too (default: script
                                 input only, a fixed cursor; F9 hands the real ones back; --script-input-only says so)
          --screens <file>       learned screens of --input-script (default %LOCALAPPDATA%\FiveStar\screens.txt)
                                 added to the desktop keys, so the keyboard and the virtual gamepad see them
          --profile-threads      with --sample-threads: also log the main image's busiest functions on running threads
          --profile-managed <start>[:<seconds>]  from <start> s into the run sample managed stacks for <seconds> (20)
                                 into <log>.managed.nettrace; fivestar profile summary reads it
          --cpu-sample <ms>[:<s>] with --run: sample up to 8 game threads every ms; every s seconds (10) log where
                                 they were: game functions, our shims, system DLLs, blocked waits
          --trace-module <dll>   with --run: the call trace also records calls into this mapped image
          --sample-threads <s>   with --run: every s seconds log the calls per shim since the last sample,
                                 DirectStorage totals and every thread's stack
          --memory-map <s>       with --run: every s seconds log the private memory by owner, protection and
                                 allocation size, its change, the largest allocations and GPU memory
          --min-free-memory <GB> with --run: end the process (code 5, after a last memory map) when the machine's
                                 free physical memory drops below this
          --max-commit <GB>      with --run: the same when the machine's commit charge passes this
          --section-heap-budget <GB>
                                 at most this much of the game's page sections backed on the GPU (default: what
                                 the GPU's system-memory budget leaves); the rest opens as it is used, or reads as zeros
          --in-place-window <MB> the largest heap opened in place over game memory (64 to 1024, a power of two; default
                                 1024): larger sections and the game's own allocations open in windows of it as used
          --evict-windows        prototype: section windows the address map needs open when programs report reading
                                 them and the least recently wanted are released for them (programs made on demand)
          --reserve-gpu-system-memory <GB>
                                 debugging: hold this much GPU system memory (upload buffers, RAM) from device creation,
                                 so its budget is as tight as on a machine with less RAM
          --main <name>          image that plays the process exe for GetModuleHandle(NULL)/GetModuleFileName(NULL)
                                 (default: first mapped exe, else the first --load)
          --patch-peb            also point PEB.ImageBaseAddress at the main image (off by default)
          --save-dir <dir>       XGameSave / local storage root (default: %LOCALAPPDATA%\FiveStar\GdkData\<TitleId>)
          --shader-dir <dir>     extracted FSSM programs: D3D12.X pipeline creation is enabled, programs are translated
                                 and compiled on first use unless --translated-dir has them
          --translated-dir <dir> translated DXIL programs (fivestar shader translate), optional
          --program-cache <dir>  programs made on first use (default: <work-dir>\program-cache)
          --shader-overrides <dir> hand-written <program>.hlsl or .dxil replacing those programs' translations
                                 (fivestar shader override-init starts one); needs programs made on first use
          --no-on-demand         only --translated-dir provides programs
          --dxc <path>           dxcompiler.dll (or dxc.exe, or their folder) for programs made on first use;
                                 default: FIVESTAR_DXC, beside the loader, then the Windows SDK
          --shader-jobs <n>      threads that make programs on first use (default: half the processors with the
                                 DXIL backend, a quarter with dxc)
          --loop-caps <caps>     loop caps of programs made on demand: victory (4096 compute, 256 vertex/mesh, 128 pixel),
                                 default, or compute,geometry,pixel; part of the program cache identity
          --superstep-round-budget <k>
                                 a superstep program's group rounds at most k per wave-operation stop of its code (at
                                 least 256): a cut before the driver's timeout culls the draw; part of the cache identity
          --host-pipelines       pipelines of more than 2 MB of DXIL are built first in a child process held to the
                                 free memory and 10 minutes; one past a limit stays a stand-in (off by default)
          --plain-supersteps     superstep forms made on demand keep every register live around one dispatch
                                 switch (the form before the liveness carry); part of the cache identity
          --fast-waves           approximate: a mesh program that needs its whole console wave runs natively on
                                 narrower waves, each wave's first lane taken for its console wave's and any-lane branches decided per wave,
                                 instead of in the superstep form (not when it reads a lane by number); part of the
                                 cache identity
          --committed-textures   a committed resource for each read-only console texture instead of the shared
                                 placed heaps (the default, also --placed-textures), whose released ranges stay mapped
          --prebuild-jobs <n>    threads that build, from device creation on, the pipelines earlier boots used that the
                                 disk cache lacks, giving way to the game's compiles (default 0: off)
          --wait-pipelines <name,...>
                                 a stand-in pipeline whose name holds one of these parts is waited for (up to 60 s, the
                                 game's hang watchdog paused) when a command list first sets it, instead of skipping its work
          --typed-image-views    image loads, gathers and stores of integer views (UINT/SINT, the stencil plane) go through
                                 uint4/int4 declarations chosen by a class the descriptor carries; part of the cache identity
          --no-shared-exponent-copies
                                 R9G9B9E5 views of R32_UINT light read the texture through the class-3 decode in the
                                 programs, not an R9G9B9E5 shadow the texels are copied into before each read (the default)
          --trace-shadows        log each R9G9B9E5 shadow's first 6 copies: source, shadow, list, states, whether the
                                 list's type could record it, and what asked for it (a read, a stand-in's fill)
          --rt-ambient scene[:share[:tint]]|constant[:level]
                                 what stands in for the screen-probe light while its rays are held: a share (1) of the
                                 mean of the game's cached screen radiance, measured each frame, as a grey of its
                                 luminance (tint 0) or with its colour (tint 1); or a constant grey (0.6)
          --shared-exponent-programs <text,...>
                                 with --typed-image-views, programs whose names hold a text filter sampled R9G9B9E5 light
                                 in R32_UINT themselves (default indirect_diffuse_lighting~; each sample grows a lot)
          --typeless-targets     GPU-written R8G8B8A8/B8G8R8A8/B8G8R8X8/R8 UNORM textures made typeless, so a view in
                                 their sRGB or integer member reads as the game asked (off by default)
          --texture-refresh-wait  every submission refreshes and waits for the textures memory changed under (off:
                                 the refresh worker does it off the submitting threads, up to a frame later)
          --kept-aliases <n>     textures retired over exactly one reused range kept alive for older descriptors (default 1)
          --unmapped-view-budget <MiB>  textures slot-70 views make over reserved memory no page of which is mapped yet,
                                 at most this much (default 1024); past it such views are null descriptors
          --max-driver-compiles <n>  driver pipeline compiles of 256 KB of DXIL or more at once (default one per 5 GB of RAM, 2 to 12)
          --compile-ceiling <GB> no such compile starts while private bytes and the running ones' expected arenas
                                 would pass this (default none: free RAM less the game's growth decides); one
                                 always may when none runs
          --shader-backend <b>   dxil (default): programs made on first use compile with the DXIL backend, dxc
                                 for those it cannot lower or dxil.dll rejects; dxc: dxc for all
          --pipeline-cache <dir> disk cache of built pipelines (default: <work-dir>\pipeline-cache)
          --no-pipeline-cache    build every pipeline anew and store none
          --eager-pipelines      build every pipeline the cache lacks at creation; default: those earlier boots
                                 used (pipelines-used.bin) at creation, the others when first set
          --warm-pipelines <shaders.rpf|blob>  no game: build the pipelines earlier boots asked for (first-use order),
                                 then every pipeline of the console pipeline cache, into the disk cache (with
                                 --shader-dir, --translated-dir), resuming where a run stopped
          --warm-used-only       warm-up: only the pipelines earlier boots asked for
          --warm-jobs <n>        pipelines the warm-up builds at once (default 4; split over the processes)
          --warm-processes <n>   warm-up processes, each building every n-th pipeline into the same cache, pipelines of
                                 4 MiB of DXIL or more left to a last pass (default one per 16 GB of RAM, 1 to 4)
          --warm-step <k>        warm-up: every k-th pipeline only
          --warm-match <text>    warm-up: only pipelines with a program whose name contains text (repeatable)
          --gdk-abi <file>       XGameRuntime thunk ABI table: "clsid iid slot FunctionName" per line;
                                 default: read from an installed PC GDK's xgameruntime.lib
          --input <file>         GameInput keyboard-and-mouse gamepad mapping ("setting value" per line)
          --ffx <dll>            PC FidelityFX API DLL (AMD FidelityFX SDK, DX12) the game's FidelityFX calls go to;
                                 without it FidelityFX reports no provider
          --d3d12-debug          D3D12 debug layer and DRED; their messages and the removal report go to the log
                                 (needs the Graphics Tools optional feature)
          --d3d12-gpu-validation --d3d12-debug plus GPU-based validation (slow)
          --force-cull-none <text>  pipelines whose programs' names hold <text> cull no faces (a winding check)
          --depth-func-override <text=Func;...>  pipelines whose programs' names hold <text> get that depth test
                                 (Never..Always) and no stencil test (a check of what culls a pass's pixels)
          --no-class-depth-bounds  the tiled lighting keeps its depth EQUAL class test (by default a depth-bounds test)
          --skip-pipelines <text,...>  pipelines whose programs' names hold a text stay stand-ins: never built or
                                 loaded, their draws skipped (to get past one that hangs the GPU)
          --no-draws             no pipeline is built and every draw and dispatch is skipped (black picture): the frame
                                 time is then the game's own CPU and our shims outside the draws
          --mirror-table-records reach|full|<n>  records every descriptor-table bind mirrors (default 64): full
                                 (1024) gives lighting its inputs but mesh programs then reach data they hang on;
                                 reach mirrors each draw's tables as far as its programs read
          --d3d12-dred           DRED alone (auto-breadcrumbs, page faults, the removal report), without the debug layer
          --d3d12-skip <i:n,...> D3D12.X slots that do nothing (list:51, queue:35, device:n), to bisect GPU faults
          --d3d12-trace-queues   log the first queue submissions, signals and waits (cross-queue protocol)
          --d3d12-breadcrumbs    a GPU marker after every draw and dispatch: a stalled queue names the one it hangs in
          --gpu-timings          GPU time per pass and queue (timestamp queries), logged with the sample line
          --frame-log <n>        every n-th frame and every slow one: the CPU side of the frame (our submit path,
                                 texture refresh, descriptor mirroring, GC pauses and collections, the present)
          --gc-sustained         the GC in SustainedLowLatency mode: no blocking full collections unless memory is low
          --gpu-upload-heaps     the descriptor window, address map and user-data ring in GPU upload heaps (resizable
                                 BAR) where the device has them, not write-combined system memory
          --vram-twin-ceiling <presents>[,pool]
                                 a measurement, not for play: programs read game memory from copies in video memory
                                 made anew only every <presents> presents; the picture is stale or wrong
          --mesh-launch          mesh draws launched by an amplification stage of ours (one record read where the game
                                 wrote it) instead of a clamp pass before each; mesh programs take a 4-byte payload
          --clamp-enhanced-barriers  enhanced barriers on the indirect-argument scratch (one per clamp) instead of
                                 three transitions per ExecuteIndirect, where the device has them; not with --mesh-launch
          --typeless-depth       depth textures in a typed depth format made typeless, so the game's shader views read them
          --no-gpu-timeout       the game's command queues without the GPU timeout: long work stalls instead of removing the device
          --d3dkmt-watch         log failing graphics kernel calls (D3DKMT), MarkDeviceAsError and device states: who removes the device
          --driver-background-work  leave the driver's background shader recompiles on (off by default: their results fill its shader tables;
                                 Windows refuses to turn them off without developer mode: the log warns, see docs/running.md)
          --discard-driver-measurements  leave them on and discard the driver's measurements every 2 s (what a refusal falls back to)
          --no-shader-table-guard  do not hold the first uses of pipelines when the driver's shader table is nearly full
          --pipeline-shader-budget <MiB>  DXIL the built pipelines may hold before unused ones are given up and rebuilt at their next use (default 0 = never)
          --output WxH           the title renders and presents at one of its modes (1280x720, 1664x936, 1920x1080,
                                 2560x1440, 3840x2160) instead of its settings' 3840x2160; build 1.0.2.1 only
          --gpu-keep-awake       a compute queue of ours runs a small looped dispatch while the game submitted within 250 ms,
                                 so the GPU's clocks stay up between submissions (--gpu-keep-awake-turns: 20 s on, 20 s off).
                                 WARNING: on an RTX 5090 it lost the device within 100 to 154 s (DEVICE_HUNG, issue #64)
          --indirect-gather      indirect records nothing earlier in their list segment can have written are clamped in
                                 one prelude before the segment instead of one pass per ExecuteIndirect
          --indirect-gather-strict  as --indirect-gather, but every memory write and dispatch ends gathering
                                 (default: a memory write only for arguments it overlaps, a dispatch only when it
                                 stores to buffers)
          --indirect-batch       with --indirect-gather: records not gathered join one clamp pass per stretch between
                                 barriers after writes (a job table filled while recording) instead of a pass each
          --check-view-liveness  before each draw and dispatch, check the bound tables' views: released resources, wrong kinds
          --d3d12-nan-scan <t>   read back the inputs of draws whose pipeline contains t (comma-separated texts) and log
                                 those holding NaN or Inf
          --d3d12-nan-scan-dwords <offset>:<n>  also show n floats of every scanned buffer and CBV from that byte offset
          --trace-exposure <n>   every n presents log the values the game reads from the exposure targets it maps
          --trace-views <t>      log what each table record of draws whose pipeline contains t resolves to (CPU only)
          --capture-dispatch <t>[@n],...  capture the n-th dispatch (1st by default) of a compute pipeline whose name
                                 contains t, with what it reads and writes, for fivestar shader replay
          --capture-dispatch-dir <dir>  where the captures go (default: captures)
          --retire-unmapped-textures  release textures over memory the game unmaps (default: only counted)
          --keep-unmapped-textures  keep textures over memory the game unmapped that no view record names (default:
                                 retired after 120 presents unmapped and 60 unnamed, their views nulled)
          --stale-in-first-page  a new texture retires an older one it overlaps only when it starts in that one's first
                                 4 KiB (streamed textures keep only their small levels; default: any overlap retires)
          --frame-rate <r>       30 (the console's, default), 60 or uncapped: the frame events' interval and vsync
          --watch-transient      check the game's transient-memory pool and log the first corrupted node
          --watch-pool-pages     check the game's small-block pool pages for headers its allocator did not write
          --watch-4g-lines       log changes to the first 64 KiB after each 4 GiB line of GPU-writable game memory
          --skip-compute-chains  skip the rest of a command list's dispatches after one skipped for a stand-in
          --empty-indirect-after-skip  a command list's ExecuteIndirects after a compute dispatch skipped for a stand-in run nothing
          --retire-waits-all-queues  a submission holding the game's retire write waits for every other queue
          --no-window-guard      programs' stores and atomics through 32-bit pointers reach the descriptor window (by
                                 default they are dropped and counted per program at each captured present)
          --buffer-records-heap-index  buffer descriptor records hold the desktop heap index where the console has the
                                 view's base address (the behaviour before; programs that build pointers from a V# break)
          --typed-buffer-views   buffer views at slots programs reach by heap index keep the game's typed or structured
                                 kind (the behaviour before); by default they are raw, as programs read them
          --check-null-addresses  root views, indirect argument and count buffers, bottom-of-pipe writes and buffer
                                 copies that would reach a null GPU address or read past their buffer are logged
                                 (the first 8 of each kind, with list and pipeline) and dropped
          --depth-twins          UAVs of a depth texture's depth or stencil plane write a colour twin, copied into it
                                 before the next draw that binds it (needs --mirror-table-records reach)
          --target-clear-census  every 300 presents, the render targets bound in the frame before, how many the game cleared
                                 and those it never cleared; GPU-written textures made over one another's memory, once each
          --clear-targets-each-frame
                                 diagnostic: each render target is cleared to 0 at its first bind in a frame
          --user-data-constants  draws pass their user data as root constants, not a root CBV in upload memory (needs
                                 programs made on demand)
          --trace-pool-stores    programs made on demand log their stores into the transient pool and the game heap
          --watch-hang           log the game's main state, loading-screen flags and heartbeat, and every thread's
                                 stack while the heartbeat its hang watchdog checks stands still
          --watch-movies         log the game's Bink movies: each one's state as it changes, its frame every 5 s
          --break-line-writes    with --watch-4g-lines: hardware write breakpoints on every thread at + 0, + 0xbf8 and
                                 + 0x1be0 of the game's 2.2 GiB graphics heap; each CPU write there is logged with
                                 its instruction and stack (a GPU write raises nothing)
          --d3d12-trace-draws    log what the first draws bind (pipeline, targets, vertex buffers, tables) and read
                                 their render targets back
          --d3d12-trace-pipeline <text,...>  with the draw trace, also log the draws of pipelines named with any of the
                                 texts in the frame after each captured present
          --d3d12-probe-traced   those draws also read back their first render target (a float target's mean and max)
          --d3d12-trace-records <offset,...>  for those draws, the window records at these byte offsets from each
                                 user SGPR holding a window offset: dwords, the V#/T# base's map state, a V#'s first values
                                 (offset@byte: also the dword at that byte of the V#'s buffer, e.g. 0x380@0x11c;
                                 offset@sgprN+byte: at that byte past user SGPR N, e.g. 0xac0@sgpr14+0xc; a trailing
                                 >sgprM follows the dword as a record number in the table SGPR M points to and shows
                                 the view there, read back with --d3d12-probe-traced, e.g. 0xac0@sgpr14+0xc>sgpr15)
          --d3d12-watch-target WxH:format  with the draw trace, every pass that renders into a target of this shape
                                 (and each traced dispatch) reads it back after captured presents, e.g. 2560x1440:10
          --d3d12-watch-limit <n>  watched-target readbacks per frame (16); each pipeline's first write to a target only
          --d3d12-watch-rendered  watch only those targets the game binds as render targets (the lit HDR, not
                                 the UAV-only outputs of the same shape)
          --log-draws off|brief|full  per-draw text of the draw trace: brief (default) writes traced work only, full also
                                 the first draws and those after each captured present (each read back), off none
          --capture-when-world   with the draw trace, capture 3 frames 100 presents apart from the first present that
                                 records over 1000 draws and dispatches (the world), wherever it arrives
          --probe-pipeline <text>  mesh programs named with <text> are made with probe code: per group its output
                                 counts, culling, vertex 0's position and the load sites that read out of range,
                                 unmapped pages or null records, summed up at each captured present (--capture-presents);
                                 pixel and compute programs per wave, with the first float site whose value is NaN or Inf
          --d3d12-dump-targets   the draw trace, and in the frame after each captured present every render and depth
                                 target read back when a list leaves it, as PNG in the capture folder
          --dump-frame <n|world[+n]|Ns>  the draw trace, and in the frame after present n (world+n: n presents after the
                                 first world frame, 300 for world; Ns: N seconds after start) every target and UAV texture at
                                 least 480 wide read back after each pass that wrote it, as NNN-pass-WxH-fmtF.png
                                 (HDR exposed, plus -auto.png at its mean; normals and depth mapped; stats in the log;
                                 unchanged targets not written; later frames continue when readback memory runs out)
          --dump-frame-dir <dir> where --dump-frame writes (default frame-<n>)
          --dump-frame-max <n>   PNGs --dump-frame writes at most (160)
          --dump-frame-filter <text,...>  only passes whose pipeline name holds one of the texts
          --dump-frame-min-width <n>  narrower targets are not dumped (480; 80 takes the lighting's tile masks)
          --capture-dir <dir>    write read-back planes and render targets there as PNG
          --capture-presents <n,...> present numbers whose plane is read back (default 60,300,900,1500)
          --pix-capture <n,...>  PIX GPU capture of those presents (WinPixGpuCapturer.dll, winget Microsoft.PIX)
          --pix-capture-dir <dir>  where the present-N.wpix files go (default pix-captures)
          --aftermath [dir]      NVIDIA Nsight Aftermath GPU crash dumps and shader debug info there (default aftermath);
                                 needs GFSDK_Aftermath_Lib.x64.dll of the Aftermath SDK (developer.nvidia.com/nsight-aftermath)
          --aftermath-dll <path> that DLL (else next to the loader, else %NSIGHT_AFTERMATH_SDK%\lib\x64)
          --aftermath-markers    an Aftermath event marker per draw, dispatch and copy naming its pipeline, and call stacks
          --aftermath-resources  Aftermath resource tracking: names the resource at a page fault (driver memory per resource)
          --aftermath-selftest   provoke a GPU page fault on the NVIDIA adapter and check a dump is written (exit 0), then exit
          --aftermath-shader-info  Aftermath shader debug info (.nvdbg) for every pipeline: many GB with ~84,000 pipelines
          --present-test         present moving color bars instead of the game's frame (checks the swap chain)
          --raytracing          XboxBvhBuilder.dll's CPU paths: console acceleration-structure sizes and BLAS
                                 rehydration into game memory (off: desktop sizes, nothing written)
          --raytracing-gpu       run XboxBvhBuilder.dll's constructor for an inventory of its 52 GPU builder pipelines:
                                 stand-ins never built, no builder program runs (docs/bvh-builder.md)
          --rt-traversal         run the game's BVH traversal programs (screen probes, irradiance cache, reflections,
                                 shadows) with --raytracing's paths; a node in unmapped memory misses; passes still
                                 held by --skip-pipelines keep their stand-ins, the screen-probe light is not greyed
          --no-rt-stand-ins      held ray-tracing passes leave their outputs as they are (default without --raytracing:
                                 shadow masks fully lit, reflections black, the screen-probe light a grey)
          --no-lighting-waits    the lighting pipelines (cascade shadows, shadow masks, tiled lighting, TL_PS, exposure,
                                 bloom, tone map) are skipped while stand-ins instead of waited for up to 60 s at first use
          --no-map-readback      a Map of a small GPU-written target (the exposure statistics) leaves the game's memory as
                                 it is (default: the GPU's texels are read back into it)
          --map-readback-wait    such a Map waits for the GPU to finish all submitted work and reads the row then (default:
                                 it gets the row of a copy an earlier Map started, and the game's thread does not wait)
          --xdsp-convolution     run the XDsp convolution effects on the CPU (default: silence; response format inferred)
          --audio-capture <wav>  record what the game plays (process loopback, 48 kHz stereo) and print how much had sound
          --desktop-timers       keep 15.6 ms timer ticks (default with --run: 1 ms, which the game's audio mixer needs)
          --bcpack-tables <file> the BCPack tables (bcpack-tables.bin, not shipped; fivestar setup imports them):
                                 every BCPack codec decodes exactly; without them BC3, BC6H and BC7 have no
                                 decoder and BC4 and BC5 decode approximately
          --bcpack-missing <m>   DirectStorage BCPack pages without exact or approximate blocks:
                                 placeholder (default: neutral blocks of the page's format) | fail (E_NOTIMPL)
          --package-root <dir>  package root for XPackage / MicrosoftGame.config (default: main image directory)
          --device-type <n>      XSystemGetDeviceType result (default 7 = XboxScarlettAnaconda)
          --network <mode>       offline (default: connectivity level None) | sealed-online (InternetAccess); either
                                 way every WinHTTP, Winsock and libHttpClient connection fails inside the process
          --network-level <l>    connectivity level reported instead: 0..4 or Unknown, None, LocalAccess,
                                 InternetAccess, ConstrainedInternetAccess
          --patches <path>       code patch file, or folder of *.fspatch files (repeatable, applied in order)
          --mods <dir>           mod folder laid over the package root (repeatable, first has priority)
          --manual <a.dll,b.dll> map these modules with this loader instead of LoadLibrary (repeatable)
          --system-crt           do not map the shipped MSVC runtime (default: msvcp140*/vcruntime140*/concrt140/
                                 vccorlib140/vcomp140 are mapped from the target's directory if present)
          --search <dir>         extra directory for --manual modules and for modules the PC does not have
          --unresolved <mode>    stub (log + return 0, default) | trap (log + terminate on call) | error (fail load)
          --no-entry             do not call DllMain
          --no-shim <mod!func>   disable one shim (exercise the unresolved-import path)
          --no-exports           do not list the target's exports
          --unload               call DllMain(PROCESS_DETACH) and free images at the end
          --work-dir <dir>       where generated TLS host DLLs go (default: <exe dir>\work)
          --log <file>           full log (every import and its resolution)
          -v                     verbose console (every import, every shim call)
          --list-safe-calls      list the export call recipes
          --classify <file>      classify "module!symbol" lines (shim, Xbox stub, package, host or missing; the
                                 --search directories are the package folder; for fivestar exe analyze)
          --classify-out <file>  where the classification goes (default: standard output)
        """;
}
