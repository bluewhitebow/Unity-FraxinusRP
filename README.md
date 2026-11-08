# Unity-FraxinusRP

[English](README.md) | [简体中文](README.zh-CN.md)

A **Scriptable Render Pipeline** (SRP) designed for visual rendering workflows and interactive debugging in Unity.

Built around **UltraRP**, Unity-FraxinusRP lets you connect shadows, geometry, lighting and post-processing in an editable **Renderer Graph**. Inspect intermediate textures directly inside nodes, trace how each stage shapes the final image, and adapt the rendering workflow to your project.

UltraRP combines this workflow with Hybrid Lit, realtime rectangle lights, APV / APRTV probe lighting, STP / SGSR upscaling and streaming virtual textures. Ported from the Fraxinus Engine Unity 6 branch, it targets **Unity 2022.3.62f3** and runs without URP.

[Installation and usage](docs/Usage.md) · [Changes from 1.x](docs/ChangesFrom1.x.md) · [Local dependencies](docs/manifest.local.json) · [Git dependencies](docs/manifest.git.json)

[![Sponza rendered in UltraRP, with intermediate texture previews in the Deferred Renderer Graph](docs/images/ultrarp-renderer-graph-sponza.png)](docs/images/ultrarp-renderer-graph-sponza.png)

*Sponza and its Deferred Renderer Graph: the final image above, rendering nodes, resource connections and intermediate previews below. Click to view the full image.*

## Trace the image through the rendering workflow

In the graph, shadow maps and the color-grading LUT connect to the nodes that consume them. GBuffer generation, deferred lighting, object draws, sky rendering and post-processing form the image together. Texture previews make the results of those connections visible: when color or shadows look wrong, follow the stages to find where the change begins, choosing a preview camera, texture and RGBA channels.

The same graph controls the rendering workflow. To draw a group of ForwardOnly materials after deferred lighting, connect a DrawObjects node, configure its shader tags, render queue and layer mask, then pass color and depth targets to the following stages. Connections, draw settings and stage outputs can be inspected together. Custom nodes and pass injection provide extension points for project-specific processing.

Renderer Graph supports Forward, Forward+, Stencil Deferred and Clustered Deferred. **Nested contexts** create separate color, depth and frame data for intermediate results at different resolutions, then explicitly composite them into the parent target. They share the same camera view. Fraxinus Core's underlying RenderGraph manages pass and resource dependencies, while the graph editor organizes the rendering stages. [Renderer Graph and contexts](Packages/FraxinusRP/Documentation~/ScriptableNode.md)

## How UltraRP compares with URP and HDRP

**URP emphasizes general-purpose rendering and Renderer Feature extensions. HDRP provides a comprehensive feature set for high-fidelity rendering. UltraRP makes editing the whole rendering workflow a central part of development.** The comparison below uses the same engine generation: **Unity 2022.3 / URP 14 / HDRP 14**, with their built-in capabilities.

| Area | Official URP 14 | Official HDRP 14 | UltraRP |
| --- | --- | --- | --- |
| **Editing the rendering workflow** | Renderer implements the main flow; extend it with Renderer Features / injected passes | Pipeline implements the main flow; inject draws or fullscreen processing through Custom Passes | Connect rendering stages and resources in Renderer Graph; extend nodes and nest contexts |
| **Inspecting intermediate results** | Separate tools such as Frame Debugger and Rendering Debugger | Separate tools such as Frame Debugger and Rendering Debugger | Stage textures and RGBA channels previewed inside nodes, with access to Render Graph Viewer / Rendering Debugger |
| **Realtime area lights** | Rectangle / Disc are baked only | Realtime Rectangle / Tube; Rectangle supports shadows | LTC rectangle lights, up to 16 per camera, currently without area-light shadows |
| **Adaptive probes** | Traditional Light Probe Groups; no APV | Experimental APV with automatic placement, streaming and lighting-scenario blending | APV with a standalone Compute baker, including placement, sky occlusion and diffuse bounces; no DXR required |
| **Indirect lighting after light changes** | Precomputed Enlighten realtime GI | SSGI / RTGI, or switching and blending prebaked lighting scenarios | APRTV responds to directional-light and HDR sky changes using precomputed transport |
| **Anti-aliasing and upscaling** | TAA; FSR 1 spatial upscaling | TAA Upscale, DLSS, FSR 1 and other filters | TAA, FSR 1, plus STP, SGSR1 spatial upscaling and SGSR2 temporal upscaling |
| **Streaming virtual textures** | No official SVT rendering integration | Native SVT + Shader Graph; transparent graphs fall back to ordinary sampling | Native SVT + UltraRP Lit / Unlit Shader Graph, including transparent-material feedback on supported paths |

These differences suit different development needs. Renderer Features fit projects that use the official URP ecosystem and add effects within its existing flow. HDRP provides more complete ready-made paths for features such as shadowed area lights and SSGI / RTGI. UltraRP is useful when a project needs to reorganize drawing stages, combine forward and deferred materials, or inspect a stage's inputs and outputs directly in Renderer Graph, alongside APRTV, STP / SGSR and other companion features on Unity 2022.3.

Official references: [Unity 2022.3 feature comparison](https://docs.unity3d.com/2022.3/Documentation/Manual/render-pipelines-feature-comparison.html), [URP pass extensions](https://docs.unity3d.com/Packages/com.unity.render-pipelines.universal@14.0/manual/renderer-features/intro-to-scriptable-render-passes.html), [HDRP Custom Pass](https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@14.0/manual/Custom-Pass.html), [HDRP upscaling](https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@14.0/manual/Dynamic-Resolution.html) and [SVT material support](https://docs.unity3d.com/2022.3/Documentation/Manual/svt-use-in-shader-graph.html).

## From material lighting to scene illumination

**Hybrid Lit** retains the Lit material workflow, using rough diffuse, GGX, micro-shadowing and FGD environment reflections, with Clear Coat support. LTC rectangle lights incorporate the source's dimensions into diffuse and specular lighting. GTAO and main-light Contact Shadows add occlusion and contact detail, while histogram-based Auto Exposure handles brightness adaptation. These features contribute to material and scene lighting on the selected rendering paths. [Hybrid lighting](Packages/FraxinusRP/Documentation~/HybridLighting.md) · [GTAO](Packages/FraxinusRP/Documentation~/AmbientOcclusion.md) · [Contact shadows and exposure](Packages/FraxinusRP/Documentation~/ContactShadowsAndExposure.md)

Scene indirect lighting can use baked **APV** probes or **APRTV** precomputed transport. With geometry and materials held constant, APRTV updates probe lighting as the directional light and HDR sky change, allowing different sun directions, light colors and sky environments. It relies on precomputed static-scene data: geometry or material changes require rebaking, and dynamic occlusion is outside its current scope. Light Transport supplies the Compute ray queries used for APV baking. [APV baking](Packages/Fraxinus-RPCore/Documentation~/StandaloneAPVBaking.md) · [APRTV](Packages/Fraxinus-RPCore/Documentation~/APRT.md)

## Material authoring and image reconstruction

Materials can use handwritten shaders or **UltraRP Lit / Unlit Shader Graph targets**, which also generate auxiliary passes for shadows, depth, normals, Meta and motion vectors. Materials that need texture streaming can use Texture Stacks and native **SVT**, with visible-tile feedback driving loading and configurable CPU / GPU cache budgets. [Shader Graph](Packages/FraxinusRP/Documentation~/ShaderGraph.md) · [SVT](Packages/FraxinusRP/Documentation~/VirtualTexturing.md)

Rendering resolution can be chosen independently of output resolution. **SGSR1** reconstructs a single frame, while **SGSR2** uses motion vectors and camera history for temporal reconstruction; **STP** is another available option. The pipeline integrates these paths with Render Scale, post-processing and history resets, so different reconstruction methods can be evaluated in the same scene. [Upscaling configuration](Packages/FraxinusRP/Documentation~/Upscaling.md)

## The four packages

| Package | UPM identifier | Version | Role |
| --- | --- | --- | --- |
| [FraxinusRP](Packages/FraxinusRP/README.md) | `com.bluewhitebow.fraxinus-rp` | `0.1.0` | ScriptableNode, UltraRP, render nodes and material / lighting integration. |
| [Fraxinus-RPCore](Packages/Fraxinus-RPCore/README.md) | `com.unity.render-pipelines.core` | `14.0.99` | RenderGraph, shared rendering tools, probe baking and data management. |
| [Fraxinus-ShaderGraph](Packages/Fraxinus-ShaderGraph/FraxinusChanges.md) | `com.unity.shadergraph` | `14.0.12` | Shader Graph fork exposing the internal APIs needed by UltraRP targets. |
| [Fraxinus-Light-Transport](Packages/Fraxinus-Light-Transport/README.md) | `com.fraxinus.rendering.light-transport` | `1.0.1` | Compute ray queries, acceleration structures and sampling. |

Each UPM package retains its own version. Use all four packages from one checkout or Git revision. Matching version numbers do not make official packages interchangeable with these forks.

## Getting started

1. Put this checkout beside your Unity project and merge the `dependencies` from [manifest.local.json](docs/manifest.local.json) into the project's `Packages/manifest.json`, preserving other dependencies. The [usage guide](docs/Usage.md) also covers Git installation.
2. Open the consuming project with **Unity 2022.3.62f3** and let Package Manager resolve dependencies.
3. Create a pipeline asset and its associated Renderer Graph through **Assets > Create > FraxinusEngine > UltraRP > UltraRP Asset**. Connect drawing, shadow and post-processing nodes using the [graph configuration guide](Packages/FraxinusRP/Documentation~/Installation.md).
4. Assign the UltraRP asset in **Project Settings > Graphics** and check the active **Quality** override. Use the included UltraRP shaders or create Lit / Unlit graphs through **Assets > Create > Shader Graph > UltraRP**.

This repository contains packages and documentation. Open a consuming Unity project in the Editor. FraxinusRP currently does not ship a sample scene or sample generator.

## Support and validation

- **Dependencies:** Core and Shader Graph retain Unity package IDs and replace the corresponding official packages. UltraRP needs the companion forks; official Shader Graph `14.0.12` does not provide the required internal API access.
- **Lighting paths:** Hybrid Lit and Shader Graph Lit / Unlit render through Forward / ForwardOnly. Rectangle lights support the documented Forward paths and ordinary Lit Clustered Deferred; Stencil Deferred and Shader Graph area-light receivers are outside that scope.
- **Graph and material extensions:** `JobGraphProcessor` currently uses a synchronous fallback. Advanced Shader Graph targets and VFX integration still have isolated components. Project-defined nodes need reflection-preservation rules from the [IL2CPP guide](Packages/FraxinusRP/Documentation~/IL2CPP.md).
- **Platforms and textures:** Unity labels SVT experimental. XR, hardware dynamic resolution and child contexts are among the paths that do not collect feedback. Other features have their own camera and platform limits, documented individually.

Tested environments and feature coverage are documented in [UltraRP validation](Packages/FraxinusRP/Documentation~/Validation.md), [Core APV tests](Packages/Fraxinus-RPCore/Tests~/Unity2022APV/README.md), and [Light Transport smoke tests](Packages/Fraxinus-Light-Transport/Tests~/GpuSmoke/README.md).

## Source and licensing

The layout follows [FraxinusGraphics](https://github.com/bluewhitebow/FraxinusGraphics), with source drawn from the Fraxinus Unity 2022.3 development project. Package source revisions are listed in [PackageSources.json](docs/PackageSources.json).

Licenses and third-party notices remain with their respective packages; no new common license is assigned. See the [Core license](Packages/Fraxinus-RPCore/LICENSE.md), [Shader Graph license](Packages/Fraxinus-ShaderGraph/LICENSE.md), [Light Transport license](Packages/Fraxinus-Light-Transport/LICENSE.md) and [Hybrid lighting attribution](Packages/FraxinusRP/ShaderLibrary/Lighting/README.md). Parts of Hybrid lighting are adapted from IllusionRP, including algorithms originating in Unity HDRP.
