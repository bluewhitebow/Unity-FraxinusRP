# BRG implementation port

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
On Unity 6 and Tuanjie 1.8.0+ the renderer submits camera and shadow commands to the native BRG output.
Tuanjie uses its own indirect-command layout and LOD flags, and the 2022 shadow renderer list with per-slice `ShadowDrawingSettings.splitData`.
Editor picking uses direct commands. On stock Unity 2022.3 the registration
components remain loadable and live source renderers retain ownership. Expand
baked-only groups to reconstruct ordinary GameObjects for these versions.

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

The 1.7.6 editor (2022.3.62t1b7) exposes direct BRG commands. The 1.8.0 editor (2022.3.62t2) adds the native indirect command, its output pointer and count, and indirect draw ranges. The optional `UseLightmapSingleKeyword` flag first appears in 1.8.2 (2022.3.62t4). The package's generated camera and shadow commands use the common 1.8.0 fields. See the [1.7 output structure](https://docs.unity.cn/cn/tuanjiemanual/1.7/ScriptReference/Rendering.BatchCullingOutputDrawCommands.html) and [1.8 indirect command](https://docs.unity.cn/cn/tuanjiemanual/1.8/ScriptReference/Rendering.BatchDrawCommandIndirect.html).

Fraxinus uses raw visibility buffers. For materials shared with other BRG batches, the shader also matches the engine's uniform-buffer layout: 64 KiB from 1.8.0 through 1.10.0 and 16 KiB from 1.10.1. See the [1.10.1 UBO fix](https://unity.cn/editor1-10-1/release-note).

The Unity 6 implementation uses these APIs for occlusion and wind:

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

## Embedded package copies

Duckov-2022 and Duckov-6 use embedded copies of the rendering packages. Keep their shared
code in sync while preserving project-specific Unity package versions.

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

The [equivalence audit](EquivalenceAudit.md) separates implementation from validation.
In the earlier port, both stock editors verified their canonical package paths and
default submission availability, without a custom handler. The Unity 2022 six-suite
aggregate passed, and its rebuilt Windows Player passed two GPU transform checks and
30 production wind frames. Unity 6 passed
compilation, four algorithm suites, 28 native BRG checks and five actual Fraxinus graph
history/retest image cases. The earlier 450-assertion native wind suite remains recorded
against its original snapshot; the new Player run separately verifies shared-source wind.
Results remain in the independent validation project. Those stock Unity 2022 runs
did not exercise native indirect scene output. Unity 6's native image fixtures test
selected behaviors, not full-game visual equivalence or performance.
