# 2026-10-29 package sample cleanup

The package no longer ships example scenes, sample-generation Editor menus or a
Package Manager sample entry. Production asset-creation menus, renderer graph
editing, inspectors and shader tooling remain available. Existing host-project
scenes are managed separately. Earlier records below describe their historical
snapshots; the removed content is available in pre-cleanup commit `02a80e8`.

# 2026-10-29 AO visibility and APRTV corrections

APRTV probe lighting previously bypassed AO because it enters the emission
channel. Upgraded lighting paths now occlude that probe response separately,
preserving authored material emission and the existing appearance when AO is off.
The actual APRTV Sponza scene now responds with Direct Lighting Strength at zero.

Half-resolution reconstruction now checks depth against the receiver's local
plane, preventing distant sloped surfaces from losing AO while retaining depth
edge rejection. Full-resolution tracing also keeps the same screen-space radius
limit as half-resolution tracing.

Targeted GPU checks cover 18 reconstruction cases and three radius comparisons;
Editor lighting and normal-variant regressions pass. The current scene was also
captured with AO off/on and direct strength 0/0.25/1. These corrections were not
rebuilt into a new Player. Details are in [AmbientOcclusion.md](AmbientOcclusion.md).

# 2026-10-29 Ground Truth Ambient Occlusion

Adds a default-off **Lighting / Ground Truth Ambient Occlusion** Volume override.
The renderer prepares complete opaque depth and normals, runs a compute horizon
search, then filters and reconstructs visibility before lighting. Half and full
resolution tracing support main-context perspective and orthographic cameras.
AO works independently of the camera post-processing switch.

Forward and Hybrid Lit consume the shared visibility interface. Lit and Simple
Lit Deferred apply the minimum of material and screen occlusion to GI before
emission; a GBuffer flag prevents Stencil from applying indirect AO twice.
Clustered Deferred binds AO to each lighting kernel, including rectangle lights.
Direct-light influence is configurable and defaults to zero.

Camera changes reset the texture and keyword, child contexts use neutral AO and
restore their parent, and build prefiltering keeps runtime AO toggles available.
Lit depth-normal passes now preserve material normals with either signed or oct
encoding. The full prepass adds geometry cost; this version has no temporal
history, bent normals or raster fallback. Setup, supported receivers and
validation boundaries are documented in [AmbientOcclusion.md](AmbientOcclusion.md).
Windows / D3D11 checks pass for 104 Editor captures, 52 targeted DepthNormals
variant checks, and 85 Player captures with unused-variant stripping enabled.

# 2026-10-28 Scene View and Clustered rectangle lighting

The Hybrid Lighting demo graph now connects its color-grading LUT to post
processing. This fixes the solid gray Scene View when its post-processing toggle
is enabled. Unity 2022 Gizmos and wire overlays also use the supported render-context
APIs instead of unavailable renderer-list factories.
The demo keeps Native Pass disabled: the existing Unity 2022 color/LUT artifact
with Native Pass, post-processing and Gizmos enabled is separately reproduced on
the previous Renderer and remains unresolved.

Ordinary Lit now evaluates rectangle lights in Clustered Deferred, using the
shared LTC function and explicit per-kernel LUT bindings. Specular Highlights Off
preserves the GBuffer reflectivity used to reconstruct diffuse lighting and
suppresses direct specular. Hybrid remains ForwardOnly; Stencil Deferred area
lighting and tile/cluster culling of rectangles remain outside this change.
The Editor GPU regression covers 35 captures; details are in
[HybridLighting.md](HybridLighting.md).

# 2026-10-28 Hybrid Lit and rectangle lights (phase 1)

Adds `Ultra Render Pipeline/Hybrid Lit`, using the existing Complex Lit pass
contract with enhanced BRDF, FGD environment lighting and AO. Ordinary Lit,
Complex Lit and Hybrid Lit Forward share rectangle LTC lighting, with up to 16
lights per camera and independent diffuse/specular contributions. Existing LTC
tables and FGD resources are reused with explicit RenderGraph texture reads.

Rectangle settings now support Realtime and preserve dimensions and authored
bake mode in the additional light data for Unity 2022 players. Native rectangle
slots remain neutral in punctual lighting so cookie and shadow indices stay
aligned. The historical validation used a separate generated scene; its generator
is no longer distributed in the package.

This phase does not add area cookies/shadows or ordinary Lit deferred area
lighting. Hybrid Lit can render through ForwardOnly nodes in deferred graphs.
Shader Graph is not included in the supported area-light receivers for this phase.
Configuration and validation scope: [HybridLighting.md](HybridLighting.md).

# 2026-10-27 renderer features and graph UI

This update adds main-light Contact Shadows and histogram Auto Exposure, improves
Renderer Graph editing, and consolidates InjectPass registration. The recovered
production changes are in `64b4c7575895d27e850b178548f0496820963bad`.

- **Contact Shadows:** default-off Volume control for opaque receivers in Forward,
  Stencil Deferred and Clustered Deferred. Complete depth is prepared before the
  injected pass; lighting consumers declare the texture dependency.
- **Auto Exposure:** 128-bin histogram, percentile clipping, EV limits and per-camera
  adaptation. Exposure runs before Bloom and tonemapping and supports history reset.
- **Shadows:** correct directional near-plane clamping, align bias with soft-shadow
  filter quality, and migrate legacy URP light overrides while retaining Ultra data.
- **Injection:** Contact Shadows and APRT use InjectPass. Module discovery is cached
  per scripting domain; each Renderer owns one ordered fallback queue. Completed
  work skips fallback traversal, while unsupported primary nodes can still fall back.
  Opaque depth copying now follows the last opaque node.
- **Renderer Graph:** dark blue-gray styling with teal accents, grouped UI Toolkit
  toolbar, node categories and execution indicators, grid, camera selector and zoom
  controls. Fit View avoids the toolbar/footer. Global Preview is available beside
  the preview resolution and stays synchronized with settings and Undo.
- **Allocation fixes:** texture identity checks avoid boxing, preview names are
  cached, contact injection lookup uses indexed access, and APRT shares Core's
  scene-light tracker.

Effect setup and limitations are in [Contact Shadows and Auto Exposure](ContactShadowsAndExposure.md).
Both effects are disabled by default. This implementation targets ordinary 2D
cameras; XR, child contexts and hardware dynamic resolution are excluded. Contact
Shadows adds depth-prepass work and only sees geometry present in camera depth.

Use the matching Core and optional URP commits listed below; the current installation
recipe is in [Installation.md](Installation.md). Recovery checks passed for Editor,
Player, Player with virtual texturing, the shared bridge and both Fraxinus Editor
assemblies. Unity completed script reload and resource import. An import worker
reported a MemoryStream error during the first reload; the main Editor stayed
responsive and the next refresh completed without a new occurrence. No new Player
execution or CPU/GPU performance comparison was performed during recovery.

## Render allocation details

Texture-handle comparisons now preserve resource identity without boxing.
Contact-shadow injection lookup uses indexed access, and preview texture names
are cached per node. APRT reuses Core's scene-light tracker instead of allocating
a scene-query array on each collection. Live light properties and native query
ordering are preserved; initialization and membership changes can still allocate.

Update these package commits together:

- Fraxinus-RPCore: `5a7a0c68efae9e2c527332c15349227077326679`.
- Fraxinus-URPExtensions: `162e7059c38ae0e47bcf6ce92e75889ea08c28fb`, when URP is installed.

The engine-friend bridge now belongs to Core, with its assembly name and GUIDs
preserved. An older URP copy contains the same bridge and must be updated to
avoid duplicate assemblies. FraxinusRP does not require the URP package.

The tested scene recorded zero managed allocations under the render-loop entry
for 30 consecutive Play Mode frames. This is an allocation result, not a complete
CPU/GPU performance comparison. See [Validation.md](Validation.md) for scope,
tracker tradeoffs and the profiling incident encountered during validation.

# 2026-10-23 main update

This update requires paired RPCore commit
`2bb68b2b4161530b11936568fce6dc19e75c8a31` for native submission and RenderGraph
exception handling. It also includes the APRT ownership fix referenced below.
The existing `unity2022-checkpoint-2026-10-23` tag remains the earlier SGSR
checkpoint. See [Installation.md](Installation.md) for the matching dependencies.

# Bloom allocation fix

Bloom's Uber parameters are copied as a value into each recorded pass, removing
the 80-byte object allocated per enabled camera. Disabled cameras still reset
the Bloom state, and the sampling pass retains its texture dependency.

# APRTV rendering integration

Selecting Adaptive PRT Volumes now initializes the Core APRT backend explicitly
and records camera relighting before material shading. Lit, Simple Lit, Baked Lit
and UltraRP Lit Shader Graph sample its runtime output in Forward, stencil
Deferred and Clustered Deferred. The serialized probe-system values are
unchanged. APV and APRTV shader variants are retained and stripped independently.

The integration uses Core's existing APRT baking-set, lighting-scenario and
sampling implementation. It declares RenderGraph buffer dependencies, supports
Base/Overlay inheritance and records probe debug drawing after opaque objects.
Preview, reflection and depth-only cameras disable APRTV sampling and debug
state. Missing probe data retains the native light-probe fallback.

APV's native probe-system override, sky occlusion and cell streaming remain
APV-specific, matching Fraxinus-URPExtensions. APRTV exposes scenario controls
without APV-only streaming and SH memory settings. The initial integration
allocated a scene-light query array for each relevant camera; the 2026-10-27
update above replaces that query with change tracking. See
[Validation.md](Validation.md) for evidence.

This integration requires RPCore commit
`533b8b3f2879467cf990c060a7b57ed7711d97bf`, which gives each APRT runtime its own
relight ComputeShader and releases it on cleanup. It prevents stale outputs
observed with the shared Resources instance on Unity 2022 / D3D11.

# RenderGraph exception handling

The paired RPCore removes repeated exception wrappers from typed pass callbacks,
native nodes and async submission. Recovery runs at graph execution boundaries;
immediate mode retains its own recording-time boundary. Async command buffers
remain owned until submission succeeds, and graph cleanup uses one final state
reset. Native-pass closure and GPU resource quarantine remain protected on
failure paths. This reduces explicit exception regions; no frame-time speedup
has been measured. See [Validation.md](Validation.md) for the accepted checks.

# Native render pass fixes

The paired RPCore uses one NativePassCompiler execution flow for Unity 2022
and Unity 6. The version-specific partial file is removed; conditional
compilation stays at engine API calls, required state and missing descriptor
types. Begin/Next/End, fences and cleanup use the same call sites.

Unity 2022 submission selects the backbuffer depth surface immediately before
BeginRenderPass. Shared resource handles retain their original bindings for
regular and unsafe passes, including Gizmos. This fixes
`BeginRenderPass: Cannot use color-only surface as depth attachment` without
breaking ordinary depth binding.

Depth copies derive sampling and output format from each call's source and
destination resources. The shared MSAA, resolve, output and backbuffer switches
and cleanup reset are removed. Pass data snapshots camera type and viewport
instead of retaining mutable camera data.

Source texture bindings are now recorded in command order. Previously, recording
two copies with different sources changed the shared material before submission,
so both draws could sample the last source. Color and depth output use materials
with fixed ZWrite states created once at initialization; recording copies no
longer changes material properties.

Final depth copies explicitly bind the output color/depth pair and apply the
camera viewport, preserving existing color and depth outside a partial viewport.
Y-flip follows the actual source and destination surfaces, including Game
cameras that output to RenderTextures. Final Gizmos use output depth, matching
the color dimensions after SGSR upscaling. Unity 6 keeps its existing native
command-buffer API; the 2022 attachment conversion is confined to the context
submission branch in ExecuteBeginRenderPass.

This fix requires the paired RPCore change; updating FraxinusRP alone does not
correct the native attachment binding.

Clustered deferred lighting keeps its texture-based GBuffer layout when native
render passes are enabled. Framebuffer fetch is only enabled for stencil deferred
lighting; intermediate targets and depth copies follow that choice so compute
lighting reads the expected depth and Light Layers textures.

The unified compiler passes 11 cases and 132 real Play Mode frames on Unity
2022.3.62f3 / D3D11, including shared-renderer cameras, Gizmos and SGSR2. Forward,
Clustered Light Layers and 4x MSAA color captures match exactly with native
rendering enabled or disabled. The preceding depth-copy validation passed 30
copies and GPU readback of 24,948 float values/color channels with zero error,
including consecutive sources, partial viewports and MSAA depth. Non-Editor
Release C# compilation passes for the unified code; Unity 6 has source review
only. Evidence and limits are recorded in [Validation.md](Validation.md).

# 2026-10-23 SGSR1 and SGSR2

Source tag: `unity2022-checkpoint-2026-10-23`, Unity 2022.3.62f3. Companion
revisions and package versions are unchanged. See [Installation.md](Installation.md).

Added SGSR1 spatial and SGSR2 temporal upscaling to UltraRP's Quality settings,
using the Fraxinus-URPExtensions implementation as the reference. SGSR2 uses
object motion vectors, projection jitter and separate camera histories, with
linear fallback selected before jitter when requirements are not met. Both
filters preserve alpha; Film Grain runs after upscaling and outside the history.

Unity 2022.3.62f3 / D3D11 validation passes 43 Editor Play Mode frames and a
Windows Mono Player RenderTexture smoke test. Temporary acceptance scripts are
not shipped. Configuration, reset behavior, evidence and tested limits are in
[Upscaling.md](Upscaling.md). No companion package changes are required.

# 2026-10-22 validation tooling cleanup

Source tag: `unity2022-checkpoint-2026-10-22`, Unity 2022.3.62f3. Core remains at
`038ad6278a1f92195b654724a78d92865a6017d5`; all companion revisions and package
versions are unchanged. See [Installation.md](Installation.md).

Removed the migration's 20 published Editor validation scripts and the local
Inspector regression added during this fix, together with their probe shader and
test-only prefilter checks. Build diagnostics, variant stripping and production
build recovery remain active. The Basic Forward sample now only selects its
pipeline and materials and restores settings on disable; screenshot, command-line,
automatic-exit and node-test code and unused URP comparison assets were removed.

That checkpoint retained the advanced sample generator and Package Manager sample.
Both have since been removed from the current package; their source remains in
`unity2022-checkpoint-2026-10-22`. Shader Graph uses the standard UltraRP Lit/Unlit
creation menus. Historical test results and source hashes remain in the documents.

The published source loses 8,816 C# lines overall while retaining the advanced
sample creator. Unity compilation, six generated scenes/pipelines and the Basic
Forward pixel check pass; the Inspector fix passes four before/after cases.
No Player build was repeated. Report hashes and limits are in
[Validation.md](Validation.md).

# 2026-10-22 inactive pipeline asset Inspector fix

The UltraRP asset Inspector no longer creates runtime renderers while drawing
its Rendering settings. An unfinished graphics-API check had no active checks
but still called GetRenderer, causing GPULights initialization to dereference
missing UltraRP runtime shaders before the pipeline was active.

The regression reproduces the original GPULights.cs:469 exception in four
Built-in/URP-selection configurations. The same IMGUI Layout/Repaint cases pass
after the fix, create no renderers and preserve settings. This is an Editor-only
fix; no runtime rendering code changed. See [Validation.md](Validation.md).

# 2026-10-21 unified context Get checkpoint (historical)

Source tag: `unity2022-checkpoint-2026-10-21-get`, Unity 2022.3.62f3. Use Core
commit `038ad6278a1f92195b654724a78d92865a6017d5`; package versions remain
FraxinusRP 0.1.0 and Core 14.0.99. See
[Installation.md at that tag](https://github.com/bluewhitebow/FraxinusRP/blob/unity2022-checkpoint-2026-10-21-get/Documentation~/Installation.md).

## Changes

- Render passes use `frameData.Get<T>()` throughout. Core's ContextContainer
  reads borrowed ScopeData overrides before owned frame data; Contains and
  GetOrCreate follow the same lookup. Create rejects visible duplicates, and
  disposal resets only owned items.
- ContextStateManager switches the frame binding at context boundaries and
  detaches it on Reset. Ordinary main bindings read owned data directly; custom
  main overrides and nested restoration remain supported. ContextData retains
  its public type and inherits Core's borrowed storage.
- Allocation validation uses a current-thread GC.Alloc recorder with an
  intentional-allocation control. The Editor's byte counter returned zero even
  for that control, so historical 0 B readings are not valid allocation evidence.

## Validation

- 45 container checks and seven nested GPU scenarios pass. SG graphics across
  Forward/Deferred/Clustered, 30 pure-depth requests and Editor Play Mode
  URP -> UltraRP -> URP pixels and shader state pass.
- Warmed million-read loops and 10,000 switch/reset/injection cycles record
  zero GC.Alloc events; positive controls record one event. Initialization,
  capacity growth and whole-frame allocations are outside that measurement.
- All 44 host/isolated ProjectSettings files and the temporary URP reference
  renderer are restored. Validation is Windows/D3D11 Editor; no Player rebuild,
  IL2CPP or XR validation was performed for this refactor.

See [UnifiedContextGetSummary.json](UnifiedContextGetSummary.json) for source
and report hashes, and [Validation.md](Validation.md) for run order and limits.

# 2026-10-21 rendering integration checkpoint (historical)

Source tag: `unity2022-checkpoint-2026-10-21`, Unity 2022.3.62f3. Package versions
remain FraxinusRP 0.1.0 and Core 14.0.99. Use Core commit
`a7eaff92af3e6c5c936cdb5062ac30e70c9539bf`; this historical pairing is recorded
in [Installation.md at that tag](https://github.com/bluewhitebow/FraxinusRP/blob/unity2022-checkpoint-2026-10-21/Documentation~/Installation.md).

## Changes

- Nested subcontexts now render through Forward, stencil Deferred and Clustered
  paths with independent camera, rendering, depth/normal and lighting resources.
  Injection follows scope and inherited enablement; exiting restores parent
  targets and published shader state. Generic context lookup remains cached.
- STP and TAA now resolve once per camera frame, maintain valid motion inputs,
  and reset history across scale and enablement changes. Opaque rigid meshes,
  skinned meshes and UltraRP SG Lit/Unlit provide object motion vectors.
- Data-driven and screen-space Lens Flare run in the render graph. Screen flares
  work with Bloom disabled or intensity zero; build stripping retains their
  required Bloom filter. The paired Core fixes correct projection, orthographic
  depth comparisons and integer shader parameters.
- Pure-depth RenderTextures use a dedicated renderer path respecting clipping,
  material ZWrite, culling, viewport and depth clear/preservation settings.
- SG Meta and MotionVectors passes support alpha clipping and vertex deformation;
  depth/normals, rendering layers, front-face inputs and negative-scale tangent
  handling are completed. SG materials continue to use ForwardOnly in Deferred;
  this does not add an SG GBuffer pass or a complete Decal target.
- Terrain detail mesh, grass and billboard defaults plus SpeedTree 7/8 defaults
  resolve to the active hand-written shaders. SpeedTree 9 remains unavailable.

## Validation

- Subcontexts: seven GPU scenarios with three render requests each, including
  nested rendering, resource restoration and parent/child Clustered isolation.
- Temporal: 31 Play Mode frames covering STP scale changes, history reset, TAA,
  rigid/SG/skinned motion and ForceNoMotion.
- Lens Flare and Shader Graph: 151 and 126 checks respectively, including actual
  image readbacks. Pure-depth targets: 30 requests across three rendering modes.
- Terrain defaults: 114 selected shader pass compilation requests, followed by
  84 graphics checks over 63 render requests using real terrain details.
- Windows x64 Mono build: 65 shaders and 20 compute shaders checked with no shader
  errors; all 17 scene-required variant groups observed retained. The Player
  passes lit/unlit pixels and runtime node checks through explicit offscreen
  render requests. The initial hidden-window ordinary-camera run timed out and
  is not counted as a pass.
- Historical warmed context measurements returned 0 B from the byte counter.
  A later positive control found that this Editor's counter also returned zero
  for intentional allocations; see the correction in [Validation.md](Validation.md).

The new acceptance scope is Windows/D3D11 mono rendering. IL2CPP, XR, other
graphics APIs, native render passes, ordinary-window presentation, full lightmap
baking and real SpeedTree import/wind/LOD are not established by this follow-up.
All 44 host/isolated ProjectSettings files match their saved baseline after
cleanup. See [FeatureCompletionSummary.json](FeatureCompletionSummary.json)
and [Validation.md](Validation.md) for source-specific evidence and limits.

# 2026-10-20 development update (historical)

This update builds on `unity2022-checkpoint-2026-10-20` for Unity 2022.3.62f3.
It is a source checkpoint on main; the existing installation tag and package
version are unchanged.

## Changes

- Shader Graph Lit/Unlit materials render through `UltraForwardOnly` in Forward,
  Stencil Deferred and Clustered paths. The depth/normals pass supports both raw
  and packed normals, clipping and vertex displacement. This does not add a
  Shader Graph GBuffer pass. Custom Forward draw lists must include
  `UltraForwardOnly`.
- Ordinary Player builds reject compiler/import errors in included Shader,
  ComputeShader and Shader Graph assets and save a diagnostics report. Unrelated
  erroneous assets do not block the build. Diagnostics are scoped by included
  asset and configured graphics API, not by exact keyword variant.
- Stencil and Clustered shader stripping use independent renderer declarations.
  Unknown/custom graphs retain both paths conservatively. Stencil materials are
  created lazily and only requested passes are checked.
- Clustered lighting fixes Spot cookie projection, first-frame cookie indices,
  main/additional Directional cookies and actual local-light bounds counts.
  The Player smoke fixture also fixes pipeline-tag ownership during shutdown.
- Expected adapter validation failures and missing-shader handling use direct
  checks. Exception handling remains at extension and build-recovery boundaries.
- Context access uses `Get<T>()` with reusable type-indexed borrowed-reference
  storage. The sub scope preserves independent resource/camera data and shared
  rendering data. Missing entries return null; clearing the container does not
  reset the borrowed objects. External producers must migrate the old fixed
  ContextInfo fields to `ContextInfo.data`.

## Validation

- Shader Graph follow-up: 169 GPU region probes over Forward, Stencil and
  Clustered, including raw/packed normals and fallback controls.
- Lighting/build follow-up: 961 GPU pixel/count checks, 19 diagnostics controls,
  positive/negative ordinary Player builds, two final feature Player builds and
  four complete Player smoke runs; its acceptance report passes 36 checks.
- Exception cleanup: existing ScriptableNode and diagnostics suites, two
  ordinary Player build controls and settings recovery pass all nine checks.
- Context storage: all 258 Runtime files compile in Release; 21 Unity checks
  pass. The historical byte counter returned 0 B for warmed reads and switching,
  but a later positive control invalidated that counter as allocation evidence;
  see [Validation.md](Validation.md). Container initialization and capacity
  growth still allocate.

These are successive, source-specific validation runs, not a claim that the
complete graphics matrix was repeated after every edit. Graphics acceptance is
Windows/D3D11. Cookie tests use grayscale cookies and a SimpleLit receiver;
hard Point shadows are covered, while RGB cookies, soft Point-shadow quality
and other graphics APIs remain outside acceptance. Context allocation checks
run in the Editor and do not establish whole-frame or IL2CPP zero allocation.

See [Validation.md](Validation.md) for evidence and reproduction,
[ShaderGraph.md](ShaderGraph.md) for supported passes, and
[Migration.md](Migration.md) for API and build-declaration contracts.
