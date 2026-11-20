# Changelog

## [Unreleased]

- Ported BRG storage, upload, camera/shadow culling, LOD/crossfade, registration,
  configuration, editor tools and shaders into Fraxinus namespaces.
- Restored source config bootstrap/preloading/resource repair, native renderer/terrain
  takeover and destructive Bake/Expand behavior.
- Restored native ObjectDispatcher tracking through Core's engine-friend wrapper.
- Matched history HZB after opaque rendering and retest after complete prepasses;
  retained each camera's retest context across interleaved camera/shadow dispatches.
- Added our `BatchDrawCommandIndirect` and the requested empty native submission method.
  Isolated the additional missing Unity 2022 SpeedTree wind and Umbra query bindings.
- Preserved package identity and Runtime/Editor assembly names and GUIDs while replacing
  the former GPUInstancer implementation.

## [0.1.0]

- Initial package layout.
