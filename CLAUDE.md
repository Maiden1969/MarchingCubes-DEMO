# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

Unity 6 (6000.5.10f1, URP 17.5.0) demo of DRG-style fully destructible voxel terrain: SDF lattice per chunk, meshed with Marching Cubes via compute shader, edited at runtime (carve/add spheres). Editor is at `E:\Unity\6000.5.10f1\Editor\Unity.exe`. Language of user communication: Chinese.

## Commands

There are no build scripts or tests committed. Compile check via Unity batchmode:

```bash
"E:\Unity\6000.5.10f1\Editor\Unity.exe" -batchmode -nographics -quit \
  -projectPath "F:\UnityProject\DEMO" -logFile "F:\UnityProject\DEMO\compile_check.log"
```

The user usually has the editor open — the batchmode run then fails on the project lock (return code 1). In that case read `Logs/Editor.log` for the last `CompileScripts` block and `error CS` lines instead. Note: the editor only recompiles when it regains focus — if the log shows no new compile after a code change, ask the user to click into the editor window first.

To smoke-test runtime behavior with the editor locked: copy `Assets`, `Packages`, `ProjectSettings` to a temp dir (e.g. `%LOCALAPPDATA%\Temp\demo_smoke`), add an `Assets/Editor/` smoke-test script exposing a static `-executeMethod` that reflection-drives the manager's private lifecycle (Awake/Start/Update) with a manually created MainCamera, and run batchmode against the copy. Smoke-test scripts must `using Unity.Jobs;` (Schedule extension) and reflection `SetValue` on value-type fields must box to the exact field type.

New Input System is active (`activeInputHandler: 1`) — `UnityEngine.Input` legacy class throws at runtime; use `UnityEngine.InputSystem` (`Keyboard.current`, `Mouse.current`).

## Pipeline status (read this first)

The compute-shader pipeline is wired end-to-end. The abandoned Burst-job refactor was deleted (2026-09-01): `Scripts/Terrain/*`, `Scripts/Grid/*`, `CubicMethod/Common.cs` + `ComputeShaderManager.cs`, `Shaders/Noise.compute` + `Math.compute`, `Scripts/Prefabs/Chunk.prefab` are gone.

**Scene is wired (verified 2026-09-03)**: `SampleScene.unity` has `ChunkManager` (chunkSize 32 / maxDistance 200 / updateFrequency 2 / sdfResolution 33 / carveRadius 1.5; computeShader = MarchingCubes.compute, material = Default.mat, generationLayer = 6) and an `SdfWorld` GameObject whose children are `SdfFloor`/`SdfCapsule`/`SdfSphere` prefab instances. No missing scripts. `Nav.NavManager` must be added manually (empty GameObject + component; terrainMask defaults to layer 6).

## Architecture

Data flow: `ChunkManager.InitializeSdfField` (CPU: `SdfFieldEditJob` bakes the `SdfWorld` shape list into the lattice — no GPU upload, the lattice stays CPU-side) → `MarchingCubes` (dispatch `MarchingCubes.compute`) → `BuildMesh` readback + `RecalculateNormals` → `Chunk.SetMesh`. Runtime edits: `CarveAt` (left-click raycast, backfaces on) → `SdfField.Edit` per affected chunk (chunk range = `hit ± 3×radius`; it must cover the Sub modification region `{f < r - d}`, not just the carve sphere — see the carve-time bullet below) → `MarkChunkDirty(coord)` + `SdfChanged` event → dirty chunks remesh at the end of `UpdateChunks`. `SdfGenerator` (heightfield FBM2 + cave FBM3) is dead code kept for future noise terrain.

### Seamless invariant and resolution rules

Seamlessness depends on **integer-exact lattice decomposition**, shared by `SdfGenerator` (generation) and any future edit code:

```
world = (float3)(chunkCoord * cells + lattice) * voxelSize - halfSize
cells = sdfResolution - 1;  voxelSize = chunkSize / cells;  halfSize = chunkSize / 2
```

- `sdfResolution` (serialized on ChunkManager) = SDF lattice resolution per axis, **both borders included** = `cells + 1`. The MC sampling resolution is derived: `cells = sdfResolution - 1`. A past bug: writing `_voxelSize = size / _resolution` (int/int truncation) silently broke everything — the float cast is load-bearing.
- The kernel reads lattice values directly from `StructuredBuffer<float> SDF_VALUES` — flat layout `z*res*res + y*res + x`, `res = sdfResolution` (= cells + 1), indexed with the cell's corner coords. No Texture3D/sampler: the CPU `values` array is the only truth, uploaded to one reusable scratch `ComputeBuffer` per dispatch (no per-chunk GPU copy). SDF convention: negative = solid, positive = air.
- `SdfField` = CPU `values` NativeArray (the truth) — **no GPU copy at all**. `HasSurface` comes from the edit job's TLS reduction: per-thread min/max into a 2×128-slot `threadMinMax` NativeArray via `[NativeSetThreadIndex]` (no float atomics exist in this Collections version), reset before each schedule; `Apply()` merges the 128 pairs on the main thread; `HasSurface` = min<0 && max>=0, no second scan pass. `GenerateChunk` skips MC dispatch + upload for chunks without a surface. Layout index `z*(N*N) + y*N + x`.
- **Chunk-border seams (verified 2026-09-07 via temp-copy smoke test, `SeamSmoke.cs` in the smoke dir)**: with the integer-exact decomposition and the kernel's local positions `-(size/2) + corner * size/cells` (mirror of the CPU formula; for the demo config cells=32 the voxel size is exactly 1.0), neighboring chunks produce **bit-identical** border lattice values (1089/1089) and bit-identical border vertices (118/118) — the meshes are provably seamless; renders showed no cracks along the border line. The MC tables were also numerically verified face-consistent AND mirror-consistent (shared border faces triangulate identically on both sides; the classic table emits no in-face triangles even for saddle faces — it connects them through the cell interior, so there is also no duplicated border triangle / z-fighting). This holds for generation AND for edits applied to both sides of a border (the edit job and the MC kernel are bit-deterministic on identical inputs).
- **Carve-time border consistency (2026-09-07 seam bug, fixed)**: the invariant above only holds if every edit is applied to EVERY chunk containing a modified lattice point. `SdfEditOp.Sub` = `max(f, -g)` — its modification region is `{f < r - d}`, NOT the carve sphere: for a sphere shape it is the ellipsoid `{|p-C| + |p-hit| < r + R}` (C/R = shape center/radius), i.e. the edit propagates through the shape body along the hit→center direction, far past the carve AABB. `CarveAt` used to edit only `[hit ± r]`, so the far-side chunk at a border never received the edit → shared border lattice values diverged → the two chunks interpolated different border vertices (the visible seam). Fixed by widening the affected range to `hit ± 3×radius`. Exact coverage bound: `margin ≥ R + r/2` (3r ⇔ R ≤ 2.5r — if larger shapes are added, derive the margin from the shape radius). The Floor is an unbounded half-space (`f = y < r - d` holds at ANY depth), so no finite margin covers it exactly: deep-solid values still diverge below the surface band. That is harmless — deep divergence never reaches the surface band, and later surface carves dominate the field there (`-g ≈ 0 > f` on both sides), so visible seams do not recur. Caveat for future `Add`/回填: `Add` = `min(f, g)` does NOT dominate diverged points near its surface, so refilling would re-expose seams at old divergences — unify the field depth (clamp deep solid) or extend coverage BEFORE implementing Add.

### Marching Cubes details (MarchingCubes.compute + CubicMethod/MarchingCubes.cs)

- Kernel iterates `cells³` voxels (4×4×4 threads, dispatch `ceil(cells/4)` per axis — 8×8×8 exceeded D3D11's per-group register budget, warning X4714). The kernel emits **per-triangle vertices in winding (v0, v2, v1)** with the legacy x-ordering dedup swap and NaN snap guard (`|v| < 0.0002f` → snap to corner). Preserve these — they define mesh consistency (dedup) and face orientation.
- **MC tables flow: `CubicMethod/McTables.cs` (C# port of the 256+4096 values) → ComputeBuffers → shader `StructuredBuffer<uint> edgeTable` / `StructuredBuffer<int> triTable`.** D3DCompiler cannot map dynamic indexing of global `static const` arrays to cs_5_0 ("cannot map expression to cs_5_0 instruction set"), so the tables must NOT go back into the shader as static consts. `Shaders/Tables.compute` is kept on disk only as the reference copy of the data — it is no longer `#include`d. The small LoopTable0/1/2 arrays stay as `static const` in the shader, but their indexing loops MUST keep `[unroll]` (unrolled indices are compile-time constants).
- The kernel does **not** write the Normals buffer. The live path calls `Mesh.RecalculateNormals()` in `GenerateChunk`; the kernel emits unique per-triangle vertex indices, so this yields flat (per-face) normals. `SdfGenerator.ComputeNormals` (SDF-gradient normals) is dead code.
- **Winding & culling**: the kernel's winding `(v0, v2, v1)` orients every front face toward AIR (negative = solid). A carve (`SdfEditOp.Sub`) surface therefore fronts the bowl's INTERIOR — with a single-sided material the far wall of a carved bowl is back-face culled from outside and the hole reads as a see-through gap. The terrain material must be double-sided: `Chunk.Init` sets `_Cull = 0` on the per-chunk material instance (shadow casting was already `TwoSided`).
- `Counters` holds emitted vertex count / index count; `CubicMethod.MarchingCubes.BuildMesh` reads them back (clamped to `maxVertices = cells³ * 6` — the kernel guards writes but the counters keep counting).
- `MarchingCubes` owns one persistent buffer set, reused across chunks; `Dispatch` resets the counters. Released in `ChunkManager.OnDestroy`.

### Chunk streaming, caching and dirtiness

- 200-instance GameObject pool built at runtime via `AddComponent` (no prefabs). Chunk world center = `chunkCoord * chunkSize`.
- `ChunkCache` holds `Dictionary<Vector3Int, Mesh>` + `Dictionary<Vector3Int, SdfField>`. Unload returns the GameObject to the pool and keeps the caches; reload is a cache hit. Replacing a cache entry destroys the old mesh/texture.
- **Dirty tracking lives on chunk COORDS, not on the pooled `Chunk`** (GameObjects are reused across coords — `Chunk.isDirty` was removed as a bug): `ChunkManager._dirtyChunks` (`HashSet<Vector3Int>`) + `MarkChunkDirty(coord)`. On load: dirty or cache-miss → full regenerate (SDF + mesh) and cache update; clean → reuse cached mesh. Loaded dirty coords remesh at the end of `UpdateChunks` (no per-frame budget yet — a large edit will hitch); unloaded dirty coords keep the flag and regenerate on next load.
- Burst `ChunkVisibilityJob` (every `updateFrequency` frames): the 3×3×3 around the player's chunk is always loaded; beyond that, distance + frustum-AABB culling.
- Scene has no "Player" tag → `ChunkManager` falls back to `Camera.main` as the stream center; `FlyCamera` (WASD + mouse, Input System) is the test camera. "Generated" layer at index 6 in TagManager. No asmdefs. `TutorialInfo/*` is URP template boilerplate; ignore.

### Navigation (custom SDF navmesh, `Scripts/Nav/NavManager.cs`)

Self-built navmesh for DRG-style all-terrain walkers (spiders walk walls/ceilings — Unity NavMesh cannot do arbitrary-up agents, so no NavMeshSurface): nodes are SDF iso-surface samples; A* on the node graph; LOS smoothing. **Right-click** terrain queries a path from the camera (yellow Debug line); left-click carves and the nav rebuilds + auto re-queries.

- Nav cells reuse the same integer lattice decomposition as the SDF (cell (i,j,k) = cube between lattice points i..i+1); a chunk's cells are fully determined by its own `SdfField.values` → seamless across chunks, no border handling.
- Surface detection: single-band rule `minCorner < 0 && maxCorner >= 0` (exact-zero lattice planes yield exactly one node band). Node = cell center projected along the SDF gradient (`pos = center - sdf * normalize(grad)`); node normal = SDF gradient (any orientation). Gradient trilinear is clamped at chunk borders (normals slightly approximate on curved surfaces; positions exact).
- Registry: `_cellToNode` (cell-key → node index into pooled `_nodes`) + `_chunkNodes` (chunk coord → owned indices). **Adjacency is implicit** (26-neighbor cell-key lookups in A*; no edges stored) → chunk rebuilds reconnect automatically.
- Incremental: subscribes to `ChunkManager.SdfChanged` (fired by `InitializeSdfField` and `CarveAt` only — `GenerateChunk` only reads the field). Events queue into `_dirtyNav`, rebuilt in NavManager's own Update (snapshot the queue first — `GetSdfField` during rebuild can re-fire the event).
- A*: binary heap with `heapIndex` back-index for O(log n) decrease-key; euclidean weights/heuristic; per-query state reset via `_touched`. LOS smoothing: greedy farthest-visible; visible = 0.5-step SDF samples with `sdf <= 1.2 && sdf >= -0.5`. Known limitation: LOS can tunnel through walls thinner than 1 unit.
- Nav coverage = `ChunkCache` SdfField coverage (`Sample` creates fields on demand, so coverage grows during queries).

## Known future work (not implemented)

- Runtime `Add`/回填 (mentioned in the Project blurb but not wired to input): has a precondition before implementation — see the carve-time seam bullet above (`Add` = `min(f, g)` re-exposes seams at border values diverged below the surface band; unify field depth with a deep-solid clamp or extend edit coverage first).
- Per-frame remesh budget (dominant cost is the MeshCollider recook), `ChunkCache` LRU cap (SdfField eviction requires persistence first — the field IS the edit history), save/persistence, low-res collision meshes.
- Navigation next steps: walkability clearance (agent radius/height via SDF sampling along -normal), polygon-region compression if node count grows, spider walker (speed = arc-length rate along the path, surface snapping via SDF gradient).
