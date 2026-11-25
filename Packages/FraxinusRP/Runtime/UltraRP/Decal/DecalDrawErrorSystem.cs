using UnityEngine;

namespace FraxinusEngine.Rendering.Ultra
{
    internal class DecalDrawErrorSystem : DecalDrawSystem
    {
        private DecalTechnique _technique;

        public DecalDrawErrorSystem(DecalEntityManager entityManager, DecalTechnique technique) 
            : base("DecalDrawErrorSystem.Execute", entityManager) 
        {
            _technique = technique;
        }

        protected override int GetPassIndex(DecalCachedChunk decalCachedChunk)
        {
            switch (_technique)
            {
                case DecalTechnique.DBuffer:
                    return ((decalCachedChunk.passIndexDBuffer == -1) && (decalCachedChunk.passIndexEmissive == -1)) ? 0 : -1;
                case DecalTechnique.ScreenSpace:
                    return decalCachedChunk.passIndexScreenSpace == -1 ? 0 : -1;
                case DecalTechnique.GBuffer:
                    return decalCachedChunk.passIndexGBuffer == -1 ? 0 : -1;
                case DecalTechnique.Invalid:
                    return 0;
                default:
                    return 0;
            }
        }

        protected override Material GetMaterial(DecalEntityChunk decalEntityChunk) => _entityManager.errorMaterial;
    }
}

