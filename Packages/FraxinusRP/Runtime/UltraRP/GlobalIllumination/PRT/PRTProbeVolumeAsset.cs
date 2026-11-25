using UnityEngine;

namespace FraxinusEngine.Rendering.Ultra
{
    /// <summary>
    /// Asset for storing PRT probe volume data
    /// </summary>
    [PreferBinarySerialization]
    public class PRTProbeVolumeAsset : ScriptableObject
    {
        [SerializeField] 
        private bool _hasValidData;

        [SerializeField, HideInInspector]
        private CellDataObsolute _cellData = CellDataObsolute.GetDefault();

        /// <summary>
        /// Whether volume asset has valid data
        /// </summary>
        public bool HasValidData => _hasValidData;

        /// <summary>
        /// Clear volume asset contained data
        /// </summary>
        public void Clear()
        {
            _hasValidData = false;
            _cellData = CellDataObsolute.GetDefault();
        }

        /// <summary>
        /// Get and set cell data in this asset
        /// </summary>
        public CellDataObsolute CellData
        {
            get => _cellData;
            set 
            { 
                _cellData = value;
                _hasValidData = true;
            }
        }
    }
}
