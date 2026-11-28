# Status

GTA VI does not run yet. Updated 27 November 2026.

## Where it is

- **Boots to the world.** Logo, intro, first-run screens, story load and the opening scenes run; the start-up freeze
  is fixed.
- **Picture, by stage** (read from one frame's intermediate images, `--dump-frame`):
  - Geometry, normals, depth: right.
  - Surface colour: right for most surfaces since textures the game still uses are no longer retired by their
    neighbours. Characters and clothing draw when the skinned passes are not skipped. Open: hair, vehicle bodies,
    some odd colours.
  - Lighting: sun with cascade shadows, sky, and a stand-in ambient term now reach the frame. The prologue yard
    reads as the game's sunset scene. The bars of sunlight on shadowed walls are gone: the stand-in of the held
    ray-traced sun-shadow passes wrote "lit" over the cascade shadow textures as well as its own outputs. Wrong:
    in some runs sunlit ground in the yard shows tile-edged dark bands (from the cascade path: no ray-traced
    classification runs in those frames); tile-shaped
    sun on hair; no ray-traced distant shadows or leak plugging; lamps with shadow slots are dark,
    skinned bodies are held, some black blocks.
  - Post-processing: the posterized rings and most of the smear are gone since a translator fix for literal
    samplers (about 39,000 sample sites read an arbitrary sampler from the heap). Velocity infinities are open.
  - Exposure: not re-checked since lighting shades.
- **Device loss (NVIDIA):** fixed in one 14-minute boot. The driver gave the device up inside `ExecuteCommandLists`
  when an internal shader table passed its 512 MiB maximum (no timeout, no GPU fault). A third of the table was the
  driver's background recompiles; the loader now turns those off (`--driver-background-work` turns them on), and
  the table stays at about 350 MiB. Open: the mode may need Windows developer mode; new areas add shaders; an
  RTX 5090 report (#64) loses the device earlier in another way.
- **Speed:** a world frame takes about 170-260 ms (260 with character bodies drawn). The GPU waits at barriers more than
  it computes: replayed alone the heaviest passes take microseconds. Batching the indirect-argument clamps saved
  about 20% in a test but still breaks the picture. Game memory in video memory gains little (measured).
- **Frozen PROLOGUE card: fixed (27 November).** A translator bug made one cascade-shadow compute program write its
  lists over the start of a graphics heap, which holds the quad the final composite draws. With the fix the game
  shows the prologue yard as live 3D past the card, still wrongly lit.
- **Translation output changed again (27 November):** the translator batch and this fix are on main, so shaders are
  translated and compiled again on the next run (hours on a first run).
- **AMD:** world geometry draws since #55.

## Known problems

| Problem | Next step |
|---|---|
| Lighting pass shades almost no pixels | find why its depth-equal test fails; then directional light and ambient inputs |
| Velocity target holds infinities | a divide by a zero previous-frame w; not traced yet |
| Driver shader table has about 160 MiB of headroom | smaller translated shaders |
| Character depth pass (LinearDepthSkin) too slow to run | held; it costs ~800 ms GPU a frame when let through |
| GPU writes to game memory in system RAM are slow | video-memory copies; a finer address map |
| Dark bands on sunlit ground | tiles the game hands to ray-traced sun shadows read "in shadow"; make them use the cascade shadows |
| Orange streaks through hair; magenta lines on the ground | not traced yet |
| Ray tracing | not built; those passes are held and stand-ins fill some outputs |
![Progress](progress.svg)

## Components

| Area | Result |
|---|---|
| Shaders | all 25,908 native programs translate and compile to DXIL; our own DXIL backend, dxc as fallback |
| Shader cache | a re-run with nothing changed takes 26 s instead of about 70 minutes |
| Driver pipelines | about 3,850 pipelines warm up in about 35 minutes; ray-tracing ones are skipped |
| Mesh fallback | all 8,765 mesh programs also build as compute plus vertex pulling, for GPUs without mesh shaders |
| Translator checks | 520 shader tests, most on D3D12 WARP; the RDNA decoder matches LLVM on all 44 million instructions |
| Archives | about 68% of archive bytes extract; file names unknown |
| Textures | all 357,115 export exactly with the user's BCPack tables (99.44% without) |
| Models | 99.94% of 4.3 million geometries decode; OBJ export |
| Videos | 515 Bink 2 videos extract |
| Audio | all 1,603,231 sounds export (Opus, XMA2, PCM); XMA2 decoder matches FFmpeg |
| Executable | all 587 imports resolved; the six Xbox libraries load |
| Console runtime | 161 GDK functions shimmed; saves, user and store work offline |
| Graphics (D3D12.X) | every console object wraps a desktop one; console-only methods are logged |
| Setup | `fivestar setup` prepares a dump in one resumable command |

Details per area: [Roadmap](Roadmap.md).
