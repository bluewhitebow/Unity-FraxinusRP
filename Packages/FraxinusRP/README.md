# FraxinusRP

基于 Unity **2022.3.62f3** 和现有 **Fraxinus-RPCore 14.0.99** 的 ScriptableNode 与 UltraRP，移植自 Fraxinus Engine 的 Unity 6 分支。包名为 `com.bluewhitebow.fraxinus-rp`。

## 使用

Git 安装配置和配套仓库见 [Installation.md](Documentation~/Installation.md)。当前 `main` 新增 Hybrid Lit 与实时矩形面光；历史 SGSR 检查点为 `unity2022-checkpoint-2026-10-23`。更新内容见 [Updates.md](Documentation~/Updates.md)。

1. 保留配套的 Fraxinus-RPCore，并将本项目的 `Fraxinus-ShaderGraph` 作为 `com.unity.shadergraph` 的本地依赖。这个 Shader Graph **14.0.12** 分支为 UltraRP Target 增加内部 API 的程序集访问权限；直接换回官方包会缺少该权限。
2. 使用 **Assets → Create → FraxinusEngine → UltraRP → UltraRP Asset** 创建管线资产；同目录会同时创建并关联 Renderer Graph。也可通过同级的 **Ultra Renderer** 菜单单独创建渲染图。
3. 打开 Renderer Graph 配置渲染节点和资源连接，将管线资产指定到 **Project Settings → Graphics** 及当前 **Quality** 等级。节点配置见 [Installation.md](Documentation~/Installation.md)。
4. 使用 **Assets → Create → Shader Graph → UltraRP → Lit Shader Graph / Unlit Shader Graph** 创建图。图中选择 UltraRP Target；支持范围见 [ShaderGraph.md](Documentation~/ShaderGraph.md)。
5. 在 Pipeline Asset 的 **Quality → Upscaling Filter** 选择 **SGSR1 / SGSR2**，配合 Render Scale 和相机后处理使用；配置及回退条件见 [Upscaling.md](Documentation~/Upscaling.md)。

Hybrid Lit 与实时矩形面光支持 Forward / ForwardOnly 及普通 Lit 的 Clustered Deferred，最多 16 盏无阴影面光；材质、配置及支持边界见 [HybridLighting.md](Documentation~/HybridLighting.md)。

Volume 的 **Lighting → Ground Truth Ambient Occlusion** 提供 GTAO，默认关闭；通过完整深度／法线预绘制接入 Forward、Hybrid 与普通 Lit / Simple Lit Deferred。配置和范围见 [AmbientOcclusion.md](Documentation~/AmbientOcclusion.md)。

包内不发布示例场景或示例生成器。已有宿主工程中的场景和资产由工程自行管理。

## 实现与验证

ScriptableNode 保留原来的程序集与类型身份，支持节点依赖执行、序列化、复制粘贴、Undo/Redo 和窗口重载。`JobGraphProcessor` 使用明确标识的同步执行后备实现；不提供 Job 并行调度。自定义节点的 IL2CPP 保留规则见 [IL2CPP.md](Documentation~/IL2CPP.md)。

UltraRP 保留独立的编辑器程序集以与 URP 共存。Shader Graph 14 的 Lit/Unlit Target 已接入；Decal、Fullscreen、Canvas、SixWay 子目标和 VFX 集成仍在 `Optional~`，继承的 SpeedTree、Autodesk 等高级图资源也保持隔离。移植过程中重映射的 365 个冲突 GUID 记录在 [GuidRemapping.json](Documentation~/GuidRemapping.json)。

首次检查点的 Windows / D3D11 验收包括：ScriptableNode 编辑与真实域重载、Shader Graph 导入和渲染、灯光阴影及后处理 A/B 对照、URP 切换，以及 8 个 Player 构建配置的 16 次独立运行。其中 Shader Graph 使用实际 IL2CPP / High 裁剪构建；六个功能场景分别通过 RenderTexture 和屏幕输出验证。该历史[验收摘要](Documentation~/AcceptanceSummary.json)包含完整报告的哈希；总报告保留在宿主项目的 `Logs/FraxinusRP/Phase2Acceptance.json`。

前一检查点 `unity2022-checkpoint-2026-10-21` 接入嵌套子上下文渲染、STP／物体运动矢量、两类 Lens Flare、纯深度 RenderTexture，以及 Shader Graph Meta／MotionVectors 等配套 Pass，并补全 Terrain 和 SpeedTree 7／8 默认 Shader。该版专项 GPU 测试及 Windows Mono 离屏 Player 烟测已通过；历史[验收摘要](Documentation~/FeatureCompletionSummary.json)记录结果和证据哈希。

已发布的 `-get` 检查点将上下文读取统一为 `Get<T>()`：配套 Core 直接解析当前作用域，未覆盖的数据回退到帧容器，Begin／End 负责切换绑定。45 项容器检查、7 个嵌套 GPU 场景、Shader Graph／纯深度回归及 Editor 中 URP → UltraRP → URP 切换均通过，见[该轮验收摘要](Documentation~/UnifiedContextGetSummary.json)。预热后的指定读取和切换循环记录到 0 个 `GC.Alloc` 事件，正控制记录到 1 个；旧字节计数器在此 Editor 中无效，历史 0 B 读数不能证明无分配。该轮未重新构建 Player。

2026-10-22 检查点修复未启用 UltraRP 时查看 Pipeline Asset 触发的 Inspector 空引用，并移除移植验收脚本，C# 净减少约 8,800 行。当时通过 Unity 编译、六种示例生成、Basic Forward 像素检查及 Inspector 修复前后对照；该轮未重新构建 Player。对应工具和示例可在历史检查点查阅，当前包保留正式构建检查和变体裁剪。

本次接入 SGSR1 空间超分辨率和 SGSR2 时序超分辨率，包含运动矢量、独立相机历史、历史重置及不支持配置的回退。Unity 2022.3／D3D11 下 43 帧 Editor GPU 检查、Windows Mono 构建及 20 帧 Player 离屏渲染通过；配置和验收范围见 [Upscaling.md](Documentation~/Upscaling.md)。临时测试脚本未随包发布。

历史测试环境、构建源码版本边界、保留警告和未覆盖范围集中在 [Validation.md](Documentation~/Validation.md)。上述功能及统一读取改动未复验 IL2CPP 和屏幕输出；XR、VFX、APV、PRT、完整虚拟纹理及其他平台仍不在当前验收范围。Unity 6 的原生 GPU Resident Drawer 在 2022 中不可用。

配套 Core / URP / Shader Graph 改动和迁移注意事项见 [Migration.md](Documentation~/Migration.md)。

构建时的功能收集、关键词预过滤、普通 Shader 裁剪及构建报告说明见 [ShaderBuild.md](Documentation~/ShaderBuild.md)。

## Hybrid 光照来源与许可证

Hybrid Lit 的部分 BRDF / FGD 算法和矩形面光 LTC 积分适配自 AkiKurisu 的 [IllusionRP](https://github.com/AkiKurisu/IllusionRP)（源版本 `91495f7`），其中包含源于 Unity HDRP 的算法。Fraxinus 复用自身的 LTC 数据、FGD 资源和渲染框架。

完整来源说明和原始 Unity Companion License 声明保留在 [光照库 README](ShaderLibrary/Lighting/README.md)。迁移设计中的源包根目录对应上游仓库的 `Packages/com.kurisu.illusion-render-pipelines/`。
