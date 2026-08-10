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
    }

    SubShader
    {
        Tags
        {
            "RenderType"="Opaque"
        }

        LOD 100

        Pass
        {
            ZWrite On
            Cull Back

            CGPROGRAM

            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog

            #include "UnityCG.cginc"

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

                UNITY_FOG_COORDS(4)
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;

            samplerCUBE _Environment;

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
            // Rounded box fake normal
            // ------------------------------------------------------------

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
                radius = min(radius, min(halfExtents.x, min(halfExtents.y, halfExtents.z)) - 1e-4);

                if (radius <= 1e-5)
                    return BoxFaceNormalOS(p, halfExtents);

                float3 inner = clamp(p, -halfExtents + radius, halfExtents - radius);
                float3 n = p - inner;
                float lenN = length(n);

                if (lenN <= 1e-5)
                    return BoxFaceNormalOS(p, halfExtents);

                return n / lenN;
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

                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);

                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.worldNormal = UnityObjectToWorldNormal(v.normal);
                o.localPos = v.vertex.xyz;

                UNITY_TRANSFER_FOG(o, o.vertex);

                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                const float AIR_IOR = 1.0;
                const float ICE_IOR = 1.31;

                float3 halfExtents = _BoxHalfExtents.xyz;

                float3 P = i.worldPos;
                float3 localP = i.localPos;

                float3 meshN_WS = normalize(i.worldNormal);

                // --------------------------------------------------------
                // 1. Rounded corner normal in object space
                // --------------------------------------------------------

                float3 roundedN_OS =
                    RoundedBoxNormalOS(localP, halfExtents, _RoundRadius);

                float3 roundedN_WS =
                    normalize(UnityObjectToWorldNormal(roundedN_OS));

                float3 N =
                    normalize(lerp(meshN_WS, roundedN_WS, _RoundCornerStrength));

                // camera -> surface
                float3 V =
                    normalize(P - _WorldSpaceCameraPos);

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
                    texCUBE(_Environment, reflectionDir).rgb
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
                    normalize(mul((float3x3)unity_WorldToObject, insideDir_WS));

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
                    exitP_WS = mul(unity_ObjectToWorld, float4(localExit, 1.0)).xyz;

                    thicknessWS = distance(P, exitP_WS);

                    // Approximate rounded normal at exit point
                    float3 exitRoundedN_OS =
                        RoundedBoxNormalOS(localExit, halfExtents, _RoundRadius);

                    float3 exitRoundedN_WS =
                        normalize(UnityObjectToWorldNormal(exitRoundedN_OS));

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
                    texCUBE(_Environment, normalize(exitDir_WS)).rgb
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
                    tex2D(_MainTex, i.uv).rgb;

                iceColor *= baseColor;

                fixed4 col = fixed4(iceColor, 1.0);

                UNITY_APPLY_FOG(i.fogCoord, col);
                return col;
            }

            ENDCG
        }
    }
}
