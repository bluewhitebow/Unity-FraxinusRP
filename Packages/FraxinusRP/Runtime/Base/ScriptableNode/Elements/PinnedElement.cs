using System;
using UnityEngine;

namespace FraxinusEngine.Base
{
    [Serializable]
    public class PinnedElement
    {
        public static readonly Vector2 defaultSize = new Vector2(150, 200);

        public Rect position = new Rect(Vector2.zero, defaultSize);
        public bool opened = true;
        public SerializableType editorType;

        public PinnedElement(Type type)
        {
            this.editorType = new SerializableType(type);
        }
    }
}