# Fraxinus GPU Batcher

GPU instance registration, culling and native BRG indirect drawing for Fraxinus RP on Unity 6 and Tuanjie 1.8.0+ (2022.3.62t2 onward). The Terrain Demo was run on Tuanjie 1.11.2.
In stock Unity 2022.3, scene components stay available for serialization and ordinary renderers draw their own objects.
Runtime types use `Fraxinus.GPUBatcher`; editor tools use `Fraxinus.GPUBatcher.Editor`.

## Install in Unity 2022.3.62f3

In Package Manager choose **+ → Add package from disk…** and select this directory's
`package.json`. Built-in and standard URP projects can import the components with
the public Unity packages. Terrain and MeshRenderers handle rendering in this mode.
For baked-only instance groups, use **Expand** to create ordinary renderers.
To run the native indirect renderer, open the full Duckov project with its embedded
Fraxinus packages in a supported Tuanjie or Unity 6 editor.

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

Camera and shadow draw descriptors are translated to each engine's native BRG command layout.
Check `GPUBatcherRenderer.canSubmitDraws` before acquiring the renderer or registering instances.
It requires compute shaders, GPU instancing, indirect arguments and raw BRG buffers. Unsupported engines or devices return false.

## Configuration and diagnostics

Set **Project Settings > Graphics > Shader Stripping > BatchRendererGroup Variants** to **Keep All** for Player builds.
In the Terrain Demo, the performance panel switches between GPU Batcher and Terrain. **Tools > Fraxinus GPU Batcher > System Window** shows instance and draw-command counts.

Edit the active configuration in **Edit > Project Settings > Graphics > Fraxinus GPU Batcher**.
The page edits the same asset used by the Player. Configuration is discovered or created automatically under
`Assets/FraxinusGPUBatcherData/FraxinusGPUBatcherConfig.asset`; existing project configs
are found first. Shader references are repaired automatically and the config is added
to Player Settings preloaded assets. Live GameObject and Terrain registration takes
over source rendering when the backend supports indirect submission. Explicit Bake
removes its source children. Baked-only groups and instances created through the API
require a working submission backend. Use Expand to reconstruct GameObjects from a baked group.

Open **Window > Analysis > Rendering Debugger** and select **GPU Batcher** in the sidebar for culling
overrides, Scene view diagnostics and live instance, chunk and draw-command counts.
Enable **Visualize Culling In Scene View** and use **Culling Reference Camera** to inspect a Game
camera's visibility from another angle. Clear the selection to follow the current Main Camera.

Fraxinus graph injection runs culling before depth/shadow rendering. History HZB is
built after opaque rendering without requesting a full prepass. Current-frame retest
uses a complete depth prepass before opaques. The atlas normalizes device depth to
near=1/far=0, handles depth/UV conventions and resolves MSAA 2D or array depth using
the farthest sample. Per-camera retest state survives interleaved camera/shadow work.

Baked occlusion uses Unity's native `CullingGroup` with spheres enclosing each
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
The GPU rendering path uses the repository's Core, Fraxinus RP and Shader Graph adaptations.

The scope is the existing single-view Fraxinus pipeline. Stereo cameras retain the
mono safety gate; extending the render pipeline to XR is outside this package update.
Native branch/leaf wind values may be zero before the engine's first wind update;
the suite checks complete current/history parameter families after simulation advances.

Earlier validation results are documented in the equivalence audit for the revisions tested.

See [port notes](Documentation~/Port.md) and the
[equivalence audit](Documentation~/EquivalenceAudit.md) for mapping, validation and
remaining engine/pipeline differences.
