# Using Unity-FraxinusRP

[English](Usage.md) | [简体中文](Usage.zh-CN.md) · [Repository](../README.md)

Use **Unity 2022.3**, with **2022.3.62f3** matching the source project. This repository is a collection of UPM packages. A consuming Unity project supplies its own `Assets`, `ProjectSettings` and `Packages/manifest.json`.

The repository contains the four companion packages for **ScriptableNode / UltraRP**: Core, Light Transport, Shader Graph and FraxinusRP, plus the optional **Fraxinus GPU Batcher**. The examples below install all five. GPU Batcher shares one implementation across Unity 2022.3 and Unity 6, with native indirect submission enabled by default on stock Unity 6 and a bundled Vulkan backend for stock Windows x64 Unity 2022. Omit `com.bluewhitebow.fraxinus-gpu-batcher` when it is not needed. UltraRP does not require URP.

Use the four companion packages and, when installed, GPU Batcher from the same revision. The BRG port requires Core's native object-change tracking wrapper, UltraRP's culling and per-node injection hooks, and Shader Graph's SpeedTree adaptations; matching package version numbers alone are insufficient.

## Install from a local checkout

Place the checkout beside the consuming project:

```text
workspace/
├── Unity-FraxinusRP/
│   └── Packages/
└── MyUnityProject/
    └── Packages/
        └── manifest.json
```

Merge these entries into the project's `Packages/manifest.json` → `dependencies`, keeping its other dependencies. [manifest.local.json](manifest.local.json) contains the same dependency block in a JSON file.

```json
{
  "com.unity.render-pipelines.core": "file:../../Unity-FraxinusRP/Packages/Fraxinus-RPCore",
  "com.fraxinus.rendering.light-transport": "file:../../Unity-FraxinusRP/Packages/Fraxinus-Light-Transport",
  "com.unity.shadergraph": "file:../../Unity-FraxinusRP/Packages/Fraxinus-ShaderGraph",
  "com.bluewhitebow.fraxinus-rp": "file:../../Unity-FraxinusRP/Packages/FraxinusRP",
  "com.bluewhitebow.fraxinus-gpu-batcher": "file:../../Unity-FraxinusRP/Packages/Fraxinus-GPU-Batcher"
}
```

`file:` paths are relative to the consuming project's **Packages directory**. The examples use the default clone directory name, `Unity-FraxinusRP`. Adjust the paths if your checkout has a different name or is not beside the project.

Unity resolves the remaining registry and built-in dependencies from the package declarations. The source project's resolved shared versions include Burst **1.8.28**, Collections **2.6.6** and Mathematics **1.3.2**. If the consuming manifest explicitly pins older versions, review those pins when adopting this set.

## Install from Git

Merge the following entries into `dependencies`, or use [manifest.git.json](manifest.git.json). The `?path=` selects an individual UPM package inside the repository; the repository root is not itself a UPM package.

```json
{
  "com.unity.render-pipelines.core": "https://github.com/bluewhitebow/Unity-FraxinusRP.git?path=/Packages/Fraxinus-RPCore#main",
  "com.fraxinus.rendering.light-transport": "https://github.com/bluewhitebow/Unity-FraxinusRP.git?path=/Packages/Fraxinus-Light-Transport#main",
  "com.unity.shadergraph": "https://github.com/bluewhitebow/Unity-FraxinusRP.git?path=/Packages/Fraxinus-ShaderGraph#main",
  "com.bluewhitebow.fraxinus-rp": "https://github.com/bluewhitebow/Unity-FraxinusRP.git?path=/Packages/FraxinusRP#main",
  "com.bluewhitebow.fraxinus-gpu-batcher": "https://github.com/bluewhitebow/Unity-FraxinusRP.git?path=/Packages/Fraxinus-GPU-Batcher#main"
}
```

Git must be available to Unity and able to access the repository. `#main` follows development; for reproducible projects, replace **every** `#main` with the same published commit hash or tag. Keep the consuming project's lockfile under version control.

Packages with the same ID already embedded under the consuming project's `Packages/` take precedence over manifest references. Back up any local work and move obsolete embedded copies outside that directory when switching to checkout or Git dependencies. Confirm the actual package source in Package Manager after Unity resolves the manifest.

## Configure ScriptableNode / UltraRP

The Graphics pipeline asset and the current Quality override determine which pipeline runs. Installing UltraRP does not select it automatically or convert existing materials and renderer assets.

1. Create **Assets > Create > FraxinusEngine > UltraRP > UltraRP Asset**. This also creates and connects a Renderer Graph. The adjacent **Ultra Renderer** menu creates a standalone graph.
2. Open the Renderer Graph and configure its nodes. The basic Forward path connects the Camera Target through `RenderDrivenPass → DrawObjectsPass → PostProcessPass`; drawing nodes need the `UltraForward` / `UltraForwardOnly` tags. Add the corresponding transparent drawing node for transparent objects.
3. Connect `ColorGradingLutPass.colorLut` to `PostProcessPass.colorLut` for post-processing. For main-light shadows, connect the shadow output to its receivers and configure the shadow node's scheduling. Follow the [graph setup](../Packages/FraxinusRP/Documentation~/Installation.md) and [ScriptableNode guide](../Packages/FraxinusRP/Documentation~/ScriptableNode.md).
4. Assign the UltraRP asset in Graphics / Quality. Create Lit or Unlit graphs from **Assets > Create > Shader Graph > UltraRP**, or use the package's hand-written UltraRP shaders.
5. Configure effects using [Hybrid Lighting](../Packages/FraxinusRP/Documentation~/HybridLighting.md), [GTAO](../Packages/FraxinusRP/Documentation~/AmbientOcclusion.md), [Contact Shadows / Auto Exposure](../Packages/FraxinusRP/Documentation~/ContactShadowsAndExposure.md) and [upscaling](../Packages/FraxinusRP/Documentation~/Upscaling.md).

The matching Shader Graph fork is required for UltraRP's internal target APIs. Its package version remains `14.0.12`; choosing the registry package with that version will not supply the same assembly access. FraxinusRP currently has no importable sample scene or sample generator.

## Optional GPU Batcher

[Fraxinus GPU Batcher](../Packages/Fraxinus-GPU-Batcher/README.md) shares its instance registration, storage, transform upload, camera / shadow culling, LOD and indirect-argument generation across Unity 2022.3 and Unity 6. **Stock Unity 6 installs the built-in native indirect writer; stock Windows x64 Unity 2022 can use the bundled Vulkan plugin.** Start the Editor or Player with `-force-vulkan`; add `-disableFraxinusVulkanBRG` for a disabled control run. An installed synchronous `BRGIndirectSubmission.submitHandler` takes precedence. `BRGIndirectSubmission.isAvailable` reports only that handler, while `GPUBatcherRenderer.canSubmitDraws` also includes the Vulkan backend. Check the latter before registration takes over source Renderers / Terrain. Without an available backend, registered objects have no indirect rendering output. Bake removes source child GameObjects; use a disposable scene copy when evaluating these workflows.

The Vulkan backend requires the matching Fraxinus DOTS shader includes from Core and FraxinusRP. Unity BRG selects the material and shader pass; private mesh index buffers associate each draw with its GPU arguments, while per-view raw buffers hold copied instance data and visibility. Budget memory for these mesh copies and view buffers. The visibility offset limit is 8,388,607 elements. The packaged binary targets Windows x64 Vulkan; separate custom-host NativeContract adapters for other graphics APIs or platforms are not included in this package distribution.

The BRG implementation replaces the previous GPUInstancer code, Profiles, public APIs and importable demos without automatic migration. Existing project data must be recreated for the new components and config; keeping the same UPM identifier and `0.1.0` version does not preserve those contracts.

Edit the active Player configuration under **Edit > Project Settings > Graphics > Fraxinus GPU Batcher**. Use **Window > Analysis > Rendering Debugger > GPU Batcher** for culling overrides and Scene view diagnostics; the **Tools > Fraxinus GPU Batcher > System Window** retains detailed resource and buffer inspection. Live counters are available when compiled with `DEBUG`. Configuration is discovered or created at `Assets/FraxinusGPUBatcherData/FraxinusGPUBatcherConfig.asset`; existing project configs are found first, shader references are repaired, and the selected config is added to Player Settings preloaded assets. GameObject and terrain registration use the new `GPUBatcher*` / `TerrainGPUBatcherRegisterer` components.

Install the matching Core, FraxinusRP and Shader Graph revision together. Core provides native ObjectDispatcher tracking through `NativeObjectChangeTracker`; the RP's `InjectPass.ShouldExecute` hook lets HZB history select the final supported opaque pass. Shader Graph supplies SpeedTree 8 DOTS wind mappings and preserves the internal wind-capture passes in Player builds. The BRG package owns its history HZB and current-frame retest paths: history is built after opaque rendering without a full prepass, while retest requests complete prepass depth. This replaces the old GPU Batcher's use of `HiZDepthHistory`.

Both versions use native `CullingGroup` results from the current camera cull for baked occlusion. Spheres enclosing chunk bounds provide conservative visibility and may retain more candidates than the reference AABB query; camera occlusion does not filter shadow candidates. SpeedTree wind uses retained hidden native `Tree` proxies, two private capture passes and GPU copies of current/history parameters, without per-frame CPU wind readback. These paths use available native APIs even though Unity 2022 lacks the reference wind-manager and Umbra AABB-query bindings. See the [port notes](../Packages/Fraxinus-GPU-Batcher/Documentation~/Port.md) and [equivalence audit](../Packages/Fraxinus-GPU-Batcher/Documentation~/EquivalenceAudit.md) for supported algorithms, validation scope and remaining engine boundaries.

## Common setup issues

| Symptom | What to check |
| --- | --- |
| Missing `ScopeData` or typed RenderGraph APIs | Check that UltraRP and Core come from the same repository revision, and that old embedded copies are not taking precedence. |
| UltraRP Shader Graph access / target compilation errors | Confirm `com.unity.shadergraph` resolves to `Fraxinus-ShaderGraph`, not the registry package. |
| GPU Batcher cannot find native tracking or injection APIs | Update Core, FraxinusRP, Shader Graph and GPU Batcher together from the same revision. |
| Registered GPU Batcher objects disappear | Check `GPUBatcherRenderer.canSubmitDraws` on the active graphics device. For stock Windows x64 Unity 2022, use `-force-vulkan`, enable the packaged plugin and install the matching Fraxinus DOTS shaders. Registration still takes over the original Renderer / Terrain. |
| Missing package / invalid Git path | Check access to GitHub, that `?path=/Packages/...` matches the exact directory, and that all references use an available revision. |
| Pipeline looks unchanged | Check Graphics and the current Quality pipeline asset; installing packages alone does not select a pipeline. |
| Pink materials or an empty UltraRP view | Check shader compatibility, the Renderer Graph connections and drawing tags. URP Renderer Features are not UltraRP graph nodes. |
| Package guide points to a different repository / checkpoint | Use this page's coordinated dependency configuration. |

See [UltraRP validation](../Packages/FraxinusRP/Documentation~/Validation.md) for tested environments and feature scope. Validate the consuming project's scenes, target platform and Player build during integration.
