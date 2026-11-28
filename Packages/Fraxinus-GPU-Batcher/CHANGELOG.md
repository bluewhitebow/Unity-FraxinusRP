# Changelog

## [Unreleased]

- Support installation into stock Unity 2022.3.62f3 projects through Package Manager using public Unity dependencies. Fraxinus RP supplies the optional GPU rendering integration.
- Added native BRG indirect submission for Tuanjie 1.8.0+ alongside Unity 6. Stock Unity 2022.3 uses its scene renderers.

## Earlier port revisions

- Unified field, property, event and constant naming with Fraxinus RP. Shared
  constants now use private values and read-only properties; serialized field
  aliases preserve existing configuration and prefab data.

- Consolidated Unity 2022 and Unity 6 packages into one maintained source, consumed by
  validation projects through local UPM `file:` references. Archived former copies.
- Added a single synchronous `BRGIndirectSubmission.submitHandler` for camera/shadow
  output; availability follows handler registration. `FRAXINUS_BRG_INDIRECT_API`
  installs the built-in writer on stock Unity 6 or matching engine backports. Custom
  APIs can register a handler at startup; no handler means false without output writes.
- Unified ObjectDispatcher access under the common `Unity.InternalAPIEngineBridge.024`
  friend assembly. Version macros select shadow, categorization and editor APIs;
  Core GPUDriven remains Unity 6-only through its assembly-definition constraint.
- Verified shared package paths and backend selection. Shared-source
  regressions passed in both editors: the 2022 six-suite aggregate and rebuilt
  Player's two GPU transform checks/30 wind frames; Unity 6 compilation, four
  algorithm suites, 28 native BRG checks and five actual graph image cases. These
  recorded runs used stock engines without a custom submission handler.
  Reports remain in the independent validation project's `Validation/Results/UnifiedPackages`.
- Ported BRG storage, upload, camera/shadow culling, LOD/crossfade, registration,
  configuration, editor tools and shaders into Fraxinus namespaces.
- Restored source config bootstrap/preloading/resource repair, native renderer/terrain
  takeover and destructive Bake/Expand behavior.
- Restored native ObjectDispatcher tracking through Core's engine-friend wrapper.
- Matched history HZB after opaque rendering and retest after complete prepasses;
  retained each camera's retest context across interleaved camera/shadow dispatches.
- Normalized HZB depth/UV conventions and added viewport-aware reduction and
  farthest-sample MSAA 2D/array resolution with explicit slice selection.
- Implemented native baked occlusion through per-camera `CullingGroup` enclosing
  spheres, current-cull snapshots and stale-bounds protection; shadow candidates
  remain independent of camera occlusion.
- Implemented SpeedTree wind capture through retained hidden `Tree` proxies, private
  native cull/draw rendering and GPU copies of current/history constants. Preserved
  original meshes and Tree/LOD hierarchy, and added SpeedTree 8 Shader Graph DOTS
  mappings. The rendering path requires no per-frame CPU wind readback.
- Passed 120 HZB checks with actual GPU execution/readback and six native baked
  occlusion compatibility checks, including Core filtering and lifecycle behavior.
- Before consolidation, passed 450 native wind capture assertions, including cold initialization,
  full parameters after native simulation advances, temporal history and cleanup.
- Before consolidation, passed the six-suite isolated runner, 114 shader-program checks, 61 compute
  import/support checks and two subgraphs. Built a Windows Player containing a real
  SpeedTree model; the executable passed two GPU transform readbacks and 30 frames
  of production-context wind capture with native history and WindZone response.
- Isolated capture offsets on per-proxy materials so native Tree property updates
  cannot redirect another prototype's output. Verified nonzero source slots,
  ownership, and unreadable 16/32-bit mesh index buffers.
- Preserved the two internal wind-capture passes through the Built-in ShaderGraph
  stripper for Players that install their render pipeline at runtime.
- Kept this update scoped to the existing single-view Fraxinus pipeline; stereo
  cameras retain the mono safety gate and full XR pipeline work is excluded.
- Added our `BatchDrawCommandIndirect`; intentional source-renderer takeover remains
  shared, including when no indirect submission handler is installed.
- Preserved package identity and Runtime/Editor assembly names and GUIDs while replacing
  the former GPUInstancer implementation.

## [0.1.0]

- Initial package layout.
