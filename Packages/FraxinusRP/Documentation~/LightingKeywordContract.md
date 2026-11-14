# Camera lighting keywords and build filtering

`UltraRenderer` owns `_ADDITIONAL_LIGHTS`, `_ADDITIONAL_LIGHTS_VERTEX` and
`_REFLECTION_PROBE_BLENDING`. Its ForwardLights setup establishes their values
for every camera, including a camera rendering directly to a depth texture.
Zero visible additional lights keep the configured additional-light mode ON;
the light count remains zero, so this does not add lighting. Forward+ instead
uses its own lighting path and disables both ordinary additional-light modes.

When unused-variant stripping is enabled, every declared build consumer must
agree before a keyword becomes required. The additional-light mode must be
known and no graph may require Forward+. Reflection probe blending must be
enabled by every pipeline asset. Quality assets and explicitly declared
AssetBundle/runtime pipeline consumers all participate in this intersection.
A different mode, disabled capability, missing pipeline, custom renderer or
undeclared provider preserves OFF. The native prefilter and managed stripper
use this same intersection. The managed path checks the actual pass, shader
stage and compiler platform; absence of a fragment-only keyword in a separate
vertex program is not interpreted as OFF. Local keywords with the same names
are not camera globals.

Custom rendering providers can declare `[PreservesCameraLightingKeywords]`
after review. The declaration applies to that exact class, is not inherited,
and includes nested passes and callbacks. The provider must preserve the
camera's additional-light mode, reflection blending, their `LightData` inputs
and global keywords whenever it draws ordinary FRP materials. Drawing custom
materials is supported; temporarily changing these reserved camera globals
to request a stripped state is outside this contract. Applications requiring
that behavior must retain both states by leaving the provider undeclared or
disabling unused-variant stripping for the build.

The same reserved-keyword rule applies to `OnRenderObject`, camera-capture
callbacks, pipeline events, `beforeFinalResolve`, dynamic module replacement
and external plugins. Merely exposing a callback does not introduce a new
lighting configuration. New or replaced providers must obey the declaration;
unlisted pipeline assets used at runtime must be registered as build consumers.

All serialized nodes are inspected, including inactive/disconnected nodes,
and the actual nested postprocess providers are checked. The native provider
list uses exact types. In the Duckov host's historical integration, five bridge
classes declared this contract after review of their normal drawing, FOW,
Shapes, ALINE and UIBlur paths. Those project classes and plugins are not
included in this package; a plugin upgrade or added callback that changes
these globals requires reviewing the declaration.

Shadow preservation requires the separate
`[PreservesCameraLightingKeywords(preservesShadowKeywords: true)]` opt-in;
the parameter defaults to false. Main/additional shadow OFF variants can be
removed only when all build consumers enable the capability and every declared
graph proves that the corresponding shadow pass runs before receiver draws,
including empty-shadow-map setup. Unknown providers or execution order retain
OFF. Ordinary soft-shadow ON/OFF and cascade selection remain dynamic.

For a pass that actually declares both required binary axes, candidates along
those axes reduce from four combinations to one. A pass with only the
additional-light axis reduces from two to one. These are candidate counts,
not a measured wall-clock speedup. `ShaderBuildReport.json` records the
required globals and managed `RequiredGlobal` decisions; native filtering
happens before the report's managed callback counts.
