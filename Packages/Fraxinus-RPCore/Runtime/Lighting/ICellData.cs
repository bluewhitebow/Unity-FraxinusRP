using System.Collections.Generic;
using Unity.Collections;
using static UnityEngine.Rendering.ProbeBrickIndex;

namespace UnityEngine.Rendering
{
    public interface IPerScenarioData
    {

    }

    public interface ICellData
    {
        IEnumerable<KeyValuePair<string, IPerScenarioData>> EnumerateScenarioEntries();

        bool TryGetScenarioEntry(string name, out IPerScenarioData data);

        bool RemoveScenarioEntry(string name);

        bool RenameScenarioEntry(string oldName, string newName);

        void CleanupPerScenarioData(IPerScenarioData data);

        void Cleanup(bool cleanScenarioList);
    }

    public interface ICellData<TPerScenario> : ICellData
        where TPerScenario : struct, IPerScenarioData
    {
        Dictionary<string, TPerScenario> scenarios { get; }
        NativeArray<Vector3> probePositions { get; set; }
        NativeArray<Brick> bricks { get; set; }

        bool TryGetPerScenario(string scenarioName, out TPerScenario data);
    }
}