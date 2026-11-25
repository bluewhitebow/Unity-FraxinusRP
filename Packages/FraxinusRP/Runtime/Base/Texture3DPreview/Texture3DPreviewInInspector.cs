using UnityEngine;

namespace FraxinusEngine.Base
{
    public class Texture3DPreviewInInspector : MonoBehaviour
    {
        [Texture3DPreview]
        public Texture3D texture3D;
    }

}