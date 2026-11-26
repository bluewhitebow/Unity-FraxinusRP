# Reference equivalence audit

Reference: `D:/projects/unityProjects/BRGInstance/Assets/BRGInstancedRenderer`,
rechecked on 2026-11-22. The 74 reference code/shader assets matched the original
import hashes. The port retains the managed registration/storage workflows and
command-generation contracts, with Fraxinus pipeline, depth and native-API adaptations.
Source comparison does not establish identical rendered output or performance.

`Tools/Rendering/AuditGPUBatcherPort.py` maps identifiers, namespaces, package paths
and GUIDs, then compares tokens while preserving literals and ignoring whitespace
and comments. Its mapping covers those 74 reference assets; URP/HDRP integrations,
patchers and the URP texture-grass shader gate are replaced or excluded. The current
HZB and native bridges contain intentional algorithm/API changes, so the original
import's token-equality counts are not a completion metric. `SourceAudit.json` records
the audited snapshot's hashes and validation results. Package-owned compatibility
code also requires runtime checks beyond the reference-file comparison.

## Restored source behavior

- Config discovery/creation, shader resource repair and Player Settings preloading.
  Project config storage uses writable `Assets/FraxinusGPUBatcherData`.
- GameObject/Terrain registration takes over source rendering when indirect drawing is
  available. Explicit Bake and Expand retain their reference authoring behavior.
- Native ObjectDispatcher GlobalTRS/type tracking, including assets, through Core's
  existing engine-friend assembly. The former scene polling implementation is removed.
- History HZB after opaque rendering, without forcing a full prepass. Current-frame
  retest after the full depth prepass. History validity follows the source atlas-size
  policy; extra projection, target, exact-size and frame-age invalidation is removed.
- Orthographic, overlay, partial viewport and dynamic-resolution cameras use actual
  Fraxinus depth dimensions and projection data. HZB normalizes depth/UV conventions
  and handles single-sampled or MSAA 2D/array depth with explicit slice selection.
- Baked occlusion uses current-cull native `CullingGroup` visibility with conservative
  enclosing spheres, independent camera snapshots and stale-bounds protection.
- SpeedTree current/history capture is implemented through hidden native `Tree`
  proxies retaining the original mesh and Tree/LOD hierarchy, two private native
  capture passes and GPU slot copies. Shader Graph's SpeedTree 8 helpers have DOTS
  property mappings. The rendering path does not read wind back to the CPU each frame.
- Debug spheres retain Lit surface defaults and alpha 0.5.

Chunk/pool storage and compaction, prototype/signature extraction, registration,
baked chunk building and terrain extraction retain the reference algorithms. Command
templates preserve source flags, offsets, range filtering and cascade/fade rules.
The HZB compute/HLSL implementation now normalizes device conventions and resolves
MSAA array inputs. Wind capture/upload integration and SpeedTree shader helpers also
contain adaptations; they are not claimed to be unchanged reference files.

The package-owned indirect descriptor's 14 fields and flag values were checked
against Unity 6000.0.31f1 metadata. CPU/GPU structs retain source layouts. Semantic
field equality does not permit casting the descriptor to Unity's native ABI.

## Remaining boundaries and adaptations

| Area | Port behavior / reason |
| --- | --- |
| Indirect native output | Unity 6 enables `FRAXINUS_BRG_INDIRECT_API` and writes native camera and shadow draws. The custom Win64 Unity 2022.3.62f3 Vulkan editor submits indexed draws through its BRG writer. Stock Unity 2022 builds visibility and arguments. |
| SpeedTree native wind | Available Unity 2022 `Tree` rendering captures current/history constants through two private native capture passes and GPU copy. Source mesh and Tree/LOD hierarchy are preserved. The original managed wind-manager binding remains absent. |
| SpeedTree initialization/cost | Native branch/leaf values can remain zero before the first engine wind update; full parameter families are checked after simulation advances. Each distinct retained native source/prototype adds two capture passes per rendering context plus the slot copy. |
| Baked Umbra query | Available native `CullingGroup` queries enclosing spheres after the current camera cull. Spheres can retain more chunks than the original native AABB query. Camera results do not filter shadow candidates. |
| Pipeline | Fraxinus graph hooks, shader tags/includes and depth resolving replace URP/HDRP integration. Lighting/output depends on Fraxinus. |
| HZB depth inputs | Canonical near=1/far=0 depth, UV-origin and clip-depth conversion, viewport-aware reduction, and farthest-sample MSAA 2D/array resolving with explicit slice selection. |
| XR scope | The existing Fraxinus pipeline is single-view. Its mono safety gate remains; extending the pipeline to stereo/XR is outside this update. Array-depth support is not a stereo-support claim. |
| HZB lifetime | Immutable build snapshots and per-camera retained retest contexts. Camera uniforms/output buffers are rebound after other camera/shadow work, replacing the previous skip-on-interleave behavior. First-split retest remains as in the source. |
| Cleanup | Idempotent disposal and job/context cleanup prevent use of released containers. |
| Editor/assets | Fraxinus names/menus/resource paths; source demo/logo/help assets and URP/HDRP package patchers omitted. Debug sphere uses a Fraxinus Lit shader rather than generated URP/HDRP auxiliary passes. |
| Shared source/API selection | Both editors resolve the same package directories. Shadow APIs, categorization aliases and GraphView overrides use version macros; GPUDriven compilation uses its `.asmdef` constraint. Storage, culling, HZB and wind code is shared. |

The single ObjectDispatcher wrapper uses the common
`Unity.InternalAPIEngineBridge.024` friend identity and native query signatures
available in both tested engines. There are no parallel version-specific wrappers.
The metadata comparison in `NativeCapabilities.json` records the newer wind and
BRG AABB bindings that are absent in that engine. The compatibility paths use
`Tree` rendering and `CullingGroup`; they do not create new managed/native bindings.

## Shared-source validation status

All maintained package files now live in
`D:/projects/unityProjects/Duckov-Recovered/ExportedProject/Packages`. Both validation
projects use local UPM `file:` references. Validation checked the actual resolved
paths for GPU Batcher, Fraxinus RP, Core and Shader Graph. The recorded stock-engine
runs had no custom handler: submission was unavailable in 2022 and available in Unity 6.
These results do not validate a custom engine backport or a newly registered handler.

The consolidated source has passed:

- Unity 2022's six-suite aggregate and a rebuilt Windows Player with two GPU transform
  readbacks and 30 production-context wind frames.
- Unity 6 compilation, four algorithm suites, 28 native BRG checks and five actual
  Fraxinus graph image cases covering history/retest occlusion and visibility recovery.
- Both editors' shared-path checks: GPU Batcher, Fraxinus RP, Core and Shader Graph
  resolve to the canonical directories; default submission availability was false in
  stock 2022.3.62f3 and true in stock 6000.0.85f2, with no custom handler registered.

Recorded results are retained in the independent `Fraxinus-Unity6-Validation`
project's `Validation/Results/UnifiedPackages`, separate from earlier independent runs.

## Recorded validation before consolidation

Recorded Unity 2022.3.62f3 runs before the shared-source consolidation include:

- All six Editor suites passed: native bridges,
  runtime/data, command parity, HZB, baked occlusion and shaders.
- Core/Fraxinus/GPU Batcher compilation, 114 targeted D3D/Vulkan shader programs,
  61 compute kernel/keyword import/support checks on D3D11, two Shader Graph
  subgraphs, and native ObjectDispatcher tracking.
- Runtime/data checks for config, registration, GPU upload, intentional source
  takeover, destructive Bake/Expand and cleanup.
- Nine generated camera commands, six shadow commands and 96 GPU indirect-argument
  values checked against reference expectations.
- 120 HZB checks, including actual graph/compute execution and GPU readback. These
  cover history/retest scheduling, camera variants, viewport bounds, depth/UV/clip
  conventions, pyramid contents, unequal MSAA samples and independent array slices.
- Six baked-occlusion compatibility checks using an actual Umbra bake. They verify
  immediate results after native culling, enclosing-sphere bounds, changed chunks,
  stale generations, interleaved cameras, missing baked data, disposal, and Core
  filtering that preserves shadow candidates and honors the config toggle.
- 450 native SpeedTree capture assertions passed in a separate Play Mode run,
  including a source never rendered beforehand, duplicate/empty slots, write-range
  preservation, complete parameters after initialization, temporal history, WindZone
  changes, same-frame reinitialization and source/proxy cleanup. The initial native
  identity state is allowed; complete branch/leaf families are checked after ten frames.
  Additional cases cover independent sources at nonzero buffer offsets, reference-counted
  ownership, and unreadable UInt16/UInt32 index buffers with nonzero base vertices.
- A Windows Mono Development Player build containing a real SpeedTree model passed.
  The standalone executable passed two GPU transform readbacks and 30 frames through
  the production render-context wind hook. Each frame poisons then checks all 32 wind
  vectors; native history, complete parameter families and WindZone response passed.
  Real build callbacks verify that both capture passes retain their runtime variants.

Suite results and matching hashes belong to the validated snapshot in `SourceAudit.json`;
earlier test runs do not validate later code changes automatically.

The earlier independent Unity 6 snapshot also passed native camera color/depth/shadow
draw checks and five actual Fraxinus graph history/retest image cases. Its graph uses
the ten package nodes, reconnecting opaque -> sky -> post while omitting six unavailable
`TeamSoda.Duckov.Core` nodes. Former package copies are archived under
`Validation/Backups/BeforeSharedPackages-*`; new results must validate the canonical
source rather than those archived copies.

The independent validation project's README lists its retained test entry points.
Conditional compilation against Editor dependency DLLs is not a Player build. Native
and GPU execution was tested on D3D11; Vulkan shader compilation and tests of supplied
depth conventions do not establish execution on Vulkan or every graphics backend.

The HZB projection helper matches the reference's `OccluderDerivedData.FromParameters`
formula. Synthetic graph fixtures distinguish full prepass depth from final opaque
depth and exercise MSAA resolving and camera variants. Algorithm-only fixtures do not
establish native indirect rendering; the Unity 6 image fixtures exercise that separately.
Neither establishes full-game image/performance equivalence. XR validation remains
outside the scope of this single-view update.
