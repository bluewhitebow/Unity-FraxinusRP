# Fraxinus GPU Batcher

This package was synchronized from Duckov-Recovered commit
`75eb0a83bbbe5f7adf40ad48742365aebbc58440`
on 2026-11-24. Recorded source tests and `SourceAudit.json` describe their captured
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
semantics. An installed synchronous `BRGIndirectSubmission.submitHandler` takes
precedence for camera and shadow draws. `BRGIndirectSubmission.isAvailable`
reports that binding. Stock Unity 6 enables `FRAXINUS_BRG_INDIRECT_API` through the
assembly definition and installs the built-in native writer. A custom engine can
enable that symbol when it backports the complete matching bindings, or register its
own `submitHandler` at startup for a different API. With no handler, `TrySubmit`
returns false without allocating or modifying native output.

The custom Unity 2022.3.62f3 Editor and its Player templates use the bindings
in `Runtime/Plugins/FraxinusNativeContract`. Unity imports the Win64 and
Android ARM64 libraries directly from the package. When upgrading an existing
project, remove its previous `Assets/Plugins/FraxinusNativeContract` copy to
avoid importing the same plugins twice.

Stock Windows x64 Unity 2022 can also execute the package's indexed commands through
the included Vulkan plugin. Start the editor or Player with `-force-vulkan`.
`GPUBatcherRenderer.canSubmitDraws` includes this backend. Add
`-disableFraxinusVulkanBRG` for a control run. Unity's BRG renderer lists select
the Fraxinus DOTS material and shader passes. Each active view uses private mesh
index buffers to associate those draws with the GPU's indirect arguments.
The compatibility path copies instance data and GPU visibility into a per-view
raw BRG buffer. Budget memory for mesh copies and these view buffers. The shader
path requires the bundled Fraxinus DOTS includes. Its visibility offset limit is
8,388,607 elements. The packaged binary targets Windows x64 Vulkan.
Its [native source](https://github.com/zgsxof/Duckov-Recovered/blob/f48854aa6e2d7fbdb4d27236a4330fa2d3e1f7fa/Tools/Rendering/NativeBRGBridge/VulkanPlugin/FraxinusBrgVulkan.cpp)
and [build script](https://github.com/zgsxof/Duckov-Recovered/blob/f48854aa6e2d7fbdb4d27236a4330fa2d3e1f7fa/Tools/Rendering/NativeBRGBridge/VulkanPlugin/Build-Windows.ps1)
are retained at the earlier Vulkan-backend source commit linked above. Rebuilding requires the Unity
Vulkan PluginAPI headers, Vulkan headers and the Visual Studio C++ toolchain.

Edit the active configuration in **Edit > Project Settings > Graphics > Fraxinus GPU Batcher**.
The page edits the same asset used by the Player. Configuration is discovered or created automatically under
`Assets/FraxinusGPUBatcherData/FraxinusGPUBatcherConfig.asset`; existing project configs
are found first. Shader references are repaired automatically and the config is added
to Player Settings preloaded assets. Source renderers/terrain are taken over as in
the reference, and Bake removes the baked source children. Check
`GPUBatcherRenderer.canSubmitDraws` on the active graphics device before takeover.

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

Install this suite through the [repository usage guide](../../docs/Usage.md).
The source validation projects resolved the Duckov-Recovered package directories
through local UPM `file:` dependencies. Recorded source regressions passed in both editors: the 2022 six-suite run and rebuilt Player's two
GPU transform checks/30 wind frames, plus Unity 6's four algorithm suites, 28 native
BRG checks and five actual graph image cases. Those pre-plugin runs used stock engines
without a custom handler: submission was unavailable in 2022 and available in Unity 6.
Recorded results are kept in the independent `Fraxinus-Unity6-Validation` project's
`Validation/Results/UnifiedPackages`.

The Windows Vulkan backend was subsequently tested on 2022.3.62f3 in the Editor
and a Win64 Mono Player, including first-draw color, depth, directional shadows,
GPU-driven 0/1/2 instance counts, and 289 independently placed/colored instances
in one draw. The Fraxinus graph test also covered HZB history and retest.

See [port notes](Documentation~/Port.md) and the
[equivalence audit](Documentation~/EquivalenceAudit.md) for mapping, validation and
remaining engine/pipeline differences.
