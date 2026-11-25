using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using FraxinusEngine.Rendering.Ultra;

using UnityEditor;
using UnityEditor.ProjectWindowCallback;

namespace FraxinusEditor.Rendering.Ultra
{
    static partial class PipelineFactor
    {
        [MenuItem("Assets/Create/FraxinusEngine/UltraRP/UltraRP Asset", priority = CoreUtils.Sections.section2 + CoreUtils.Priorities.assetsCreateRenderingMenuPriority + 1)]
        static void CreatUltraRenderPipeline()
        {
            ProjectWindowUtil.StartNameEditingIfProjectWindowExists(0, ScriptableObject.CreateInstance<DoCreateNewUltraRP>(), "New UltraRenderPipleineAsset.asset", null, null);
        }

        [MenuItem("Assets/Create/FraxinusEngine/UltraRP/Ultra Renderer", priority = CoreUtils.Sections.section3 + CoreUtils.Priorities.assetsCreateRenderingMenuPriority + 2)]
        static void CreateUltraScriptableData()
        {
            ProjectWindowUtil.StartNameEditingIfProjectWindowExists(0, ScriptableObject.CreateInstance<DoCreateNewUltraRenderer>(),
                "New Ultra Renderer.asset", null, null);
        }

        [MenuItem("Assets/Create/FraxinusEngine/UltraRP/Post-process Data", priority = CoreUtils.Sections.section5 + CoreUtils.Priorities.assetsCreateRenderingMenuPriority)]
        static void CreatePostProcessData()
        {
            ProjectWindowUtil.StartNameEditingIfProjectWindowExists(0, ScriptableObject.CreateInstance<CreatePostProcessDataAsset>(), "CustomPostProcessData.asset", null, null);
        }

        class DoCreateNewUltraRP : EndNameEditAction
        {
            public override void Action(int instanceId, string pathName, string resourceFile)
            {
                //Create asset
                AssetDatabase.CreateAsset(Create(CreateRendererAsset(pathName)), pathName);
            }
        }

        class DoCreateNewUltraRenderer : EndNameEditAction
        {
            public override void Action(int instanceId, string pathName, string resourceFile)
            {
                var instance = CreateRendererAsset(pathName, false);
                ProjectWindowUtil.ShowCreatedAsset(instance);
            }
        }

        static UltraRenderPipelineAsset Create(ScriptableRendererGraph rendererData = null)
        {
            var instance = ScriptableObject.CreateInstance<UltraRenderPipelineAsset>();

            instance.rendererGraphList[0] = (rendererData != null) ? rendererData : ScriptableObject.CreateInstance<ScriptableRendererGraph>();

            instance.conservativeEnclosingSphere = true;

            ResourceReloader.ReloadAllNullIn(instance, UltraRenderPipelineAsset.packagePath);

            ProjectWindowUtil.ShowCreatedAsset(instance);

            return instance;
        }

        static ScriptableRendererGraph CreateRendererAsset(string path, bool relativePath = true, string suffix = "Renderer")
        {
            ScriptableRendererGraph data = CreateUltraRendererGraph();
            string dataPath;
            if (relativePath)
                dataPath = $"{Path.Combine(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path))}_{suffix}{Path.GetExtension(path)}";
            else
                dataPath = path;

            AssetDatabase.CreateAsset(data, dataPath);
            ResourceReloader.ReloadAllNullIn(data, UltraRenderPipelineAsset.packagePath);
            return data;
        }

        static ScriptableRendererGraph CreateUltraRendererGraph()
        {
            var grap = ScriptableObject.CreateInstance<UltraRendererGraph>();
            grap.postProcessData = PostProcessData.GetDefaultPostProcessData();
            grap.InitPostProcessData();
            return grap;
        }

        internal class CreatePostProcessDataAsset : EndNameEditAction
        {
            public override void Action(int instanceId, string pathName, string resourceFile)
            {
                var instance = CreateInstance<PostProcessData>();
                AssetDatabase.CreateAsset(instance, pathName);
                ResourceReloader.ReloadAllNullIn(instance, UltraRenderPipelineAsset.packagePath);
                Selection.activeObject = instance;
            }
        }
    }
}