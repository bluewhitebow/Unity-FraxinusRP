# Shader 构建与变体裁剪

UltraRP 的 `Strip Unused Variants` 同时控制提前关键词过滤、普通 Shader 的功能裁剪，以及声明语义的 Shader Pass/组合规则。规则属于管线构建回调，不依赖游戏的 Android 构建脚本。通用功能规则使用管线标签和全局关键词；需要更强语义保证的 Pass 规则读取独立语义声明，包内标准 Shader 及源码等价的恢复副本继续兼容旧规则。

`UltraRPShaderBuildFeatures` 收集目标平台包含的所有管线资产和 Renderer 配置。需要保留的功能取并集，允许删除的关键词取交集：只要任意配置需要某个状态，该状态就会保留。缺失或未知的管线实现贡献空的禁用集合。它不缓存跨构建状态；每次构建重新收集。

`UltraRPShaderPrefiltering` 将共同能力写入临时 `_prefilter*` 字段，通过 `ShaderKeywordFilter` 在原生变体展开前减少组合。`Library/FraxinusRP/ShaderPrefilteringRecovery.json` 保存原值和对应 YAML 行；构建结束、取消或下次编辑器恢复时还原这些字段，不替换用户的完整管线资产。

随后 `UltraRPShaderVariantRules` 检查每个 SubShader 的 `UltraPipeline` 标签及真正声明为全局的关键词，在编译前删除仍然存在的不可能状态。普通材质、Soda 自定义 Pass、包内和恢复版粒子 Shader 使用相同规则。与已有后处理规则在同一个注册者中以 OR 组合，避免 Core 对多个独立 stripper 使用 `All()` 而互相否决。

## 当前能力规则

- 调用 Fraxinus 自身的 `PlatformAutoDetect.ShAutoDetect` 解析 SH 模式。当前 Auto 在普通 Android 构建中是 PerPixel；不复制 URP 17 的不同移动端判定。Mixed、PerVertex 及不同质量档的组合均保留各自需要的状态与 OFF 状态。
- 调用运行时的 `SupportsPerLightSoftShadowQuality` 判定阴影质量模式。使用动态质量时过滤静态 LOW/MEDIUM/HIGH；普通 `_SHADOWS_SOFT` 的开关状态始终保留，因为游戏可以在运行中调整阴影画质。
- Forward Plus 单独检查每个声明节点的 `RequestRenderingMode` 虚方法实现和 DrawObjects 配置。检查包含禁用、断开的节点。未知图、缺失类型、未知重写或不同的虚方法槽保持保守，不执行自定义节点代码。后处理节点继承默认 None 实现，不会阻止独立的 Forward Plus 判断。
- 按资产能力过滤未启用的 LOD Cross Fade、APV、APRT、Light Layers、Light Cookies、Mixed Lighting 和反射探针混合/盒投影；这些能力仍合并所有构建质量档。
- Mixed Lighting 同时处理 `LIGHTMAP_SHADOW_MIXING`、`SHADOWS_SHADOWMASK` 和旧的 `_MIXED_LIGHTING_SUBTRACTIVE`。APV/APRT 仅由统一预过滤字段控制，资产 `_lightProbeSystem` 上不再另挂绕过总开关和消费者并集的过滤属性。
- 主灯/附加灯模式、阴影支持及 Alpha Output 也加入共享能力快照。原生预过滤与脚本裁剪使用同一份并集需求，包含 Bundle 额外声明的消费者。灯数、阴影距离、级联数和动态软阴影开关不被当作静态裁剪依据。
- SSAO 的开关变体保持可用，摄像机 Volume 可以在运行时改变它。
- 普通 Shader 的 `DEBUG_DISPLAY`、`SCREEN_COORD_OVERRIDE` 同时由预过滤和脚本裁剪处理，遵守各自显式开关以及 `Strip Unused Variants`。后处理的这些构建级规则和 HDR 显示输出规则不依赖 VolumeProfile 效果裁剪开关；Profile 扫描失败也不会禁用已启用的构建级规则。Bloom 等效果和整张后处理 Shader 的裁剪仍需独立启用且扫描成功。
- 关闭 HDR 显示输出时，预过滤同时删除 `_EASU_RCAS_AND_HDR_INPUT`、`_GAMMA_20_AND_HDR_INPUT` 两个复合关键词；保留普通 FSR/RCAS、Gamma 和内部 HDR 颜色分级状态。
- `LutBuilderHdr`、`BlitHDROverlay`、`HDRDebugView`、`XRMirrorView` 的 HDR 输出轴也有 managed 后备规则，覆盖未提前启用 native 过滤的隐式 Bundle 入口。按精确路径、名称、实际 Pass/Fragment/平台声明授权，不因缺少 VolumeProfile 删除这些整张 Shader。
- DBuffer 独立检查每个资产全部声明图中的贴花节点，包括禁用或断开的节点；不依赖整图 Pass 类型白名单，也不把 Forward-only 当成没有贴花。显式 DBuffer 按 Albedo / AlbedoNormal / AlbedoNormalMAOS 分别保留 MRT1 / MRT2 / MRT3。各图和质量档需要的模式取并集，OFF 始终保留；例如，全部配置仅需要 MRT3 时，可以过滤 MRT1/MRT2。
- Automatic 按实际运行时代码保守保留 MRT3 和 OFF：`DecalRenderPassNode.GetDBufferSettings` 对 Automatic 新建 AlbedoNormalMAOS 配置，不使用序列化的 surfaceData；`GetTechnique` 也可能选择不使用 DBuffer 的 ScreenSpace/GBuffer。显式 ScreenSpace 在延迟路径变为 GBuffer，仍不使用 DBuffer。分析不调用依赖编辑器平台的 `GetTechnique`。
- 缺失、派生或无法解析的图，缺失节点，派生贴花提供者、未知 technique、缺失实际使用的 settings 或无效显式 DBuffer surfaceData，都保守保留 DBuffer 全部模式。无关的合法自定义 Pass 不被视为贴花提供者，也不会执行其渲染回调。

通用功能裁剪同时处理禁止状态与已证明必需的状态。删除必需状态的 OFF 变体前，必须检查实际 Pass、阶段和编译平台的关键词声明；Vertex 阶段缺少 Fragment 关键词不会被解释为功能关闭。同名的 `shader_feature_local` 也不会被当成运行时全局关键词。

## 相机灯光状态与 OFF 裁剪

全部构建消费者具有相同附加灯模式、没有 Forward Plus 图，并且渲染扩展遵守相机灯光契约时，只保留 PerPixel 或 PerVertex 状态。零灯仍使用该程序，灯数保持零。全部消费者启用反射探针混合时，同样只保留其 ON 状态。两项分别取交集，混合质量档和额外 Bundle 消费者会恢复各自需要的状态。原生预过滤和 managed 后备共用此结果。

存在 Forward+ 或 Deferred+ 时，共享功能快照还记录允许的灯光关键字组合。当前运行时的普通 PerPixel 路径使用 `_FORWARD_PLUS=0, _ADDITIONAL_LIGHTS=1`；使用 Forward+ 灯光列表的路径使用 `1,0`。它们不是两个相互独立的功能。managed 剥离删除已证明不可能的双开、双关组合，避免接入一个新渲染模式后把原有一组扩成四组。

组合需求跨全部管线资产取并集。附加灯关闭或 PerVertex 的配置贡献各自需要的状态；未知图、未知模式和未证明的灯光提供者保留全部组合。即使所有图声明 Forward+，仍保留禁用对应节点后需要的普通路径。过滤只处理实际 Pass、阶段和编译平台声明的全局轴，并将缺少的轴从匹配中排除；未声明附加灯轴的 GBuffer、同名 local 关键词和分离的 Vertex/Fragment 程序不会被当成双关。报告中的 `LightingCombination` 记录此规则的判定。

离屏深度相机使用材质 Forward Pass，因此在绘制前也初始化 ForwardLights。自定义节点使用独立的 `[PreservesCameraLightingKeywords]` 声明；完整运行约定见 [LightingKeywordContract.md](LightingKeywordContract.md)。此规则保留主/附加阴影、软阴影、SSAO 和级联的动态状态。

## 自定义扩展的屏幕空间阴影声明

`UltraRPShaderScreenShadowUsage` 独立判断 `_MAIN_LIGHT_SHADOWS_SCREEN`，不会把这一项证明当成整个自定义节点的渲染行为证明。全部构建资产、全部声明图（包括禁用、断开的节点）以及自动发现的扩展都确认不使用该状态时，原生过滤保留主灯普通阴影、级联阴影和 OFF，删除屏幕空间主灯阴影状态。当前 Fraxinus 运行时代码只创建并重置这个关键字，没有内置启用路径；Contact Shadows 使用独立的 `_ContactShadowTexture`，并不等于此状态。

包提供 `[ScreenSpaceShadowUsage(false)]`，供已经审查的自定义节点、后处理、Uber processor 和自动注入模块明确声明自身不产生或启用该状态。声明必须覆盖该类型委托的材质、插件绘制和回调。属性的 `Inherited=false`，子类必须重新声明；未声明、声明为 `true`、未知图、缺失节点和缺失托管引用均保留该状态。分析只读取声明，不执行自定义渲染代码。关闭 `Strip Unused Variants` 时不应用该裁剪。

自动发现检查与运行时一致：`PostProcessPass` 会实例化全部具体 `BasePostPass` / `IUberEffectProcessor`，即使没有序列化到图中；`ScriptableRenderer` 会实例化具有公开无参构造函数的具体 `BuiltinPass` / `InjectPass`。未审查且未明确声明的类型阻止屏幕空间阴影裁剪。通用 Forward/Deferred 证明仍只接受已审查原生模块，屏幕空间阴影声明不能绕过它，因此不会据此删除自定义材质的 GBuffer、Deferred 或其它 Pass。

Duckov 宿主工程的历史集成审查了以下五个精确节点类型。这些项目节点、插件和游戏资产不随本包提供；其他宿主必须审查自己的实现：

- `DuckovUltraTransparentPass` 仅将输入需求改为 Depth/Color，继续使用内置 DrawObjects。
- `DuckovUltraLegacyObjectsPass` 仅控制已有 DrawObjects 的执行和附加绘制剔除掩码。
- `DuckovUltraFogOfWarPass` 仅增加相机筛选；其 `FOW.FogOfWarPass` 基类设置 FOW 材质参数、`_BlitTexture`、`_BlitScaleBias`，然后执行合成 Blit。
- `DuckovUltraLegacyFullscreenPass` 设置 Blit 输入并使用声明材质合成；当时审查的材质和游戏代码没有启用屏幕空间主灯阴影的状态。
- `DuckovUltraPluginPass` 的 Shapes DLL 调用链经 Mono.Cecil 检查：`DrawCommand.AppendToBuffer` 只枚举 `ShapeDrawCall.AddToCommandBuffer`，后者仅调用 `DrawMesh` / `DrawMeshInstanced`；ALINE 的提交路径最终仅调用 `DrawMesh`。其最终合成回调包含的 TranslucentImage 使用 `ScalableBlur`，DLL 中实际切换的材质关键字只有 `BACKGROUND_FILL_NONE` / `BACKGROUND_FILL_COLOR`，没有主灯阴影关键字。

该次 Duckov 审查的 Assets 代码以及材质、ShaderVariantCollection、序列化资产中均未发现 `_MAIN_LIGHT_SHADOWS_SCREEN` 的启用或保存状态。后续若给这些节点加入屏幕空间主灯阴影提供者，需要删除或更新声明；单纯调整软阴影、Contact Shadows、SSAO 或 DBuffer 不影响这项独立声明。SSAO 的 `GroundTruthAmbientOcclusionPass` 会实际开启 `_SCREEN_SPACE_OCCLUSION`，因此仍保留该功能的 ON/OFF 状态。

## Rendering Layers 输出需求

`_WRITE_RENDERING_LAYERS` 与 `_LIGHT_LAYERS` 是两项能力。`UltraRPShaderRenderingLayerUsage` 检查全部资产的 Light Layers、所有图的 `DecalSettings.decalLayers`、精确原生节点及自动发现的模块/后处理。仅全部证明不需要写层纹理时，shared snapshot 与 native `_prefilterWriteRenderingLayers` 同时删除 ON。空图、缺失类型、未知扩展、派生贴花提供者以及未知设置均保留。

自定义扩展可以使用独立的 `[RenderingLayerUsage(false)]`，声明覆盖自身输入、委托材质与回调；它不授予屏幕阴影或整 Pass 裁剪权限，也不从基类继承。序列化输入可用 `[RenderingLayerUsage(false, nameof(requirements))]` 指定字段，分析读取字段而不执行 getter。所有序列化 `ScriptableRenderPassInput` 字段均检查，出现未审查的新位时仍保留。直接改变管线保留关键词的代码必须声明对应需求。

上述 Duckov 宿主的五种扩展节点也曾单独审查 Rendering Layers 需求；Fullscreen 的 `requirements` 逐实例读取。此证明依据声明的管线需求，不依赖当前 `RenderingLayerUtils.RequireRenderingLayers` 返回 false 的实现缺口。未来增加提供者或委托行为时必须更新声明。

## Pass 与无效组合

`UltraRPShaderPassRules` 按 SubShader 的独立语义声明选择规则。未声明的自定义 HLSL、ShaderGraph、fallback 子 Shader 不自动套用标准语义；标准 Lit、SimpleLit、Particles Lit/SimpleLit、Terrain Lit/Add/Base 及仅名称和换行不同的恢复副本仍兼容旧检查。`ShaderUtil.PassHasKeyword` 按当前 Pass、阶段和编译平台检查声明，结果在本次 scope 内缓存。

- Forward Fragment 中，全部主灯和附加灯阴影控制关键词均明确存在时，删除软阴影开启但所有阴影关闭的组合。
- 所有声明图都证明仅使用 Forward 时，删除附加灯阴影开启但 PerPixel/Forward+ 均关闭的组合。混合或延迟图保留这些状态。
- 仅在对应功能已证明不用时删除 GBuffer、ShadowCaster 及阴影投射灯类型。Realtime Enlighten 不需要时可删除 Meta；标准编辑器 Picking/Selection Pass 不进入运行包。
- 所有管线都关闭 Terrain Holes 时，删除审核地形 Shader 的 Alpha Test 变体；普通材质透明裁切保持原有行为。
- `FraxinusLightmapRules=Standard` 允许移除方向性 Lightmap 开启、静态及动态 Lightmap 均关闭的无效组合。要求当前 Fragment 实际声明全部三项关键词；缺少动态轴的 Terrain Add 保留，草的 SH 路径不自动套用。Vulkan 联合 Vertex 回调使用实际 Fragment 声明。

这些规则在唯一的 Core stripper 内与通用功能和后处理规则组合。它们不根据关键词缺失猜测未知 Pass 的行为；相机灯光的 OFF 裁剪使用上述独立运行时契约和实际程序声明。

这些规则依据构建时声明的配置。新增运行时注入的 Renderer、修改 SH/Forward Plus/DBuffer 等功能模式时，应把对应配置纳入构建声明，或关闭 `Strip Unused Variants`。自定义节点若直接写 DBuffer 纹理/全局关键词，或者运行时注入新的贴花提供者，也必须声明对应的贴花配置；单独检查现有节点不能推断任意自定义代码的副作用。未知 Pass 和不确定图功能仍保留。Player、AssetBundle、SBP/Addressables 的入口与缓存策略见 [AssetBundleShaderBuild.md](AssetBundleShaderBuild.md)。

## 自定义 Shader 的独立语义声明

在 `RenderPipeline=UltraPipeline` 的 SubShader `Tags` 内声明已审查的行为：

| Tag | 值 | 允许使用的规则 |
| --- | --- | --- |
| `FraxinusTerrainHoles` | `true` | `_ALPHATEST_ON` 只控制 Terrain 洞；所有构建消费者都关闭洞时移除该状态 |
| `FraxinusShadowRules` | `Standard` | Forward 使用标准主灯、附加灯、软阴影关键字关系，允许移除已证明无效的阴影组合 |
| `FraxinusPassRules` | `Standard` | GBuffer、ShadowCaster、Meta、Picking/Selection 等 Pass 遵循标准管线调用约定 |
| `FraxinusLightmapRules` | `Standard` | 方向性 Lightmap 仅在静态或动态 Lightmap 启用时有效 |

四项独立授权。地形洞声明不允许删除整 Pass，阴影或 Lightmap 组合声明不意味着可以删除 GBuffer；普通透明裁剪、草叶剪裁不能声明为 Terrain 洞。Unity 2022 会将布尔标签的 `True` 规范化为 `true`，地形洞规则兼容这两种写法；命名的 `Standard` 仍须精确匹配。未知值（含 `False`/`None`）关闭对应规则。声明作用于整个 SubShader，不支持 Pass 级覆盖；不同语义必须拆分 SubShader 或保持未声明。源码或 include 改变语义时应同步更新声明。

地形洞通过带 Tag 条件的 `ShaderKeywordFilter` 提前过滤，`_ALPHATEST_ON` 不加入全局禁用关键字集合。所有质量档和额外 Bundle 消费者的需求合并：任何一个支持洞、未知管线或缺失配置都保留。`Strip Unused Variants` 关闭时不移除；managed 规则作为未使用显式 native scope 的构建入口的后备。

源头删除固定或未使用的关键字维度与 strip 相互补充。它不依赖构建开关，因为删除的两态在被审查的 Pass 中本来就行为等价；不能把实际动态功能简单改为 `shader_feature`。Vulkan/GL 会忽略 `_fragment` 等阶段后缀，仅增加后缀不能减少这些平台的组合。ShaderVariantCollection 是保留、预热与覆盖诊断资料，不能作为未经验证的运行时完整白名单。

## 构建报告

每次构建生成 `Library/FraxinusRP/ShaderBuildReport.json`，包含当前目标、功能保留理由和每个 Shader/Pass/阶段的 managed 输入、Core 输出与最终保留数。这里的输入已经经过原生预过滤和 Unity 内置裁剪；完整组合数量仍查 Unity 原始构建日志。规则判定计数是本 stripper 的删除建议，Core 其他 stripper 可能否决，因此不能把建议数直接当作实际删除数。

报告在长批次编译前及构建结束时写入。失败而未到达最终 observer 的批次标为未完成。自动原生 Bundle 构建没有结果回调，报告不会将“回调结束”写成“构建成功”；显式 scope 可以记录结果。

`ShaderBuildReport.previous.json` 保留覆盖前的报告；`ShaderBuildComparison.json` 将当前观察到的 Shader/Pass 与已接受基线比较。基线按 target、buildKind、Graphics API 和 development/release 分开，保存在 `Library/FraxinusRP/ShaderBuildBaselines/`。只有同一 Pass 的回调阶段、SubShader/Pass 索引和次数相同才计算增量；增量构建未回调的 Shader 不记为删除或节省。旧报告缺少 API 元数据时会明确标注。

可比 Pass 的最终 managed 输入保留数达到基线的两倍，且净增至少 1,024 时，Console 输出 `UltraRP shader variants grew`，注明 Shader、Pass、前后数量及比较文件。长批次会在原生编译前检查，每个 Pass 每次构建只警告一次。该提示不删除有效变体，也不自动终止构建。

完成且确认成功的构建可自动更新基线；有增长报警、新的不可比覆盖或部分缺失的报告不会自动覆盖已有基线。Unity 2022 在 postprocess 回调中暂时返回 Unknown 时，编辑器在构建结束后核对原生 BuildReport 的 GUID、开始时间、目标和输出路径，确认 Succeeded 后才尝试更新。失败和中断报告不接受。

确认增长是新功能的必要成本，或导入旧的完整报告后，可执行 `Tools > Fraxinus > Shader Build > Use Last Complete Report as Baseline`。此入口允许显式接受完整的 Unknown 报告并记录其原始状态，不会伪造成功结果。基线是本机 Library 中的数据；清空 Library 会丢失，需要保留报告用于后续对比。

## 验证与构建耗时

阴影 OFF 的移除与 URP 的 empty-shadow-map 运行契约一致：所有构建资产都启用对应能力，并且每个声明图中的主光/追加光阴影节点可达、active、严格早于接收绘制。缺少节点、未知提供者、执行顺序并列或上下文未配对时保留 OFF。离屏深度相机也执行图中原生阴影节点并声明纹理读取，恢复相机矩阵后再绘制材质 Forward Pass。普通软阴影 ON/OFF、级联选择仍保留。

`Realtime Area Lights` 是管线资产的构建能力。旧资产默认启用；项目可以在不需要实时矩形面光源时关闭。共享 `_FRAXINUS_AREA_LIGHTS` 编译轴由原生预过滤和脚本规则选择单态：所有已知资产启用则只保留 ON，全部关闭则只保留 OFF，混合或未知配置保留两态。运行时按资产能力设置关键词，不按当前可见灯数来回切换。GPU deferred compute 的共享库也声明该轴，并有对应计算 Shader 裁剪；低目标等级不会因为此开关被提高，FGD/IBL 不随 LTC 路径关闭。

URP 49 项规则的逐项覆盖、仍可补的优化及 FRP 动态语义差异见 [StrippingCoverageAudit.md](StrippingCoverageAudit.md)。已实现 strip 不等于所有无用维度都已消除。

Duckov 宿主的历史 EditMode 验证覆盖多质量档交集、未知配置、SH 运行策略、动态软阴影和贴花保留、DBuffer 多模式与未知提供者、OFF/MRT3 保留、真实 Soda/粒子关键词、Shader scope 切换、字段恢复及注册者组合。相关临时测试入口已清理；历史结果由 Duckov 宿主另行保存，测试资产和报告不随本包提供。

编译检查和规则测试不等同于实际 Android 构建或真机画面验证。实际剩余变体数应检查构建日志中的 `After settings filtering`、`After scriptable stripping`，以及缓存命中、新编译数量和耗时。保留 `Library/ShaderCache`，无需为启用新规则清空已有缓存。
