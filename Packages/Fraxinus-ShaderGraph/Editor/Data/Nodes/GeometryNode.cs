using System;
using System.Text.RegularExpressions;
using UnityEditor.ShaderGraph.Drawing.Controls;
using ShaderGraphPopupList = UnityEditor.ShaderGraph.Drawing.Controls.PopupList;
using UnityEngine;
using UnityEditor.Graphing;
using System.Collections.Generic;
using System.Linq;
using UnityEditor.ShaderGraph.Internal;

namespace UnityEditor.ShaderGraph
{
    abstract class GeometryNode : AbstractMaterialNode
    {
        public GeometryNode()
        {
            m_PreviewMode = PreviewMode.Preview3D;
        }

        public virtual List<CoordinateSpace> validSpaces => new List<CoordinateSpace> { CoordinateSpace.Object, CoordinateSpace.View, CoordinateSpace.World, CoordinateSpace.Tangent };

        [SerializeField]
        private CoordinateSpace m_Space = CoordinateSpace.World;

        [PopupControl("Space")]
        public ShaderGraphPopupList spacePopup
        {
            get
            {
                var names = validSpaces.Select(cs => cs.ToString().PascalToLabel()).ToArray();
                return new ShaderGraphPopupList(names, (int)m_Space);
            }
            set
            {
                if (m_Space == (CoordinateSpace)value.selectedEntry)
                    return;

                m_Space = (CoordinateSpace)value.selectedEntry;
                Dirty(ModificationScope.Graph);
            }
        }
        public CoordinateSpace space => m_Space;

        public override bool hasPreview
        {
            get { return true; }
        }
    }
}
