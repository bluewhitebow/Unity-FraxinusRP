# SGSR1 and SGSR2

Select **Snapdragon Game Super Resolution 1.0** or **2.0** in the UltraRP asset's
**Quality > Upscaling Filter**, then set **Render Scale** (for example, 0.67).
Enable post-processing on the camera and keep the graph's main PostProcess node
active. Both filters also run at 1.0 render scale on game cameras. Scales above
1.0 use the existing downsampling path.

SGSR1 reconstructs a single frame using local edges. SGSR2 adds camera jitter,
object motion vectors and a separate output-resolution history. SGSR2 supplies
its own anti-aliasing, so it does not run FXAA, SMAA or a second TAA resolve.
The graph's optional TAA entry does not control SGSR2.

The shaders and CPU integration originate from Fraxinus-URPExtensions. SGSR runs
after the main post effects and before the final output conversion. Film Grain
is applied at output resolution after upscaling, outside SGSR2's history. Alpha
is retained when the pipeline's post-process alpha output option is enabled.
FSR's sharpness controls do not apply to SGSR.

## Requirements and fallback

Both filters require shader model 4.5, a supported shader in the renderer graph's
PostProcessData, a 2D camera target, and no XR or dynamic resolution. SGSR2 also
requires a floating-point motion/depth target, post-processing, motion vectors,
MSAA disabled, and a single Base camera without overlays. Subcontexts do not
share the main camera's temporal history. Unsupported configurations use linear
scaling; SGSR2 eligibility is resolved before projection jitter is applied.

SGSR2 history is reset when input or output dimensions change, the filter is
disabled or switched, a frame is skipped, or `UltraAdditionalCameraData.ResetTemporalHistory()`
is called. Request a reset when teleporting the camera or cutting between
shots. Repeated rendering of the same camera in one frame resets that resolve
and the next frame to avoid mixing history with unmatched motion transforms.

Default PostProcessData includes both shaders. Custom PostProcessData must also
populate `sgsr1PS` and `sgsr2PS`; resources are referenced by the graph for Player
builds. Qualcomm's shader license is retained in
[SGSR/LICENSE.md](../Shaders/PostProcessing/SGSR/LICENSE.md).

SGSR2 follows the reference package's placement after post-processing. Spatial
warps such as Lens Distortion alter color without warping the original motion
and depth buffers; their temporal image quality needs separate evaluation.

## Validation

Unity 2022.3.62f3, Windows, NVIDIA GTX 1650, Direct3D 11. Local evidence is under
`Logs/FraxinusRP/SGSR-2026-10-23` in the host project; temporary test scripts are
not part of the package.

`Graphics-04` passes 43 Play Mode frames covering both upscale passes, object
motion, input/output resizing, 1.0 render scale, explicit reset, filter switching,
separate camera histories, alpha, and Film Grain. Actual 4x MSAA, missing shader
and disabled RenderDriven configurations fall back without jitter and start fresh
history when restored. CPU parameter checks cover repeated renders in one frame
and the following frame. FSR, TAA and STP RGB smoke checks also pass; STP alpha
preservation is outside this acceptance and its existing output alpha is zero.

The accepted Editor report has SHA-256
`509547b5893ba5a1e3b317617e74bb6332d9f033cbd97a24c5c77a6a2f5bc2f5`.

`Player-02` also builds and runs successfully as a Windows Mono Player using
Direct3D 11. Explicit camera render requests on separate Player frames exercise
Linear, SGSR1, SGSR2 and SGSR2 after object movement and a public history reset.
The compiled SGSR passes execute, produce finite nonempty pixels that differ
from Linear, and preserve alpha 0.25. The build has no errors; its one warning
comes from the existing `Hidden/Universal/DebugViewTiles` shader. The first
hidden-window Player fixture produced no automatic camera frames; `Player-02`
uses explicit requests and validates RenderTexture output, not window presentation.

The accepted Player report has SHA-256
`ee41c18b68f1de318d63411c3647f12fc0f16fe8b5f2615c21b03a2839a07570`.

These results do not establish window presentation, HDR-display output, XR,
other graphics APIs or IL2CPP builds.
