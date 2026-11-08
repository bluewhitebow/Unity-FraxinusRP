# Duckov 适配回同步（2026-11-08）

来源为 `Duckov-Recovered/ExportedProject/Packages`，Git 提交
`a42451f9154c187ce018376af694d2a218066b91`。按包 ID 对应到本仓库的四个包，
提取功能适配，保留原有包目录、版本、资源 GUID 和 URP 迁移能力。

| 来源包 ID | 本仓库目录 | 同步范围 |
| --- | --- | --- |
| `com.bluewhitebow.fraxinus-rp` | `Packages/FraxinusRP` | 27 个已有文件、9 个新增源码或 Shader 及其 `.meta` |
| `com.unity.render-pipelines.core` | `Packages/Fraxinus-RPCore` | 5 个运行时文件 |
| `com.unity.shadergraph` | `Packages/Fraxinus-ShaderGraph` | 无功能差异，保持原样 |
| `com.fraxinus.rendering.light-transport` | `Packages/Fraxinus-Light-Transport` | 无功能适配，保留原有程序集访问权限 |

## 同步内容

- Cluster Deferred 增加光栅光照与 Framebuffer Fetch 路径，与 Compute 路径共享光照实现；补齐 Cookie 纹理依赖、方向光数量和 Specular workflow 解码。
- Unity 2022 / DX11 的延迟渲染兼容修复：限制 Framebuffer Fetch、拆分提交边界，修复旧版 Native RenderPass 的 depth/stencil 绑定。深度复制在录制时判断目标方向，避免执行时访问未分配的瞬态附件。
- TAA / STP 初始化先确认主 Context 中存在启用的 TAA Pass；调试模式禁用后处理时不再启用 TAA。
- Decal 创建、删除及属性更新等待相关 Job；延后创建预览 Pass，避免反序列化线程调用 Unity API；逐相机清理 DBuffer 关键字并修正清理 Shader 的管线标签。
- Unity 2022 屏幕空间 UI 通过 `DrawUIOverlay` 绘制，声明并绑定图中的颜色与深度目标。
- 新增 Color Lookup 与 Panini Projection Volume 组件、Panini 渲染 Pass，以及 `PostProcessPass.beforeFinalResolve` 扩展事件。
- 开放 `supportsSoftShadows` 的 setter 和 `rendererIndex` getter，便于运行时画质设置及相机集成。
- Core 的 Rendering Layer 反射回退指向 UltraRP，资源查找新增 FraxinusRP 路径，明确 Profiling Recorder 类型；`VolumeProfile.isDirty` 恢复为公开字段，兼容旧插件的 `ldfld/stfld`，同时维护细分脏状态。

## 配置变化

`DeferredObjectPass.clusterLightingMode` 默认为 `Auto`：Framebuffer Fetch 可用时选择光栅路径，否则使用 Compute。
也可显式选择 `Raster` 或 `Compute`。Unity 2022 / DX11 禁用 Framebuffer Fetch，但显式 `Raster` 仍可使用普通 GBuffer 采样。

需要 TAA / STP 的 Renderer Graph 应启用主 Context 的 TAA 后处理项。
需要在延迟光照之后补画 ForwardOnly 材质的 DrawObjects 节点可开启 `forwardOnlyInDeferred`；Overlay 相机仍沿用完整前向标签列表。
Color Lookup 和 Panini 默认 Volume 参数均不启用效果。

## 保留与排除

- 保留 `UltraAdditionalLightData.Migration.cs`、运行时迁移入口及 Editor 初始化调用；来源为了移除 URP 而删除的迁移能力没有带回。
- 保留 Core 和 Light Transport 对 URP 的 `InternalsVisibleTo`；它们不构成新增包依赖。Core 在新增 FraxinusRP 资源根目录后，继续保留原有 URP 资源查找回退。
- 保留 Core、Shader Graph 的 `Tests/` 布局；来源仅将相同内容改名为 `UpstreamTests~/`。
- 忽略纯换行变化、测试 `obj` 缓存和机器路径。不引入项目 manifest、锁文件、场景、材质、Collections、A* 或 Shader Variant Collection Tool 等独立包。

## 本次验证

- 新增 9 个 `.meta` GUID 在四包中无冲突；新增 Shader 资源路径存在，递归检查的 177 条 Shader include 可解析。
- `git -c core.whitespace=cr-at-eol diff --check` 通过；该选项适配仓库已有的 CRLF 行尾。
- 使用 Unity 2022.3.62f3 自带 Roslyn、来源工程的 Bee 宏和引擎引用，对当前仓库 978 个 C# 文件、8 个 Core / Fraxinus Runtime 与 Editor 程序集完成离线编译，0 错误。包含保留的 URP 迁移文件和全部新增 C#；按依赖顺序引用本次生成的程序集。46 条警告均为未使用或未赋值字段。
- 使用实际 `VolumeProfile.cs` 和最小 Unity stubs，runtime / `UNITY_EDITOR` 两种构建各通过 28 个断言，覆盖字段 ABI、动态 IL 读写、Add/Remove/Reset 及脏状态清理。

验证脚本与输出位于被 Git 忽略的 `.validation/`：`compile_packages.py`、`package-compile/report.json` 和 `VolumeProfileHarness/`。
没有启动 Unity Editor 导入流程或执行 IL 后处理；这些检查不代表 Shader 实际编译、GPU 画面回归或 Player 构建已通过。
