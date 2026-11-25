using UnityEngine;
using UnityEditor;
using FraxinusEngine.Rendering.Ultra;

namespace FraxinusEditor.Rendering.Ultra
{
    [CustomEditor(typeof(PRTProbeVolumeAsset))]
    internal class PRTVolumeAssetEditor : PropertyFetchEditor<PRTProbeVolumeAsset>
    {
        private CellDataObsolute CellData => _target.CellData;

        public override void OnInspectorGUI()
        {
            using (new EditorGUILayout.VerticalScope("box"))
            {
                DrawDataSizeInfo();
            }
        }

        private void DrawDataSizeInfo()
        {
            // Calculate sizes for each data type
            var surfelSize = CalculateSurfelDataSize();
            var brickSize = CalculateBrickDataSize();
            var factorSize = CalculateFactorDataSize();
            var probeSize = CalculateProbeDataSize();
            var validitySize = CalculateValidityDataSize();
            var totalSize = surfelSize + brickSize + factorSize + probeSize + validitySize;

            // Display counts
            EditorGUILayout.LabelField("Data Counts:", EditorStyles.miniBoldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"Surfels: {GetArrayLength(CellData.surfels):N0}", GUILayout.Width(120));
                EditorGUILayout.LabelField($"Bricks: {GetArrayLength(CellData.bricks):N0}", GUILayout.Width(120));
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"Factors: {GetArrayLength(CellData.factors):N0}", GUILayout.Width(120));
                EditorGUILayout.LabelField($"Probes: {GetArrayLength(CellData.probes):N0}", GUILayout.Width(120));
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"Validity Masks: {GetArrayLength(CellData.validityMasks):N0}", GUILayout.Width(160));
            }

            EditorGUILayout.Space(5);

            // Display sizes
            EditorGUILayout.LabelField("Memory Usage:", EditorStyles.miniBoldLabel);

            using (new EditorGUILayout.VerticalScope())
            {
                DrawSizeRow("Surfels", surfelSize, Surfel.stride, GetArrayLength(CellData.surfels));
                DrawSizeRow("Bricks", brickSize, 8, GetArrayLength(CellData.bricks));
                DrawSizeRow("Factors", factorSize, 8, GetArrayLength(CellData.factors));
                DrawSizeRow("Probes", probeSize, 8, GetArrayLength(CellData.probes));
                DrawSizeRow("Validity", validitySize, 4, GetArrayLength(CellData.validityMasks));

                EditorGUILayout.Space(3);
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("Total Size:", EditorStyles.boldLabel, GUILayout.Width(80));
                    EditorGUILayout.LabelField(FormatBytes(totalSize), EditorStyles.boldLabel);
                }
            }

            EditorGUILayout.Space(5);

            // Display additional statistics
            DrawStatistics();
        }

        private static void DrawSizeRow(string label, long totalBytes, int structSize, int count)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"{label}:", GUILayout.Width(60));
                EditorGUILayout.LabelField(FormatBytes(totalBytes), GUILayout.Width(80));
                EditorGUILayout.LabelField($"({structSize} bytes × {count:N0})", EditorStyles.miniLabel);
            }
        }

        private void DrawStatistics()
        {
            if (GetArrayLength(CellData.probes) == 0) return;

            EditorGUILayout.LabelField("Statistics:", EditorStyles.miniBoldLabel);

            // Calculate average factors per probe
            float avgFactorsPerProbe = (float)GetArrayLength(CellData.factors) / GetArrayLength(CellData.probes);

            // Calculate average surfels per brick
            float avgSurfelsPerBrick = GetArrayLength(CellData.bricks) > 0 ?
                (float)GetArrayLength(CellData.surfels) / GetArrayLength(CellData.bricks) : 0;

            using (new EditorGUILayout.VerticalScope())
            {
                EditorGUILayout.LabelField($"Avg Factors per Probe: {avgFactorsPerProbe:F1}");
                EditorGUILayout.LabelField($"Avg Surfels per Brick: {avgSurfelsPerBrick:F1}");
            }
        }

        private long CalculateSurfelDataSize()
        {
            return GetArrayLength(CellData.surfels) * Surfel.stride;
        }

        private long CalculateBrickDataSize()
        {
            // SurfelIndices: int start (4) + int end (4) = 8 bytes
            return GetArrayLength(CellData.bricks) * 8;
        }

        private long CalculateFactorDataSize()
        {
            // BrickFactor: int brickIndex (4) + float weight (4) = 8 bytes
            return GetArrayLength(CellData.factors) * 8;
        }

        private long CalculateProbeDataSize()
        {
            // FactorIndices: int start (4) + int end (4) = 8 bytes
            return GetArrayLength(CellData.probes) * 8;
        }

        private long CalculateValidityDataSize()
        {
            // float: 4 bytes per probe
            return GetArrayLength(CellData.validityMasks) * 4;
        }

        private static int GetArrayLength<T>(T[] array)
        {
            return array?.Length ?? 0;
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes == 0) return "0 B";

            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            int suffixIndex = 0;
            double size = bytes;

            while (size >= 1024 && suffixIndex < suffixes.Length - 1)
            {
                size /= 1024;
                suffixIndex++;
            }

            return $"{size:F2} {suffixes[suffixIndex]}";
        }
    }
}
