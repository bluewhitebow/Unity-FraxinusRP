# Streaming Virtual Texturing

UltraRP integrates Unity's native Streaming Virtual Texturing (SVT) backend.
Shader Graph materials sample Texture Stacks and write visible tile requests
into a feedback buffer. UltraRP downsamples the feedback and submits it to the
native resolver; Unity handles tile loading and texture caches. This integration
covers the supported paths below and does not provide complete HDRP feature parity.
The existing terrain Runtime Virtual Texturing (RVT) system remains independent.
Addressables integration is outside the current scope.

## Enable and author materials

1. Enable **Project Settings > Player > Other Settings > Virtual Texturing**,
   then restart the Editor. Unity 2022.3 initializes its native VT module at
   startup; recompiling scripts or shaders alone does not activate it.
2. Use an UltraRP Lit or Unlit Shader Graph. Add a **Virtual Texture** property,
   assign every layer of its Texture Stack, and sample it with a **Sample Virtual
   Texture** node. Connect the sampled result to the material's surface output.
3. Configure cache sizes and optional coarse-mip preloading in the pipeline
   asset's **Virtual Texturing** section.

Ordinary Texture2D samples do not become virtual automatically. When project VT
support is disabled, Core's Texture Stack macros select ordinary texture
sampling. A textured image with VT disabled therefore does not demonstrate
native streaming.

## Cache configuration

The pipeline exposes a CPU cache budget, GPU cache budgets by texture format,
and controls for the number of coarse mips and textures preloaded per frame.
The production defaults remain **256 MB CPU** and **128 MB GPU per format**;
the GPU entry with format **None** supplies the default for unspecified formats.
These are configured budgets, not measurements of actual resident texture data.

Size GPU caches for each texture format and the visible working set. An
undersized cache can cause tile-admission failures, including native **W1003**
warnings, and prevent requested detail from arriving. A smaller budget does not
by itself demonstrate successful eviction or memory savings. Increase the
relevant format's budget or reduce the working set when admission fails.
Coarse-mip preloading reduces initial missing detail but does not replace the
feedback path or sufficient cache capacity.

## Rendering and shader integration

Feedback collection supports non-XR 2D Game, Scene View, and Reflection cameras,
including ordinary and MSAA targets. Software render scale uses the actual
camera target dimensions. UltraRP Lit and Unlit Shader Graph color passes emit
packed native feedback. Depth, shadow, and other auxiliary passes can sample
Texture Stacks without emitting feedback.

Transparent materials select complete foreground or background requests using a
Bayer mask and separate blending on the feedback target; tile IDs must not be
interpolated. Built-in UltraRP forward shaders write an empty feedback sentinel
when ordinary materials cover virtual-textured surfaces. Alpha clipping and
MSAA coverage also apply to this output.

External forward shaders drawn by the same DrawObjects MRT pass must include
`ShaderLibrary/ShaderVariables.hlsl` from the UltraRP package and write either an
empty sentinel or packed native feedback to `ULTRA_VT_FEEDBACK_TARGET`. Follow
`PackVTFeedbackWithAlpha`, premultiply its RGB by its alpha, and configure
independent `Blend 1 One OneMinusSrcAlpha` and `Blend 2 One OneMinusSrcAlpha`
states. Feedback uses target 1, or target 2 when rendering layers occupy target
1. Custom draw passes that bind the feedback buffer must preserve this layout.
An empty-feedback output does not convert an ordinary Texture2D sample into SVT.

## Limits and engine requirements

Hardware dynamic resolution, XR arrays, nested render contexts, and custom node
targets do not collect feedback. These views cannot independently request their
required tiles; they may only see tiles requested by supported views or
preloaded coarse mips. Native RenderGraph compiler compatibility is not yet
established. Cache residency, eviction behavior, memory savings, and long-term
stability must be assessed for the application's content and workload.

VT requires compute shaders, asynchronous GPU readback, compatible MRT formats,
and independent MRT blending. Each camera owns native resolvers and persistent
readback textures. Inactive camera resources retire after 60 seconds when the
renderer next records a view; renderer disposal releases remaining resources.

The integration uses Unity 2022.3's native `Resolver`, `Streaming`, and `System`
APIs, guarded by `ENABLE_VIRTUALTEXTURES`. It does not replace Unity's native
streaming backend. Unity labels SVT experimental; consult its versioned
[requirements and compatibility](https://docs.unity3d.com/2022.3/Documentation/Manual/svt-requirements-compatibility.html)
and [Shader Graph workflow](https://docs.unity3d.com/2022.3/Documentation/Manual/svt-use-in-shader-graph.html).
