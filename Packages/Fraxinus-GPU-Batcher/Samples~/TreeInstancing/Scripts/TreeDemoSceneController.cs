using Fraxinus.GPUBatcher;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Fraxinus.GPUBatcher.Samples
{
    public class TreeDemoSceneController : MonoBehaviour
    {
        [SerializeField]
        private Terrain terrain;
        [SerializeField]
        private GPUInstancerTerrainSettings treeSettings;
        [SerializeField]
        private List<GPUInstancerPrototype> treePrototypes = new List<GPUInstancerPrototype>();

        public Text GPUIStateText;
        public Text FPSCountTextText;

        private FPS _fpsCounter;
        private GPUInstancerTreeProfile _profile;
        private GPUInstancerTreeManager _manager;

        private void Awake()
        {
            _fpsCounter = GetComponent<FPS>();
        }

        private void Start()
        {
            QualitySettings.shadowDistance = 450;

            if (terrain == null)
                terrain = FindObjectOfType<Terrain>();
            if (terrain == null)
            {
                Debug.LogError("The Tree Instancing sample requires a Terrain.", this);
                return;
            }

            // Start runs after Driver's AfterSceneLoad bootstrap, so this
            // sample-owned transient channel cannot be swept as stale state.
            _profile = new GPUInstancerTreeProfile
            {
                displayName = "Tree Instancing Demo",
                terrainSettings = treeSettings,
                prototypeList = treePrototypes == null
                    ? new List<GPUInstancerPrototype>()
                    : new List<GPUInstancerPrototype>(treePrototypes),
                initializeWithCoroutine = true
            };
            _manager = _profile.CreateManager();
            if (_manager == null)
            {
                Debug.LogError("Could not create the Tree Instancing sample manager.", this);
                return;
            }

            _manager.SetupManagerWithTerrain(terrain);
            GPUInstancerAPI.RegisterManager(_manager);
        }

        private void Update()
        {
            FPSCountTextText.text = "FPS: " + _fpsCounter.FPSCount;
        }

        public void ToggleManager()
        {
            if (_manager == null || _manager.isDisposed)
                return;

            if (_manager.isActive)
                GPUInstancerAPI.UnregisterManager(_manager);
            else
                GPUInstancerAPI.RegisterManager(_manager);
            GPUIStateText.text = _manager.isActive ? "GPU Instancer ON" : "GPU Instancer OFF";
            GPUIStateText.color = _manager.isActive ? Color.green : Color.red;
        }

        private void OnDestroy()
        {
            GPUInstancerTreeManager manager = _manager;
            _manager = null;
            _profile = null;
            if (manager == null)
                return;

            try
            {
                GPUInstancerAPI.UnregisterManager(manager);
            }
            finally
            {
                manager.Dispose();
            }
        }
    }
}
