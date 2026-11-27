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
  - Lighting: the tiled lighting pass shades its pixels (its depth-equal test cannot work on the GPU after our
    depth copy; a depth-bounds rule replaces it). With the held scattering passes let through, the sky fills too.
    Before post-processing a frame is now a recognisable lit scene, with brightness bands 256 pixels wide and 16
    high and some black blocks.
  - Post-processing: makes it worse. The final frame has a black sky, flat banded colour and blur; one
    intermediate target is full of NaN and the depth-of-field passes output zero.
  - Exposure: not re-checked since lighting shades.
- **Device loss (NVIDIA):** fixed in one 14-minute boot. The driver gave the device up inside `ExecuteCommandLists`
  when an internal shader table passed its 512 MiB maximum (no timeout, no GPU fault). A third of the table was the
  driver's background recompiles; the loader now turns those off (`--driver-background-work` turns them on), and
  the table stays at about 350 MiB. Open: the mode may need Windows developer mode; new areas add shaders; an
  RTX 5090 report (#64) loses the device earlier in another way.
- **Speed:** a world frame takes about 200-400 ms. Targets: 30 fps, then 60.
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
| Brightness bands in the lighting | they follow the packed underwater mask the lighting reads; the water passes that write it are being replayed offline |
| Post chain ruins the frame | the velocity target holds infinities (a divide by a zero previous-frame w); the pass after TAA that loses the sky |
| Driver shader table has about 160 MiB of headroom | smaller translated shaders |
| Hair and vehicle bodies missing from surface colour | bring back the remaining skipped skinned passes one by one |
| GPU writes to game memory in system RAM are slow | video-memory copies; a finer address map |
| Final frame in the prologue yard is orange, banded and covered in bars | the freeze on the PROLOGUE card is fixed; lighting inputs and exposure next |
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
