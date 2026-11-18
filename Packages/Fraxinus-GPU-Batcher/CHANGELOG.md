# Changelog

## [Unreleased]

- Replaced the official Universal RP dependency with the current Fraxinus RP, Core, Shader Graph, Burst, Collections, and Mathematics versions.
- Integrated camera submission with the Fraxinus camera lifecycle.
- Moved Hi-Z production and per-camera resource ownership into Fraxinus RP's `HiZDepthPass` and `HiZDepthHistory`; GPU Batcher only requests and reads framework history.
- Removed the package's `GPUInstancerHiZPass`, camera occlusion-generator component, and private depth-pyramid compute utilities.
- Updated Lit shader passes, Shader Graph targets, include paths, and the default Lit binding for Fraxinus RP.
- Rebound the sample materials to Fraxinus Lit and removed obsolete URP material-upgrade markers.
- Preserved selected-camera filtering and invalidated Hi-Z history when the camera target, size, pipeline, or render scale changes.
- Made `GPUInstancerManager` and its Prefab/Tree/Detail implementations plain managed runtime objects with explicit `IDisposable` lifetimes.
- Moved all channel authoring to embedded Profiles and updated Driver, Hub, editor tooling, and samples to create runtime managers from those Profiles.
- Removed the Manager-asset workflow completely. This greenfield package does not load, migrate, or preserve old Manager assets or scene bindings; channels must be authored as Profiles.
- Removed internal uses of obsolete settings and manager-asset APIs; Runtime, Editor, and Samples compile with CS0612/CS0618/CS0619 treated as errors.

## [0.1.0] - 2026-09-07

- Initial Fraxinus GPU Batcher package layout.
- Split runtime and editor assemblies.
- Moved the original GPU Instancer demos to package samples.
- Embedded the Unity 2022.3 URP shader support directly in the package.
- Removed legacy UnityPackage auto-import and old-layout migration behavior.
- Redirected generated settings and assets to `Assets/FraxinusGPUBatcherData`.
- Added URP Renderer Feature driven per-camera rendering and removed the camera handler component.
- Promoted runtime scripts directly under `Runtime/` and moved resources and shaders to package-root folders.
