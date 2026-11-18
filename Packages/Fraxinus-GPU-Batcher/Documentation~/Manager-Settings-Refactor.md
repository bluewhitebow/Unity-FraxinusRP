# GPUInstancer Manager / Profile 拆分设计

状态：已实施

范围：把通道配置、运行时状态和项目级设置拆开，由 Profile 描述通道，由 Driver 创建并管理普通 C# Manager。

> 本包按绿地项目处理，不提供旧 Manager 资产、旧场景绑定或旧 API 的兼容与转换。升级到本结构时，需要重新创建 Profile，并重新填写 TerrainProxy 的 `profileId`。

---

## 1. 最终约束

- `GPUInstancerManager` 及 Prefab / Tree / Detail 派生类是普通 C# 运行时对象，实现 `IDisposable`。
- Manager 不继承 `ScriptableObject`，不是资产，也不能被序列化到场景或项目设置中。
- Manager 只能通过 `new` 或 Profile 的 `CreateManager()` 创建；不能使用 `CreateInstance`、`CreateAsset` 或 `CreateAssetMenu`。
- `GPUInstancerProjectSettings` 是唯一的项目级设置类型，统一入口是 `GPUInstancerConstants.projectSettings`。
- 通道配置只存放在 `GPUInstancerProjectSettings.runtimeConfig.profiles` 中。
- Terrain 场景绑定只保存稳定的 Profile ID，不保存 Manager 引用。
- Driver / Hub 对自己创建的 Manager 负责注销、停用和 `Dispose`。
- 包内不保留旧类型、旧字段、旧别名、兼容 carrier、转换窗口或自动扫描兜底。

以下内容明确不受支持：

- 旧 Prefab / Tree / Detail Manager `.asset`
- 旧场景中直接序列化的 Manager 引用
- `managerAssets`
- `detailManager` / `treeManager` 场景字段
- `GPUInstancerSettings`、`GPUInstancerSettingsExtension` 和 `gpuiSettings`
- 为上述内容提供的 `[Obsolete]` 转发成员、旧 GUID 占位类型或 `[MovedFrom]` 兼容路径

---

## 2. 目标结构

```text
GPUInstancerProjectSettings                 // 全局 ScriptableObject，唯一落盘配置
  └─ GPUInstancerRuntimeConfig              // [Serializable] 内嵌容器
       └─ List<GPUInstancerProfile>         // [SerializeReference] 多态列表
            ├─ GPUInstancerPrefabProfile  → CreateManager() → GPUInstancerPrefabManager
            ├─ GPUInstancerTreeProfile    → CreateManager() → GPUInstancerTreeManager
            └─ GPUInstancerDetailProfile  → CreateManager() → GPUInstancerDetailManager

GPUInstancerDriver.Bootstrap(projectSettings.runtimeConfig)
  → 为启用的 Prefab Profile 创建 Manager
  → TerrainProxy 按 Profile ID 惰性创建 Tree / Detail Manager
  → GPUInstancerManagerHub.Register(manager)

GPUInstancerManagerHub
  → Initialize / Activate / Tick / PrepareFrame / RenderForCamera
  → Deactivate / Dispose
```

这套结构只有 `GPUInstancerProjectSettings`、Prototype 等真正的配置数据是 Unity 资产。Manager 始终是一次运行会话内的瞬态对象。

---

## 3. 分层与职责

| 层 | 类型 | 形态 | 职责 |
|---|---|---|---|
| 项目设置 | `GPUInstancerProjectSettings` | `ScriptableObject` | shader bindings、SRP、bounds、编辑器选项；持有 `runtimeConfig` |
| 通道清单 | `GPUInstancerRuntimeConfig` | `[Serializable]` 普通类 | 保存多态 Profile 列表并按 ID 查询 |
| 通道配方 | `GPUInstancerProfile` 及派生类 | `[Serializable]` 普通类 | 保存通道参数并创建对应 Manager |
| 运行时引擎 | `GPUInstancerManager` 及派生类 | `IDisposable` 普通对象 | GPU 资源、实例数据、调度状态和运行时场景引用 |
| 创建与映射 | `GPUInstancerDriver` | 静态运行时服务 | Profile 到 Manager 的幂等映射、场景绑定和生命周期 |
| 调度 | `GPUInstancerManagerHub` | 静态运行时服务 | 注册集合和逐帧分发 |
| 场景绑定 | `GPUInstancerTerrainProxy` | `MonoBehaviour` | 保存 Tree / Detail Profile ID，把 Terrain 交给运行时 Manager |

配置与状态的边界：

- Profile 保存 prototype、culling、layer mask、通道开关等可编辑数据。
- Manager 保存 buffer、线程、Hi-Z、当前 Camera / Terrain、运行时数据和生命周期标志。
- Profile 不执行 Tick，不持有 ComputeBuffer，也不保存场景对象。
- Manager 可以读取 Profile，但不能把运行时变化写回 Profile 或 ProjectSettings 资产。

---

## 4. Profile 工厂

Profile 接口只约束工厂结果，不使用反射推断 Manager 类型：

```text
interface IGPUInstancerProfile
{
    bool enabled { get; }
    string displayName { get; }
    GPUInstancerManager CreateManager();
}

interface IGPUInstancerProfile<TManager> : IGPUInstancerProfile
    where TManager : GPUInstancerManager
{
    new TManager CreateManager();
}
```

每个具体 Profile 直接构造对应 Manager，并把自身作为唯一配置源注入。Driver 通过非泛型接口统一创建，调用方需要具体返回类型时使用泛型接口。

禁止通过 `GetInterfaces()`、`Activator.CreateInstance` 或 Unity 的 `ScriptableObject.CreateInstance` 猜测和创建 Manager。

---

## 5. Config 与持久化

`GPUInstancerRuntimeConfig` 是内嵌在 `GPUInstancerProjectSettings` 中的可序列化普通类，不单独创建资产：

```text
[Serializable]
class GPUInstancerRuntimeConfig
{
    [SerializeReference]
    List<GPUInstancerProfile> profiles;
}
```

使用 `List<GPUInstancerProfile>` 而不是接口列表，以满足 Unity 的序列化规则。每个 Profile 有一次性生成且持久化的稳定 ID，供场景组件引用。

Profile 类型名和命名空间发布后应保持稳定。由于本包不承担旧数据转换，破坏 Profile 的托管引用类型名时，调用方需要重新创建受影响的 Profile。

---

## 6. Driver 与生命周期

Driver 是 Manager 的所有者。标准流程如下：

1. 从 `GPUInstancerConstants.projectSettings.runtimeConfig` 取得 Config。
2. 遍历启用的 Prefab Profile，通过 `GetOrCreate(profile)` 创建唯一 Manager。
3. Tree / Detail Profile 等待 TerrainProxy 按 ID 请求后惰性创建。
4. 将 Manager 注册到 Hub，由 Hub 初始化、激活和逐帧调度。
5. 场景卸载、播放停止、脚本重载或显式 Shutdown 时，先停止回调和协程，再注销、停用并 `Dispose` Manager。
6. 清空 Profile 到 Manager 的映射以及所有反向场景引用。

`GetOrCreate(profile)` 对同一个 Profile 必须幂等。Driver 不扫描磁盘 Manager 资产，也不通过 `FindObjectsOfTypeAll<GPUInstancerManager>` 发现实例。

直接使用 API 创建的 Manager 同样是普通对象；若它不交给 Driver 托管，调用方必须显式 `Dispose`。

---

## 7. Terrain 场景绑定

项目设置不能稳定保存场景对象。`GPUInstancerTerrainProxy` 只序列化两个标识：

```text
string detailProfileId;
string treeProfileId;
```

绑定过程：

1. Proxy 启用时用 ID 从 `runtimeConfig` 查找 Profile。
2. 调用 `GPUInstancerDriver.GetOrCreate(profile)`。
3. 将当前 Terrain 交给对应 Tree / Detail Manager。
4. Proxy 禁用或销毁时解除各自通道绑定，不影响同一 Profile 的其他 Terrain。

Profile ID 缺失或无法解析时应给出清晰诊断并跳过绑定。不能回退到 Manager 引用、资产搜索或旧字段。

---

## 8. 编辑器工作流

所有通道编辑都集中在 `GPUInstancerProjectSettings` Inspector 的 Instancers 区域：

- 新增、删除、排序和启用 Prefab / Tree / Detail Profile。
- 展开 Profile 后显示原通道编辑界面。
- Terrain 菜单创建或选择 Profile，并把 Profile ID 写入 TerrainProxy。
- “Go to” 操作选中 ProjectSettings，并定位到对应 Profile。
- Simulate 通过 Driver 获取瞬态 Manager；停止预览和编辑器重载时释放它。

Editor foldout、选中项和模拟开关属于编辑器会话状态，不写回运行时 Manager，也不污染 Profile 资产。

编辑器不提供 Manager 资产创建入口，不显示 Manager 资产 Inspector，不提供旧数据转换按钮。

---

## 9. API 规则

- 全局设置只使用 `GPUInstancerConstants.projectSettings`。
- API 需要通道实例时接受 Profile，或接受由 Profile 创建的 Manager。
- 对外暴露的 Manager 参数与返回值表示运行时对象，不表示可持久化资产。
- API 不接受旧 Manager carrier，不读取 `managerAssets`，不为旧名称提供转发重载。
- 销毁 Unity 组件不能代替释放 Manager；生命周期终点必须调用 `Dispose`。

---

## 10. Samples

Samples 直接展示 Profile / 普通运行时 Manager 工作流：

- 示例场景不引用 Manager `.asset`。
- 示例脚本不使用 `CreateInstance<GPUInstancerManager>`。
- 三个改造示例都由调用方创建并持有临时 Profile / Manager，再通过 API 注册运行时 Manager。
- Tree 示例直接调用 `SetupManagerWithTerrain` 绑定 Terrain，不依赖 TerrainProxy 或项目 Config。
- 示例组件在 `OnDestroy` 中先注销 Manager，再在 `finally` 中显式 `Dispose`。
- Samples 中不保留 `ManagerAssets` 目录或旧 GUID 的 PPtr。

---

## 11. 验收标准

- `GPUInstancerManager` 的继承链中没有 `UnityEngine.Object` 或 `ScriptableObject`。
- Prefab / Tree / Detail Manager 均由 Profile 工厂或显式 `new` 创建。
- 包内没有 Legacy 命名空间、carrier、`managerAssets`、旧 Terrain Manager 字段或转换逻辑。
- 包内没有 `GPUInstancerSettings`、`GPUInstancerSettingsExtension`、`gpuiSettings` 兼容别名。
- Runtime、Editor 和 Samples 中没有因本次重构保留的 `[Obsolete]` 成员或 warning suppression。
- Play Mode 启动后，Hub 中的对象都是当前会话创建的运行时 Manager。
- 同一 Profile 只对应一个活动 Manager；多 Terrain 可以安全共享 Tree / Detail 通道。
- Shutdown 后没有活动回调、协程、静态 Manager 缓存或未释放 GPU 资源。
- Profile 配置不会被 Play Mode 的运行时状态写脏。
- 示例场景不存在旧 Manager GUID、旧字段或丢失脚本引用。
- Runtime 与 Editor 在 `CS0612`、`CS0618`、`CS0619` 视为错误时可编译。

---

## 12. 实现结论

1. Manager 是实现 `IDisposable` 的普通 C# 对象，绝不作为 Unity 资产存在。
2. ProjectSettings 是唯一项目级配置资产，Config 与多态 Profile 内嵌其中。
3. Driver 维护 Profile 到 Manager 的映射，并完整拥有托管实例的生命周期。
4. TerrainProxy 只持久化 Profile ID，Manager 引用仅存在于当前运行会话。
5. 主入口只有 `GPUInstancerConstants.projectSettings`。
6. 本包不兼容、不识别、也不转换旧 Manager 资产和旧场景绑定。
