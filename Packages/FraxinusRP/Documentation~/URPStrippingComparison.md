# Fraxinus 与 URP 的 Shader 裁剪对比

参考实现为 URP 17.0.3 / Unity 6000；Fraxinus 侧的历史对照使用 Duckov 宿主工程与 Unity 2022.3.62f3。两者的构建 API、渲染功能和运行时关键词约定不同，裁剪规则必须结合 Fraxinus 的实际行为使用。

## 实现对应关系

| 环节 | URP 17 | Fraxinus |
| --- | --- | --- |
| 功能收集 | ShaderBuildPreprocessor 汇总管线和 Renderer | UltraRPShaderBuildFeatures 汇总目标平台的管线资产、声明节点与额外 Bundle 消费者 |
| 原生预过滤 | CreatePrefilteringSettings 与 ShaderKeywordFilter | UltraRPShaderPrefiltering 临时设置过滤字段，结束或中断后恢复原始字段和 dirty 状态 |
| 编译前裁剪 | ShaderScriptableStripper | UltraRPPostProcessShaderStripper 组合通用功能、后处理与已审查 Pass 规则；Compute 独立处理 |
| 必需状态与 OFF 变体 | 根据 URP Renderer 的执行保证裁剪 | 根据相机灯光约定及扩展声明证明；未知扩展、混合需求保留相应状态 |
| Player 与 Bundle | 构建和 Shader 预处理回调 | UltraRPShaderBuildSession 统一管理 Player、原生 Bundle、显式 scope 与 SBP |
| 增量缓存 | Unity/SBP 缓存输入 | 原生包装器和 SBP 适配器把裁剪配置、实现及输出纳入缓存依赖，继续复用 ShaderCache |
| 诊断 | Unity 编译日志与 Core 报告 | 每个 Shader/Pass/阶段记录实际输入、Core 输出、最终保留数及候选判定理由 |

全部消费者的功能需求取并集，允许禁用的状态取交集。所有目标资产都确认不需要某项功能，才能删除对应状态。缺失或未知实现不会被当作功能关闭。

## Fraxinus 的关键区别

- 主灯、附加灯、软阴影、SH、DBuffer、Forward Plus、Probe Volume 和渲染层分别分析，不能用一个宽泛的 Renderer 白名单替代各项证明。
- 软阴影、灯数、阴影距离和级联存在运行时变化；不能仅按编辑器当前值删除动态状态。
- 内部 HDR 渲染与 HDR 显示输出不同。输出规则不依赖 VolumeProfile 的后处理效果裁剪开关。
- 自定义 Shader 使用独立语义标签声明允许的规则；未知 Pass、ShaderGraph 或改变过实现的恢复副本保留未证明可删的组合。
- Vulkan/GLES 可能把联合程序标为 Vertex。需要按实际 Pass、平台和 Fragment 声明判断，不能把缺失声明直接当成关闭状态。
- Unity 2022.3 的隐式原生 Bundle 缺少可靠的独立 begin/end 回调，因此保持原生过滤中立，并在 managed 回调中重新检查配置。显式 scope 可以提前准备过滤且保证恢复。
- SBP 自动适配器负责会话与缓存失效；在此 Unity/SBP 版本上，获取已验证的原生预过滤收益仍应在最外层构建入口创建 scope。

实现细节见 [ShaderBuild.md](ShaderBuild.md)、[AssetBundleShaderBuild.md](AssetBundleShaderBuild.md)、[LightingKeywordContract.md](LightingKeywordContract.md)。逐项规则覆盖见 [StrippingCoverageAudit.md](StrippingCoverageAudit.md)。

## 验证与性能数据

以下为 Duckov 宿主工程的历史案例；游戏 Assets、验收工具和报告不随本包提供。该宿主的规则、生命周期、恢复和缓存曾通过 Unity Test Runner 验证，相关临时测试及辅助入口已清理。

2026-11-12 的 Duckov 完整 Android Vulkan 复测保留 44,854 个图形编译变体，全部命中本地缓存，新增编译为 0，图形编译批次耗时合计 6 分 08 秒。完整包耗时 68 分 11 秒，包含场景处理、资源写入、压缩、IL2CPP、Gradle 及资源恢复。托管裁剪报告 finalRetained 为 44,962，与编译器日志的统计范围不同。

这是已有缓存和当时机器负载下的观测，不是与 URP 的同场景冷缓存性能对比，也不能将全部耗时归因于 Shader。原始指标与设备复测记录由 Duckov 宿主另行保存。

评估后续优化时，应保持目标、场景、功能配置和缓存条件可比，同时记录原生过滤数量、脚本裁剪数量、缓存命中、新增编译和完整构建分项。不要通过清空 ShaderCache 验证功能正确性。
