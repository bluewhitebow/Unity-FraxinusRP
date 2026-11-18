# Fraxinus GPU Batcher

GPU-driven instancing and terrain/detail batching for Unity 2022.3 and Fraxinus RP.

The package depends on `com.bluewhitebow.fraxinus-rp` 0.1.0 and its matching
Core RP / Shader Graph stack. Use this repository's companion Fraxinus forks of
`com.unity.render-pipelines.core` 14.0.99 and `com.unity.shadergraph` 14.0.12;
these forks retain their Unity package IDs. Burst 1.8.28, Collections 2.6.6, and Mathematics
1.3.2 match the host framework. The runtime and editor assemblies reference
`Fraxinus.Runtime`; installing the official Universal RP package is unnecessary.

## Assemblies

- `Fraxinus.GPUBatcher.Runtime` — runtime managers, data model, API, compute resources, and shaders.
- `Fraxinus.GPUBatcher.Editor` — inspectors, setup tools, shader utilities, and editor integrations.
- `Fraxinus.GPUBatcher.Samples` — optional demo scripts imported with the package sample.

The public C# namespace is `Fraxinus.GPUBatcher`; editor-only types use
`Fraxinus.GPUBatcher.Editor`, and demo scripts use
`Fraxinus.GPUBatcher.Samples`. There is no legacy namespace or assembly shim.

## Project data

Generated prototypes, billboard textures, serialized transforms, and converted
shaders are written to `Assets/FraxinusGPUBatcherData/`. Package files remain
the immutable defaults.

## Fraxinus RP integration

GPU Batcher submits culling and indirect draws through Fraxinus RP's
`beginCameraRendering` callback, before the pipeline culls shadow casters and
visible objects. Runtime managers are created from the Profiles in the project
settings. Camera components do not need a separate GPU Batcher handler.

Fraxinus RP owns Hi-Z generation through its `HiZDepthPass` and per-camera
`HiZDepthHistory`. GPU Batcher requests the history only when occlusion culling
is enabled and borrows its texture for culling. The framework manages the depth
prepass, RenderGraph execution, history validation, and resource release. No
GPU Batcher Hi-Z pass or camera generator component is needed.

Hi-Z uses base-camera 2D depth. Stereo and dynamically scaled targets skip
occlusion culling. The first frame, expired history, or changes to the camera
target, size, pipeline, or render scale skip occlusion until a valid pyramid has
been generated. Disabling `HiZDepthPass` in the Renderer Graph also disables
Hi-Z occlusion; frustum and distance culling continue normally.

Use **Tools > Fraxinus GPU Batcher > Check Fraxinus Integration** to check the
active pipeline. Runtime rendering covers base Game cameras; editor previews
continue to use the package's Scene View preview controls.

The included Lit shader follows the current Fraxinus Lit passes and retains
GPU Instancer procedural instancing. Its default binding maps
`Ultra Render Pipeline/Lit` to `GPUInstancer/Ultra Render Pipeline/Lit`.
Shader Graph assets use the Fraxinus Ultra targets and package include paths.

Runtime scripts live directly below `Runtime/`; package resources and shaders
live in the root `Resources/` and `Shaders/` folders.

## Samples

Import **Fraxinus GPU Batcher Demos** from Package Manager to copy the examples from
`Samples~` into the project.

Some inherited demo scenes retain legacy URP camera, post-processing, UI, or
texture references. Rebind those components and resources in the consuming
project before using the scenes; sample-script compilation does not validate
their serialized scene setup.

## Validation

The source integration recorded the following checks with Unity 2022.3.62f3
against its embedded Fraxinus packages:

- Seven compilation scopes cover the updated Fraxinus RP Runtime in Editor and
  Android, plus GPU Batcher Runtime, Editor, and Samples in Editor and Runtime /
  Samples with Android player conditionals.
- 432 shader programs compile across D3D and Android Vulkan, covering Lit and
  all five Shader Graphs, each pass, procedural instancing, and LOD cross-fade.
- 221 Hi-Z checks pass in an isolated Unity project with GPU Batcher removed:
  110 on D3D11 and 111 on Vulkan. They cover framework pass discovery, request
  gating, RenderGraph execution, GPU mip readback (including odd dimensions),
  history invalidation, resizing, and resource release.

These checks do not include a full Android Player build or game-scene visual
acceptance. The upstream temporal occlusion algorithm still uses previous-frame
depth; rapid camera movement needs scene-level evaluation.
