# BRG implementation port

Source: `D:/Unity/BRGInstance/Assets/BRGInstancedRenderer`, version 1.2.0.
The original copyright notices are retained in adapted source files.

| Reference | Fraxinus package |
| --- | --- |
| `BRGInstancedRenderer` namespace | `Fraxinus.GPUBatcher` |
| `BRGRenderer` and core partials | `GPUBatcherRenderer`, `Runtime/Core` |
| `BRGInstancedRendererConfig` | `GPUBatcherConfig`, `Runtime/Config` |
| `BRG*` registration components | `GPUBatcher*`, `Runtime/Registration` |
| `TerrainBRGRegisterer` | `TerrainGPUBatcherRegisterer` |
| `BRG_*` compute kernels/assets | `GPUBatcher_*`, `Runtime/Core/Compute` |
| URP render passes | Fraxinus `InjectPass` modules, `Runtime/Integration` |
| Unity 6 native indirect command | Package-owned `Rendering.BatchDrawCommandIndirect` |

The instance algorithms, CPU/GPU layouts and compute shader contracts are retained.
Config discovery/creation/resource repair/preloading, renderer/terrain takeover,
destructive Bake and Expand follow the reference. The default config location is
writable project data instead of the immutable package directory.

Draw templates and per-view command scratch data are renderer-owned until a native
backend exists. Editor picking uses the native direct-command path and Fraxinus picking
shader. The descriptor must be converted field by field: Unity 2022 and Unity 6 have
different native output fields and LOD flag values, so it must not be cast to native ABI.
`BRGIndirectSubmission.TrySubmit` stays empty on every Unity version by design.

ObjectDispatcher tracking uses the same GlobalTRS and type-change queries as the source.
Core's existing `Unity.RenderPipelines.Universal.2D.Internal` engine-friend assembly
provides a small owned-snapshot wrapper; there is no scene-wide polling or reflection.
The GPU package therefore requires this repository's Core fork. Fraxinus also provides
a small `InjectPass.ShouldExecute` hook so history HZB selects the final opaque node
supported by the current camera before consuming the once-per-context injection.
This suite retains `PrepareFrameResources(...)` for the resource-requirement hook;
the source port calls it `PrepareFrame(...)`. Both return `RenderPassInputSummary`.

History HZB samples opaque depth after the last main-context opaque producer, resolving
MSAA depth at that point if necessary. It does not request a full depth prepass.
Retest uses the complete prepass after `RenderDrivenPass`. CameraData view/projection
matrices match rendered depth, including overlay projection changes; scaled dimensions
follow the active descriptor. Each camera retains its retest data and restores its
uniforms after interleaved camera/shadow work. The reference's first-split retest policy
is retained. The source's depth convention and single-view HZB assumptions apply.

## Native engine gaps

Apart from indirect submission, Unity 2022's managed engine metadata lacks:

- `SpeedTreeWindManager.UpdateWindAndWriteBufferWindParams` and the wind parameter
  iterator: automatic native current/history wind extraction cannot be called.
  The narrow bridge reports unsupported and fills neutral wind parameters.
- `BatchCullingContext.occlusionBuffer` and `BatchRendererGroup.OcclusionTestAABB`:
  baked Umbra queries cannot be issued. The bridge reports unsupported and retains
  candidates; frustum/distance/GPU HZB culling still runs.

Neither gap can be removed by renaming methods or using the existing friend assembly.
They need additional native engine bindings. These are explicitly distinct from the
user-requested indirect submission placeholder.

## Package integration

Runtime/Editor assembly identities and GUIDs are preserved. Imported asset GUIDs and
shader-helper references are remapped consistently. Source demos, logos, help URLs,
and URP/HDRP package patchers are omitted; Fraxinus integration replaces those paths.
Shaders use Fraxinus tags/includes. The debug sphere retains Lit surface defaults and
alpha 0.5 through a Fraxinus shader; generated URP/HDRP auxiliary passes are omitted.
Disposal guards and explicit graph-context lifetimes prevent released storage reuse.

The automatically discovered BRG injectors have no reviewed shader-stripping
contracts in this suite, so installing the package conservatively retains variants
that those unknown modules might need. UltraRP can be used without this package.

The [equivalence audit](EquivalenceAudit.md) preserves the source snapshot's
comparison and validation records. `SourceAudit.json` is historical source evidence;
its hashes do not assert that this suite's renamed hooks or other adaptations match.
Reproducible helpers (`GPUBatcher*Checks.cs` and `AuditGPUBatcherPort.py`) are in the
[source repository's Tools/Rendering directory](https://github.com/zgsxof/Duckov-Recovered/tree/148ad81fd976e6a5f76624ce081c779c2bd61dd9/Tools/Rendering).
They are not distributed in this package repository. Checks cannot establish final
rendered output or performance while native indirect submission is empty.
