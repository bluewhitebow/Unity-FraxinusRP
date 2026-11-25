using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.UIElements;
using FraxinusEngine.Base;
using UnityEditor;
using UnityEditor.Experimental.GraphView;

namespace FraxinusEditor.Base
{
    class CreateNodeMenuWindow : ScriptableObject, ISearchWindowProvider
    {
        BaseGraphView   _graphView;
        EditorWindow    _window;
        Texture2D       _icon;
        EdgeView        _edgeFilter;
        PortView        _inputPortView;
        PortView        _outputPortView;

        public void Initialize(BaseGraphView graphView, EditorWindow window, EdgeView edgeFilter = null)
        {
            this._graphView = graphView;
            this._window = window;
            this._edgeFilter = edgeFilter;
            this._inputPortView = edgeFilter?.input as PortView;
            this._outputPortView = edgeFilter?.output as PortView;

            // Transparent icon to trick search window into indenting items
            if (_icon == null)
                _icon = new Texture2D(1, 1);

            _icon.SetPixel(0, 0, new Color(0, 0, 0, 0));
            _icon.Apply();
        }

        void OnDestroy()
        {
            _graphView = null;
            _window = null;
            _edgeFilter = null;
            _inputPortView = null;
            _outputPortView = null;
            if (_icon != null)
            {
                DestroyImmediate(_icon);
                _icon = null;
            }
        }

        public List<SearchTreeEntry> CreateSearchTree(SearchWindowContext context)
        {
            var tree = new List<SearchTreeEntry>
            {
                new SearchTreeGroupEntry(new GUIContent("Create Node"), 0),
            };

            if (_graphView == null || _graphView.graph == null || _window == null)
                return tree;
            if (_edgeFilter == null)
                CreateStandardNodeMenu(tree);
            else
                CreateEdgeNodeMenu(tree);

            return tree;
        }

        void CreateStandardNodeMenu(List<SearchTreeEntry> tree)
        {
            // Sort menu by alphabetical order and submenus
            var nodeEntries = _graphView.FilterCreateNodeMenuEntries().OrderBy(k => k.path);
            var titlePaths = new HashSet<string>();
            
            foreach (var nodeMenuItem in nodeEntries)
            {
                var nodePath = nodeMenuItem.path;
                var nodeName = nodePath;
                var level    = 0;
                var parts    = nodePath.Split('/');

                if(parts.Length > 1)
                {
                    level++;
                    nodeName = parts[parts.Length - 1];
                    var fullTitleAsPath = "";
                    
                    for(var i = 0; i < parts.Length - 1; i++)
                    {
                        var title = parts[i];
                        fullTitleAsPath += title;
                        level = i + 1;
                        
                        // Add section title if the node is in subcategory
                        if (!titlePaths.Contains(fullTitleAsPath))
                        {
                            tree.Add(new SearchTreeGroupEntry(new GUIContent(title)){
                                level = level
                            });
                            titlePaths.Add(fullTitleAsPath);
                        }
                    }
                }
                
                tree.Add(new SearchTreeEntry(new GUIContent(nodeName, _icon))
                {
                    level    = level + 1,
                    userData = nodeMenuItem.type
                });
            }
        }

        void CreateEdgeNodeMenu(List<SearchTreeEntry> tree)
        {
            // The drag helper clears the transient edge after opening the search window.
            // Keep using the endpoints captured by Initialize when the tree is rebuilt.
            var port = _inputPortView ?? _outputPortView;
            if (port == null)
                return;
            var entries = NodeProvider.GetEdgeCreationNodeMenuEntry(port, _graphView.graph);

            var titlePaths = new HashSet<string>();

            var nodePaths = NodeProvider.GetNodeMenuEntries(_graphView.graph);

            //tree.Add(new SearchTreeEntry(new GUIContent($"Relay", _icon))
            //{
            //    level = 1,
            //    userData = new NodeProvider.PortDescription{
            //        nodeType = typeof(RelayNode),
            //        portType = typeof(System.Object),
            //        isInput = _inputPortView != null,
            //        portFieldName = _inputPortView != null ? nameof(RelayNode.output) : nameof(RelayNode.input),
            //        portIdentifier = "0",
            //        portDisplayName = _inputPortView != null ? "Out" : "In",
            //    }
            //});

            var sortedMenuItems = entries.Select(port => (port, nodePaths.FirstOrDefault(kp => kp.type == port.nodeType).path)).OrderBy(e => e.path);

            // Sort menu by alphabetical order and submenus
            foreach (var nodeMenuItem in sortedMenuItems)
            {
                var nodePath = nodePaths.FirstOrDefault(kp => kp.type == nodeMenuItem.port.nodeType).path;

                // Ignore the node if it's not in the create menu
                if (String.IsNullOrEmpty(nodePath))
                    continue;

                var nodeName = nodePath;
                var level    = 0;
                var parts    = nodePath.Split('/');

                if (parts.Length > 1)
                {
                    level++;
                    nodeName = parts[parts.Length - 1];
                    var fullTitleAsPath = "";
                    
                    for (var i = 0; i < parts.Length - 1; i++)
                    {
                        var title = parts[i];
                        fullTitleAsPath += title;
                        level = i + 1;

                        // Add section title if the node is in subcategory
                        if (!titlePaths.Contains(fullTitleAsPath))
                        {
                            tree.Add(new SearchTreeGroupEntry(new GUIContent(title)){
                                level = level
                            });
                            titlePaths.Add(fullTitleAsPath);
                        }
                    }
                }

                tree.Add(new SearchTreeEntry(new GUIContent($"{nodeName}:  {nodeMenuItem.port.portDisplayName}", _icon))
                {
                    level    = level + 1,
                    userData = nodeMenuItem.port
                });
            }
        }

        // Node creation when validate a choice
        public bool OnSelectEntry(SearchTreeEntry searchTreeEntry, SearchWindowContext context)
        {
            if (_graphView == null || _graphView.graph == null || _window == null)
                return false;
            // window to graph position
            var windowRoot = _window.rootVisualElement;
            var windowMousePosition = windowRoot.ChangeCoordinatesTo(windowRoot.parent, context.screenMousePosition - _window.position.position);
            var graphMousePosition = _graphView.contentViewContainer.WorldToLocal(windowMousePosition);

            var nodeType = searchTreeEntry.userData is Type ? (Type)searchTreeEntry.userData : ((NodeProvider.PortDescription)searchTreeEntry.userData).nodeType;
            
            // Check DisallowMultipleNode attribute before creating
            var disallowMultipleAttr = nodeType.GetCustomAttribute<DisallowMultipleNodeAttribute>();
            if (disallowMultipleAttr != null)
            {
                // Check if a node of this type already exists in the graph
                var existingNode = _graphView.graph.nodes.FirstOrDefault(n => n.GetType() == nodeType);
                if (existingNode != null)
                {
                    string title = string.IsNullOrEmpty(disallowMultipleAttr.customTitle) ? nodeType.Name : disallowMultipleAttr.customTitle;
                    EditorUtility.DisplayDialog("无法创建节点", $"只能在图中添加一个 {title} 节点", "确定");
                    return false;
                }
            }

            _graphView.RegisterCompleteObjectUndo("Added " + nodeType);
            var view = _graphView.AddNode(BaseNode.CreateFromType(nodeType, graphMousePosition));

            if (searchTreeEntry.userData is NodeProvider.PortDescription desc)
            {
                var targetPort = view.GetPortViewFromFieldName(desc.portFieldName, desc.portIdentifier);
                if (_inputPortView == null)
                    _graphView.Connect(targetPort, _outputPortView);
                else
                    _graphView.Connect(_inputPortView, targetPort);
            }

            return true;
        }
    }
}