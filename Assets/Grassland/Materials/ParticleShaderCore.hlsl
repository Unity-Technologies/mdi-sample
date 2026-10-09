// Shared body of ParticleShader's two passes.

#ifndef GRASSLAND_PARTICLE_CORE_INCLUDED
#define GRASSLAND_PARTICLE_CORE_INCLUDED

// Core.hlsl before Lighting.hlsl: Lighting.hlsl uses its types but does not include it.
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"


        // SHADER_REQUIRE_MULTIDRAWINDIRECT is added automatically by `#pragma require multidrawindirect` 
        #ifdef SHADER_REQUIRE_MULTIDRAWINDIRECT
            // The draw id comes from DrawIndex, a SPIR-V BuiltIn (Vulkan) with no DXIL equivalent
            #ifdef SHADER_API_VULKAN
            #   define MDI_DRAWID_INPUT   [[vk::builtin("DrawIndex")]] uint drawIndex : DRAWID;
            #   define MDI_GET_DRAWID(v)  v.drawIndex
            #else
            #   error Multi-draw needs a draw-id builtin, which only SPIR-V provides. Use the portable pass on this backend.
            #endif
        #endif

            struct vertdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                uint vid : SV_VertexID;
                uint iid : SV_InstanceID;
            #ifdef SHADER_REQUIRE_MULTIDRAWINDIRECT
                MDI_DRAWID_INPUT
            #endif
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
                half height : TEXCOORD1;
                float3 worldPos : TEXCOORD2;
                half fog : TEXCOORD4;
                float2 lodFade : TEXCOORD5; // x = band position t (0..1), y = (0 fade-out, 1 fade-in)
                half gust : TEXCOORD6;
                // Already divided to (layer, LOD): the raw sub-draw id would read past drawParams.
                nointerpolation uint paramId : TEXCOORD7;
            };

            struct fragdata
            {
                half4 col : SV_Target;
            };

            TEXTURE2D(_Colormap);
            SAMPLER(sampler_Colormap);
            // One slice per (layer, LOD): a multi-draw call binds one texture for every sub-draw.
            TEXTURE2D_ARRAY(_LayerTexArray);   // RGB = albedo, A = coverage
            SAMPLER(sampler_LayerTexArray);

            CBUFFER_START(UnityPerMaterial)
                float4 _Colormap_ST;
                half4 _Color0;
                half4 _Color1;
                half4 _SubsurfaceColor;
                float _SssIntensity;
                float _SssPower;
                float _SssBase;
                float _AmbientStrength;
                float _BaseOcclusion;
                float _BaseOcclusionReach;
            CBUFFER_END

            // Bound per-LOD via MaterialPropertyBlock.
            StructuredBuffer<float3> vertexBuffer;
            StructuredBuffer<float2> uvBuffer;
            // Every draw's instances live in one buffer, in a region starting at drawId * instanceCapacity
            StructuredBuffer<float4> culledPositionsBuffer;
            StructuredBuffer<uint2> culledWindBuffer;
            uint instanceCapacity;

            // Shared per-frame uniforms
            float worldScale;
            float windShade;       // how much a passing gust darkens the grass

            // LOD screen-door cross-fade over one boundary; lodBandWidth == 0 = hard switch.
            float3 lodCenter;    // camera world pos (LOD distance is measured from here)
            float lodBandWidth;  // transition band width (m); 0 = hard
            float _DebugLod;     // 1 = tint by LOD (green = near/LOD0, red = far/LOD1)

            struct DrawParams
            {
                float4 clumpScale;       // xyz = per-clump geometry scale
                float4 coverageChannel;  // which channels carry alpha coverage
                float modelHeight;
                float windScale;
                float heightVariance;
                float lodDistance;
                float lodLevel;          // 0 = near LOD, 1 = far LOD
                float alphaCutoff;
                float textured;          // 1 = a texture is bound for this draw
                float textureSlice;      // slice of _LayerTexArray this draw samples
                float coverageOnly;      // 1 = that texture is a mask, color from the gradient

                // Must match DrawParams.Stride in ProceduralRenderingFeature.cs
                float pad0, pad1, pad2;  // pads the struct to 80 bytes (5 x float4)
            };
            StructuredBuffer<DrawParams> drawParams;
            // Sub-draws fan out per cell, so the draw id divides down to its (layer, LOD). 1 when off.
            uint cellCount;
        #ifdef SHADER_REQUIRE_MULTIDRAWINDIRECT
            StructuredBuffer<uint> drawIdTable;   // sub-draw slot -> (layer, LOD) draw id
        #else
            uint drawId;   // portable pass only: set per draw via the MaterialPropertyBlock
        #endif

            float2 UnpackHalf2(uint v) { return float2(f16tof32(v & 0xFFFFu), f16tof32(v >> 16)); }

            // 4x4 ordered (Bayer) dither threshold in [0,1) from screen-pixel position.
            static const float _Bayer4x4[16] = {
                 0.0/16.0,  8.0/16.0,  2.0/16.0, 10.0/16.0,
                12.0/16.0,  4.0/16.0, 14.0/16.0,  6.0/16.0,
                 3.0/16.0, 11.0/16.0,  1.0/16.0,  9.0/16.0,
                15.0/16.0,  7.0/16.0, 13.0/16.0,  5.0/16.0
            };

            half3 ColormapAlbedo(half height)
            {
                half4 colmap = (half4)SAMPLE_TEXTURE2D(_Colormap, sampler_Colormap, float2(0.5, height));
                half a = _Color0.w * (1.0h - height) + _Color1.w * height;
                return colmap.xyz + lerp(_Color0.xyz * _Color0.w, _Color1.xyz * _Color1.w, height * a);
            }

            float LodDitherThreshold(float2 pixel)
            {
                int2 p = int2(fmod(pixel, 4.0));
                return _Bayer4x4[p.y * 4 + p.x] + (0.5 / 16.0);
            }

            v2f vert(vertdata v)
            {
            #ifdef SHADER_REQUIRE_MULTIDRAWINDIRECT
                uint did = drawIdTable[MDI_GET_DRAWID(v)];
            #else
                uint did = drawId;
            #endif
                uint paramId = did / max(cellCount, 1u);
                DrawParams dp = drawParams[paramId];
                float modelHeight = dp.modelHeight;
                float windScale = dp.windScale;
                float heightVariance = dp.heightVariance;
                float lodDistance = dp.lodDistance;
                float lodLevel = dp.lodLevel;
                float4 clumpScale = dp.clumpScale;

                v2f o;
                o.uv = uvBuffer[v.vid];

                uint instIndex = did * instanceCapacity + v.iid;
                float4 inst = culledPositionsBuffer[instIndex];   // xyz = world pos (conformed)
                uint2 packed = culledWindBuffer[instIndex];
                float3 vRaw = vertexBuffer[v.vid];
                float bladeH = saturate(vRaw.y / max(modelHeight, 1e-4));

                float2 yawSinCos = UnpackHalf2(asuint(inst.w));
                float2 windXZ = UnpackHalf2(packed.x);
                float2 gustHash = UnpackHalf2(packed.y);
                o.gust = (half)gustHash.x;

                // Per-clump scale, then random yaw, then place.
                float3 vp = vRaw * clumpScale.xyz;
                // Per-clump random height variation (stable hash of world XZ), height axis only.
                vp.y *= 1.0 + gustHash.y * heightVariance;
                vp.xz = float2(yawSinCos.y * vp.x + yawSinCos.x * vp.z,
                              -yawSinCos.x * vp.x + yawSinCos.y * vp.z);
                float3 worldPos = vp + inst.xyz;

                // ---- Wind -------------------------------------------------------------------------
                // Displacement goes as bladeH SQUARED so the blade curves; linear height shears it stiffly.
                float curve = bladeH * bladeH * windScale;
                worldPos.xz += windXZ * curve;
                // Leaning shortens vertical reach; without it the tips stretch and the grass looks rubbery.
                worldPos.y -= length(windXZ) * curve * 0.35;

                o.vertex = TransformWorldToHClip(worldPos);
                o.height = (half)bladeH;
                o.worldPos = worldPos;
                o.fog = ComputeFogFactor(o.vertex.z);

                // Band position from the clump's world XZ, so it is constant across the clump.
                float2 lodFade = float2(0.0, 0.0);
                if (lodBandWidth > 1e-4)
                {
                    float d = distance(inst.xz, lodCenter.xz);
                    float hb = lodBandWidth * 0.5;
                    if (d > lodDistance - hb && d < lodDistance + hb)
                    {
                        float t = saturate((d - (lodDistance - hb)) / lodBandWidth);
                        // near LOD (lodLevel 0) fades OUT as d grows; far LOD (lodLevel 1) fades IN.
                        lodFade = float2(t, lodLevel < 0.5 ? 0.0 : 1.0);
                    }
                }
                o.lodFade = lodFade;
                o.paramId = paramId;
                return o;
            }

            fragdata frag(v2f i)
            {
                DrawParams dp = drawParams[i.paramId];
                half lodLevel = (half)dp.lodLevel;
                half textured = (half)dp.textured;
                float textureSlice = dp.textureSlice;
                half coverageOnly = (half)dp.coverageOnly;
                half4 coverageChannel = (half4)dp.coverageChannel;
                half alphaCutoff = (half)dp.alphaCutoff;

                fragdata fragout;

                // Complementary ordered dither: role 0 keeps dither >= t, role 1 the rest, so coverage holds.
                if (lodBandWidth > 1e-4)
                {
                    float thr = LodDitherThreshold(i.vertex.xy);
                    float t = i.lodFade.x;
                    clip(i.lodFade.y < 0.5 ? (thr - t) : (t - thr));
                }

                // textured: 1 = RGBA texture (RGB albedo, A coverage), 0 = colormap gradient, mesh silhouette.
                half3 albedo;
                // Gradient-coloured draws only: a gust band on a daisy petal just reads as texture flicker.
                half proceduralColor = 1.0h;
                if (textured > 0.5h)
                {
                    half4 tex = (half4)SAMPLE_TEXTURE2D_ARRAY(_LayerTexArray, sampler_LayerTexArray, i.uv, textureSlice);
                    if (coverageOnly > 0.5h)
                    {
                        // Mask: the texture decides the silhouette, the gradient decides the color.
                        clip(dot(tex, coverageChannel) - alphaCutoff);
                        albedo = ColormapAlbedo(i.height);
                    }
                    else
                    {
                        clip(tex.a - alphaCutoff);
                        albedo = tex.rgb;
                        proceduralColor = 0.0h;
                    }
                }
                else
                {
                    albedo = ColormapAlbedo(i.height);
                }

                // Fake AO at the base of the foliage
                half ao = lerp(1.0h - (half)_BaseOcclusion, 1.0h,
                               smoothstep(0.0h, 1.0h, saturate(i.height / max((half)_BaseOcclusionReach, 0.001h))));
                albedo *= ao;

                // Wind gusts darken the grass as they pass
                albedo *= 1.0h + proceduralColor * (half)windShade * 0.6h * (i.gust * 2.0h - 1.0h);

                // Debug LOD: tint by LOD level (green = near/LOD0, red = far/LOD1); still lit + clipped.
                if (_DebugLod > 0.5)
                    albedo = (lodLevel < 0.5h) ? half3(0.1h, 0.85h, 0.1h) : half3(0.9h, 0.1h, 0.1h);

                // Soft two-sided foliage shading
                half3 viewDir = (half3)normalize(_WorldSpaceCameraPos - i.worldPos);
                half3 n = half3(0.0h, 1.0h, 0.0h);

                // No shadow sampling: the shadowmap is not reliably bound in a custom indirect pass.
                Light mainLight = GetMainLight();

                // Wrapped (half-Lambert) diffuse — soft, never crushes to black.
                half wrap = saturate(dot(n, mainLight.direction) * 0.5h + 0.5h);
                half3 direct = albedo * mainLight.color * wrap;

                // Sky ambient fill (dominant, keeps the grass reading as a soft canopy).
                half3 ambient = albedo * (half3)SampleSH(n) * (half)_AmbientStrength;

                // Two-sided translucency: strongest looking toward the sun through the blades
                half back = saturate(dot(viewDir, -mainLight.direction));
                half trans = (pow(back, (half)_SssPower) + (half)_SssBase) * (half)_SssIntensity * i.height;
                half3 subsurface = (half3)_SubsurfaceColor.rgb * trans * mainLight.color;

                half3 color = direct + ambient + subsurface;

                color = MixFog(color, i.fog);
                fragout.col = half4(color, 1.0h);
                return fragout;
            }
#endif // GRASSLAND_PARTICLE_CORE_INCLUDED
