using System.Runtime.CompilerServices;

// Fraxinus.ObjectTracking.asmdef has a descriptive filename but must keep the
// assembly identity Unity.InternalAPIEngineBridge.024: CoreModule metadata in
// Unity 2022.3.62f3 and 6000.0.85f2 explicitly grants this name access to ObjectDispatcher.
// The previous Universal.2D friend name differs between those engines. This common
// engine bridge keeps one native implementation; recheck InternalsVisibleTo when
// adopting another Unity version. It lives in Core so both consumers share tracking.
[assembly: InternalsVisibleTo("Fraxinus.Runtime")]
[assembly: InternalsVisibleTo("Fraxinus.GPUBatcher.Runtime")]
