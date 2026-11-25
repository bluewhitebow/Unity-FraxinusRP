using System;
using System.Collections.Generic;
using FraxinusEngine.Base;

namespace FraxinusEditor.Base
{
    [Serializable]
    public class CopyPasteHelper
    {
        public List<JsonElement> copiedNodes = new List<JsonElement>();
        public List<JsonElement> copiedGroups = new List<JsonElement>();
        public List<JsonElement> copiedEdges = new List<JsonElement>();
    }
}