using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

namespace FraxinusEngine.Rendering.Ultra
{
    public class MaterialLibrary
    {
        static readonly FieldInfo[] s_shaderFields = typeof(PostProcessData.ShaderResources).GetFields();
        readonly Dictionary<string, Shader> _shaders = new Dictionary<string, Shader>();
        readonly Dictionary<string, Material> _materials = new Dictionary<string, Material>();
        readonly HashSet<string> _failedRequests = new HashSet<string>();
        
        public void Init(PostProcessData data)
        {
            Cleanup();
            if (data == null || data.shaders == null)
            {
                Debug.LogError("UltraRP postprocess shader resources are missing.");
                return;
            }
            
            foreach (var field in s_shaderFields)
                if (field.FieldType == typeof(Shader))
                    _shaders[field.Name] = field.GetValue(data.shaders) as Shader;
        }
        
        public Material GetMaterial(string materialName)
        {
            if (string.IsNullOrEmpty(materialName))
                return null;
            if (_materials.TryGetValue(materialName, out var material) && material != null)
                return material;
            if (_failedRequests.Contains(materialName))
                return null;

            // A Player may intentionally strip effects that its profiles never
            // declare. Allocate and validate only the effect actually requested.
            if (!_shaders.TryGetValue(materialName, out var shader) || shader == null || !shader.isSupported)
            {
                _failedRequests.Add(materialName);
                Debug.LogError("UltraRP requested an unavailable postprocess shader: " + materialName +
                    ". Check its resource, platform support, and the effects declared for shader stripping.");
                return null;
            }

            material = CoreUtils.CreateEngineMaterial(shader);
            _materials[materialName] = material;
            return material;
        }
        

        public bool HasMaterial(string materialName)
        {
            return !string.IsNullOrEmpty(materialName)
                && _shaders.TryGetValue(materialName, out var shader) && shader != null && shader.isSupported;
        }
        
       
        public IEnumerable<Material> GetMaterials()
        {
            foreach (var entry in _shaders)
                if (entry.Value != null && entry.Value.isSupported)
                    yield return GetMaterial(entry.Key);
        }
        
        public IEnumerable<Shader> GetShaders()
        {
            foreach (var shader in _shaders.Values)
                if (shader != null && shader.isSupported)
                    yield return shader;
        }
        
        internal void Cleanup()
        {
            foreach (var material in _materials.Values)
            {
                if (material != null)
                    CoreUtils.Destroy(material);
            }
            _materials.Clear();
            _shaders.Clear();
            _failedRequests.Clear();
        }
        
        internal void Reload(PostProcessData data)
        {
            Init(data);
        }
    }
}
