# Fraxinus GPU Batcher

This package was synchronized from Duckov-Recovered commit
`5494613cdff36fd003f0537a60fd0b1b5498060d`
on 2026-11-22. Recorded source tests and `SourceAudit.json` describe their captured
snapshots, not a GPU/Player validation rerun of this checkout or its later changes.
This suite retains `PrepareFrameResources(...)` and its existing Core/RP adaptations.

BRG instance rendering for Unity 2022.3 and Unity 6 with Fraxinus RP, maintained
from one package source. Validated editor versions are 2022.3.62f3 and 6000.0.85f2.
Runtime types use `Fraxinus.GPUBatcher`; editor tools use `Fraxinus.GPUBatcher.Editor`.

C# naming follows Fraxinus RP: private/protected fields use `_camelCase`, private
static fields use `s_camelCase`, and private constants use `k_camelCase`. Public and
internal fields, properties and events use camelCase; methods use PascalCase.
Shared constant values are exposed through read-only properties, not public const
fields. For example, use `GPUBatcherRenderer.instance`,
`GPUBatcherConfig.packagePath` and `BRGIndirectSubmission.isAvailable`.
Renamed serialized fields retain `FormerlySerializedAs` mappings for existing assets.

`Runtime` contains C# code. Shader assets live in the package-root `Shaders` folder,
with GPU kernels in `Shaders/Compute`. Automatically loaded wind-capture assets live
in the package-root `Resources` folder.

The package includes prototype registration, chunk/pool storage, incremental TRS
upload, matrix generation, colors/light probes, camera/shadow culling, LOD/crossfade,
visibility buffers and indirect argument generation. Config, camera/prototype settings,
GameObject registration, baked groups, terrain trees/details and editor tools retain
the reference workflow.

`Rendering.BatchDrawCommandIndirect` is our descriptor with Unity 6 field/flag
semantics. Camera and shadow draws share `BRGIndirectSubmission.TrySubmit`, which
calls the single synchronous `submitHandler`; `isAvailable` reflects whether a
handler is installed. Stock Unity 6 enables `FRAXINUS_BRG_INDIRECT_API` through the
assembly definition and installs the built-in native writer. A custom engine can
enable that symbol when it backports the complete matching bindings, or register its
own `submitHandler` at startup for a different API. With no handler, `TrySubmit`
returns false without allocating or modifying native output.

Configuration is discovered or created automatically under
`Assets/FraxinusGPUBatcherData/FraxinusGPUBatcherConfig.asset`; existing project configs
are found first. Shader references are repaired automatically and the config is added
to Player Settings preloaded assets. Source renderers/terrain are taken over as in
the reference, and Bake removes the baked source children. Registered objects need a
submission handler to render indirectly; stock Unity 2022 has none by default.

Fraxinus graph injection runs culling before depth/shadow rendering. History HZB is
built after opaque rendering without requesting a full prepass. Current-frame retest
uses a complete depth prepass before opaques. The atlas normalizes device depth to
near=1/far=0, handles depth/UV conventions and resolves MSAA 2D or array depth using
the farthest sample. Per-camera retest state survives interleaved camera/shadow work.

Baked occlusion uses Unity 2022's native `CullingGroup` with spheres enclosing each
chunk. Results are captured after the same camera's native cull and applied before
GPU upload. This is conservative relative to the reference AABB query; camera
occlusion does not filter shadow candidates.

Object/transform tracking uses native ObjectDispatcher through Core's shared
`Unity.InternalAPIEngineBridge.024` engine-friend assembly, recognized
by both editors; there is one wrapper implementation. SpeedTree wind capture uses retained hidden `Tree`
proxies preserving the source mesh and Tree/LOD hierarchy, two private native
cull/draw capture passes and GPU copies into current/history instance slots. Shader
Graph's SpeedTree 8 helpers map those slots through DOTS instancing. The rendering
path performs no per-frame CPU wind readback.
The package requires the repository's Core, Fraxinus RP and Shader Graph adaptations.

The scope is the existing single-view Fraxinus pipeline. Stereo cameras retain the
mono safety gate; extending the render pipeline to XR is outside this package update.
Native branch/leaf wind values may be zero before the engine's first wind update;
the suite checks complete current/history parameter families after simulation advances.

Install this suite through the [repository usage guide](../../docs/Usage.md).
The source validation projects resolved the Duckov-Recovered package directories
through local UPM `file:` dependencies. Recorded source regressions passed in both editors: the 2022 six-suite run and rebuilt Player's two
GPU transform checks/30 wind frames, plus Unity 6's four algorithm suites, 28 native
BRG checks and five actual graph image cases. These recorded runs used stock engines
without a custom handler: submission was unavailable in 2022 and available in Unity 6.
Recorded results are kept in the independent `Fraxinus-Unity6-Validation` project's
`Validation/Results/UnifiedPackages`.

See [port notes](Documentation~/Port.md) and the
[equivalence audit](Documentation~/EquivalenceAudit.md) for mapping, validation and
remaining engine/pipeline differences.
