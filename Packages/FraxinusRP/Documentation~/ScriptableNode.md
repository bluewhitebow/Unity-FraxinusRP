# ScriptableNode on Unity 2022.3

ScriptableNode lives in `Runtime/Base/ScriptableNode` and `Editor/Base/ScriptableNode`. Runtime types use the `FraxinusEngine.Base` namespace in `Fraxinus.Runtime`; graph windows use `FraxinusEditor.Base` in `Fraxinus.Editor`. The Editor layer uses Unity 2022's experimental GraphView API. Runtime graphs do not depend on GraphView.

## Creating and executing graphs

Create a `BaseGraph` ScriptableObject, instantiate nodes through `BaseNode.CreateFromType<T>`, add them to the graph, then connect the destination input port to the source output port:

```csharp
var graph = ScriptableObject.CreateInstance<BaseGraph>();
var source = BaseNode.CreateFromType<MySource>(Vector2.zero);
var sink = BaseNode.CreateFromType<MySink>(new Vector2(200, 0));
graph.AddNode(source);
graph.AddNode(sink);
graph.Connect(sink.GetPort(nameof(MySink.input), null),
              source.GetPort(nameof(MySource.output), null));
graph.UpdateComputeOrder();
using (var processor = new ProcessGraphProcessor(graph))
    processor.Run();
```

Ports are fields derived from `NodeParameter`, marked `[Input]` or `[Output]`. `Process` reads and writes their values; the processor transfers values between dependent nodes. Dispose processors when their owner closes or switches graphs, because they subscribe to graph changes. Asset owners are responsible for saving and destroying their graphs.

`JobGraphProcessor` currently derives from `ProcessGraphProcessor`. Its `usesSynchronousFallback` property is `true`; `Run()` executes managed nodes synchronously in dependency order and returns with completed outputs. It does not schedule Burst or parallel Jobs. A real Jobs implementation needs a separate unmanaged node execution contract.

## Connections and type adapters

Compatibility is evaluated from output type to input type regardless of which end starts an Editor drag. The default path accepts an input assignable from the output, including a derived parameter flowing into its base parameter. An unsafe base-to-derived connection is rejected. Editor connections also reject reversed directions, self-connections and duplicates.

An `ITypeAdapter` extension can provide a static conversion method taking source and destination parameters. Conversion methods must be non-generic `void` methods with exactly two ordinary parameters, without `ref`, `out` or pointer parameters. The target parameter object is updated in place. Reverse conversion is optional and requires its own method. Language-level implicit/explicit operators are not automatically port converters.

Adapter discovery uses deterministic type ordering. A concrete adapter requires a public parameterless constructor. Abstract/open generic adapters are skipped; constructor/enumerator failures, invalid type declarations or conversion signatures, and duplicate conversion pairs reject the whole adapter with a diagnostic warning. Failed registration does not leave partial rules or stop unrelated adapters loading. Every ordinary static method declared on an adapter is treated as a conversion method, so keep unrelated static helpers outside the adapter type.

The existing `GetCompatibleTypes`/`GetIncompatibleTypes` declarations remain symmetric compatibility rules. A compatibility declaration alone does not implement data transfer: provide a conversion method or the corresponding custom port I/O behavior.

Mono uses compiled expression delegates for normal transfers. IL2CPP uses field reflection and direct parameter assignment or reflected adapter invocation, avoiding runtime expression compilation. Both transfer paths read the current port objects, including fields replaced after connecting.

## Editing, persistence and lifecycle

A `DefaultGraphWindow` can display a `BaseGraph` through `InitializeGraph`. Derived windows can provide their own GraphView and toolbar. Derived lifecycle overrides should call the base implementations so event subscriptions, pinned panels, processors and inspector objects are released.

Copying nodes includes their internal edges even when the edges were not selected. Copying a group includes its member nodes. Paste assigns fresh node GUIDs, remaps group members and internal endpoints, and works across graphs; edges to nodes outside the copied set are excluded. Copy preserves the current selection. Paste and existing-edge reconnect support Undo/Redo. Reconnection takes its Undo snapshot before removing the previous connection. The edge creation menu filters candidates in output-to-input order for either drag direction and retains the captured port after the drag helper clears its temporary edge. Each listener owns its menu; graph release destroys that menu and clears its window/port references.

Graph deserialization rebuilds GUID indexes and explicitly binds each edge to the receiving graph. This matters for `JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(source), copy)`: serialized Unity object references must not make the copied edges resolve to the source graph. Call `copy.Deserialize()` and update compute order after overwriting an already-created graph. Save/load and `AssetDatabase.CopyAsset` also rebuild independent node/edge state.

Deleting a node removes incident edges and group membership. GraphView's explicit save marks the graph dirty and saves that persistent asset. Window disable, close, graph switch and domain reload release old view callbacks before rebuilding. Node inspectors belong to each view; node-menu caches count active graph users so closing one window does not invalidate another window's cache.

## UltraRP render contexts

`ContextBeginNode` and `ContextEndNode` delimit a render scope within an UltraRP graph. Keep the camera's `RenderDriven` setup before the first Begin, and connect camera-target ports in execution order. For a simple scope, connect the parent target to `Begin.inCameraTarget`, `Begin.outCameraTarget` through the child rendering passes, and the last child target to `End.inCameraTarget`. Continue parent rendering from `End.outCameraTarget`.

| Port | Value |
| --- | --- |
| `ContextBeginNode.outCameraTarget` | The new child color and depth targets. |
| `ContextEndNode.outColorTexture` | The finished child color supplied to End, including a replacement target produced by a child pass. |
| `ContextEndNode.outCameraTarget` | The restored parent's active color and depth targets. |

End also publishes the finished child color as `_SubContextColor`. This global names the most recently completed scope; retain the explicit color port when several scope outputs are needed. Ending a scope does not composite its color into its parent. Use a texture-blit or composition node with the child color as source and the restored parent target as destination.

Scopes may nest. For example, connect `Begin outer -> child pass -> Begin inner -> inner pass -> End inner`, continue the outer passes from `End inner.outCameraTarget`, then connect their final target to `End outer`. Every End restores its immediate parent. Begin/End pairs must be balanced in the graph's execution order; an unmatched End or an unfinished scope is reported as an error. Connect scope transitions explicitly instead of relying on the order of unrelated graph branches.

Each Begin scales the dimensions of its input color target by its configured factor, clamped to 0.1 through 1.0, with a minimum size of one pixel. Consequently, nested factors multiply: two 0.5 scopes below a 256 by 128 target render at 128 by 64 and 64 by 32. Child targets use one sample and explicit dimensions without dynamic resolution scaling. `Clear` initializes a fresh child target. `CopyFromMain` copies the current parent's color into the child; inside a nested scope, this means the enclosing child rather than the original main camera. Depth remains local to the new scope.

Each Begin retains separate `CameraData`, `ResourceData`, and `RenderingData` instances. Child changes to render flags or camera settings do not mutate those parent instances. The Unity camera, view, culling results and read-only lighting inputs are reused: these scopes provide another render target for the same view, not an independently culled camera. Per-scope buffers and retained data are rebound for each frame.

Pass implementations use `frameData.Get<T>()` for both scoped and shared data. The frame container reads its current borrowed scope directly and falls back to its owned items for types not bound in that scope. Begin/End switch that binding, so consumers do not query the manager. `Contains<T>()` and `GetOrCreate<T>()` follow the same lookup; creation belongs to the frame container. `ContextStateManager.Get<T>()` reads only the current scope and returns null for an absent item. The generic `ContextData` storage inherits Core's `ContextContainer.ScopeData` and borrows references; it does not clone data. `Clear` and manager `Reset` release bindings without resetting borrowed objects, and frame disposal resets only the frame's owned items. Isolation comes from Begin supplying distinct mutable instances. Warmed lookup and scope switching reuse storage; initialization and capacity growth may allocate.

Injection uses the `InjectPassAttribute.scopeFilter` flags: `Main` permits the main context, `Sub` permits every child context, `All` permits both, and `None` permits neither. Begin's `Auto` mode honors these flags. `Disabled` suppresses injected passes throughout that child scope and its descendants, including a nested Begin configured as `Auto`; it does not suppress explicitly connected graph nodes. An injector runs at most once per context per frame, even when several nodes are eligible attachment points. Returning from a child restores the parent's injection policy.

Module types and injection attributes are discovered once per scripting domain; each Renderer creates its own module instances and caches node bindings when the graph changes. `finalInjectPostPass` adds one entry per injector to the Renderer's ordered fallback queue. Before PostProcess, that queue runs only injectors that have not run in the current context, including those whose normal node is unsupported for the current frame. If all eligible fallback injectors already ran, the queue is skipped. The graph shows normal bindings at their nodes and shows `InjectedPasses(Fallback)` at PostProcess only for injectors without a normal binding in that scope.

Camera histories remain owned by the main camera. Children have no history manager, TAA history or STP history, and use no temporal jitter or camera antialiasing. They do not update the main GPU-occlusion history, capture camera output, or resolve a final presentation target. Put main-camera temporal processing and presentation after the outer End. Supporting temporal effects independently inside a child would require history allocated and updated for that scope and its dimensions.

Begin and End record the current camera matrices and screen dimensions, and bind the scope's depth, normal, rendering-layer, motion and DBuffer textures. Missing textures are rebound to the graph's black fallback to avoid retaining a completed child's resources. Clustered deferred scopes build their own light grid at their target dimensions while reusing the same view's light inputs; End restores the parent's light-grid buffers and constants. Decal passes read the current scope, and End restores the parent's DBuffer bindings, DBuffer MRT keywords and decal-layer keyword. These rules apply at each nested boundary, including a parent with no DBuffer resources.

Historical context-storage and nested-rendering results are recorded in [Validation.md](Validation.md). The DBuffer scenario covered target dimensions and restored keywords, not every decal-projector or material appearance.

## Project extensions and IL2CPP preservation

`Runtime/link.xml` preserves `Fraxinus.Runtime`, and the package's linker processor explicitly supplies that file to UnityLinker. Project-defined nodes, parameters, adapters and reflected callbacks in other assemblies need their own preservation rules. See [IL2CPP and managed stripping](IL2CPP.md) for the package integration and project `Assets/link.xml` example.

A type-level `[Preserve]` retains the type and its default constructor; it does not retain every reflected field and callback. A typical project node declares preservation on its ports and processing override as well:

```csharp
using System;
using FraxinusEngine.Base;
using UnityEngine.Scripting;

[Serializable, Preserve]
public sealed class MySource : BaseNode
{
    public int value = 17;
    [Output, NonSerialized, Preserve] public IntNodeParameter output;
    [Preserve] protected override void Process() => output.value = value;
}
```

Also preserve project custom port methods, custom behavior callbacks and adapter conversion methods discovered through reflection. Alternatively, preserve the relevant extension types with `preserve="all"` in a project `Assets/link.xml`. Explicitly referenced built-in parameter types are covered by the package rules. Arbitrary types or closed generic instantiations created only from runtime strings still require project-specific IL2CPP preservation/code-generation validation.

## Historical validation

The Unity 2022 migration checkpoints exercised dependency execution, graph cloning,
adapters, editing, Undo/Redo and a real script-domain reload. Windows IL2CPP runs
also exercised preserved project nodes. These results cover the tested scenarios;
project callbacks, custom adapters and manual graph-window interactions still need
their own checks. The temporary validation entry points have been removed.
See [Validation.md](Validation.md) for historical results and source-version limits.
