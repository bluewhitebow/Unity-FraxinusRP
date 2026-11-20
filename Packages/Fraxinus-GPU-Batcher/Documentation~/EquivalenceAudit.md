# Reference equivalence audit

This document preserves the comparison performed in the Duckov-Recovered source
snapshot `148ad81fd976e6a5f76624ce081c779c2bd61dd9` on 2026-11-20 against
`D:/Unity/BRGInstance/Assets/BRGInstancedRenderer`. Reference hashes matched the
original import in that snapshot. The port retains the
reference's managed instance behavior and compute algorithms, with Fraxinus pipeline
adaptation and explicit native engine boundaries. It is not a claim of identical
rendered output or of only one missing native API.

`Tools/Rendering/AuditGPUBatcherPort.py` maps identifiers, namespaces, package paths
and GUIDs, then compares tokens while preserving literals and ignoring whitespace
and comments. Its report covers 74 reference code/shader assets: 33 are identical
after renaming, 36 contain reviewed changes, and five URP/HDRP integrations/patchers
are replaced or excluded. Changed counts also include branding, help links and guards.
`SourceAudit.json` records the source snapshot's file hashes and validation results.
This suite renames the preparation hook to `PrepareFrameResources(...)` and retains
its local Core/RP adaptations; those historical hashes do not establish a match for
this checkout or a new validation run.

## Restored source behavior

- Config discovery/creation, shader resource repair and Player Settings preloading.
  Project config storage uses writable `Assets/FraxinusGPUBatcherData`.
- GameObject/terrain renderer takeover even while indirect submission is empty;
  destructive Bake, reconstructed Expand and the source matrix decomposition.
- Native ObjectDispatcher GlobalTRS/type tracking, including assets, through Core's
  existing engine-friend assembly. The former scene polling implementation is removed.
- History HZB after opaque rendering, without forcing a full prepass. Current-frame
  retest after the full depth prepass. History validity follows the source atlas-size
  policy; extra projection, target, exact-size and frame-age invalidation is removed.
- Orthographic, overlay, partial viewport and dynamic-resolution flags no longer
  exclude an otherwise supported camera. Depth dimensions/projection come from the
  actual Fraxinus camera data. Texture-array depth uses the source's slice-zero kernel.
- Debug spheres retain Lit surface defaults and alpha 0.5.

The following remain identical after renaming: chunk/pool storage and compaction,
prototype/signature extraction, allocators, light-probe sampling, mesh combination,
SpeedTree storage/upload, all five core compute/HLSL assets, registration tracker,
baked chunk building, terrain extraction/trees and Shader Graph helper bodies.
The upload partial differs only by an unused field's removal. Command template
algorithms retain source flags, offsets, range filtering and cascade/fade rules.

The package-owned indirect descriptor's 14 fields and flag values were checked
against Unity 6000.0.31f1 metadata. CPU/GPU structs retain source layouts. Semantic
field equality does not permit casting the descriptor to Unity's native ABI.

## Remaining boundaries and adaptations

| Area | Port behavior / reason |
| --- | --- |
| Indirect native output | Requested empty `BRGIndirectSubmission.TrySubmit`; no indirect draws. CPU commands remain renderer-owned until a native backend exists. |
| SpeedTree native wind | Unity 2022 lacks the source wind manager/iterator/update binding, including nonpublic APIs. Bridge reports unsupported and supplies neutral current/history wind slots. |
| Baked Umbra query | Unity 2022 lacks both the context occlusion buffer and native AABB query binding. Bridge reports unsupported and retains candidates. |
| Pipeline | Fraxinus graph hooks, shader tags/includes and depth resolving replace URP/HDRP integration. Lighting/output depends on Fraxinus. |
| HZB platform/XR | Retains the source kernels' reversed-Z/top-origin convention. Stereo cameras and MSAA array depth remain conservatively excluded; single-sampled array depth uses slice zero. No XR equivalence is claimed. |
| HZB lifetime | Immutable build snapshots and per-camera retained retest contexts. Camera uniforms/output buffers are rebound after other camera/shadow work, replacing the previous skip-on-interleave behavior. First-split retest remains as in the source. |
| Cleanup | Idempotent disposal and job/context cleanup prevent use of released containers. |
| Editor/assets | Fraxinus names/menus/resource paths; source demo/logo/help assets and URP/HDRP package patchers omitted. Debug sphere uses a Fraxinus Lit shader rather than generated URP/HDRP auxiliary passes. |

The ObjectDispatcher wrapper uses the same native query signatures present in both
installed Unity versions. Separate metadata comparison confirms SpeedTree wind and
Umbra bindings exist in Unity 6 and are absent in Unity 2022. Reflection or friend
assembly access alone cannot provide missing native bindings.

## Validation

The source snapshot recorded the following passing suites in isolated Unity
2022.3.62f3. Its package, Core wrapper, Fraxinus hooks and helper hashes matched
that source working copy at capture; these are historical results:

- Core bridge, Fraxinus runtime, GPU Batcher Runtime/Editor compilation and GPU Batcher
  Android conditional compilation. Android uses Editor dependency DLLs, not a Player
  build. Remaining compiler warnings concern existing unused/serialized fields.
- 93 D3D/Vulkan shader programs, 56 D3D11 compute kernel/keyword checks and two subgraphs.
- 11 runtime/data checks for config, registration, GPU upload, source takeover,
  destructive Bake/Expand and cleanup; five native bridge checks.
- Nine generated camera commands, six shadow commands and 96 GPU indirect-argument
  values checked against reference expectations.
- Eight history/retest HZB cases and 69 assertions, including real graph execution
  and GPU atlas readback, camera variants, MSAA resolve, array slice zero, history
  validity and selection of the final supported main opaque pass.

Detailed source results are recorded in `SourceAudit.json`; the native metadata
comparison is in `NativeCapabilities.json`. Reproducible check helpers are in the
[source repository's Tools/Rendering directory](https://github.com/zgsxof/Duckov-Recovered/tree/148ad81fd976e6a5f76624ce081c779c2bd61dd9/Tools/Rendering),
not this package repository. Shader, behavior and HZB suites were run separately
as their relevant source changes settled.

The HZB projection helper matches the reference's `OccluderDerivedData.FromParameters`
formula. Synthetic graph fixtures distinguish full prepass depth from final opaque
depth and exercise MSAA resolving and camera variants. They do not constitute final
scene image/performance comparison, full XR validation, or native indirect rendering.
