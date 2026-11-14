# URP6 / Fraxinus shader stripping 对照（2026-11-11）

这是 2026-11-11 在 Duckov 宿主工程进行的历史审计，基准为 URP 17.0.3 的 `Editor/ShaderScriptableStripper.cs` 全部 **49 个叶子规则**。下文的“当前”“本轮”、配置读数、源码行号和测试结果均指该次审计，不代表本包当前默认配置；后续规则以 [ShaderBuild.md](ShaderBuild.md) 为准。Duckov 的游戏 Assets、测试资源和历史报告不随本包提供。此表记录源码覆盖，不把理论组合数量当成实测编译加速。

状态：**已有** = 本轮前已实现等效或保守规则；**本轮** = 本轮补齐；**可补** = 尚未实现，有明确适配方向，仍需对应测试；**保留** = FRP 动态行为或语义不同，不能直接移植 URP 的判断。部分覆盖明确列出保留范围。

| URP 叶子规则（省略公共前缀） | FRP 状态 | 对照和边界 |
| --- | --- | --- |
| VolumeFeatures_UberPostShader | 已有，保守 | Distortion、ChromaticAberration、Bloom 四模式、ACES/Neutral、FilmGrain 按已声明 VolumeProfile 并集过滤 ON。OFF 保留。Bloom 的 LQ/HQ/Dirt 不按当前参数细裁，运行时可变。 |
| VolumeFeatures_BokehDepthOfFieldShader | 已有 | 未声明任何 DoF 时裁 canonical Bokeh shader；模式/参数保持动态。 |
| VolumeFeatures_GaussianDepthOfFieldShader | 已有 | 同上；Bokeh/Gaussian 都被已声明 DoF 授权。 |
| VolumeFeatures_CameraMotionBlurShader | 已有 | 未声明 MotionBlur 时裁 canonical shader。 |
| VolumeFeatures_PaniniProjectionShader | 可补 | FRP 有 PaniniProjectionPass 和对应 shader，但 Features/PostShader 未包含 Panini；可沿用显式 postprocess opt-in、Profile 并集、canonical 身份校验。 |
| VolumeFeatures_BloomShader | 已有 | 无 Bloom 且无 ScreenSpaceLensFlare 声明时裁 Bloom shader；ScreenSpaceLensFlare 仍要 bright filter，不能仅看 Bloom 组件。 |
| UnusedFeatures_DebugDisplay | 已有 | 显式 stripRuntimeDebugShaders，shared snapshot/native + 普通 global 和已审核 post/output local 路径。 |
| UnusedFeatures_ScreenCoordOverride | 已有 | 显式开关，shared snapshot/native + 普通和已审核输出 shader 路径。 |
| UnusedFeatures_PunctualLightShadows | 已有，保守 | StandardPasses 契约 + ShadowCaster/Vertex 实际声明 + 全部构建配置 shadow support。Forward+ 可在普通 additional lights Disabled 时投射附加灯阴影，不能据普通光照模式删 punctual。 |
| UnusedFeatures_FoveatedRendering | 已有 native | AssetPrefiltering 的平台常量：非 XR 或非 PS5/Metal 删 NonUniformRaster；不能再以桌面默认状态概括所有 XR 平台。 |
| UnusedFeatures_DeferredRendering | 已有，保守 | 已证明全 Forward 时，StandardPasses 契约的 UniversalGBuffer pass 可删；不按任意自定义 pass 名猜用途。 |
| UnusedFeatures_MainLightShadows | 已有，保守 | asset capability 关闭才删 ON 状态；screen provider 可独立证明无用途。主灯/cascade/OFF 的实际选择由帧和运行时质量决定，不移植强制 select-only。 |
| UnusedFeatures_AdditionalLightShadows | 已有，保守 | 仅已证明的 Forward 配置 + 不支持 additional shadows 才禁 ON；Deferred 的 baked-shadow/empty-map 和 Forward+ 路径保留。 |
| UnusedFeatures_MixedLighting | 本轮补全 | 原有 LIGHTMAP_SHADOW_MIXING、SHADOWS_SHADOWMASK；本轮把遗漏的 _MIXED_LIGHTING_SUBTRACTIVE 接入 shared forbidden/native 同一并集。 |
| UnusedFeatures_SoftShadows | 保留动态开关 | supportsSoftShadows 可由运行时质量设置切换，普通 _SHADOWS_SOFT 的 ON/OFF 都保留；无任何阴影却开软阴影的无效组合另有规则。 |
| UnusedFeatures_SoftShadowsQualityLevels | 已有 | 依 FRP SupportsPerLightSoftShadowQuality 真值移除静态 LOW/MEDIUM/HIGH；不把当前软阴影级别视为构建全集。 |
| UnusedFeatures_HDRGrading | 保留；可加显式构建契约 | 当前明确保留 _HDR_GRADING。内部 HDR 调色不等于 HDR 显示；asset.colorGradingMode 是 public setter，运行时可变。只有所有已声明 consumers 均不可能内部 HDR 且无 HDR 输出强制时，才可删 ON。详见下文。 |
| UnusedFeatures_UseFastSRGBLinearConversion | 可补，需扩展证明 | 当前没有此 local keyword 模型。设置 getter 只读，但中间 PostProcessingData 可写，Uber processor 也能改材质；需证明全部消费者和扩展保持资产设置后，再按 exact shader/program 选择 ON/OFF，不能塞进普通 global 规则。 |
| UnusedFeatures_LightLayers | 已有 | useRenderingLayers 全部关闭才禁 _LIGHT_LAYERS；与贴花层/写层输出不是同一能力。 |
| UnusedFeatures_RenderPassEnabled | 保留；可补受限规则 | native 字段存在但 CreateData 未赋 stripNativeRenderPass；FRP RenderGraph、Stencil 和 ClusterRaster 使用 framebufferFetch 动态结果。须证明所有路径都不可能使用后才删 ON，不能照 URP renderer setting。 |
| UnusedFeatures_ReflectionProbes | 已有；补充 blending OFF 裁剪 | blending/boxProjection 全部关闭时删 ON。全部消费者启用 blending 且遵守相机灯光契约时删 blending OFF；boxProjection 仍保留 OFF。 |
| UnusedFeatures_ForwardPlus | 已有，保守 | 审核所有 graph 的 RequestRenderingMode slot 和 DrawObjects.isForwardPlus；未知 override 保留；不用 active/connected 当前值裁运行时可启用节点。 |
| UnusedFeatures_SHAuto | 已有，按 FRP 适配 | 调用 FRP PlatformAutoDetect.ShAutoDetect；删除不可能 named SH 状态，Mixed/Vertex 仍保留 OFF。不能抄 URP 的移动平台 Auto 策略。 |
| UnusedFeatures_AdditionalLights | 已有；补充 OFF 裁剪 | 合并 asset PerVertex/PerPixel/Disabled，删除不可能 named 状态。全部消费者同模式、无 Forward Plus 且遵守相机灯光契约时删 OFF；已补离屏深度相机初始化。Deferred 本身不要求普通附加灯 OFF。 |
| UnusedFeatures_ScreenSpaceOcclusion | 保留动态开关 | FRP AO 从相机 Volume 注入；当前 native 固定 Select（ON/OFF）。不能从图中缺 SSAO renderer feature 推断无 AO。 |
| UnusedFeatures_DecalsDbuffer | 已有 | 审核全部 DecalRenderPassNode；Automatic 确实采用 MRT3，不误读 serialized surfaceData。跨资产并集，未知 provider 保留，OFF 保留；GL native 禁 DBuffer。 |
| UnusedFeatures_DecalsNormalBlend | 可补 | 尚无 LOW/MEDIUM/HIGH shared 模型。FRP GetScreenSpaceSettings 的 Automatic=Low，显式用 screenSpaceSettings.normalBlend；可沿用已审核 decal provider 并集，未知/derived provider 保留。 |
| UnusedFeatures_DecalLayers | 可补 | 尚无 _DECAL_LAYERS shared/native 模型。不能拿 asset.lightLayers 代替各 decal.settings.decalLayers；可合并所有已审核 provider 和平台支持，再删除 ON。 |
| UnusedFeatures_WriteRenderingLayers | 本轮 | 新 RenderingLayerOutput 能力 + provider/graph 审核 + shared/native 赋值，证明全无使用才删 ON。URP 按 DepthNormals/Forward/GBuffer 的更细正向 select-only 本轮没有照搬。 |
| UnusedFeatures_AccurateGbufferNormals | 保留；可补 pass 受限规则 | FRP DeferredLights 构造固定 accurate=true；但 Forward DepthNormals 设置 false。可审核 GBuffer 程序专属契约后删其 OFF；全 shader select-only 会破坏 Forward DepthNormals。 |
| UnusedFeatures_LightCookies | 已有 | 所有 asset supportsLightCookies 关闭才删 ON，OFF 保留。 |
| UnusedFeatures_ProbesVolumes | 已有整体关闭；可补 band | 全部 asset 不支持 APV 时 L1/L2 都删；尚未单独合并 L1/L2 band。继续保留 OFF，覆盖非 APV 消费者。旧直接挂 _lightProbeSystem 的过滤本轮移除，避免绕过总开关/Bundle consumer 并集。 |
| UnusedFeatures_DataDrivenLensFlare | 可补 | 当前未对 canonical LensFlareDataDriven 整 shader 裁剪；runtime 受 supportDataDrivenLensFlare 限制。全 asset false + 已审核标准消费者可证明无需该 shader；不能仅按当前场景无 flare component 判断。 |
| UnusedFeatures_ScreenSpaceLensFlare | 可补 | 当前未整 shader 裁剪；runtime 受 supportScreenSpaceLensFlare + Volume 限制。可按全部能力/显式 postprocess 声明，须同时保留其 Bloom bright-filter 依赖。 |
| UnusedFeatures_XRMirrorView | 可补受限规则 | 现有只对其已审核 HDR output keywords 生效，未整 shader 删除；需实际 XR loader/build capability 和 canonical 资源身份，不能仅凭没看见 XR renderer graph。 |
| UnusedFeatures_XROcclusionMesh | 可补受限规则 | 未整 shader 删除；native XR keyword 字段也未在当前 CreateData 按 XR 建模赋值。先建立真实构建 XR 能力快照。 |
| UnusedFeatures_XRMotionVector | 可补受限规则 | 同上；先证明没有 XR consumer，不能只看 Player 图。 |
| UnsupportedVariants_DirectionalLightmap | 本轮后备；实测无增量 | 独立 FraxinusLightmapRules=Standard；仅 Fragment 三控制轴都实际声明，且 DIR ON、static/dynamic OFF 才裁。Vulkan 联合 Vertex 回调重查 Fragment。Lit/Terrain/Soda 已审核；Grass 不授权，Terrain Add 缺 dynamic 则保留。本轮 Unity 2022/Vulkan 小包中，内置阶段已将 8 态减为 7 态，FRP 规则没有再减少数量。 |
| UnsupportedVariants_EditorVisualization | 可补，低收益 | 尚无独立能力；canonical Meta 通常已经整体删除。可增加只代表 editor visualization 的契约/审核，不应给 Soda/自定义 shader 顺便授予 StandardPasses。 |
| InvalidVariants_HDR | 已有，限定输出语义 | 已审核 canonical post/output shader 中移除 HDR 输入/编码/色域组合和 EASU/Gamma HDR，native 同步；不误删内部 _HDR_GRADING。 |
| InvalidVariants_TerrainHoles | 已有 | 独立 FraxinusTerrainHoles=true，实际 alpha-test 声明、全配置不支持孔洞；Unity 导入 True→true 已兼容，不扩大到普通 alpha 材质。 |
| InvalidVariants_Shadows | 已有，精确声明 | ShadowCombinations 契约 + UltraForward/Fragment；soft 无任何 shadow，以及 additional shadow 无 PerPixel/F+ 且已知非 Deferred 才裁；缺任何控制轴就保留。 |
| UnusedPass_2D | 保留；需图契约 | 未裁 Ultra2D。FRP DrawObjectsPass 可声明任意 LightMode 列表；没有 URP Renderer2D 不代表 FRP 图不会画此 pass。 |
| UnusedPass_Meta | 已有，限定契约 | StandardPasses + 不需 Enlighten realtime GI 时裁 Meta；自定义 Meta 需独立审核。 |
| UnusedPass_ShadowCaster | 已有，保守 | StandardPasses + 全 Forward + 主/附加阴影都无支持才删；不按场景当前无投影灯删除。 |
| UnusedPass_Decals | 可补需图契约 | 当前只裁 DBuffer keyword，未按 DBuffer/ScreenSpace/GBuffer technique 整 pass 裁剪。需并集实际 technique 的自动/平台/Deferred 分支，并保护用户自定义 DrawObjects LightMode。 |
| UnusedPass_XRMotionVectors | 保留；需图契约 | 未按 pass 名删除。与 XR 专属整 shader 是两件事，FRP 图任意 LightMode 可能调用自定义同名 pass。 |
| UnusedShaders_Deferred | 已有 | 全部已声明 graph 证明不用 StencilDeferred 才删 canonical shader；FRP 另有 Clustered compute 路径独立判断。未知 graph/processor 保留。 |
| UnusedShaders_HDROutput | 可补 | 当前删 BlitHDROverlay 的 HDR keywords，没有整 shader 删除。FRP FinalBlitPass:96 仅 isHDROutputActive 才选择 HDR 资源；在 allowHDRDisplaySupport=false 且已审核消费者下可进一步整 shader 删除。 |

## Fast sRGB、HDR Grading、APV band 的实际条件

- **Fast sRGB**：`Shaders/PostProcessing/UberPost.shader:11` 和 `BokehDepthOfField.shader:4` 为 `multi_compile_local_fragment`；运行时 `UltraRenderPipeline.cs:1528` 从 asset 复制到 frame，`PostProcessPass.cs:612` 和 `DepthOfFieldPass.cs:378` 控制关键词。当时 DuckovUltraRP `_useFastSRGBLinearConversion: 1`，因此只补“全关闭删 ON”对该宿主配置没有收益；若实现全 true 删 OFF，必须建立 canonical/local/stage/program 声明和所有 build consumers 并集测试。混合 true/false 必须保留两者。普通 global feature stripper 不处理这个 local-only 轴。
- **HDR Grading**：`UltraRenderPipeline.cs:1520-1525` 先依据 `supportsHDR && colorGradingMode`，随后 HDR 输出还能强制 HDR；`PostProcessPass.cs:1412` 控制 `_HDR_GRADING`，与 SDR tonemap 位于同一关键字轴。当前 `_supportsHDR: 1`、`_colorGradingMode: 0`、Player `allowHDRDisplaySupport: 0`，静态状态是 LDR。但 `colorGradingMode` 有 public setter（Asset:878），需要显式声明运行时 HDR 调色不可用或涵盖可能配置，不能仅见屏幕 HDR 关闭便裁。当前保留是明确保守策略。
- **APV band**：`ForwardLights.cs:458-461` 根据 `probeVolumeSHBands` 选择 L1/L2。当前 `_lightProbeSystem: 0` 已禁 APV，两种 named 状态已删除，因此增加 band 细分对当前项目没有增量收益。对启用 APV 的其他消费者，正确模型是每资产得到允许的 L1/L2，再跨 Player/质量/Bundle 声明求并集并继续保留 OFF；不能用单个活动 asset 的 band 过滤所有消费者。未知 band/未知 asset 保留。

## 本轮 Lightmap 实施接口及验证状态

- 修改 `UltraRPShaderPassRules.cs`：增加 `LightmapRulesTag`、`Capabilities.LightmapCombinations`、三 Lightmap keyword、`DirectionalLightmapWithoutLightmap`；新增 `ResolveCapabilities(..., string lightmapRulesTag, Capabilities legacy=None)` 和 `IsAuditedShader(shader,out terrain,out lightmaps)` 重载。旧 bool API 只保留原三能力。
- 仅给 `Lit.shader`、`TerrainLit.shader`、`TerrainLitAdd.shader`、`TerrainLitBase.shader`、`Assets/Game/Shader/SodaTerrain.shader` 加声明；未变任何 pragma，没有给 Grass 的 SH 路径授权。
- 历史 Pass 规则验证包含 72 个 NUnit 用例，覆盖八种三轴状态、dynamic-only、每个 missing declaration、policy disabled、wrong pipeline/stage、独立能力/unknown tag veto、实际 D3D/Vulkan Fragment 声明、Terrain Add 缺 dynamic 保留、旧 Particle/SimpleLit 不扩权、Grass 保护。临时测试源码已清理，结果由 Duckov 宿主另行保存。
- 静态检查覆盖 15 个 keyword 名称与 bit 索引、重复测试方法和尾空白。Unity EditMode/真实 Vulkan Bundle 的结果、源码哈希和限制见宿主工程本轮验证证据。

所有条目都应服从 shared build consumer 并集、strip 总开关和 shader 语义授权。仅有 shader/pass 名称相似，或者关键字在当前 Vertex 回调中缺席，都不是裁掉另一条程序路径的依据。

## 额外核对的边界

- 当时 Duckov 主工程启用 `stripUnusedVariants`，但 `stripUnusedPostProcessingVariants=0`；因此 VolumeProfile 效果裁剪未执行，不能把 UberPost 数量不变归为该规则失效。
- `FastSRGB` 当时全 true 的内置路径可以仅保留 ON；实现仍需独立扩展声明/证明，不能复用屏幕阴影或 Rendering Layers 的声明。
- 原生 `ShaderKeywordFilter` 按名称匹配，普通 managed 规则则保护同名 local 关键词。自定义 local shader 不应复用保留的管线全局名字来表达另一种含义；本轮未新增对此扩展情形的真实构建覆盖。
- Core stripper 的初始化缓存依赖活动管线。同一 Editor 跨不同 RP 的构建序列尚未专项验证；本轮仅验证同 FRP 连续配置切换，不据缓存实现推断一定发生漏裁。
- 构建报告观察 managed 回调。原生阶段已全部裁掉或直接命中构建缓存的 Shader 可能没有报告行；缺行不能直接解释为规则没有覆盖。
- 恢复版粒子 Lit 的 fallback 会把包内 SimpleLit 引入构建，即便该 SimpleLit 没有直接引用它的材质。它们是不同 Shader 资源；没有做材质/fallback 迁移前，不能把数量相加当作可直接删去的重复量。
