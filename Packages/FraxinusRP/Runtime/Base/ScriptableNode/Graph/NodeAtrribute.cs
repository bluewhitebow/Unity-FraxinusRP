using System;

namespace FraxinusEngine.Base
{
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
    public class InputAttribute : Attribute
    {
        public string name;
        public bool allowMultiple = false;

        public InputAttribute(string name = null, bool allowMultiple = false)
        {
            this.name = name;
            this.allowMultiple = allowMultiple;
        }
    }

    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
    public class OutputAttribute : Attribute
    {
        public string name;
        public bool allowMultiple = false;

        public OutputAttribute(string name = null, bool allowMultiple = false)
        {
            this.name = name;
            this.allowMultiple = allowMultiple;
        }
    }

    /// <summary>
    /// Creates a vertical port instead of the default horizontal one
    /// </summary>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
    public class VerticalAttribute : Attribute
    {
    }

    /// <summary>
    /// Register the node in the NodeProvider class. The node will also be available in the node creation window.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public class NodeMenuItemAttribute : Attribute
    {
        public string menuTitle;
        public Type onlyCompatibleWithGraph;

        public NodeMenuItemAttribute(string menuTitle = null, Type onlyCompatibleWithGraph = null)
        {
            this.menuTitle = menuTitle;
            this.onlyCompatibleWithGraph = onlyCompatibleWithGraph;
        }
    }

    /// <summary>
    /// Allow you to customize the input function of a port
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public class CustomPortInputAttribute : Attribute
    {
        public string fieldName;
        public Type inputType;
        public bool allowCast;

        public CustomPortInputAttribute(string fieldName, Type inputType, bool allowCast = true)
        {
            this.fieldName = fieldName;
            this.inputType = inputType;
            this.allowCast = allowCast;
        }
    }

    /// <summary>
    /// Allow you to customize the output function of a port
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public class CustomPortOutputAttribute : Attribute
    {
        public string fieldName;
        public Type outputType;
        public bool allowCast;

        public CustomPortOutputAttribute(string fieldName, Type outputType, bool allowCast = true)
        {
            this.fieldName = fieldName;
            this.outputType = outputType;
            this.allowCast = allowCast;
        }
    }

    /// <summary>
    /// Allow you to modify the generated port view from a field. Can be used to generate multiple ports from one field.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public class CustomPortBehaviorAttribute : Attribute
    {
        public string fieldName;
        public CustomPortBehaviorAttribute(string fieldName)
        {
            this.fieldName = fieldName;
        }
    }

    /// <summary>
    /// Allow to bind a method to generate a specific set of ports based on a field type in a node
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public class CustomPortTypeBehavior : Attribute 
    {
        public Type type;
        public CustomPortTypeBehavior(Type type)
        {
            this.type = type;
        }
    }

    /// <summary>
    /// Allow you to have a custom view for your stack nodes
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public class CustomStackNodeView : Attribute
    {
        public Type stackNodeType;
        public CustomStackNodeView(Type stackNodeType)
        {
            this.stackNodeType = stackNodeType;
        }
    }

    [AttributeUsage(AttributeTargets.Field, AllowMultiple = true)]
    public class VisibleIf : Attribute
    {
        public string fieldName;
        public object value;

        public VisibleIf(string fieldName, object value)
        {
            this.fieldName = fieldName;
            this.value = value;
        }
    }

    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
    public class ShowInInspector : Attribute
    {
        public bool showInNode;

        public ShowInInspector(bool showInNode = false)
        {
            this.showInNode = showInNode;
        }
    }

    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
    public class ShowAsDrawer : Attribute { }

    [AttributeUsage(AttributeTargets.Field)]
    public class SettingAttribute : Attribute
    {
        public string name;

        public SettingAttribute(string name = null)
        {
            this.name = name;
        }
    }

    [AttributeUsage(AttributeTargets.Method)]
    public class IsCompatibleWithGraph : Attribute { }

    [AttributeUsage(AttributeTargets.Field)]
    public class ShowAsObjectDrawer : Attribute { }

    /// <summary>
    /// Indicates that only one instance of this node type can exist in a graph.
    /// When attempting to add a second instance, the operation will be blocked.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public class DisallowMultipleNodeAttribute : Attribute
    {
        public string customTitle { private set; get; }

        public DisallowMultipleNodeAttribute(string customTitle = null)
        {
            this.customTitle = customTitle;
        }
    }
}