using System;
using UnityEngine;

namespace FraxinusEngine.Base
{
    [Serializable]
    public class StickyNote
    {
        public Rect position;
        public string title = "Hello world";
        public string content = "Description";

        public StickyNote(string title, Vector2 position)
        {
            this.title = title;
            this.position.position = position;
        }
    }
}