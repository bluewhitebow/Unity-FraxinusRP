# BRG implementation port

This package was synchronized from Duckov-Recovered commit
`5494613cdff36fd003f0537a60fd0b1b5498060d`
on 2026-11-22. Recorded source tests and `SourceAudit.json` describe their captured
snapshots, not a GPU/Player validation rerun of this checkout or its later changes.
This suite retains `PrepareFrameResources(...)` and its existing Core/RP adaptations.

Source: `D:/projects/unityProjects/BRGInstance/Assets/BRGInstancedRenderer`, version 1.2.0.
The original copyright notices are retained in adapted source files.

| Reference | Fraxinus package |
| --- | --- |
| `BRGInstancedRenderer` namespace | `Fraxinus.GPUBatcher` |
| `BRGRenderer` and core partials | `GPUBatcherRenderer`, `Runtime/Core` |
| `BRGInstancedRendererConfig` | `GPUBatcherConfig`, `Runtime/Config` |
| `BRG*` registration components | `GPUBatcher*`, `Runtime/Registration` |
| `TerrainBRGRegisterer` | `TerrainGPUBatcherRegisterer` |
| `BRG_*` compute kernels/assets | `GPUBatcher_*`, `Shaders/Compute` |
| URP render passes | Fraxinus `InjectPass` modules, `Runtime/Integration` |
| Unity 6 native indirect command | Package-owned `Rendering.BatchDrawCommandIndirect` |

The instance storage, CPU/GPU layouts and command-generation contracts are retained.
HZB depth normalization and the native compatibility bridges adapt the reference
algorithms to the APIs and depth inputs available in Unity 2022.
Config discovery/creation/resource repair/preloading, renderer/terrain takeover,
destructive Bake and Expand follow the reference. The default config location is
writable project data instead of the immutable package directory.

Draw templates and per-view scratch data remain renderer-owned. Camera and shadow
submission both call the single synchronous `BRGIndirectSubmission.submitHandler`
through `TrySubmit`. Its `public static SubmitHandler submitHandler { get; set; }`
property can be assigned at startup; `isAvailable` is true whenever a handler is installed. Without one,
`TrySubmit` returns false without allocating or writing native output.

The assembly definition automatically enables `FRAXINUS_BRG_INDIRECT_API` on stock
Unity 6 to install the built-in writer. A Unity 2022 engine backport with the complete
matching bindings can define the same symbol; different bindings require a custom
handler. The built-in writer copies descriptor fields into engine-owned allocations
and marks ranges as indirect, never casting the package descriptor to the native ABI.
Handlers must finish synchronously rather than retain borrowed spans for later work.
Editor picking retains its direct-command path.

ObjectDispatcher tracking uses the same GlobalTRS and type-change queries as the source.
Core's shared `Unity.InternalAPIEngineBridge.024` engine-friend assembly
provides a small owned-snapshot wrapper; there is no scene-wide polling or reflection.
Both installed engines recognize this assembly identity, so the wrapper source and
its callers are shared rather than split into engine-specific assemblies.
The GPU package therefore requires this repository's Core fork. Fraxinus also provides
a small `InjectPass.ShouldExecute` hook so history HZB selects the final opaque node
supported by the current camera before consuming the once-per-context injection.

History HZB samples opaque depth after the last main-context opaque producer, resolving
MSAA depth at that point if necessary. It does not request a full depth prepass.
Retest uses the complete prepass after `RenderDrivenPass`. CameraData view/projection
matrices match rendered depth, including overlay projection changes; scaled dimensions
follow the active descriptor. Each camera retains its retest data and restores its
uniforms after interleaved camera/shadow work. The reference's first-split retest policy
is retained. HZB converts device depth into a canonical near=1/far=0 atlas and accounts
for texture UV origin and clip-depth range. MSAA 2D and array inputs resolve to the
farthest sample before reduction, with an explicit array slice and viewport bounds.

## Native compatibility paths

Unity 2022 still lacks the reference's managed SpeedTree wind-manager/iterator and
BRG occlusion-buffer/AABB-query bindings. The port uses available native APIs:

- Baked occlusion uses one `CullingGroup` per camera and a sphere enclosing each
  chunk AABB. Fraxinus invokes preparation immediately before native `Cull`, then
  publishes its results immediately afterward. Camera candidates are filtered after
  their CPU culling jobs complete and before GPU upload; shadow candidates are
  independent. Generation and exact-bounds checks prevent stale visibility from
  rejecting changed chunks. Missing or invalid snapshots retain candidates. The
  enclosing sphere can keep more chunks visible than the original AABB query.
- SpeedTree wind uses retained hidden native `Tree` proxies with the original source
  mesh and Tree/LOD hierarchy. A private native `Cull` and two `DrawRenderers` capture
  passes write Unity's current/history constants into a buffer; a compute copy
  distributes them to the instance slots without per-frame CPU readback. Shader
  Graph's SpeedTree 8 DOTS mappings consume those slots. Duplicate slots share a
  captured source. The additional cost is two capture passes per distinct native
  source/prototype plus the GPU copy, once per rendering context.

The native SpeedTree suite passes cold initialization, temporal history, WindZone
response, duplicate slots, range preservation and resource-lifetime checks. A newly
created native Tree can expose identity wind with zero branch/leaf parameters until
its first native simulation update. The suite accepts that initial state and checks
complete current/history parameter families after ten frames; this does not promise
fully animated branch values on the first draw.

These compatibility paths are shared by both editor versions. They do not add or
impersonate missing engine bindings. Submission availability follows the installed
handler, not the editor version alone. Stock Unity 2022 has no handler by default.

## Source validation layout

The recorded Unity 2022 and Unity 6 validation projects referenced
`Duckov-Recovered/ExportedProject/Packages` through local UPM `file:` dependencies;
their `Assets` contain independent fixtures and project data. Consumers of this
checkout should use the [suite installation guide](../../../docs/Usage.md). Former embedded validation copies are reviewable archives
under `Validation/Backups/BeforeSharedPackages-*`, not active package sources.

Fraxinus shadow culling and drawing use `UNITY_6000_0_OR_NEWER` for Unity 6's native
`CullShadowCasters`/split-index API while keeping the 2022 slice path. Categorization
namespace aliases and Shader Graph editor overrides select the appropriate API
without duplicating the classes. Explicit PopupList aliases work on both versions.
Core's GPUDriven assembly is gated by its `.asmdef` `defineConstraints`; assembly
references use that Unity mechanism rather than C# directives inside JSON.

The independent `Fraxinus-Unity6-Validation` project retains its test entry points
and recorded results under `Validation/Results/UnifiedPackages`.

## Single-view scope

This update targets the existing single-view Fraxinus pipeline. Array depth input
support does not establish stereo rendering support. The current mono safety gate
continues to exclude stereo cameras from these occlusion paths. Full XR pipeline
work is outside this update.

## Package integration

Runtime/Editor assembly identities and GUIDs are preserved. Imported asset GUIDs and
shader-helper references are remapped consistently. Source demos, logos, help URLs,
and URP/HDRP package patchers are omitted; Fraxinus integration replaces those paths.
Shaders use Fraxinus tags/includes. The debug sphere retains Lit surface defaults and
alpha 0.5 through a Fraxinus shader; generated URP/HDRP auxiliary passes are omitted.
Disposal guards and explicit graph-context lifetimes prevent released storage reuse.

The automatically discovered BRG injectors retain this suite's conservative
shader-stripping behavior for modules without reviewed stripping contracts.
UltraRP can be used without this optional package.

The [equivalence audit](EquivalenceAudit.md) separates implementation from validation.
After consolidation, both stock editors verified their canonical package paths and
default submission availability, without a custom handler. The Unity 2022 six-suite
aggregate passed, and its rebuilt Windows Player passed two GPU transform checks and
30 production wind frames. Unity 6 passed
compilation, four algorithm suites, 28 native BRG checks and five actual Fraxinus graph
history/retest image cases. The earlier 450-assertion native wind suite remains recorded
against its original snapshot; the new Player run separately verifies shared-source wind.
Results remain in the independent validation project. Those stock Unity 2022 runs
did not exercise native indirect scene output. Unity 6's native image fixtures test
selected behaviors, not full-game visual equivalence or performance.
