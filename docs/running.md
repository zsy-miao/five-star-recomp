# Running the game

`fivestar run` takes your own console dump to a running game: it checks the PC, builds what the loader needs in a
work folder (once; later runs reuse it), then starts `fivestar-loader` on the game executable.

## Shortest path

1. Install the .NET 10 SDK, the Windows SDK (for `dxc`) and the [Microsoft GDK](https://github.com/microsoft/GDK)
   (the PC GDK; its `xgameruntime.lib` gives the loader the GDK's call table).
2. Build: `dotnet build FiveStar.slnx`.
3. Check: `fivestar doctor <dump folder>`. Every line should say `ok`; a `FAIL` line says what to install or pass.
4. Run: `fivestar run <dump folder>`.

The first run extracts and translates every shader program (25,908 programs, about an hour on 24 cores, 16 GB of
disk) before the game starts; it can be stopped and resumed. Later runs start the game in under a second.

The game uses four AMD-only instruction families (SSE4a, CLZERO, MONITORX/MWAITX, CLWB). On an Intel CPU the loader
emulates them in its exception handler and logs a `cpu:` line for each place it does ([cpu.md](cpu.md)).

`fivestar` is `artifacts/bin/FiveStar.Cli/debug/fivestar.exe` after the build (or `dotnet run --project
src/FiveStar.Cli --`).

## Command

```
fivestar run <dump folder> [--work <dir>] [--debug] [--timeout <s>] [--warm] [-- <loader options>]
fivestar doctor <dump folder> [same options]
```

| Option | Meaning |
|---|---|
| `--work <dir>` | work folder; default `%LOCALAPPDATA%\FiveStar\<titleid>-<version>`, the same as `fivestar setup` |
| `--debug` | the D3D12 debug layer and DRED (`fivestar-loader --d3d12-debug`); needs the Graphics Tools optional feature |
| `--timeout <s>` | end the game after s seconds (`--run-timeout`, exit code 3) |
| `--warm` | after a translator update, build the pipelines earlier boots used before the boot ([below](#after-a-translator-update)) |
| `--shaders <dir> --translated <dir>` | use existing extracted programs and translated DXIL instead of building them; the translated folder needs `rect-gs/` |
| `--loader <exe>` | `fivestar-loader.exe`; default `FIVESTAR_LOADER`, then the folder of `fivestar`, then the build tree |
| `--dxc`, `--gdk-lib`, `--bcpack-tables`, `--jobs` | as for [setup](setup.md) |
| `-- <options>` | passed to `fivestar-loader` after its own options ([loader.md](loader.md#running-it)) |

`run` returns the loader's exit code; 1 when a check or a setup step failed, 2 for a wrong command line. `doctor`
returns 1 when a check failed.

## What run does

```mermaid
flowchart LR
  checks["checks"] --> setup["setup steps not done yet"] --> loader["fivestar-loader --run"] --> summary["summary"]
```

1. **Checks** (also all `doctor` does, followed by the state of the work folder):

   | Check | Fails when |
   |---|---|
   | `windows` | not Windows x64 |
   | `dump` | no `MicrosoftGame.Config`, another title id, no `game_scarlett_master.exe` or `shaders.rpf`; another version warns |
   | `loader` | `fivestar-loader.exe` not found |
   | `gdk` | no `--gdk-lib`, no PC GDK under `Program Files (x86)\Microsoft GDK`, no table from an earlier run and no `--gdk-abi` after `--` |
   | `bcpack` | warns when the work folder has no BCPack tables: BC3, BC6H and BC7 pages then get placeholder blocks |
   | `dxc` | dxc (and the `dxcompiler.dll` beside it, which the loader compiles programs with on first use) not found, unless `--shaders`/`--translated` give built shaders |
   | `disk` | the drive of the work folder has less free space than the steps still to do need, plus 2 GiB for the run |
   | `shaders` | with `--shaders`/`--translated`: a folder or `rect-gs/` missing; no `variants.json` warns |
   | `debug layer` | `--debug` and no `d3d12SDKLayers.dll` in System32 |

2. **Setup steps**, through the same runner and state as [`fivestar setup`](setup.md), so a step done by either
   command is done for both: `verify`, `shader-extract`, `bcpack-tables`, `gdk-abi`. The loader translates each
   program when the game first uses it ([d3d12x-pipelines.md](d3d12x-pipelines.md#programs-on-demand)); with
   `--translate-ahead` also `shader-translate` (all variants the loader picks, with `variants.json`) and `rect-gs`
   first. With `--shaders`/`--translated` only `verify`, `bcpack-tables` and `gdk-abi`.
3. **Loader**, started as

   ```
   fivestar-loader --load <dump>\game_scarlett_master.exe --no-exports --work-dir <work>\loader\tls
     --log <work>\logs\run-<time>.log --run [--run-timeout s] --shader-dir <work>\shaders\fssm
     --program-cache <work>\program-cache [--translated-dir <work>\shaders\hlsl] [--dxc <dxc>]
     --pipeline-cache <work>\pipeline-cache [--gdk-abi <work>\loader\gdk-abi.txt]
     [--bcpack-tables <work>\loader\bcpack-tables.bin] [--d3d12-debug] [<loader options>]
   ```

   `--translated-dir` is passed with `--translate-ahead` or `--translated`, `--dxc` when run gets it.

   The console shows the loader's milestones (`==` lines, errors, faults with their address, presents); the full log
   is the file. `--pipeline-cache` is left out when the loader options name `--pipeline-cache` or
   `--no-pipeline-cache`. At the end run prints a summary from the loader's lines, for example
   `fivestar-loader exit code 3: 293 presents, 0 faults, 43 warnings; full log ...`. Ctrl+C reaches the loader as well;
   run waits for it to end before the summary.

## After a translator update

A pipeline's place in the disk cache is named by its DXIL, so a new translator build (a new `fivestar-loader` with
changed shader translation) leaves every pipeline earlier boots used unbuilt: the game then waits 20 to 60 s of driver
compile at each one's first use, which is why a world load can take ten minutes after an update. `run` and `doctor`
compare the translator beside the loader with the one the last warm-up built for (`journal-warm.txt` in the pipeline
cache) and, when they differ and the journal (`pipelines-used.bin`) has entries, print a `note` with the count and an
estimate. `fivestar run --warm` then builds them before the boot: `fivestar-loader --warm-pipelines --warm-used-only`
with the boot's shader folders, program cache, pipeline cache and loader options (so the programs are made in the
same modes), split over one process per 16 GB of RAM (1 to 4; `--warm-processes` after `--` sets it), with a
`warm-up shards` progress line every 30 s and its log in `logs\warm-<time>.log`. A run stopped with Ctrl+C or by a
memory limit keeps what it built and the boot does not start; the next `run --warm` resumes.

| Machine | Processes | 5,000 journal pipelines |
|---|---|---|
| 64 GB | 3 | about 21 minutes (measured, RTX 4090: 1,837 built, the rest already cached) |
| 32 GB | 2 | about 32 minutes |
| 16 GB | 1 | about an hour; add `-- --min-free-memory 4` to stop it before the machine runs out |

The estimate is about 1.3 journal pipelines a second per process; a journal that is mostly new to the cache takes
longer. Without `--warm` the game builds them as before, at first use.

### Warm-up in the background

To play at once instead of waiting, run the warm-up beside the game, in a second terminal, with the boot's folders
and mode options and a share of the machine left to the game:

```
start "warm-up" /belownormal fivestar-loader --warm-pipelines <shaders.rpf> --warm-used-only --shader-dir <fssm> --program-cache <dir> --pipeline-cache <dir> <the boot's mode options> --warm-processes 1 --warm-jobs 2 --min-free-memory 8 --log warm.log
fivestar run
```

The warm-up takes the journal in the order earlier boots first asked for each pipeline, which is the order the game
will ask for them again (start screen, menus, then the world), so it runs ahead of the player. Both processes write
their own segments to the shared pipeline cache and every minute map the other's new ones, so a pipeline the warm-up
built reaches the game within about two minutes, also when its build was already queued there, and the warm-up
skips what the game built ([d3d12x-pipelines.md](d3d12x-pipelines.md#disk-cache)). They share the program cache as the
warm-up's own processes do. Pipelines of 4 MiB of DXIL or more need 40 GB of free commit in the warm-up and are mostly
left to the game. `--min-free-memory` ends the warm-up before the game runs short; Ctrl+C stops it, and the next
warm-up resumes. One process and two threads keep its driver compiles from taking the cores the game's own first-use
builds need.

## First run in the game

With an empty save folder the game shows a black screen that waits for a press (with a warm pipeline cache 45 to
220 s after start), the logos and the intro art, then: a minimap choice, a text-size choice, subtitles
(Off by default, Left and Right change it), brightness calibration, a controls notice, the legal and photosensitivity
screens, and a privacy policy and terms of service screen, before it loads story mode (a spinner on dark blue). The game
takes a gamepad here: A confirms, a short press too; the d-pad moves the focus. Scripted keyboard keys did nothing: Space and
Enter left the black screen standing, Down left the legal screen's focus where it was. The keyboard-and-mouse gamepad has
no key for A or the d-pad by default, so without a controller bind them with `--input`, for example `a Space` and
`dpad-down Down` ([loader.md](loader.md#running-the-executable)). The photosensitivity item
keeps the focus for 4 s. The two terms items are check boxes that A toggles (scrolling their text with the right
stick, the mouse, is not required); with both checked Confirm is enabled. The first press after Next gets the focus on the legal screen can be dropped. While pipelines compile the game can miss a
0.2 s press, and an arrow held 0.5 s or longer repeats. With the input script followed while
running ([loader.md](loader.md#running-the-executable)) these can be answered from a second shell;
`-- --input-script docs/first-run.input` answers them unattended with the virtual gamepad's buttons: it taps A until the subtitles screen shows, turns
subtitles on, taps A until the legal screen shows, then waits for each screen it needs rather than for a time. On
the terms screen it presses A, Down, A, Down, A once each, 4 s apart (a repeated A would clear a box, and the first
check mark is too small for a screen signature to show). It
recognises screens it learned from your own frames (`%LOCALAPPDATA%\FiveStar\screens.txt`); the first time, press F8
when the subtitles screen, the legal screen, the terms screen and the story load show (and the legal screen once Next has the focus), and later runs need no help. Only the first run shows these
screens (below). B
(C) goes back to the previous screen. The screens' text, headings and logos render in full; the brightness-calibration
symbol is still missing (every pixel right of the panel is 0 at level 15). It is not the frame's first draw: that is
the executable's `VS_Clear` + `PS_Clear` ([shader-containers.md](shader-containers.md#executable)), which reads its
rectangle and colour through a root SRV: the console address goes to the program as passed and is read through the
address map (`RealClearRunner` runs the real program so on WARP). The next draw (`PS_BlitColour` with a zero colour) fills the target black, and the UI is drawn over it.

**Later boots skip the screens.** At Confirm on the terms screen the game writes `Player` and `SettingsProfileSettings`
(settings and the first-run choices); the next boot goes from the logos straight to the story load. Such a boot logs
`XGameSaveGetContainerInfo("SettingsProfileSettings") -> exists True`, reads both containers and writes
`SettingsProfileSettings` no more (`Player` once more as the story load ends). It still shows the black screen that
waits for A, so it wants a script that taps `PadA`, not `first-run.input`. If its log names the first-run screens
anyway, within a second of each other, they were taught with F8 on one picture: the loader warns of such screens at
start; remove their lines from the screens file and teach them again. Start-up step 4 queues their loads one after the other on the same
save slot. Each starts with an XAsyncRun whose completion routine calls `XAsyncGetStatus(block, true)` on its own
block before it frees the block and the slot's in-use mark; the loader used to block there until the routine
returned, so the settings load found the slot in use and failed, and the screens came back.

## Developer mode

Turn on Windows developer mode (Settings > System > For developers) before a long session on an NVIDIA card.
NVIDIA's driver keeps the code of every shader in use in tables that cannot pass 512 MiB, recompiles those shaders
in the background and keeps the results there too; when a table cannot grow, the driver removes the device (the game
ends with `device removed, reason 0x887a0005`, no GPU fault, nothing in the Windows event log). The loader turns the
background recompiles off at start (`ID3D12Device6::SetBackgroundProcessingMode`, `DISABLE_BACKGROUND_WORK`), and
Windows allows that only in developer mode. Without it the log has

```
WARN  [d3d12_xs] the driver's background shader work could not be turned off: ID3D12Device6::SetBackgroundProcessingMode(DISABLE_BACKGROUND_WORK) -> 0x887a002d (DXGI_ERROR_SDK_COMPONENT_MISSING: Windows developer mode is off)
```

and the tables fill within minutes of play (issue #64: 428 MiB after 21 minutes, 285 MiB of it recompiles; with the
work off they stay near 350 MiB with none). The loader then discards the driver's measurements every 2 s instead, and
holds pipelines not used so far as stand-ins once a table is nearly full (their draws are missing, the device
stays): see `--driver-background-work` and `--no-shader-table-guard` in [loader.md](loader.md). With developer mode
on the line reads `driver background shader work off -> 0x00000000`.

## Work folder

| Path | Holds |
|---|---|
| `state/` | setup step records ([setup.md](setup.md#state-and-resume)) |
| `shaders/fssm`, `shaders/dxbc` | extracted programs and containers |
| `shaders/hlsl` | translated HLSL and DXIL, `variants.json`, `results.json`, `rect-gs/` |
| `shader-cache/` | translations and compiles by hash; a re-translate after an update reuses them |
| `loader/gdk-abi.txt`, `loader/tls/` | the GDK call table; the loader's TLS host DLLs |
| `pipeline-cache/` | the desktop pipelines built by runs and by the `pipeline-warm` setup step ([d3d12x-pipelines.md](d3d12x-pipelines.md#disk-cache)) |
| `logs/run-<yyyyMMdd-HHmmss>.log` | the full loader log of each run; the newest 5 are kept |

Saves go to the loader's default, `%LOCALAPPDATA%\FiveStar\GdkData\<TitleId>`. The default work folder is on the
system drive; pass `--work` to put the shaders' 16 GB elsewhere.
