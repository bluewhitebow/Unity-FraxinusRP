using System;
using System.Linq;
using UnityEngine;
using FraxinusEngine.Base;

namespace FraxinusEngine.Rendering.Ultra
{
    /// <summary>
    /// Options to get a shader path to UltraRP shaders when calling ShaderUtils.GetShaderGUID();
    /// <see cref="ShaderUtils"/>.
    /// </summary>
    public enum ShaderPathID
    {
        /// <summary>
        /// Use this for UltraRP Lit shader.
        /// </summary>
        Lit,

        /// <summary>
        /// Use this for UltraRP Simple Lit shader.
        /// </summary>
        SimpleLit,

        /// <summary>
        /// Use this for UltraRP Unlit shader.
        /// </summary>
        Unlit,

        /// <summary>
        /// Use this for UltraRP Terrain Lit shader.
        /// </summary>
        TerrainLit,

        /// <summary>
        /// Use this for UltraRP Particles Lit shader.
        /// </summary>
        ParticlesLit,

        /// <summary>
        /// Use this for UltraRP Particles Simple Lit shader.
        /// </summary>
        ParticlesSimpleLit,

        /// <summary>
        /// Use this for UltraRP Particles Simple Unlit shader.
        /// </summary>
        ParticlesUnlit,

        /// <summary>
        /// Use this for UltraRP Baked Lit shader.
        /// </summary>
        BakedLit,

        /// <summary>
        /// Use this for UltraRP SpeedTree 7 shader.
        /// </summary>
        SpeedTree7,

        /// <summary>
        /// Use this for UltraRP SpeedTree 7 Billboard shader.
        /// </summary>
        SpeedTree7Billboard,

        /// <summary>
        /// Use this for UltraRP SpeedTree 8 shader.
        /// </summary>
        SpeedTree8,

        /// <summary>
        /// Use this for UltraRP SpeedTree 9 shader.
        /// </summary>
        SpeedTree9,
        // If you add a value here, also add it to ShaderID in Editor/ShaderUtils.cs

        /// <summary>
        /// Use this for UltraRP Complex Lit shader.
        /// </summary>
        ComplexLit,
    }

    /// <summary>
    /// Various utility functions for shaders in UltraRP.
    /// </summary>
    public static class ShaderUtils
    {
        /// <summary>
        /// Shader IDs for UltraRP shaders, including ShaderGraph subtargets.
        /// </summary>
        public enum ShaderID
        {
            Unknown = -1,

            Lit = ShaderPathID.Lit,
            SimpleLit = ShaderPathID.SimpleLit,
            Unlit = ShaderPathID.Unlit,
            TerrainLit = ShaderPathID.TerrainLit,
            ParticlesLit = ShaderPathID.ParticlesLit,
            ParticlesSimpleLit = ShaderPathID.ParticlesSimpleLit,
            ParticlesUnlit = ShaderPathID.ParticlesUnlit,
            BakedLit = ShaderPathID.BakedLit,
            SpeedTree7 = ShaderPathID.SpeedTree7,
            SpeedTree7Billboard = ShaderPathID.SpeedTree7Billboard,
            SpeedTree8 = ShaderPathID.SpeedTree8,
            SpeedTree9 = ShaderPathID.SpeedTree9,
            ComplexLit = ShaderPathID.ComplexLit,

            // ShaderGraph IDs start at 1000, correspond to subtargets
            SG_Unlit = 1000,        // UltraRPUnlitSubTarget
            SG_Lit,                 // UltraRPLitSubTarget
            SG_Decal,               // UltraRPDecalSubTarget
            SG_SpriteUnlit,         // UltraRPSpriteUnlitSubTarget
            SG_SpriteLit,           // UltraRPSpriteLitSubTarget
            SG_SpriteCustomLit,     // UltraRPSpriteCustomLitSubTarget
            SG_SixWaySmokeLit       // UltraRPSixWaySubTarget
        }

        static readonly string[] s_shaderPaths =
        {
            "Ultra Render Pipeline/Lit",
            "Ultra Render Pipeline/Simple Lit",
            "Ultra Render Pipeline/Unlit",
            "Ultra Render Pipeline/Terrain/Lit",
            "Ultra Render Pipeline/Particles/Lit",
            "Ultra Render Pipeline/Particles/Simple Lit",
            "Ultra Render Pipeline/Particles/Unlit",
            "Ultra Render Pipeline/Baked Lit",
            "Ultra Render Pipeline/Nature/SpeedTree7",
            "Ultra Render Pipeline/Nature/SpeedTree7 Billboard",
            "Ultra Render Pipeline/Nature/SpeedTree8",
            "Ultra Render Pipeline/Nature/SpeedTree9_URP", // Reserved for the optional graph; preserve enum indices.
            "Ultra Render Pipeline/Complex Lit",
        };

        /// <summary>
        /// Retrieves a shader path for the given UltraRP Shader Path ID.
        /// </summary>
        /// <param name="id">The UltraRP Shader Path ID.</param>
        /// <returns>The path to the UltraRP shader.</returns>
        public static string GetShaderPath(ShaderPathID id)
        {
            int index = (int)id;
            int arrayLength = s_shaderPaths.Length;
            if (arrayLength > 0 && index >= 0 && index < arrayLength)
                return s_shaderPaths[index];

            Debug.LogError("Trying to access UltraRP shader path out of bounds: (" + id + ": " + index + ")");
            return "";
        }

        /// <summary>
        /// Retrieves a UltraRP Shader Path ID from a path given.
        /// </summary>
        /// <param name="path">The path to the shader.</param>
        /// <returns>The UltraRP Shader Path ID.</returns>
        public static ShaderPathID GetEnumFromPath(string path)
        {
            var index = Array.FindIndex(s_shaderPaths, m => m == path);
            return (ShaderPathID)index;
        }

        /// <summary>
        /// Checks if a given shader is a UltraRP shader or not.
        /// </summary>
        /// <param name="shader">The shader.</param>
        /// <returns>True or false if it's a UltraRP shader or not.</returns>
        public static bool IsLWShader(Shader shader)
        {
            return s_shaderPaths.Contains(shader.name);
        }

#if UNITY_EDITOR
        private static float s_mostRecentValidDeltaTime = 0.0f;
#endif

        // A delta time that does not get reset to zero when stepping paused Play Mode or using the FrameDebugger
        // (unless Time.timeScale is zero)
        // * Can be zero on the first frame after domain reload (if Time.deltaTime is also zero)
        // * The value depends on when it was last called
        // * In in practice it should not get stale as it's called at least once during a UltraRP frame
        // * Currently only used when calculating '_LastTimeParameters' for shader upload
        // * Please validate your use case if trying to reuse this somewhere else (as it might not transfer)
        internal static float PersistentDeltaTime
        {
            get
            {
#if UNITY_EDITOR
                float deltaTime = Time.deltaTime;
                // The only case I'm aware of when a deltaTime of 0 is valid is when Time.timeScale is 0
                if (deltaTime > 0.0f || Time.timeScale == 0.0f)
                    s_mostRecentValidDeltaTime = deltaTime;
                return s_mostRecentValidDeltaTime;
#else
                return Time.deltaTime;
#endif
            }
        }

#if UNITY_EDITOR
        static readonly string[] s_shaderGUIDs =
        {
            "d1abb3a09f345893b396cdcac4502546",
            "1f82a48f23da5c5491eecdebb7cc4cac",
            "b60e90e8725d58099e6fce219da6ad57",
            "400fa58e5d0d50b4b644b266b5b406fe",
            "875d04cc870350038eeab41640da5c39",
            "9578554a95cf59b28b7b70bbce9fdb7c",
            "1b9a87ad5af356efa09bfc9a20396248",
            "f94994659a4f59938ac878c190a9663f",
            "b3e2abf73ca456459b8466a440871b3e",
            "79a81a4aaf2a5e06b2f361e5314b9d76",
            "e14f54ea96f259d899a6d23233107f11",
            "5d960ee2faa95d89a01fd65688c292ee",
            "06a6eed24ba059ef93a368a2fab0d669",
        };

        /// <summary>
        /// Returns a GUID for a UltraRP shader from Shader Path ID.
        /// </summary>
        /// <param name="id">ID of shader path.</param>
        /// <returns>GUID for the shader.</returns>
        public static string GetShaderGUID(ShaderPathID id)
        {
            int index = (int)id;
            int arrayLength = s_shaderGUIDs.Length;
            if (arrayLength > 0 && index >= 0 && index < arrayLength)
                return s_shaderGUIDs[index];

            Debug.LogError("Trying to access UltraRP shader GUID out of bounds: (" + id + ": " + index + ")");
            return "";
        }

#endif
    }
}
