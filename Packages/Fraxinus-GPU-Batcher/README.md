# Fraxinus GPU Batcher

BRG instance rendering algorithms ported to Unity 2022.3 and Fraxinus RP.
Runtime types use `Fraxinus.GPUBatcher`; editor tools use `Fraxinus.GPUBatcher.Editor`.

The package includes prototype registration, chunk/pool storage, incremental TRS
upload, matrix generation, colors/light probes, camera/shadow culling, LOD/crossfade,
visibility buffers and indirect argument generation. Config, camera/prototype settings,
GameObject registration, baked groups, terrain trees/details and editor tools retain
the reference workflow.

`Rendering.BatchDrawCommandIndirect` is our descriptor with Unity 6 field/flag
semantics. `Rendering.BRGIndirectSubmission.TrySubmit` intentionally returns `false`
without modifying native output. Indirect draw submission remains unimplemented.

Configuration is discovered or created automatically under
`Assets/FraxinusGPUBatcherData/FraxinusGPUBatcherConfig.asset`; existing project configs
are found first. Shader references are repaired automatically and the config is added
to Player Settings preloaded assets. Source renderers/terrain are taken over as in
the reference, and Bake removes the baked source children. With the submission stub,
registered objects therefore have no indirect rendering yet.

Fraxinus graph injection runs culling before depth/shadow rendering. History HZB is
built after opaque rendering without requesting a full prepass. Current-frame retest
requests a complete depth prepass and runs before opaques. Per-camera retest state
survives interleaved camera/shadow work. This suite uses the
`RenderPassInputSummary PrepareFrameResources(...)` hook; the source snapshot
names that hook `PrepareFrame(...)`. UltraRP does not require this optional package.

Object/transform tracking uses Unity's native ObjectDispatcher through this repository's
Core engine-friend assembly. The package requires that Core fork together with
Fraxinus RP. Unity 2022 has no binding for the source's native SpeedTree current/history
wind extraction or Umbra AABB query; those two bridge boundaries report unsupported.
Wind data is neutral and Umbra candidates stay visible until native support is added.

See [port notes](Documentation~/Port.md) and the
[equivalence audit](Documentation~/EquivalenceAudit.md) for mapping, source-validation
records and remaining engine/pipeline differences. The copied audit describes the
source snapshot; it is not a hash match or a validation rerun of this suite.
