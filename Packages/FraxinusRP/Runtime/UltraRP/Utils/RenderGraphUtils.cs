using System.Runtime.CompilerServices;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace FraxinusEngine.Rendering.Ultra
{
    static class RenderGraphUtils
    {
        private static ProfilingSampler s_setGlobalTextureProfilingSampler = new ProfilingSampler("Set Global Texture");

        internal const int gBufferSize = 7;
        internal const int dBufferSize = 3;
        internal const int lightTextureSize = 4;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool TextureHandlesEqual(in TextureHandle left, in TextureHandle right)
        {
            // TextureHandle inherits ValueType.Equals, which boxes both handles.
            // Keep the full resource identity (including version and validity bits)
            // and builtin flag without resolving the underlying texture.
            return left.handle.Equals(right.handle) && left.IsBuiltin() == right.IsBuiltin();
        }

        internal static void UseDBufferIfValid(IRasterRenderGraphBuilder builder, ResourceData resourceData)
        {
            TextureHandle[] dbufferHandles = resourceData.dBuffer;
            for (int i = 0; i < dBufferSize; ++i)
            {
                TextureHandle dbuffer = dbufferHandles[i];
                if (dbuffer.IsValid())
                    builder.UseTexture(dbuffer);
            }
        }

        private class PassData
        {
            internal TextureHandle texture;
            internal int nameID;
        }

        public static void SetGlobalTexture(RenderGraph graph, int nameId, TextureHandle handle, string passName = "Set Global Texture",
            [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        {
            using (var builder = graph.AddRasterRenderPass<PassData>(passName, out var passData, s_setGlobalTextureProfilingSampler, file, line))
            {
                passData.nameID = nameId;
                passData.texture = handle;
                builder.UseTexture(handle, AccessFlags.Read);

                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);

                builder.SetGlobalTextureAfterPass(handle, nameId);

                builder.SetRenderFunc((PassData data, RasterGraphContext context) =>
                {
                });
            }
        }
    }
}

