using UnityEngine;
using UnityEngine.UIElements;
using FraxinusEngine.Base;
using UnityEditor;
using UnityEditor.Experimental.GraphView;

namespace FraxinusEditor.Base
{
    public class NodeBadgeView : IconBadge
    {
        Label _label;
        Texture _icon;
        Color _color;
        bool _isCustom;

        public NodeBadgeView(string message, NodeMessageType messageType)
        {
            switch (messageType)
            {
                case NodeMessageType.Warning:
                    CreateCustom(message, EditorGUIUtility.IconContent("Collab.Warning").image, Color.yellow);
                    break ;
                case NodeMessageType.Error:    
                    CreateCustom(message, EditorGUIUtility.IconContent("Collab.Warning").image, Color.red);
                    break ;
                case NodeMessageType.Info:
                    CreateCustom(message, EditorGUIUtility.IconContent("console.infoicon").image, Color.white);
                    break ;
                default:
                case NodeMessageType.None:
                    CreateCustom(message, null, Color.grey);
                    break ;
            }
        }

        public NodeBadgeView(string message, Texture icon, Color color)
        {
            CreateCustom(message, icon, color);
        }

        void CreateCustom(string message, Texture icon, Color color)
        {
            badgeText = message;
            this._color = color;
            
            var image = this.Q<Image>("icon");
            image.image = icon;
            image.style.backgroundColor = color;
            style.color = color;
            // This will set a class name containing the hash code of the string
            // We use this little trick to retrieve the label once it is added to the graph
            visualStyle = badgeText.GetHashCode().ToString();
        }

        protected override void ExecuteDefaultActionAtTarget(EventBase evt)
        {
            // When the mouse enter the icon, this will add the label to the hierarchy
            base.ExecuteDefaultActionAtTarget(evt);

            if (evt.eventTypeId == MouseEnterEvent.TypeId())
            {
                // And then we can fetch it here:
                GraphView gv = GetFirstAncestorOfType<GraphView>();
                var label = gv?.Q<Label>(classes: new string[]{"icon-badge__text--" + badgeText.GetHashCode()});
                if (label != null)
                    label.style.color = _color;
            }
        }
    }
}
