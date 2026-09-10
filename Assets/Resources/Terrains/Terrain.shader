Shader "Custom/TerrainStandardChecker"
{
    Properties
    {
        _ColorA ("Color A", Color) = (0.75, 0.75, 0.75, 1)
        _ColorB ("Color B", Color) = (0.25, 0.25, 0.25, 1)
        _CellSize ("Cell Size", Float) = 1.0

        [HideInInspector] _MainTex ("Texture", 2D) = "white" {}
        _ShadowColor ("Shadow Color Tint", Color) = (0.5, 0.5, 0.5, 1)
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" "RenderPipeline"="UniversalPipeline" }
        LOD 100

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        // Every non-texture material property has to live in this buffer, otherwise
        // the SRP Batcher rejects the shader.
        CBUFFER_START(UnityPerMaterial)
            half4 _ColorA;
            half4 _ColorB;
            float _CellSize;
            float4 _MainTex_ST;
            half4 _ShadowColor;
        CBUFFER_END
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            // Compiles variants for directional light shadows (replaces multi_compile_fwdbase)
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH

            // Decal support. URP writes decals into the DBuffer before opaque
            // geometry draws, but a surface only shows them if it reads them
            // back out -- there is no pass that stamps them on from outside.
            // Under the screen-space technique none of these are set and the
            // whole thing compiles away.
            #pragma multi_compile_fragment _ _DBUFFER_MRT1 _DBUFFER_MRT2 _DBUFFER_MRT3

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DBuffer.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                // Declares internal coordinates needed to sample the shadow map
                #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                    float4 shadowCoord : TEXCOORD2;
                #endif
                float4 positionCS : SV_POSITION;
            };

            Varyings vert (Attributes input)
            {
                Varyings output = (Varyings)0;

                VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = vertexInput.positionCS;
                output.positionWS = vertexInput.positionWS;
                output.uv = TRANSFORM_TEX(input.uv, _MainTex);

                // Coordinates calculation needed to map screen space/light space shadows
                #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                    output.shadowCoord = GetShadowCoord(vertexInput);
                #endif
                return output;
            }

            half4 frag (Varyings input) : SV_Target
            {
                // Sample unlit base texture
                half4 col = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv);

                float cellSize = max(_CellSize, 0.0001);

                // World-space checkerboard on XZ plane.
                // Cell Size = 1 means each tile is 1 x 1 Unity world units.
                float2 cell = floor(input.positionWS.xz / cellSize);
                float checker = fmod(cell.x + cell.y, 2.0);
                checker = abs(checker);

                half4 checkerColor = lerp(_ColorA, _ColorB, checker);
                half3 albedo = checkerColor.rgb;

                // Albedo only. The decal's normal and MAOS channels are dropped
                // because this surface has none of its own to blend them with:
                // it is flat-shaded off a checker and lit by nothing but the
                // main light's shadow term.
                #if defined(_DBUFFER)
                    ApplyDecalToBaseColor(input.positionCS, albedo);
                #endif

                #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                    float4 shadowCoord = input.shadowCoord;
                #elif defined(MAIN_LIGHT_CALCULATE_SHADOWS)
                    float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                #else
                    float4 shadowCoord = float4(0, 0, 0, 0);
                #endif

                // Calculates shadow attenuation: 1.0 = fully lit, 0.0 = fully shadowed
                Light mainLight = GetMainLight(shadowCoord);
                half attenuation = mainLight.shadowAttenuation;

                // Interpolate between the unlit texture and the shaded color tint based on shadow mapping.
                // Shadowing is applied after the decal so a splat in shadow is
                // darkened with the ground it sits on rather than glowing on it.
                half3 shadowResult = lerp(albedo * _ShadowColor.rgb, albedo, attenuation);

                return half4(shadowResult, checkerColor.a);
            }
            ENDHLSL
        }

        // Pass needed so this object can cast shadows onto other objects
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma vertex shadowVert
            #pragma fragment shadowFrag

            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            // Set by ShadowUtils.SetupShadowCasterConstantBuffer; _LightPosition is only
            // meaningful for punctual lights, _LightDirection for directional ones.
            float3 _LightDirection;
            float3 _LightPosition;

            struct ShadowAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };

            struct ShadowVaryings
            {
                float4 positionCS : SV_POSITION;
            };

            ShadowVaryings shadowVert (ShadowAttributes input)
            {
                ShadowVaryings output;

                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);

                #if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
                    float3 lightDirectionWS = normalize(_LightPosition - positionWS);
                #else
                    float3 lightDirectionWS = _LightDirection;
                #endif

                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));
                output.positionCS = ApplyShadowClamping(positionCS);
                return output;
            }

            half4 shadowFrag (ShadowVaryings input) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }

        // Pass needed so decals land on this surface at all.
        //
        // Turning decals on makes URP ask for scene normals, and asking for
        // normals swaps the depth prepass for a depth+normals one -- the two are
        // an either/or, not both. A surface with only a DepthOnly pass drops out
        // of the prepass entirely at that point, so the DBuffer resolves its
        // projections against whatever is behind this one and the decal lands
        // somewhere else, or nowhere.
        //
        // Tagged DepthNormalsOnly rather than DepthNormals: the partial prepass a
        // deferred renderer runs only accepts that tag, and this shader has no
        // GBuffer pass that could fill its normals in instead.
        Pass
        {
            Name "DepthNormalsOnly"
            Tags { "LightMode"="DepthNormalsOnly" }

            ZWrite On
            Cull Back

            HLSLPROGRAM
            #pragma vertex depthNormalsVert
            #pragma fragment depthNormalsFrag

            // Deferred wants octahedral-packed normals, forward wants them plain.
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT

            struct DepthNormalsAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };

            struct DepthNormalsVaryings
            {
                float3 normalWS : TEXCOORD0;
                float4 positionCS : SV_POSITION;
            };

            DepthNormalsVaryings depthNormalsVert (DepthNormalsAttributes input)
            {
                DepthNormalsVaryings output;

                VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normalInput = GetVertexNormalInputs(input.normalOS);
                output.positionCS = vertexInput.positionCS;
                output.normalWS = NormalizeNormalPerVertex(normalInput.normalWS);
                return output;
            }

            half4 depthNormalsFrag (DepthNormalsVaryings input) : SV_Target
            {
                #if defined(_GBUFFER_NORMALS_OCT)
                    float3 normalWS = normalize(input.normalWS);
                    float2 octNormalWS = PackNormalOctQuadEncode(normalWS);
                    float2 remappedOctNormalWS = saturate(octNormalWS * 0.5 + 0.5);
                    half3 packedNormalWS = half3(PackFloat2To888(remappedOctNormalWS));
                    return half4(packedNormalWS, 0.0);
                #else
                    return half4(NormalizeNormalPerPixel(input.normalWS), 0.0);
                #endif
            }
            ENDHLSL
        }

        // Pass needed so this object shows up in _CameraDepthTexture
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }

            ZWrite On
            ColorMask R
            Cull Back

            HLSLPROGRAM
            #pragma vertex depthVert
            #pragma fragment depthFrag

            struct DepthAttributes
            {
                float4 positionOS : POSITION;
            };

            struct DepthVaryings
            {
                float4 positionCS : SV_POSITION;
            };

            DepthVaryings depthVert (DepthAttributes input)
            {
                DepthVaryings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }

            half4 depthFrag (DepthVaryings input) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }
    }
}
