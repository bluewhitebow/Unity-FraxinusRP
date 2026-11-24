# Unity-FraxinusRP 使用指南

[English](Usage.md) | [简体中文](Usage.zh-CN.md) · [仓库首页](../README.zh-CN.md)

使用 **Unity 2022.3**，其中 **2022.3.62f3** 与源工程一致。本仓库是一组 UPM 包，使用它们的 Unity 工程自行提供 `Assets`、`ProjectSettings` 和 `Packages/manifest.json`。

本仓库包含 **ScriptableNode / UltraRP** 的四个配套包：Core、Light Transport、Shader Graph 和 FraxinusRP，以及可选的 **Fraxinus GPU Batcher**。下方配置安装全部五个包。GPU Batcher 在 Unity 2022.3 与 Unity 6 共用一套实现，原版 Unity 6 默认启用原生间接提交，原版 Windows x64 Unity 2022 可使用包内 Vulkan 后端。不需要 GPU Batcher 时，请省略 `com.bluewhitebow.fraxinus-gpu-batcher`。UltraRP 不依赖 URP。

四个配套包及选装的 GPU Batcher 请使用同一提交中的版本。BRG 移植依赖 Core 的原生对象变化追踪封装、UltraRP 的剔除与逐节点注入钩子，以及 Shader Graph 的 SpeedTree 适配，仅匹配包版本号不足以保证兼容。

## 使用本地 checkout

将本仓库与使用它的工程放在同一级目录：

```text
workspace/
├── Unity-FraxinusRP/
│   └── Packages/
└── MyUnityProject/
    └── Packages/
        └── manifest.json
```

把以下条目合并到工程 `Packages/manifest.json` 的 `dependencies` 中，保留工程其他依赖。[manifest.local.json](manifest.local.json) 提供同样的 JSON 依赖配置。

```json
{
  "com.unity.render-pipelines.core": "file:../../Unity-FraxinusRP/Packages/Fraxinus-RPCore",
  "com.fraxinus.rendering.light-transport": "file:../../Unity-FraxinusRP/Packages/Fraxinus-Light-Transport",
  "com.unity.shadergraph": "file:../../Unity-FraxinusRP/Packages/Fraxinus-ShaderGraph",
  "com.bluewhitebow.fraxinus-rp": "file:../../Unity-FraxinusRP/Packages/FraxinusRP",
  "com.bluewhitebow.fraxinus-gpu-batcher": "file:../../Unity-FraxinusRP/Packages/Fraxinus-GPU-Batcher"
}
```

`file:` 路径相对于使用方工程的 **Packages 目录**。示例采用默认克隆目录名 `Unity-FraxinusRP`；如果 checkout 目录名称不同，或没有与工程放在同一级目录，请相应调整路径。

其他官方及内置依赖由 Unity 按包声明解析。源工程已解析的共享依赖包括 Burst **1.8.28**、Collections **2.6.6** 和 Mathematics **1.3.2**。若使用方 manifest 显式锁定了更旧版本，接入时应检查这些固定版本。

## 使用 Git 安装

将以下条目合并到 `dependencies`，也可使用 [manifest.git.json](manifest.git.json)。`?path=` 用于选择仓库中的单个 UPM 包；仓库根目录本身不是一个 UPM 包。

```json
{
  "com.unity.render-pipelines.core": "https://github.com/bluewhitebow/Unity-FraxinusRP.git?path=/Packages/Fraxinus-RPCore#main",
  "com.fraxinus.rendering.light-transport": "https://github.com/bluewhitebow/Unity-FraxinusRP.git?path=/Packages/Fraxinus-Light-Transport#main",
  "com.unity.shadergraph": "https://github.com/bluewhitebow/Unity-FraxinusRP.git?path=/Packages/Fraxinus-ShaderGraph#main",
  "com.bluewhitebow.fraxinus-rp": "https://github.com/bluewhitebow/Unity-FraxinusRP.git?path=/Packages/FraxinusRP#main",
  "com.bluewhitebow.fraxinus-gpu-batcher": "https://github.com/bluewhitebow/Unity-FraxinusRP.git?path=/Packages/Fraxinus-GPU-Batcher#main"
}
```

Unity 需要能够调用 Git 并访问该仓库。`#main` 跟随开发；需要可复现版本时，将**所有** `#main` 替换为同一个已发布提交哈希或标签，并将使用方工程的 lockfile 纳入版本控制。

使用方工程 `Packages/` 下同 ID 的 embedded 包优先于 manifest 引用。切换到 checkout 或 Git 依赖时，先备份本地改动，将过期的 embedded 副本移出该目录；等待 Unity 解析后，在 Package Manager 确认实际包来源。

## 配置 ScriptableNode / UltraRP

Graphics 资产与当前 Quality 覆盖决定实际运行的管线。安装 UltraRP 不会自动启用它，也不会转换已有材质或 Renderer 资产。

1. 创建 **Assets > Create > FraxinusEngine > UltraRP > UltraRP Asset**，同时会创建并关联 Renderer Graph；同级 **Ultra Renderer** 菜单可单独创建图。
2. 打开 Renderer Graph 配置节点。基础 Forward 路径使用 `RenderDrivenPass → DrawObjectsPass → PostProcessPass` 的 Camera Target 连接；绘制节点使用 `UltraForward` / `UltraForwardOnly` 标签。透明物体需要对应的透明绘制节点。
3. 将 `ColorGradingLutPass.colorLut` 接到 `PostProcessPass.colorLut` 以使用后处理。主光阴影还需连接阴影输出并配置阴影节点调度，参见[图配置说明](../Packages/FraxinusRP/Documentation~/Installation.md)及 [ScriptableNode 指南](../Packages/FraxinusRP/Documentation~/ScriptableNode.md)。
4. 在 Graphics / Quality 指定 UltraRP 资产。通过 **Assets > Create > Shader Graph > UltraRP** 创建 Lit / Unlit 图，或使用包内手写 UltraRP Shader。
5. 按功能文档配置 [Hybrid 光照](../Packages/FraxinusRP/Documentation~/HybridLighting.md)、[GTAO](../Packages/FraxinusRP/Documentation~/AmbientOcclusion.md)、[Contact Shadows / 自动曝光](../Packages/FraxinusRP/Documentation~/ContactShadowsAndExposure.md)及[超分辨率](../Packages/FraxinusRP/Documentation~/Upscaling.md)。

UltraRP 的 Target 内部 API 需要配套 Shader Graph 分支。它的版本仍为 `14.0.12`，安装同版本的官方包不会提供相同的程序集访问权限。FraxinusRP 当前没有可导入示例场景或示例生成器。

## 可选 GPU Batcher

[Fraxinus GPU Batcher](../Packages/Fraxinus-GPU-Batcher/README.md) 在 Unity 2022.3 与 Unity 6 共用实例注册、存储、变换上传、相机／阴影剔除、LOD 和间接参数生成实现。**原版 Unity 6 自动安装内置原生间接提交实现；原版 Windows x64 Unity 2022 可使用包内 Vulkan 插件。** 用 `-force-vulkan` 启动 Editor 或 Player，添加 `-disableFraxinusVulkanBRG` 可进行禁用对照。已安装的同步 `BRGIndirectSubmission.submitHandler` 优先使用。`BRGIndirectSubmission.isAvailable` 仅表示是否已安装 handler，`GPUBatcherRenderer.canSubmitDraws` 还包含 Vulkan 后端；注册接管原 Renderer／Terrain 前，应检查后者。没有可用后端时，注册物体没有间接绘制输出。Bake 会删除源子 GameObject，评估这些流程时请使用可丢弃的场景副本。

Vulkan 后端依赖同一修订 Core 与 FraxinusRP 中的 Fraxinus DOTS Shader includes。Unity BRG 选择材质和 Shader Pass，独立 mesh index buffer 将每次绘制关联到对应 GPU 参数；每个视图的 raw buffer 保存复制的实例数据和可见性。需要为这些网格副本及视图缓冲预留内存。visibility offset 上限为 8,388,607 个元素。包内二进制仅面向 Windows x64 Vulkan；其他图形 API 或平台使用的独立定制宿主 NativeContract 适配器不包含在本包分发中。

BRG 实现替换了旧 GPUInstancer 代码、Profile、公开 API 和可导入示例，不提供自动迁移。已有项目数据需要按新组件与配置重建；UPM 标识和 `0.1.0` 版本号不变，不代表这些契约仍兼容。

通过 **Edit > Project Settings > Graphics > Fraxinus GPU Batcher** 编辑 Player 使用的配置。在 **Window > Analysis > Rendering Debugger** 的 **GPU Batcher** 面板调整剔除开关、查看 Scene 视图诊断；**Tools > Fraxinus GPU Batcher > System Window** 保留详细资源和缓冲检查。实时计数需要编译时启用 `DEBUG`。配置会在 `Assets/FraxinusGPUBatcherData/FraxinusGPUBatcherConfig.asset` 自动查找或创建，优先使用已有项目配置，修复 Shader 引用，并将选定配置加入 Player Settings 的 Preloaded Assets。GameObject 和地形注册改用新的 `GPUBatcher*`／`TerrainGPUBatcherRegisterer` 组件。

Core、FraxinusRP 与 Shader Graph 必须使用同一修订的配套版本。Core 通过 `NativeObjectChangeTracker` 提供原生 ObjectDispatcher 追踪；RP 的 `InjectPass.ShouldExecute` 钩子让 HZB 历史选择最后一个受支持的不透明 Pass。Shader Graph 提供 SpeedTree 8 DOTS 风参数映射，并在 Player 构建中保留内部风捕获 Pass。BRG 包自己管理历史 HZB 与当前帧重测：历史在不透明渲染后生成，不要求完整预通道；重测会申请完整预通道深度。这替代了旧 GPU Batcher 对 `HiZDepthHistory` 的使用。

两个版本都使用当前相机剔除得到的原生 `CullingGroup` 结果处理烘焙遮挡。包围每个 chunk 边界的球体提供保守可见性判断，相比原实现的 AABB 查询可能保留更多候选；相机遮挡不会过滤阴影候选。SpeedTree 风通过保留的隐藏原生 `Tree` 代理、两个私有捕获 Pass 和 GPU 复制获取当前／历史参数，不逐帧向 CPU 回读风数据。这些路径使用现有原生 API，Unity 2022 仍缺少原实现的 wind-manager 与 Umbra AABB 查询绑定。算法范围、验证边界和剩余引擎接口差异见[移植说明](../Packages/Fraxinus-GPU-Batcher/Documentation~/Port.md)与[等价性审查](../Packages/Fraxinus-GPU-Batcher/Documentation~/EquivalenceAudit.md)。

## 常见配置问题

| 现象 | 检查项 |
| --- | --- |
| 缺少 `ScopeData` 或类型化 RenderGraph API | 确认 UltraRP 与 Core 来自本仓库同一提交，并检查旧 embedded 副本是否优先生效。 |
| UltraRP Shader Graph 内部访问或 Target 编译错误 | 确认 `com.unity.shadergraph` 解析到 `Fraxinus-ShaderGraph`，而不是官方包。 |
| GPU Batcher 找不到原生追踪或注入 API | 将 Core、FraxinusRP、Shader Graph 与 GPU Batcher 一起更新到同一提交。 |
| GPU Batcher 注册后物体消失 | 检查当前图形设备上的 `GPUBatcherRenderer.canSubmitDraws`。原版 Windows x64 Unity 2022 需使用 `-force-vulkan`、启用包内插件并安装配套 Fraxinus DOTS Shader。注册仍会接管原 Renderer／Terrain。 |
| 找不到包或 Git 路径无效 | 检查 GitHub 连接、`?path=/Packages/...` 是否与目录名称一致、所有引用的修订是否存在。 |
| 安装后仍使用原来的管线 | 检查 Graphics 与当前 Quality 的管线资产；安装包本身不会选择管线。 |
| 材质变粉或 UltraRP 画面为空 | 检查材质对应的管线、Renderer Graph 连接及绘制标签。URP Renderer Feature 不是 UltraRP 图节点。 |
| 包内安装文档指向其他仓库或检查点 | 使用本页的配套依赖配置。 |

[UltraRP 验证记录](../Packages/FraxinusRP/Documentation~/Validation.md)列出了测试环境和功能范围。接入时请验证项目场景、目标平台及 Player 构建。
