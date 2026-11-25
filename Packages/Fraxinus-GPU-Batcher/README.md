# Fraxinus GPU Batcher

GPU instance registration and culling for Unity 2022.3 and Unity 6 with Fraxinus RP.
Unity 6 also submits native BRG indirect camera and shadow draws.
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

Both versions build camera and shadow visibility lists and indirect arguments. The
runtime asmdef enables `FRAXINUS_BRG_INDIRECT_API` on Unity 6. `BRGIndirectSubmission`
copies the shared draw descriptors into native BRG output there. On Unity 2022,
`GPUBatcherRenderer.canSubmitDraws` is false and `TrySubmit` leaves the output untouched.

Edit the active configuration in **Edit > Project Settings > Graphics > Fraxinus GPU Batcher**.
The page edits the same asset used by the Player. Configuration is discovered or created automatically under
`Assets/FraxinusGPUBatcherData/FraxinusGPUBatcherConfig.asset`; existing project configs
are found first. Shader references are repaired automatically and the config is added
to Player Settings preloaded assets. Live GameObject and Terrain registration leaves
source rendering active on Unity 2022. Explicit Bake removes its source children.
Baked-only groups and instances created solely through the API need Unity 6 for
BRG rendering. Use Expand to reconstruct GameObjects from a baked group.

Open **Window > Analysis > Rendering Debugger** and select **GPU Batcher** in the sidebar for culling
overrides, Scene view diagnostics and live instance, chunk and draw-command counts.
Enable **Visualize Culling In Scene View** and use **Culling Reference Camera** to inspect a Game
camera's visibility from another angle. Clear the selection to follow the current Main Camera.

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

Maintain package code only in
`D:/projects/unityProjects/Duckov-Recovered/ExportedProject/Packages`. Validation
projects resolve those directories through local UPM `file:` dependencies. Shared-source
regressions passed in both editors: the 2022 six-suite run and rebuilt Player's two
GPU transform checks/30 wind frames, plus Unity 6's four algorithm suites, 28 native
BRG checks and five actual graph image cases. Those pre-plugin runs used stock engines
without a custom handler: submission was unavailable in 2022 and available in Unity 6.
Recorded results are kept in the independent `Fraxinus-Unity6-Validation` project's
`Validation/Results/UnifiedPackages`.

These recorded results belong to the revisions tested.

See [port notes](Documentation~/Port.md) and the
[equivalence audit](Documentation~/EquivalenceAudit.md) for mapping, validation and
remaining engine/pipeline differences.
