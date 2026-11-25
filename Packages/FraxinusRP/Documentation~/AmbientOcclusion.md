# Ground Truth Ambient Occlusion

GTAO estimates ambient visibility from the current camera's opaque depth and
normals. It runs before lighting and is controlled by the Volume stack,
independently of the camera's post-processing switch.

## Setup

Add **Lighting / Ground Truth Ambient Occlusion** to a Volume Profile, leave the
component's header checkbox on, enable the override on **Enable**, and turn it on.
A global Volume or the pipeline's default
Volume Profile can supply the settings. No renderer feature or extra graph node
is required in graphs using the normal RenderDriven entry.

The effect is disabled by default. Recommended starting settings are half
resolution, intensity 1, radius 1 world unit, 6 steps and 2 directions.
**Direct Lighting Strength** defaults to 0: ambient visibility affects indirect
lighting. Increase it deliberately to also attenuate directional, point, spot
and supported rectangle lighting. AO only sees geometry represented in the
camera depth buffer; it does not replace light shadows.

In scenes with little indirect light, increasing Intensity alone can leave the
final image almost unchanged. To also show contact occlusion in directly lit
regions, override **Direct Lighting Strength** and start at 0.25. This controls
which lighting is attenuated; Intensity only shapes the visibility texture.

The maximum pixel radius is measured in trace pixels at half-resolution 1080p.
It scales with camera and trace resolution, so changing half/full tracing keeps
the same screen-space search extent. Larger radii and sample
counts increase cost. Blur Sharpness controls rejection of samples on other depth
surfaces. Intensity is applied once, after spatial reconstruction.

## Rendering contract

The renderer prepares a camera-local snapshot before allocating camera targets.
When active, it requests a complete DepthNormals prepass and a readable depth
copy. The InjectPass after RenderDriven records two conservative depth
reductions, GTAO horizon search, and spatial denoise or bilateral upsampling.
The final texture is full resolution with visibility 1 for unoccluded pixels.

The packed trace intermediate stores visibility and linear eye depth in RG32F.
The output uses R8 UNorm when both UAV writes and sampling are supported, with
R16F as a fallback. All compute dispatches bound-check odd dimensions. The initial
implementation uses the graphics queue and does not allocate temporal history.

| Receiver | Integration |
| --- | --- |
| Lit, Simple Lit and Complex Lit Forward | Shared AO interface, including per-pixel and vertex additional lights |
| Hybrid Lit | Shared visibility feeds its diffuse multibounce and specular-occlusion approximation; ForwardOnly remains its Deferred route |
| Ordinary Lit / Simple Lit Deferred | Apply the minimum of material AO and screen AO to GI before adding emission |
| Stencil Deferred | Direct AO in lighting; a GBuffer flag prevents repeating the indirect correction on upgraded receivers |
| Clustered Deferred | Bind AO explicitly to each compute kernel; attenuate direct lighting, including rectangles, without multiplying existing GI/emission |
| APRTV probe lighting on upgraded receivers | Occlude the probe response before it enters the emission channel; keep authored material emission separate |

APRTV uses the emission channel to add probe lighting to ordinary lighting. That
transport convention does not make its probe response self-emission. With screen
AO enabled, the response uses the same minimum of material and screen AO as GI,
even when Direct Lighting Strength is zero. With screen AO disabled, it retains
the previous APRTV/material-occlusion behavior. DiffuseTransfer probe lighting
already enters the regular GI path.

Lit and Simple Lit write material flag 64 when their GI already includes screen
AO. Their original material occlusion remains in GBuffer1. Custom GBuffer writers
must implement the same contract before setting that flag. Terrain, SpeedTree
and particle GBuffer writers are not upgraded by this feature; their existing
Stencil behavior remains and full Clustered indirect-AO support is not claimed.
The legacy Stencil correction scales the combined GI/emission buffer, so emission
isolation is only guaranteed for the upgraded receivers. Custom opaque shaders
must supply a compatible DepthNormals pass to participate in the AO prepass.

Every consuming pass declares its RenderGraph texture read. Camera preparation
first publishes a white texture and disables AO, including for unsupported or
inactive cameras. Child contexts use neutral AO and restore the parent's
published texture, parameters and keyword when returning. Build prefiltering
retains AO on/off variants because Volumes can change at runtime.

## Scope and validation

The initial scope is main-context Game and Scene View Base cameras, including
perspective and orthographic projection. Preview/reflection cameras, Overlay
cameras, XR, hardware dynamic resolution and independent child-context AO are
excluded and receive neutral visibility. Transparent objects do not receive
screen AO through the shared lighting interface.

Full depth/normal preparation adds opaque geometry work, including in Deferred.
Temporal accumulation, bent normals, asynchronous compute and a raster fallback
are not implemented. The existing Unity 2022 color/LUT issue with Native
RenderGraph, post-processing and Gizmos enabled is separate from AO; see
[HybridLighting.md](HybridLighting.md).

Algorithm attribution and license notices are in the
[AO shader README](../Shaders/Lighting/AmbientOcclusion/README.md).

## APRTV and reconstruction follow-up (2026-10-29)

The actual Sponza scene uses APRTV lighting model 1 with 4,563 probes. Before the
consumer correction, enabling AO at direct strength zero produced an AO texture
but no image change. After correction, eight captures verify AO off/on and direct
strength 0/0.25/1 in Game and Scene View. At direct strength zero the mean absolute
RGB differences are 0.00470 and 0.00403 respectively, with authored light and AO
intensity settings held constant within each comparison.

Independent GPU regression reproduces eight sloped-surface reconstruction failures
and three half/full radius mismatches on the previous implementation. All 18
reconstruction cases and all three radius comparisons pass after correction,
including depth steps, thin foreground geometry, backgrounds and odd dimensions.
The existing 99-capture Editor matrix, a fresh five-capture camera/MSAA run, and
52 normal-variant checks also pass. One combined Full-to-Targeted test run read
back color instead of AO for native MSAA, while its final lighting matched; that
capture anomaly is retained in the evidence and the fresh targeted run passes.

These follow-up changes were checked in Editor, without a new Player build.
Evidence is in `D:/Unity/FraxinusRPEvidence/2026-10-29/LiveAO` and the independent
project's `Reports/GTAO-Followup-2026-10-29` directory.

## Initial integration GPU regression (2026-10-29)

Unity 2022.3.62f3 on Windows / D3D11 / GeForce GTX 1650 completed 99 Editor
captures with zero failed assertions or logged errors. The independent test
project reads visibility through an explicit RenderGraph texture dependency.

- Lit, Simple Lit and Hybrid Lit: Forward, Stencil and Clustered graphs, indirect
  lighting, supported punctual/rectangle direct lighting, and nonzero emission.
- Lit material occlusion follows the minimum of material and screen AO on all
  three paths; disabling AO restores the original image.
- Full/half tracing, perspective/orthographic projection, odd 319 x 241 targets,
  and Native RenderGraph on/off.
- Volume exclusions, zero intensity, disabled components/Volumes and Preview
  cameras reset to neutral visibility; returning to an active camera restores AO.
- Scene View with post-processing disabled, plus child-context neutrality and
  parent restoration with both graph compilers.

Five additional captures verify two distinct cameras rendered alternately and
actual 4x MSAA render targets with Native RenderGraph on/off. The MSAA check
asserts the camera target descriptor's sample count, rather than only setting
the pipeline option. The AO-inactive camera stays white and the active camera
recovers the same image after switching back.

The 52 targeted DepthNormals checks also pass, covering signed/oct encoding,
material normal maps and instanced terrain normal variants. They use synchronous
shader compilation, synthetic draw submission and shader diagnostics; they do
not establish visual correctness of complete terrain or vegetation scenes.

A Windows Mono development Player built with unused-variant stripping enabled
completed 85 captures with zero failed assertions or runtime errors. Forward,
Stencil and Clustered pipeline assets are included, and AO is toggled at runtime.
The build uses ordinary built-in graphs (the Forward asset is confirmed as a
known Forward graph by prefiltering); the capture node is inserted only after
Player startup, so a custom test node cannot force conservative variant retention.
The fixture declares its occlusion-map material variant before building. Material
AO minimum composition and emission isolation pass on all three paths.

In the 320 x 240 reference scene, the contact-region visibility was 0.853 while
the open-floor visibility remained 1.0. Its GI luminance changed from 0.1162 to
0.0992, and the emissive receiver stayed at 0.2333. Native and non-native smoke
captures matched, and switching AO off restored the baseline exactly.

Raw reports and comparison images are retained outside the package at
`D:/Unity/FraxinusRPEvidence/2026-10-28/ClusterAreaValidation/Reports/GTAO-2026-10-29`.
These checks do not establish performance, IL2CPP, other graphics APIs or legacy
GBuffer receiver compatibility.

The build retains existing DeferredLit warnings about signed/unsigned comparisons,
shadow division and contact-shadow initialization, repeated across lighting
variants. The validation is not a warning-free build claim; no GTAO compute
shader warnings were reported in the final compilation.
