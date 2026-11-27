# Ray intersection

`image_bvh_intersect_ray` and `image_bvh64_intersect_ray` test one ray against one node of an AMD-format BVH (RT IP
1.1). Traversal stays in the program; the translator emits a call to `bvh_intersect_ray` in
`rdna_bvh.hlsli`, which follows AMD's own software model of the instruction (GPURT `IntersectCommon.hlsl`) operation
by operation. The game keeps AMD-format BVH memory, so only the instruction needs emulating.

## Operands

`vdata` receives 4 dwords. `vaddr` is a VGPR range or an NSA list:

| Dword (32-bit form) | Dword (bvh64) | Content |
|---|---|---|
| 0 | 0..1 | node pointer |
| 1 | 2 | ray extent (f32) |
| 2..4 | 3..5 | origin xyz (f32) |
| 5..7 | 6..8 | direction xyz (f32) |
| 8..10 | 9..11 | inverse direction xyz (f32) |

With `a16`, origin and extent stay f32 and the two directions are three packed f16 pairs: `dir.x | dir.y << 16`,
`dir.z | inv.x << 16`, `inv.y | inv.z << 16` (8 or 9 dwords). The instruction uses the given inverse direction for
boxes and the direction for triangles; it computes neither.

## T# (BVH descriptor)

| Field | Bits | Meaning |
|---|---|---|
| base address | dword0, dword1[7:0] | address bits 47..8 (dword0 bits 39..8, dword1[7:0] bits 47..40) |
| box grow | dword1[30:23] | ULPs (of 2^-24) added to each box interval |
| box sort | dword1[31] | sort hit children by entry distance |
| size − 1 | dword2, dword3[9:0] | not checked |
| triangle return mode | dword3[24] | 1: t_num, t_denom, I, J; 0: t_num, t_denom, triangle_id, hit |
| type | dword3[31:28] | 8 = BVH |

The T# is a real address, not a heap index. Nodes are read at their 64-bit address as global loads read it (`PtrLoad`):
a base whose bits 32 and up are 0 is in the loader's window (the tests), any other is game memory through the address
map. They used to be read from the window at the low 32 bits of the address, never from game memory.

A node in game memory whose page the address map has no memory for (the null view) misses: a box node has no child, a
triangle node no hit. Read as zeros, it is a box around the origin whose children all point at the BVH base, which a
ray through the origin takes (the resident-box check of Kelvaroq's VIctory fork; facts only). A base of 0 misses too.

## Nodes

Node pointer bits [2:0] give the type; node address = base + ((pointer & ~7) << 3), 64-byte units.

| Type | Node | Layout |
|---|---|---|
| 0..3 | triangle (64 B) | V0 @0, V1 @12, V2 @24, V3 @36, V4 @48, triangle_id @60; vertices per type: 0 (V0,V1,V2), 1 (V1,V3,V2), 2 (V2,V3,V4), 3 (V2,V4,V0) |
| 4 | box16 (64 B) | 4 child pointers @0; per child 3 dwords @16 + 12i: min.x\|min.y, min.z\|max.x, max.y\|max.z (f16) |
| 5 | box32 (128 B) | 4 child pointers @0; per child min.xyz, max.xyz @16 + 24i (f32) |
| 6, 7 | instance, user | result 0xffffffff × 4 |

## Results

**Box nodes.** Per child: `t0 = (min − org) × inv`, `t1 = (max − org) × inv`; per axis entry/exit chosen by
`inv >= 0`; entry = max of the three, exit = min (IEEE maxNum/minNum: a NaN operand is ignored); entry clamped to
≥ 0, exit to ≤ extent; a NaN interval misses. The child hits when entry ≤ exit × (1 + grow × 2^-24). Missed children
return 0xffffffff. With box sort the four results are ordered by entry distance with the network (0,2)(1,3)(0,1)(2,3)(1,2),
misses last and ties in node order; without it they stay in node order.

**Triangle nodes.** `e1 = v1 − v0`, `e2 = v2 − v0`, `e3 = org − v0`, `s1 = cross(dir, e2)`, `s2 = cross(e3, e1)`;
`t_num = dot(e2, s2)`, `t_denom = dot(s1, e1)`, `I = dot(e3, s1)`, `J = dot(dir, s2)`. The ray misses when
`I/d < 0`, `I/d > 1`, `J/d < 0`, `I/d + J/d > 1` or `t_num/d < 0`; a miss returns `t_num = +inf`, `t_denom = 1`. The
extent is not tested; the sign of `t_denom` is the facing. Return mode 1 gives `{t_num, t_denom, I', J'}`, where I' and
J' are picked from `{t_denom − I − J, I, J}` by the triangle_id byte of the node type (bits 1..0 for I', 3..2 for J').

## Without a built structure

Without `--raytracing` a pipeline whose programs trace rays stays a stand-in (`RayTracingPrograms`): never built nor
loaded from the pipeline cache, its draws and dispatches skipped and logged once, as `--skip-pipelines` does. A
program is known to trace rays when it is translated (its HLSL includes `rdna_bvh.hlsli`), and in later boots or from
the compile host by a marker beside its DXIL in the program cache, `<name>.rt` (`<name>.rt0` for one that does not).
A program without a marker (its DXIL made by a boot before the markers: w66b sent a held
`standard_emissive_layer~CS_RaytracingResolve` to one of its four driver compile slots) is read from its console code
(an `image_bvh` instruction) before a build and before a pipeline-cache load, so a held pipeline takes no compile
slot and is not loaded from the cache; a game thread creating a pipeline reads only what is known and the markers.
The warm-up, the journal's pre-builds and the cache go through the same build and load. Boot w65b, with the node misses below
but the passes running, still lost the device about 100 s after the world loaded; with these pipelines skipped boots
ran 300 to 600 s. VIctory holds such pipelines until its builder has run (its PR #2311; facts only). Programs of `--translated-dir` have no marker and are not held back.

The lighting still reads what the held passes would have written, so their texture outputs get a constant instead (`RtStandIns`, default on without `--raytracing`, off with `--no-rt-stand-ins`). At a skipped dispatch of a ray-traced shadow pass (`shadowmask~CS_TraceShadowRays_*`, `rtdirectionalshadows~*`) the texture UAVs its programs store through (see Which records below) are written fully lit; planar reflections (`CS_PPR_Raytrace`) black; and the screen-probe light, whose rays are held, is overwritten after `CS_ScreenProbes_TemporalResolve` runs (as VIctory's ambient stand-in: ZJOU-VICTORY 1fc58f3 after Belobug 4f45ab8, vitutu1 c3faf2a; facts only) with a share of the scene's own light (`AmbientLight`, `--rt-ambient scene[:share[:tint]]`, the default: the mean of a 32x18 grid of the game's cached screen radiance, the output of `tiled_lighting~CS_CacheScreenSpaceRadiance`, measured on the GPU right after that pass and written a frame later as a grey of its luminance, or with its colour by the tint, so the level follows the shot; taking the level from that image is the idea of VIctory's 7900 XTX fork, `crates/victory-gpu/src/ambient.rs`, which does it per screen region; none of its code is used) or with a constant grey (`--rt-ambient constant[:level]`, 0.6, also what is written until the first measurement). These images are pre-exposed by the game (the lit image and the probe light are stored divided by a per-shot scale, 256 in the prologue yard, 1 in the opening), so a constant is already relative to the shot's exposure, but not to its colour or to how much of it is lit. The value goes to the resolve's light views only: float colour views (RGBA32F, RGBA16F, R11G11B10F), and the R32_UINT views of a texture the game also reads as R9G9B9E5, written R9G9B9E5-packed. The resolve packs its light that way itself (the max channel's exponent + 0x7804000, mantissas at bits 0, 9 and 18, the exponent at 27) into the UAVs at table offsets 0x260, 0x280 and 0x2e0 of `CS_ScreenProbes_TemporalResolve_ReflectiveCaustics` (records 19, 20, 23); its other UAVs hold probe metadata (octahedral normals as x * 0.5 + 0.5, packed bit fields) and are left. No other integer view (tile lists, counters, packed fields) is ever filled. The lighting (`TL_PS_*`) samples that light through R9G9B9E5 views, so through the shadows of those R32_UINT textures (`SharedExponentAliases`): a filled source is copied into its shadow on the same list, right after the fill. Before that copy (boots up to w353) the sources held the grey and none of it reached the lit image. A small compute program (`UavFill`) writes the value by heap index as a float4, knowing each view from `CreateUnorderedAccessView` (`UavViews`, by desktop descriptor). Passes that write only raw buffers (the probe rays, the deferred gather) are left alone. These outputs are approximations, not the passes' results, so the first fill of each rule is a warning naming the pipeline and what it stands for, and every sample line counts the UAVs each rule filled (`ray-tracing stand-ins (approximate): ...`, or `none filled`). The records the resolve's fill takes are those its programs read from each table (`TableReaches.OfNames`, from the `--shader-dir` programs; a table read to its whole 1,024 records is an unbounded index and left out): the game copies descriptors to its binding entries' offsets, past the root signature's ranges, and in w151 every record of those ranges named no texture UAV (the resolve reads its tables in s4 and s5 to records 124 and 29). Without `--shader-dir` the root signature's UAV ranges are filled. A rule's first fill that writes nothing is a warning counting the records, those without a noted texture view and those refused by format.

**Which records.** A held pass's fill takes the records its programs store textures through (`StoredRecords`: the
table record of each T# an `image_store*` or `image_atomic*` takes, loaded at an immediate offset through a table
pointer in an entry SGPR). The fill used to take every texture UAV in the programs' reach, which holds other passes'
textures too. The sun's shadow on screen is held in records 62 (1280x720 R8G8B8A8_UNORM) and 63 (R8_UNORM) of the
global table in s4: the `cascadeshadows_rendering~CS_ShadowRenderTiled*` passes write them for the tiles of their
lists, and `cascadeshadows_classify~CS_ShadowUnifiedClearRT` writes 0 (in shadow) to record 63 on the tiles classified
for the ray-traced side. The tiled distant traces (`CS_RTDistantShadowTrace*_Tiled`) and
`CS_RTDistantShadowCopyDepthHalfRes` read that table to record 70 but store only through record 10 (and 2) of their own
table in s5, so the old fill set records 62 and 63 fully lit at each skipped trace, and the tiles no cascade pass wrote
afterwards showed sunlight on shadowed walls (bars of 16-pixel tiles: w357, and w371 with `--rt-stand-ins-reach`;
gone in w370 and w372). The log names, per held pass, the records filled and the texture UAVs left alone (in w370:
records 62 and 63 of s4 for each trace and the depth copy). The traces' own output (s5:10, R16_UINT) is an integer
view, which no fill writes. `--rt-stand-ins-reach` fills the whole reach as before. The rule that writes after a pass
that ran (the screen-probe light) still takes the whole reach, by format.

The tiles of the ray-traced side keep the clear's 0, so they read in shadow whatever the sun does there: in the
prologue yard (w372) the sunlit ground shows dark 16-pixel bands between the cascade-written tiles. Making those tiles
take the cascades (or the classification send every tile to the cascade lists) is open; clearing bits 1 and 6 of
the tile words `CS_ShadowTileClassify` writes (a `--shader-overrides` test, w373) did not.

| Held pass | Reach (records of s4, s5) | Stored through |
|---|---|---|
| `CS_RTDistantShadowTrace32_Tiled`, `..LeakPlug32_Tiled`, both `TemporalUpsample4X` forms | 71, 15 | s5:10 |
| `CS_RTDistantShadowCopyDepthHalfRes` | 71, 3 | s5:2 |
| `CS_RTDistantShadowFinalUpsample` | 40, 5 | s5:3 |
| `shadowmask~CS_TraceShadowRays_Checkerboard` | 179, 15 | s5:10 to 12 |
| `CS_PPR_Raytrace` | 107, 12 | s5:7 to 9 |

Some programs only serve ray tracing but trace no rays. They are held by name (`RayTracingPrograms.ServesRays`):
- the `bvh_` groups (`bvh_skinning`, `bvh_skinning_smh`) skin geometry for structures that are never built;
- `CS_RaytracingResolve` shades the hits of rays that are never traced;
- `rtdirectionalshadows` feeds and upsamples the held distant-shadow traces.

Boot w70e held only the tracing programs and lost the device after the world loaded. w71 also skipped these and survived the world load.

Nothing else is held: 64 of the 25,908 programs hold an `image_bvh` instruction, and a program named for ray tracing
that traces none is built and run. `--skip-pipelines Raytrac` is therefore not needed and holds three such passes of
a world boot (w172), none of which has a stand-in:

| Pass | What it does |
|---|---|
| `deferred_tiled~CS_LaserLightShadowRaytracingFallback` | 9 instructions: stores "no hit" (0x7f7fffff) per element of the buffer the held `CS_LaserLightShadowRaytracing` would fill |
| `shadowmask~CS_GenerateShadowMasks_Raytraced`, `_RaytracedCheckerboard` | sample the cascades (34 `image_sample_c_lz`) and write the shadow masks; the only non-hair mask generators that boot creates |

The other fallbacks (`CS_PPR_RaytraceFallback`, `CS_ScreenProbes_TraceRays_Fallback`, `CS_Primary_Gather_Fallback`,
`CS_GatherAndShadeAlpha_Fallback`, the `_Voxels` forms) trace none either.


The loader builds no acceleration structure without `--raytracing` (XboxBvhBuilder builds nothing), so programs it
makes on demand define `FIVESTAR_BVH_MISS` (`BvhMiss`, part of the program cache identity): every node misses and
nothing is read. A box node returns four invalid children, a triangle node t_num inf over t_denom 1 (return mode 0:
hit_status 0). The game's traversal loops then end at once. Before, about 70 inline ray-tracing compute programs
(`CS_RaytracingResolve`, the ray-traced shadows, `bvh_*`) traversed whatever title memory the T# base pointed at,
following garbage child pointers until the driver reset the context: the world-load DEVICE_REMOVED of boot w63,
gone when those programs were skipped. VIctory found the same (its PR #2311: the world ray-tracing pass walked unbuilt
memory and hung NVIDIA at world entry) and holds such pipelines until its builder has run. Their loops are bounded besides by the loop guard (65,536 iterations per
compute invocation, [shader-translation.md](shader-translation.md#loop-guard)), which alone is no time bound for
such loops.

## Exactness

Every float operation is one IEEE f32 round-to-nearest operation in GPURT's order, written `precise` so the compiler
cannot contract or reorder it. The divisions of the hit test are done in f64 and rounded once (the correctly rounded
f32 quotient), so the program needs `DoublePrecisionFloatShaderOps`. EXEC applies: inactive lanes do not write
`vdata`.

## Unknown

- The internal precision of the hardware datapath; results may differ in the last bits and in hit decisions for rays
  exactly through edges or box corners.
- Denormal handling (PC GPUs may flush f32 denormals).
- Triangle node types 2 and 3 and return mode 0 (layout inferred).
- A triangle_id source index of 3 (returns 0.0 here).
