# Player、AssetBundle 与 Addressables 的 Shader 构建

本文的 Unity 2022.3.62f3 / SBP 1.23.1 实测来自 Duckov 宿主工程的历史验证；该工程、测试 Shader 和构建产物不随本包提供。其他宿主应按自己的 Unity/SBP 版本与构建入口验证过滤时机。

`UltraRPShaderBuildSession` 统一管理构建数据。Player 在 `IPreprocessBuildWithReport` 中创建会话，正常结束时根据 BuildReport 清理。Shader 与 Compute 回调共用一个最早执行的预处理器；已知构建边界会提前准备关键字过滤字段。每次准备重新收集目标平台的 Graphics/Quality 管线资产，不能沿用上次构建的功能结论。字段已准备不代表所有 Unity 构建 API 都会消费原生过滤规则，应以实际日志为准。

实际 Unity 2022.3 原生 `BuildPipeline.BuildAssetBundles` 验证发现：连续同步调用可以复用 Shader 回调对象，也不提供独立 begin/end 回调。因此无显式范围的原生 Bundle 仅使用 managed stripping：派生的 native filter 字段置为保留所有状态，每个 Shader/Compute 批次重新检查当前项目配置哈希，需求变化时更新会话和规则。这样 OFF→ON→OFF 不会保留上次快照，也不会在下一次 native 展开之前误删新需要的状态。相同配置且没有 Editor update 的连续原生构建可能合并在同一诊断会话内；需要准确的逐构建报告和提前过滤时使用下方显式范围。

当时验证使用的 SBP 1.23.1 会创建并清理 Unity Shader 回调。平台切换还会触发 `activeBuildTargetChanged`，用于在 Unity 因 callback flags 相同而复用回调对象时提前重新准备过滤。不能只等第一个 `OnProcessShader`：那时原生变体展开已经开始。

可选 SBP 集成还接入标准 `PostScriptsCallback`：每次构建在目标切换/脚本编译之后、依赖计算之前重新准备，覆盖同一帧连续构建和 callback flags 相同的情况；`PostWritingCallback` 结束会话并恢复字段。显式范围拥有者保持会话直到范围释放。Core 的 Shader/Compute stripper 在管线匹配时始终注册，实际开关在每个会话中重新判断，避免第一次关闭裁剪使 Core 缓存永久排除该 stripper。

Unity 2022.3.62f3 + SBP 1.23.1、Android/Vulkan 单 API 实测中，自动模式的测试 Shader 在 managed 回调中仍收到 48 个变体，裁剪后为 1；Scaling Setup 为 12→4。在进入 `ContentPipeline.BuildAssetBundles` **之前**创建显式 scope 后，managed 输入已经分别缩减为 1 和 4，最终数量相同。相同输入/输出目录、缓存开启的 OFF→ON 构建均验证通过。由此确认自动 `PostScriptsCallback` 对本版本的 native 过滤准备时机过晚；自动模式仍有完整的 managed 裁剪与配置缓存失效，显式 scope 则同时获得已验证的 native 预过滤收益。原生 `BuildPipeline.BuildAssetBundles` 的显式范围也已验证会提前缩减枚举数量。

## 自定义构建脚本

原生 Bundle 推荐直接把 Unity 调用替换为以下入口；两种常用重载均支持，并在内部自动启用提前过滤和 finally 恢复：

```csharp
using FraxinusEditor.Rendering.Ultra;

var manifest = UltraRPAssetBundleBuilder.BuildAssetBundles(outputPath, builds, options, target);
// 或使用 Inspector 中已有的 AssetBundle 分组：
// UltraRPAssetBundleBuilder.BuildAssetBundles(outputPath, options, target);
```

包装器按输出目录保存上次成功构建的裁剪配置。首次使用、管线/Renderer/全局裁剪配置或实现变化、输出缺失或被其他构建修改时，自动添加 `ForceRebuildAssetBundle`；配置与输出匹配时允许 Unity 原生增量构建。只重新生成 Bundle，不清空 Shader 编译缓存。失败/中断前先使旧记录失效，成功且完成字段恢复后才写新记录；DryRun 不会认证缓存。记录在 `Library/FraxinusRP/NativeBundleConfigurations`，包含产物时间/长度及文本 manifest 内容哈希。

`additionalPipelineAssets` 是两个重载的可选末参数，用于声明消费这些 Bundle 的额外管线配置。目标必须已切换到请求的平台，并在 Graphics/Quality 中配置 Fraxinus。

若已有自定义 API 调度，需要异常路径立即恢复、准确逐构建报告或 SBP 自定义上下文，可使用可释放的范围：

```csharp
using FraxinusEditor.Rendering.Ultra;

// 先切到实际构建目标，声明所有消费这些 Bundle 的管线/Renderer 配置。
using (var scope = new UltraRPAssetBundleBuildScope(target, outputPath, additionalPipelineAssets))
{
    var manifest = BuildPipeline.BuildAssetBundles(outputPath, builds, options, target);
    scope.SetResult(manifest != null);
}
```

**SBP/Addressables 推荐在最外层构建入口之前创建 scope**，以获得提前减少枚举空间的收益；不要等到 `PostScripts` 或 `PostPacking` 回调中才创建。使用现有 SBP 参数和内容即可：

```csharp
using FraxinusEditor.Rendering.Ultra;
using UnityEditor.Build.Pipeline;

// target 已切换到 parameters.Target，outputPath 为实际输出目录。
using (var scope = new UltraRPAssetBundleBuildScope(target, outputPath, additionalPipelineAssets))
{
    var code = ContentPipeline.BuildAssetBundles(parameters, content, out var results);
    scope.SetResult(code >= ReturnCode.Success);
}
```

Addressables 的标准 SBP 构建也应将已有的同步 `AddressableAssetSettings.BuildPlayerContent(...)` 调用包进同样的 scope，根据 `AddressablesPlayerBuildResult.Error` 是否为空调用 `SetResult`。上述数值来自直接 SBP 实测；自定义 Addressables builder 需按自己的实际任务流程验证。无需关闭 SBP 缓存或清空 ShaderCache。

`SetResult` 只记录成功/失败；`Dispose` 无论结果如何都会还原临时过滤字段。传入的额外资产与项目质量档需求合并，不能替代 Graphics/Quality 中实际配置的 Fraxinus 管线，否则 Core 会按错误的 RenderPipeline 标签筛掉 Shader。范围拒绝嵌套、Player 构建中调用以及目标平台不匹配。

单独使用 scope 或裸 `BuildPipeline.BuildAssetBundles` 不会改变调用者传入的缓存选项；原生增量构建可能直接复用整个 Bundle，从而不调用 Shader stripper。裁剪配置变化后需自行添加 `ForceRebuildAssetBundle`，或使用上述包装器。标准 SBP/Addressables 由下方写入缓存集成处理。

没有显式范围时，Unity 没有通用的原生 AssetBundle 结束结果回调，因此同步构建返回后的 Editor update 清理；日志标记为 `bundle callbacks completed; build result unavailable`，不会伪称成功。连续同步构建在检测到配置变化时先清理旧状态。中断、异常或重载后，`Library/FraxinusRP/ShaderPrefilteringRecovery.json` 仍负责恢复原始派生字段和 dirty 状态。异步或自定义跨帧构建应使用显式范围并保持到实际结束。

## SBP 写入缓存

可选程序集 `Fraxinus.UltraRP.SBP.Editor` 仅在安装兼容 SBP 1.20–2.x 时启用。它接入标准 `ContentPipeline.BuildCallbacks.PostPackingCallback`，保留已有回调及失败结果，在 `WriteSerializedFiles` 查询缓存前更新每个写入操作的 `DependencyHash`。

额外哈希包括目标、图形 API、管线和 Renderer Graph 序列化配置、全局裁剪设置、启用效果裁剪时的 VolumeProfile/组件、Graphics/Quality Settings，以及裁剪实现、实际图节点/后处理条目和所有发现的模块/后处理/处理器所在程序集的 MVID（去重排序，并包含基类程序集）。未知扩展也包含在内，因此只改变自定义代码或声明属性、没有修改资产时，仍会使旧包缓存失效。`_prefilter*`、`_refilter*` 临时字段被排除；持久资产的实例引用转为 GUID/local file ID，避免仅因编辑器重启就改变哈希。规则或需求变化会重新写相关 Bundle，继续复用现有 Shader 编译缓存。

自定义 SBP 上下文若替换 `IScriptsCallback`/`IPackingCallback`，或任务列表省略对应 Post 回调，应使用显式构建范围，并在生成写入命令之后、`WriteSerializedFiles` 之前调用 `UltraRPShaderBuildCache.Apply(writeData)`。标准回调接入不能推断被第三方完全替换的任务流程。此集成没有修改 SBP/Core 包，也不清空 ShaderCache 或整个内容缓存。

构建报告位于 `Library/FraxinusRP/ShaderBuildReport.json`。它统计 managed callback 收到的变体，不冒充原生变体总数；原生预过滤数量仍以 Unity 构建日志中的 `After settings filtering` 为准。

Player 报告的 `status` 记录 `OnPostprocessBuild` 当时的 `BuildReport.summary.result`；Unity 2022.3 实测此时可能仍为 `Unknown`，最终成功/失败应以 `BuildPipeline.BuildPlayer` 返回的报告或 Unity 最终构建结果为准。
