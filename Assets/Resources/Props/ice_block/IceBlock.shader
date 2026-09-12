Shader "Unlit/IceBlock"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}

        _IceColor ("Ice Color", Color) = (0.65, 0.85, 1.0, 1.0)

        _NoiseScale ("Noise Scale", Float) = 4.0
        _NoiseStrength ("Noise Strength", Range(0, 1)) = 0.15
        _NoiseEpslion ("Noise Epslion", Range(0.001, 8.0)) = 0.1

        _Absorption ("Absorption", Range(0, 5)) = 0.8
        _Thickness ("Thickness Scale", Range(0, 3)) = 1.0

        _Environment ("Environment Cubemap", Cube) = "" {}

        _ReflectionStrength ("Reflection Strength", Range(0, 2)) = 1.0
        _RefractionStrength ("Refraction Strength", Range(0, 2)) = 1.0

        _BoxHalfExtents ("Box Half Extents", Vector) = (0.5, 0.5, 0.5, 0.0)
        _RoundRadius ("Round Radius", Range(0, 0.5)) = 0.08
        _RoundCornerStrength ("Round Corner Strength", Range(0, 1)) = 1.0

        // Rounded Corner Silhouette: ray-marches the rounded box in the forward,
        // shadow and depth passes, so the block, its cast shadow and
        // _CameraDepthTexture all share one rounded silhouette instead of the
        // mesh's sharp box. Costs a per-pixel march and disables early-Z in those
        // passes; off falls back to a sharp box with faked rounded normals.
        //
        // With it on, Round Corner Strength scales the radius that actually
        // shapes the block (and the shading normal is the exact one); with it off
        // it only blends the faked normal towards the mesh normal.
        //
        // Receive Shadows: the forward pass is unlit, so the main light's shadow
        // has to be applied by hand. Strength/Tint control how dark and how cold
        // the shadowed ice gets.
        [Header(Shadows)]
        [Toggle(_ROUNDED_SILHOUETTE_ON)] _RoundedSilhouette ("Rounded Corner Silhouette", Float) = 1
        [Toggle(_RECEIVE_SHADOWS_ON)] _ReceiveShadows ("Receive Shadows", Float) = 1
        _ShadowStrength ("Receive Shadow Strength", Range(0, 1)) = 0.5
        _ShadowTint ("Receive Shadow Tint", Color) = (0.35, 0.45, 0.65, 1.0)
    }

    SubShader
    {
        Tags
        {
            "RenderType"="Opaque"
            "RenderPipeline"="UniversalPipeline"
        }

        LOD 100

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        // Every non-texture material property has to live in this buffer, otherwise
        // the SRP Batcher rejects the shader.
        CBUFFER_START(UnityPerMaterial)
            float4 _MainTex_ST;

            float4 _IceColor;

            float _NoiseScale;
            float _NoiseStrength;
            float _NoiseEpslion;

            float _Absorption;
            float _Thickness;

            float _ReflectionStrength;
            float _RefractionStrength;

            float4 _BoxHalfExtents;
            float _RoundRadius;
            float _RoundCornerStrength;

            float _RoundedSilhouette;
            float _ReceiveShadows;
            float _ShadowStrength;
            float4 _ShadowTint;
        CBUFFER_END

        // ------------------------------------------------------------
        // Rounded box geometry, shared by the forward and shadow passes
        // ------------------------------------------------------------

        // A radius larger than the smallest half extent would invert the shape.
        float ClampRoundRadius(float3 halfExtents, float radius)
        {
            float maxRadius =
                max(min(halfExtents.x, min(halfExtents.y, halfExtents.z)) - 1e-4, 0.0);

            return clamp(radius, 0.0, maxRadius);
        }

        float3 BoxFaceNormalOS(float3 p, float3 halfExtents)
        {
            float3 safeExt = max(halfExtents, float3(1e-5, 1e-5, 1e-5));
            float3 ap = abs(p / safeExt);

            if (ap.x > ap.y && ap.x > ap.z)
                return float3(sign(p.x), 0.0, 0.0);
            else if (ap.y > ap.z)
                return float3(0.0, sign(p.y), 0.0);
            else
                return float3(0.0, 0.0, sign(p.z));
        }

        float3 RoundedBoxNormalOS(float3 p, float3 halfExtents, float radius)
        {
            radius = ClampRoundRadius(halfExtents, radius);

            if (radius <= 1e-5)
                return BoxFaceNormalOS(p, halfExtents);

            float3 inner = clamp(p, -halfExtents + radius, halfExtents - radius);
            float3 n = p - inner;
            float lenN = length(n);

            if (lenN <= 1e-5)
                return BoxFaceNormalOS(p, halfExtents);

            return n / lenN;
        }

        // Signed distance to the rounded box (negative inside).
        float SdRoundedBoxOS(float3 p, float3 halfExtents, float radius)
        {
            radius = ClampRoundRadius(halfExtents, radius);

            float3 q = abs(p) - (halfExtents - radius);

            return length(max(q, 0.0)) +
                   min(max(q.x, max(q.y, q.z)), 0.0) -
                   radius;
        }

        // Sphere-traces the rounded box from a ray origin sitting on (or outside)
        // the mesh box. Returns false when the ray misses, which is exactly the
        // sliver of mesh surface that the rounded corners cut away.
        bool RayMarchRoundedBoxOS(
            float3 ro,
            float3 rd,
            float3 halfExtents,
            float radius,
            out float tHit)
        {
            float maxDist = 2.0 * length(halfExtents) + 1e-3;
            float epsilon = 1e-4 * max(maxDist, 1.0);

            tHit = 0.0;

            [loop]
            for (int step = 0; step < 32; ++step)
            {
                float d = SdRoundedBoxOS(ro + rd * tHit, halfExtents, radius);

                if (d < epsilon)
                    return true;

                tHit += d;

                if (tHit > maxDist)
                    break;
            }

            return false;
        }

        // The silhouette follows how round the block actually looks: at
        // _RoundCornerStrength 0 it collapses back to the plain mesh box.
        float RoundedSilhouetteRadius()
        {
            return _RoundRadius * _RoundCornerStrength;
        }

        // Marches the rounded box from a point on the mesh box surface along a
        // world-space ray. Returns false when the ray misses, which is exactly the
        // sliver of mesh surface that the rounded corners cut away.
        bool RoundedBoxHitOS(float3 rayOriginOS, float3 rayDirWS, out float3 hitOS)
        {
            float3 halfExtents = _BoxHalfExtents.xyz;

            float3 rayDirOS = normalize(TransformWorldToObjectDir(rayDirWS));

            float tHit;
            bool hit =
                RayMarchRoundedBoxOS(
                    rayOriginOS, rayDirOS, halfExtents, RoundedSilhouetteRadius(), tHit);

            hitOS = rayOriginOS + rayDirOS * tHit;
            return hit;
        }
        ENDHLSL

        Pass
        {
            Name "ForwardUnlit"
            Tags { "LightMode"="UniversalForward" }

            ZWrite On
            Cull Back

            HLSLPROGRAM

            #pragma vertex vert
            #pragma fragment frag

            #pragma shader_feature_local _ _ROUNDED_SILHOUETTE_ON
            #pragma shader_feature_local_fragment _ _RECEIVE_SHADOWS_ON

            // Unity cannot resolve #pragma inside #if, so the shadow variants are
            // always declared; they are only sampled when _RECEIVE_SHADOWS_ON is set.
            #pragma multi_compile_fragment _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT

            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Fog.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;

                float2 uv : TEXCOORD0;

                float3 worldPos : TEXCOORD1;
                float3 worldNormal : TEXCOORD2;
                float3 localPos : TEXCOORD3;

                float fogFactor : TEXCOORD4;
            };

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            TEXTURECUBE(_Environment);
            SAMPLER(sampler_Environment);

            // ------------------------------------------------------------
            // Hash / Noise
            // ------------------------------------------------------------

            float hash31(float3 p)
            {
                p = frac(p * 0.1031);
                p += dot(p, p.yzx + 33.33);
                return frac((p.x + p.y) * p.z);
            }

            float noise3D(float3 p)
            {
                float3 i = floor(p);
                float3 f = frac(p);

                f = f * f * (3.0 - 2.0 * f);

                float n000 = hash31(i + float3(0,0,0));
                float n100 = hash31(i + float3(1,0,0));
                float n010 = hash31(i + float3(0,1,0));
                float n110 = hash31(i + float3(1,1,0));

                float n001 = hash31(i + float3(0,0,1));
                float n101 = hash31(i + float3(1,0,1));
                float n011 = hash31(i + float3(0,1,1));
                float n111 = hash31(i + float3(1,1,1));

                float x00 = lerp(n000, n100, f.x);
                float x10 = lerp(n010, n110, f.x);
                float x01 = lerp(n001, n101, f.x);
                float x11 = lerp(n011, n111, f.x);

                float y0 = lerp(x00, x10, f.y);
                float y1 = lerp(x01, x11, f.y);

                return lerp(y0, y1, f.z);
            }

            float3 noiseNormal(float3 worldPos)
            {
                float e = _NoiseEpslion;
                float3 p = worldPos * _NoiseScale;

                float dx =
                    noise3D(p + float3(e,0,0)) -
                    noise3D(p - float3(e,0,0));

                float dy =
                    noise3D(p + float3(0,e,0)) -
                    noise3D(p - float3(0,e,0));

                float dz =
                    noise3D(p + float3(0,0,e)) -
                    noise3D(p - float3(0,0,e));

                return float3(dx, dy, dz);
            }

            // ------------------------------------------------------------
            // Ray-box exit intersection
            // ro is assumed inside box
            // ------------------------------------------------------------

            bool RayBoxExit(float3 ro, float3 rd, float3 halfExtents, out float tExit)
            {
                float3 rdSafe = rd + (1.0 - step(1e-6, abs(rd))) * 1e-6;
                float3 invRd = 1.0 / rdSafe;

                float3 t0 = (-halfExtents - ro) * invRd;
                float3 t1 = ( halfExtents - ro) * invRd;

                float3 tMin3 = min(t0, t1);
                float3 tMax3 = max(t0, t1);

                float tEnter = max(max(tMin3.x, tMin3.y), tMin3.z);
                float tLeave = min(min(tMax3.x, tMax3.y), tMax3.z);

                tExit = tLeave;
                return tLeave > max(tEnter, 0.0);
            }

            v2f vert(appdata v)
            {
                v2f o;

                o.vertex = TransformObjectToHClip(v.vertex.xyz);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);

                o.worldPos = TransformObjectToWorld(v.vertex.xyz);
                o.worldNormal = TransformObjectToWorldNormal(v.normal);
                o.localPos = v.vertex.xyz;

                o.fogFactor = ComputeFogFactor(o.vertex.z);

                return o;
            }

        #if defined(_ROUNDED_SILHOUETTE_ON)
            half4 frag(v2f i, out float outDepth : SV_Depth) : SV_Target
        #else
            half4 frag(v2f i) : SV_Target
        #endif
            {
                const float AIR_IOR = 1.0;
                const float ICE_IOR = 1.31;

                float3 halfExtents = _BoxHalfExtents.xyz;

                float3 P = i.worldPos;
                float3 localP = i.localPos;

                // camera -> surface; GetWorldSpaceViewDir covers orthographic too.
                float3 V = -normalize(GetWorldSpaceViewDir(P));

                // --------------------------------------------------------
                // 1. Surface point and normal
                // --------------------------------------------------------

            #if defined(_ROUNDED_SILHOUETTE_ON)
                // Swap the mesh box surface for the rounded box the shadow and
                // depth passes march, so all three agree on the silhouette.
                // The hit lies on the camera ray through localP, so V still is
                // the view direction at the new surface point.
                float shapeRadius = RoundedSilhouetteRadius();

                float3 hitOS;
                clip(RoundedBoxHitOS(localP, V, hitOS) ? 1.0 : -1.0);

                localP = hitOS;
                P = TransformObjectToWorld(hitOS);

                float4 hitCS = TransformObjectToHClip(hitOS);
                outDepth = hitCS.z / hitCS.w;

                // Real geometry now, so the rounded normal is exact - blending it
                // back towards the mesh normal would only re-flatten it.
                float3 N =
                    normalize(
                        TransformObjectToWorldNormal(
                            RoundedBoxNormalOS(localP, halfExtents, shapeRadius)));
            #else
                float shapeRadius = _RoundRadius;

                float3 meshN_WS = normalize(i.worldNormal);

                float3 roundedN_OS =
                    RoundedBoxNormalOS(localP, halfExtents, shapeRadius);

                float3 roundedN_WS =
                    normalize(TransformObjectToWorldNormal(roundedN_OS));

                float3 N =
                    normalize(lerp(meshN_WS, roundedN_WS, _RoundCornerStrength));
            #endif

                // --------------------------------------------------------
                // 2. Small ice surface roughness
                // --------------------------------------------------------

                float3 noiseN = noiseNormal(P);

                N = normalize(
                    N + noiseN * _NoiseStrength
                );

                // --------------------------------------------------------
                // 3. Reflection (outside surface)
                // --------------------------------------------------------

                float3 reflectionDir =
                    reflect(V, N);

                float3 reflectionColor =
                    SAMPLE_TEXTURECUBE(_Environment, sampler_Environment, reflectionDir).rgb
                    * _ReflectionStrength;

                // --------------------------------------------------------
                // 4. Refraction into ice
                // --------------------------------------------------------

                float etaIn = AIR_IOR / ICE_IOR;

                float3 insideDir_WS =
                    refract(V, N, etaIn);

                // total internal reflection won't happen on air->ice,
                // but keep a fallback anyway.
                if (length(insideDir_WS) < 1e-5)
                {
                    insideDir_WS = reflectionDir;
                }

                // Convert refracted direction to object space.
                float3 insideDir_OS =
                    TransformWorldToObjectDir(insideDir_WS);

                // Slight offset so the ray starts just inside the cube.
                float3 localEntry =
                    localP + insideDir_OS * 1e-4;

                // --------------------------------------------------------
                // 5. Compute exit point on cube mesh
                // --------------------------------------------------------

                float tExit = 0.0;
                bool hasExit =
                    RayBoxExit(localEntry, insideDir_OS, halfExtents, tExit);

                float3 localExit = localEntry;
                float3 exitP_WS = P;
                float thicknessWS = 0.0;

                float3 exitDir_WS = insideDir_WS;

                if (hasExit)
                {
                    localExit = localEntry + insideDir_OS * tExit;
                    exitP_WS = TransformObjectToWorld(localExit);

                    thicknessWS = distance(P, exitP_WS);

                    // Approximate rounded normal at exit point
                    float3 exitRoundedN_OS =
                        RoundedBoxNormalOS(localExit, halfExtents, shapeRadius);

                    float3 exitRoundedN_WS =
                        normalize(TransformObjectToWorldNormal(exitRoundedN_OS));

                    // Refract from ice -> air
                    float etaOut = ICE_IOR / AIR_IOR;

                    exitDir_WS =
                        refract(insideDir_WS, exitRoundedN_WS, etaOut);

                    // Total internal reflection fallback
                    if (length(exitDir_WS) < 1e-5)
                    {
                        exitDir_WS =
                            reflect(insideDir_WS, exitRoundedN_WS);
                    }
                }

                // --------------------------------------------------------
                // 6. Refraction color after exiting ice
                // --------------------------------------------------------

                float3 refractionColor =
                    SAMPLE_TEXTURECUBE(_Environment, sampler_Environment, normalize(exitDir_WS)).rgb
                    * _RefractionStrength;

                // --------------------------------------------------------
                // 7. Thickness-based absorption
                // --------------------------------------------------------

                float effectiveThickness =
                    thicknessWS * _Thickness;

                // fallback: if no valid exit, keep a minimum thickness feel
                if (!hasExit)
                {
                    effectiveThickness = _Thickness;
                }

                float absorption =
                    1.0 - exp(-effectiveThickness * _Absorption);

                float3 transmission =
                    lerp(
                        refractionColor,
                        _IceColor.rgb,
                        absorption
                    );

                // --------------------------------------------------------
                // 8. Fresnel
                // --------------------------------------------------------

                float F0 =
                    pow(
                        (AIR_IOR - ICE_IOR) /
                        (AIR_IOR + ICE_IOR),
                        2.0
                    );

                float NdotV =
                    saturate(dot(-V, N));

                float fresnel =
                    F0 +
                    (1.0 - F0) *
                    pow(1.0 - NdotV, 5.0);

                // --------------------------------------------------------
                // 9. Reflection + Transmission
                // --------------------------------------------------------

                float3 iceColor =
                    lerp(
                        transmission,
                        reflectionColor,
                        fresnel
                    );

                float3 baseColor =
                    SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv).rgb;

                iceColor *= baseColor;

                // --------------------------------------------------------
                // 10. Main light shadow (this pass is unlit, so it has to be
                //     applied manually)
                // --------------------------------------------------------

            #if defined(_RECEIVE_SHADOWS_ON)
                float4 shadowCoord = TransformWorldToShadowCoord(P);

                half shadowAtten =
                    MainLightShadow(
                        shadowCoord,
                        P,
                        half4(1.0, 1.0, 1.0, 1.0),
                        _MainLightOcclusionProbes);

                // 0 = fully shadowed, 1 = lit, scaled by the material strength.
                float shadowMask =
                    lerp(1.0, shadowAtten, _ShadowStrength);

                iceColor = lerp(iceColor * _ShadowTint.rgb, iceColor, shadowMask);
            #endif

                half4 col = half4(iceColor, 1.0);

                col.rgb = MixFog(col.rgb, i.fogFactor);
                return col;
            }

            ENDHLSL
        }

        // Casts shadows into the directional / punctual light shadow maps.
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

            // Set by URP when rendering point / spot light shadows.
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #pragma shader_feature_local _ _ROUNDED_SILHOUETTE_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            // Filled in by ShadowUtils.SetupShadowCasterConstantBuffer, so these stay
            // outside UnityPerMaterial.
            float3 _LightDirection;
            float3 _LightPosition;

            struct ShadowAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
            };

            struct ShadowVaryings
            {
                float4 positionCS : SV_POSITION;
                float3 positionOS : TEXCOORD0;
            };

            // Direction the shadow-map ray travels through this fragment.
            float3 ShadowRayDirWS(float3 positionWS)
            {
            #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                return normalize(positionWS - _LightPosition);
            #else
                return -_LightDirection;
            #endif
            }

            // Direction from the fragment towards the light, as ApplyShadowBias expects.
            float3 ShadowLightDirWS(float3 positionWS)
            {
            #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                return normalize(_LightPosition - positionWS);
            #else
                return _LightDirection;
            #endif
            }

            float4 ShadowPositionHClip(float3 positionWS, float3 normalWS)
            {
                float4 positionCS =
                    TransformWorldToHClip(
                        ApplyShadowBias(positionWS, normalWS, ShadowLightDirWS(positionWS)));

                return ApplyShadowClamping(positionCS);
            }

            ShadowVaryings shadowVert (ShadowAttributes input)
            {
                ShadowVaryings output;

                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);

                // The mesh box fully contains the rounded box, so rasterising it
                // gives the rounded path a conservative set of candidate pixels.
                output.positionCS = ShadowPositionHClip(positionWS, normalWS);
                output.positionOS = input.positionOS.xyz;

                return output;
            }

        #if defined(_ROUNDED_SILHOUETTE_ON)
            half4 shadowFrag (ShadowVaryings input, out float outDepth : SV_Depth) : SV_Target
            {
                float3 rayDirWS =
                    ShadowRayDirWS(TransformObjectToWorld(input.positionOS));

                float3 hitOS;

                // Missed the rounded box entirely: this pixel is part of the
                // corner the rounding cut away, so it must not occlude.
                clip(RoundedBoxHitOS(input.positionOS, rayDirWS, hitOS) ? 1.0 : -1.0);

                float3 hitWS = TransformObjectToWorld(hitOS);

                float3 hitNormalWS =
                    normalize(
                        TransformObjectToWorldNormal(
                            RoundedBoxNormalOS(
                                hitOS, _BoxHalfExtents.xyz, RoundedSilhouetteRadius())));

                float4 positionCS = ShadowPositionHClip(hitWS, hitNormalWS);

                outDepth = positionCS.z / positionCS.w;
                return 0;
            }
        #else
            half4 shadowFrag (ShadowVaryings input) : SV_Target
            {
                return 0;
            }
        #endif
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

            #pragma shader_feature_local _ _ROUNDED_SILHOUETTE_ON

            struct DepthAttributes
            {
                float4 positionOS : POSITION;
            };

            struct DepthVaryings
            {
                float4 positionCS : SV_POSITION;
                float3 positionOS : TEXCOORD0;
            };

            DepthVaryings depthVert (DepthAttributes input)
            {
                DepthVaryings output;

                // The mesh box fully contains the rounded box, so rasterising it
                // gives the rounded path a conservative set of candidate pixels.
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.positionOS = input.positionOS.xyz;

                return output;
            }

        #if defined(_ROUNDED_SILHOUETTE_ON)
            half4 depthFrag (DepthVaryings input, out float outDepth : SV_Depth) : SV_Target
            {
                float3 positionWS = TransformObjectToWorld(input.positionOS);

                // camera -> surface; GetWorldSpaceViewDir covers orthographic too.
                float3 rayDirWS = -normalize(GetWorldSpaceViewDir(positionWS));

                float3 hitOS;
                clip(RoundedBoxHitOS(input.positionOS, rayDirWS, hitOS) ? 1.0 : -1.0);

                float4 positionCS = TransformObjectToHClip(hitOS);
                float roundedDepth = positionCS.z / positionCS.w;

                // The march re-derives the depth from an interpolated object-space
                // position, so it lands a few ULPs away from what the forward pass
                // computes for the same hit. Clamp to the mesh surface and nudge a
                // hair further from the camera, so a forward fragment can never be
                // rejected by ZTest LEqual and speckle the block. (Depth priming,
                // which tests Equal, cannot be made safe this way and is not
                // supported while the rounded silhouette is on.)
            #if UNITY_REVERSED_Z
                outDepth = min(roundedDepth, input.positionCS.z) * (1.0 - 1e-6);
            #else
                outDepth = max(roundedDepth, input.positionCS.z) + 1e-6;
            #endif

                return 0;
            }
        #else
            half4 depthFrag (DepthVaryings input) : SV_Target
            {
                return 0;
            }
        #endif
            ENDHLSL
        }
    }
}
