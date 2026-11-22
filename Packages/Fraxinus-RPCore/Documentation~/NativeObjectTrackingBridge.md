# Native object tracking across Unity versions

`Runtime/SceneLightTracking/Fraxinus.ObjectTracking.asmdef` owns the
ObjectDispatcher wrapper and scene-light tracking code. Both Fraxinus RP and GPU
Batcher reference this one assembly; there is no reflection-based native access
or second implementation to keep synchronized.

The filename describes its purpose. Its internal `name` must remain
`Unity.InternalAPIEngineBridge.024`: Unity grants native access to that exact
assembly identity. Renaming the file does not rename the compiled assembly.

The `UnityEngine.CoreModule.dll` metadata shipped with **2022.3.62f3** and
**6000.0.85f2** was inspected directly. Both contain the exact attribute:

```csharp
[assembly: InternalsVisibleTo("Unity.InternalAPIEngineBridge.024")]
```

This was also checked in each editor's Windows x64 Mono Development and
non-Development Player CoreModule assemblies; all four include the same friend.

The old `Unity.RenderPipelines.Universal.2D.Internal` identity is a friend in
2022.3 but changes to `Unity.RenderPipelines.Universal.2D.Runtime` in 6000.0.
Using the common engine-bridge identity avoids version-dependent asmdef names.
Its existing asset GUID is preserved so GUID references survive the rename.

When adding support for another Unity editor, check that editor's CoreModule
friend list and ObjectDispatcher signatures, and check package assembly names
for collisions with the reserved bridge identity. Then compile and exercise
native create/change/destroy and transform snapshots in Editor and Player.
The metadata evidence above establishes only the two listed engine versions.
