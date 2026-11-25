using System.IO;
using UnityEngine;
using UnityEditor;

namespace FraxinusEditor.Base
{
    public class NodeGraphProcessorMenuItems
    {
        static readonly string s_nodeBaseName = "Node.cs";
        static readonly string s_nodeViewBaseName = "NodeView.cs";
        static string s_nodeTemplatePath = null;
        internal static string nodeTemplatePath
        {
            get
            {
                if (s_nodeTemplatePath == null)
                {
                    var template = Resources.Load<TextAsset>("NodeTemplate.cs");
                    s_nodeTemplatePath = AssetDatabase.GetAssetPath(template);
                }
                return s_nodeTemplatePath;
            }
        }

        static string s_nodeViewTemplatePath;
        internal static string nodeViewTemplatePath
        {
            get
            {
                if (s_nodeViewTemplatePath == null)
                {
                    var template = Resources.Load<TextAsset>("NodeViewTemplate.cs");
                    s_nodeViewTemplatePath = AssetDatabase.GetAssetPath(template);
                }

                return s_nodeViewTemplatePath;
            }
        }

        protected static class MenuItemPosition
        {
            public const int afterCreateScript = 81;
            public const int beforeCreateScript = 79;
        }

        protected static string GetCurrentProjectWindowPath()
        {
            var path = "";
            var obj = Selection.activeObject;

            if (obj == null)
                return null;
            else
                path = AssetDatabase.GetAssetPath(obj.GetInstanceID());

            if (path.Length > 0)
            {
                if (Directory.Exists(path))
                    return path;
                else
                    return new FileInfo(path).Directory.FullName;
            }
            return null;
        }

        protected static void CreateDefaultNodeCSharpScritpt()
        {
            ProjectWindowUtil.CreateScriptAssetFromTemplateFile(nodeTemplatePath, s_nodeBaseName);
        }

        protected static void CreateDefaultNodeViewCSharpScritpt()
        {
            ProjectWindowUtil.CreateScriptAssetFromTemplateFile(nodeViewTemplatePath, s_nodeViewBaseName);
        }
    }
}
