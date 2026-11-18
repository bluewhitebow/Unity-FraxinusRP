# Using Unity-FraxinusRP

[English](Usage.md) | [简体中文](Usage.zh-CN.md) · [Repository](../README.md)

Use **Unity 2022.3**, with **2022.3.62f3** matching the source project. This repository is a collection of UPM packages. A consuming Unity project supplies its own `Assets`, `ProjectSettings` and `Packages/manifest.json`.

The repository contains the four companion packages for **ScriptableNode / UltraRP**: Core, Light Transport, Shader Graph and FraxinusRP, plus the optional **Fraxinus GPU Batcher**. The examples below install all five; omit `com.bluewhitebow.fraxinus-gpu-batcher` if GPU instancing is not needed. UltraRP does not require URP.

Use the four companion packages and, when installed, GPU Batcher from the same revision.

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

[Fraxinus GPU Batcher](../Packages/Fraxinus-GPU-Batcher/README.md) adds GPU instancing for prefabs, terrain, details and trees. It depends on FraxinusRP `0.1.0` and the matching Core / Shader Graph forks. Use **Tools > Fraxinus GPU Batcher > Check Fraxinus Integration** to check the active pipeline. Runtime managers are created from the Profiles in its project settings; cameras do not need a separate handler. Generated project data is stored in `Assets/FraxinusGPUBatcherData/`.

For occlusion culling, keep `HiZDepthPass` enabled in the Renderer Graph. UltraRP owns the depth prepass, pyramid and per-camera history; GPU Batcher only requests and borrows that history. Runtime rendering covers base Game cameras. Hi-Z requires 2D, non-stereo targets without dynamic scaling. The first frame, expired history, or a change to the camera target, size, pipeline or render scale skips occlusion until valid depth is available. Disabling the pass also skips occlusion; frustum and distance culling continue. The algorithm uses previous-frame depth, so evaluate rapid camera movement in the consuming scene.

Import **Fraxinus GPU Batcher Demos** through Package Manager for the optional examples. Their presence does not change FraxinusRP's sample availability.

## Common setup issues

| Symptom | What to check |
| --- | --- |
| Missing `ScopeData` or typed RenderGraph APIs | Check that UltraRP and Core come from the same repository revision, and that old embedded copies are not taking precedence. |
| UltraRP Shader Graph access / target compilation errors | Confirm `com.unity.shadergraph` resolves to `Fraxinus-ShaderGraph`, not the registry package. |
| GPU Batcher cannot find `HiZDepthHistory` | Update FraxinusRP and GPU Batcher together from the same revision; their unchanged version numbers do not guarantee matching APIs. |
| Missing package / invalid Git path | Check access to GitHub, that `?path=/Packages/...` matches the exact directory, and that all references use an available revision. |
| Pipeline looks unchanged | Check Graphics and the current Quality pipeline asset; installing packages alone does not select a pipeline. |
| Pink materials or an empty UltraRP view | Check shader compatibility, the Renderer Graph connections and drawing tags. URP Renderer Features are not UltraRP graph nodes. |
| Package guide points to a different repository / checkpoint | Use this page's coordinated dependency configuration. |

See [UltraRP validation](../Packages/FraxinusRP/Documentation~/Validation.md) for tested environments and feature scope. Validate the consuming project's scenes, target platform and Player build during integration.
