using System;
using System.Collections.Generic;
using UnityEngine;

namespace FraxinusEngine.Base
{
    [Serializable]
    public class BaseStackNode
    {
        public Vector2 position;
        public string title = "New Stack";

        public bool acceptDrop;     // Is the stack accept drag and dropped nodes
        public bool acceptNewNode;  // Is the stack accepting node created by pressing space over the stack node

        public List<string> nodeGUIDs = new List<string>();

        public BaseStackNode(Vector2 position, string title = "Stack", bool acceptDrop = true, bool acceptNewNode = true)
        {
            this.position = position;
            this.title = title;
            this.acceptDrop = acceptDrop;
            this.acceptNewNode = acceptNewNode;
        }
    }
}