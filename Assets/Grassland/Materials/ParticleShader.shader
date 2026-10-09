Shader "Unlit/ParticleShader"
{
    Properties
    {
        _Colormap("Color map", 2D) = "white" {}
        _Color0("Base tint", Color) = (0.0, 0.0, 0.0, 1.0)
        _Color1("Tip tint", Color) = (1.0, 1.0, 1.0, 1.0)
        [NoScaleOffset] _LayerTexArray("LOD texture array (RGB = color, A = coverage)", 2DArray) = "" {}

        [Header(Lighting)]
        _SubsurfaceColor("Subsurface color", Color) = (0.45, 0.72, 0.18, 1.0)
        _SssIntensity("Subsurface intensity", Range(0, 4)) = 1.3
        _SssPower("Subsurface power", Range(1, 16)) = 4.0
        _SssBase("Subsurface base (front-lit glow)", Range(0, 1)) = 0.2
        _AmbientStrength("Ambient strength", Range(0, 2)) = 1.2
        _BaseOcclusion("Base occlusion (fake AO at the base)", Range(0, 1)) = 0.55
        _BaseOcclusionReach("Base occlusion reach (up the model)", Range(0.02, 1)) = 0.35
    }

    // Multi-draw capable backends (currently limited to Vulkan)
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }
        LOD 100
        Cull Off

        // Pass 0 - portable: one indirect draw per (layer, LOD, cell)
        // Used by Vulkan GfxDevice (old backend)
        Pass
        {
            Name "Foliage"
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #pragma multi_compile_fog
            #include "ParticleShaderCore.hlsl"
            ENDHLSL
        }

        // Pass 1 - multi-draw indirect: all sub-draws are rasterized by a single draw call.
        // Used by Vulkan UGK (new backend)

        // The subdraw ID is read from the SPIR-V BuiltIn DrawIndex, which is only available on Vulkan
        Pass
        {
            Name "FoliageMDI"
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #pragma multi_compile_fog
            #pragma require multidrawindirect // This also defines SHADER_REQUIRE_MULTIDRAWINDIRECT.
            #pragma use_dxc
            #include "ParticleShaderCore.hlsl"
            ENDHLSL
        }
    }

    // Fallback for backends without multi-draw (non Vulkan)
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }
        LOD 100
        Cull Off

        Pass
        {
            Name "Foliage"
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #pragma multi_compile_fog
            #include "ParticleShaderCore.hlsl"
            ENDHLSL
        }
    }
}
