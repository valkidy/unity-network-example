Shader "Custom/Wax Candy Layers V7 Letter"
{
    // V6 color strata + multi-color outline, driven by a letter's edge distance map
    // (planar UVs, linear R = distance / _DistanceRange), with the glossy wax surface back on.
    // Edge bands follow the silhouette and the extrusion walls but stay open along the base;
    // body zones are nested arches standing on the letter's bottom center.
    Properties
    {
        [Header(Letter Shape)]
        [NoScaleOffset] _EdgeDistanceMap ("Edge Distance Map - Linear R", 2D) = "white" {}
        _DistanceRange ("Map Distance Range - Object Units", Float) = 0.5
        // Defaults describe the reference letter's map, so it renders as before.
        _DistanceMapRect ("Map Rect - Object XMin YMin Width Height", Vector) = (-1.1, 0, 2.2, 2.3)
        _LetterBounds ("Letter Bounds - Object XMin YMin XMax YMax", Vector) = (-0.7912, 0.1576, 0.7534, 2.0575)
        _FrontAxis ("Extrusion Axis - Object Space", Vector) = (0, 0, 1, 0)
        _DepthCenter ("Extrusion Center", Float) = 0
        _HalfDepth ("Half Extrusion Depth", Float) = 0.2
        _WallDistanceScale ("Wall Distance Scale - Keeps Walls In Bands", Range(0,1)) = 0.55
        _OpenBottom ("Open Bands Along Base", Range(0,1)) = 1
        _OpenBottomBlend ("Open Base Blend", Range(0.001,0.2)) = 0.04
        _OpenBottomPush ("Open Base Push", Range(0,1)) = 0.5

        [Header(Outline Flow Layers Outside To Inside)]
        _OutlineColor ("Outline Outer - Orange", Color) = (1, 0.42, 0.03, 1)
        _OutlineColor2 ("Outline Middle - Yellow", Color) = (1, 0.82, 0.08, 1)
        _OutlineColor3 ("Outline Inner - Green", Color) = (0.3, 0.9, 0.05, 1)
        _OutlineYellow ("Yellow Strength", Range(0,1)) = 0.9
        _OutlineYellowStart ("Yellow Layer Start - Across Outline", Range(0,1)) = 0.35
        _OutlineYellowWidth ("Yellow Layer Width", Range(0,1)) = 0.3
        _OutlineYellowCoverage ("Yellow Coverage Along Edge", Range(0,1)) = 0.508
        _OutlineGreen ("Green Strength", Range(0,1)) = 0.9
        _OutlineGreenStart ("Green Layer Start - Across Outline", Range(0,1)) = 0.78
        _OutlineGreenWidth ("Green Layer Width", Range(0,1)) = 0.22
        _OutlineGreenCoverage ("Green Coverage Along Edge", Range(0,1)) = 0.397
        _OutlineFlowScale ("Flow Noise Scale", Float) = 1.6
        _OutlineFlowWobble ("Flow Layer Wobble", Range(0,0.5)) = 0.5
        _OutlineFlowSoftness ("Flow Layer Softness", Range(0.001,0.5)) = 0.092
        _OutlineWidth ("Outline Width", Range(0,0.3)) = 0.12
        _OutlineShade ("Outline Outer Edge Darken", Range(0,1)) = 0.128
        _OutlineBlur ("Outline To Body Blur - Object Units", Range(0,0.2)) = 0.03

        [Header(Body Layers Anchored At Bottom Center Inside To Outside)]
        _Zone1Color ("Zone 1 - Solid Hot Pink", Color) = (1, 0.06, 0.72, 1)
        _Zone2Color ("Zone 2 - Pink", Color) = (1, 0.42, 0.84, 1)
        _Zone3Color ("Zone 3 - Pink Layer", Color) = (1, 0.55, 0.88, 1)
        _Zone4Color ("Zone 4 - Pink Layer", Color) = (0.5, 0.706, 0.774, 1)
        _BodyColor ("Body Base - Cyan", Color) = (0.1, 0.87, 0.93, 1)
        _Zone1Size ("Zone 1 Size - Width Height Fraction", Vector) = (0.79, 0.3, 0, 0)
        _Zone2Size ("Zone 2 Size - Width Height Fraction", Vector) = (1.01, 0.39, 0, 0)
        _Zone3Size ("Zone 3 Size - Width Height Fraction", Vector) = (0.92, 0.66, 0, 0)
        _Zone4Size ("Zone 4 Size - Width Height Fraction", Vector) = (0.99, 0.83, 0, 0)
        _ArchSquareness ("Arch Squareness", Range(1.5,6)) = 1.93
        _ArchLean ("Arch Lean", Range(-0.5,0.5)) = -0.24
        _ZoneWobble ("Zone Edge Wobble", Range(0,0.3)) = 0.076
        _ZoneSoftness ("Zone Edge Softness", Range(0.001,0.3)) = 0.035

        [Header(Pigment Grain Per Layer)]
        _Zone1Grain ("Zone 1 Grain", Range(0,0.6)) = 0
        _Zone2Grain ("Zone 2 Grain", Range(0,0.6)) = 0.156
        _Zone3Grain ("Zone 3 Grain", Range(0,0.6)) = 0.182
        _Zone4Grain ("Zone 4 Grain", Range(0,0.6)) = 0.063
        _BodyGrain ("Body Base Grain", Range(0,0.6)) = 0
        _OutlineGrain ("Outline Grain", Range(0,0.6)) = 0.145
        _GrainScale ("Grain Scale", Float) = 70

        [Header(Watercolor Noise)]
        _NoiseScale ("Noise Scale", Float) = 2.5
        _EdgeWobble ("Layer Edge Wobble", Range(0,0.2)) = 0.022
        _EdgeRoughness ("Layer Edge Roughness", Range(0,0.05)) = 0.003

        [Header(Ceramic Body)]
        _PuffWidth ("Puffy Edge Width - Object Units", Range(0.01,0.6)) = 0.064
        _PuffHeight ("Puffy Edge Height - Object Units", Range(0,0.2)) = 0.0198
        // About ten texels of the reference letter's 2048 map.
        _PuffSampleDistance ("Puff Gradient Footprint - Object Units", Range(0.002,0.05)) = 0.011
        _FaceDomeWidth ("Face Dome Width - Object Units", Range(0.05,1)) = 0.2
        _FaceDomeHeight ("Face Dome Height - Object Units", Range(0,0.2)) = 0.02
        _DiffuseStrength ("Body Diffuse Lighting - Zero Is Flat Pigment", Range(0,1)) = 0.65
        _DiffuseWrap ("Diffuse Softness", Range(0,1)) = 0.3
        _AmbientStrength ("Ambient Fill", Range(0,2)) = 1
        _Smoothness ("Body Smoothness Under Glaze", Range(0,1)) = 0.4
        _GlazeBreak ("Glaze Break - Lighter Rims", Range(0,1)) = 0.2

        [Header(Ceramic Glaze)]
        _Coat ("Glaze Coverage", Range(0,1)) = 1
        _CoatSmoothness ("Glaze Smoothness", Range(0,1)) = 0.96
        _SpecularStrength ("Glaze Specular And Probe Reflection", Range(0,3)) = 1
        _EnvReflection ("Studio Reflection At Grazing Angles", Range(0,2)) = 0.7
        // Screen-derivative bump: large values break highlights into ragged blobs.
        _Wobble ("Glaze Ripple", Range(0,0.1)) = 0.0005
        _StudioHighlight ("Softbox Highlight", Range(0,3)) = 1.1
        _StudioSize ("Softbox Size", Range(0.05,1)) = 0.4
        _StudioSharpness ("Softbox Edge Sharpness", Range(1,64)) = 14
        _FrontSoftbox ("Front Softbox Highlight", Range(0,3)) = 1.2
        _FrontSoftboxCenter ("Front Softbox Position - View XY", Vector) = (-0.15, 0.2, 0, 0)
        _FrontSoftboxSize ("Front Softbox Half Size - View XY", Vector) = (0.12, 0.08, 0, 0)
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" "Queue"="Geometry" }
        LOD 300
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        CBUFFER_START(UnityPerMaterial)
            float4 _LetterBounds, _FrontAxis, _EdgeDistanceMap_TexelSize, _DistanceMapRect;
            float _DistanceRange, _DepthCenter, _HalfDepth, _WallDistanceScale;
            float _OpenBottom, _OpenBottomBlend, _OpenBottomPush;
            float4 _OutlineColor, _OutlineColor2, _OutlineColor3;
            float4 _Zone1Color, _Zone2Color, _Zone3Color, _Zone4Color, _BodyColor;
            float4 _Zone1Size, _Zone2Size, _Zone3Size, _Zone4Size;
            float _OutlineYellow, _OutlineYellowStart, _OutlineYellowWidth, _OutlineGreen, _OutlineGreenStart, _OutlineGreenWidth;
            float _OutlineYellowCoverage, _OutlineGreenCoverage;
            float _OutlineFlowScale, _OutlineFlowWobble, _OutlineFlowSoftness, _OutlineWidth, _OutlineShade, _OutlineBlur;
            float _ArchSquareness, _ArchLean, _ZoneWobble, _ZoneSoftness;
            float _Zone1Grain, _Zone2Grain, _Zone3Grain, _Zone4Grain, _BodyGrain, _OutlineGrain, _GrainScale;
            float _NoiseScale, _EdgeWobble, _EdgeRoughness;
            float _PuffWidth, _PuffHeight, _PuffSampleDistance, _FaceDomeWidth, _FaceDomeHeight, _Wobble;
            float _DiffuseStrength, _DiffuseWrap, _AmbientStrength, _GlazeBreak, _EnvReflection;
            float _Smoothness, _Coat, _CoatSmoothness, _SpecularStrength;
            float _StudioHighlight, _StudioSize, _StudioSharpness, _FrontSoftbox;
            float4 _FrontSoftboxCenter, _FrontSoftboxSize;
        CBUFFER_END
        TEXTURE2D(_EdgeDistanceMap);
        SAMPLER(sampler_EdgeDistanceMap);

        struct Attributes
        {
            float4 positionOS : POSITION;
            float3 normalOS : NORMAL;
            float2 uv : TEXCOORD0;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };

        float Hash(float2 p)
        {
            float3 p3 = frac(float3(p.xyx) * 0.1031);
            p3 += dot(p3, p3.yzx + 33.33);
            return frac((p3.x + p3.y) * p3.z);
        }
        float ValueNoise(float2 p)
        {
            float2 i = floor(p), f = frac(p);
            float2 u = f * f * (3.0 - 2.0 * f);
            return lerp(lerp(Hash(i), Hash(i + float2(1, 0)), u.x),
                        lerp(Hash(i + float2(0, 1)), Hash(i + 1.0), u.x), u.y);
        }
        float FBM(float2 p)
        {
            return ValueNoise(p) * 0.53 + ValueNoise(p * 2.07 + 5.3) * 0.28
                 + ValueNoise(p * 4.13 + 1.7) * 0.13 + ValueNoise(p * 8.21 + 9.1) * 0.06;
        }
        // Superellipse radius of an arch standing on the bottom center; 1 on its boundary.
        float ArchRadius(float2 s, float2 size)
        {
            float2 q = abs(float2(s.x, max(s.y, 0.0))) / max(size, 0.001);
            return pow(pow(q.x, _ArchSquareness) + pow(q.y, _ArchSquareness), 1.0 / _ArchSquareness);
        }
        float Between(float x, float start, float width, float soft)
        {
            return smoothstep(start - soft, start + soft, x) * (1.0 - smoothstep(start + width - soft, start + width + soft, x));
        }
        float ZoneMask(float r)
        {
            return 1.0 - smoothstep(1.0 - _ZoneSoftness, 1.0 + _ZoneSoftness, r);
        }
        // Distance to the nearest silhouette edge or extrusion rim, with the base opened up.
        float LetterEdgeDistance(float2 uv, float3 positionOS)
        {
            float planar = SAMPLE_TEXTURE2D(_EdgeDistanceMap, sampler_EdgeDistanceMap, uv).r * _DistanceRange;
            float3 axis = dot(_FrontAxis.xyz, _FrontAxis.xyz) > 1e-6 ? normalize(_FrontAxis.xyz) : float3(0, 0, 1);
            float rimDepth = max(0.0, _HalfDepth - abs(dot(positionOS, axis) - _DepthCenter));
            float d = length(float2(planar, rimDepth * _WallDistanceScale));
            // Where the base is the nearest edge, d equals the height above the base.
            float aboveBase = positionOS.y - _LetterBounds.y;
            float baseNearest = 1.0 - smoothstep(0.0, _OpenBottomBlend, aboveBase - planar);
            return d + baseNearest * _OpenBottom * _OpenBottomPush;
        }
        // Surface-gradient bump: no tangent dependency; smooth mesh normals are still required.
        float3 BumpNormal(float3 positionWS, float3 normalWS, float height)
        {
            float3 dx = ddx(positionWS), dy = ddy(positionWS);
            float3 r1 = cross(dy, normalWS), r2 = cross(normalWS, dx);
            float det = dot(dx, r1);
            float3 grad = (ddx(height) * r1 + ddy(height) * r2) / (abs(det) + 1e-8) * sign(det);
            return SafeNormalize(normalWS - grad);
        }
        // World-space surface gradient of a quantity with screen derivatives (dfdx, dfdy).
        float3 SurfaceGradient(float3 positionWS, float3 normalWS, float dfdx, float dfdy)
        {
            float3 dx = ddx(positionWS), dy = ddy(positionWS);
            float3 r1 = cross(dy, normalWS), r2 = cross(normalWS, dx);
            float det = dot(dx, r1);
            return (dfdx * r1 + dfdy * r2) / (abs(det) + 1e-8) * sign(det);
        }
        // Inflates the flat faces: height = PuffHeight * (1 - (1 - d / PuffWidth)^2) near the silhouette.
        // An 8-bit map makes texel-scale differences mostly quantization noise. Differences are
        // taken over a wide footprint and only their direction is kept (a distance field has unit
        // gradient), carried to world space through the UV derivatives, which are linear per triangle.
        // The footprint is in object units rather than texels, so generated glyph maps of any
        // resolution give the same bevel; _DistanceMapRect converts it to UV.
        float3 PuffNormal(float2 uv, float3 positionOS, float3 positionWS, float3 normalWS)
        {
            float2 offset = _PuffSampleDistance / max(_DistanceMapRect.zw, 1e-4);
            float c = SAMPLE_TEXTURE2D(_EdgeDistanceMap, sampler_EdgeDistanceMap, uv).r * _DistanceRange;
            float du = (SAMPLE_TEXTURE2D(_EdgeDistanceMap, sampler_EdgeDistanceMap, uv + float2(offset.x, 0)).r
                      - SAMPLE_TEXTURE2D(_EdgeDistanceMap, sampler_EdgeDistanceMap, uv - float2(offset.x, 0)).r)
                      * _DistanceRange / (2.0 * offset.x);
            float dv = (SAMPLE_TEXTURE2D(_EdgeDistanceMap, sampler_EdgeDistanceMap, uv + float2(0, offset.y)).r
                      - SAMPLE_TEXTURE2D(_EdgeDistanceMap, sampler_EdgeDistanceMap, uv - float2(0, offset.y)).r)
                      * _DistanceRange / (2.0 * offset.y);
            float3 gradU = SurfaceGradient(positionWS, normalWS, ddx(uv.x), ddy(uv.x));
            float3 gradV = SurfaceGradient(positionWS, normalWS, ddx(uv.y), ddy(uv.y));
            float3 gradD = du * gradU + dv * gradV;
            float gradLength = length(gradD);
            // Unit length inside the letter; fades where the map is clamped to zero outside it.
            float valid = saturate(gradLength * 2.0);
            float t = saturate(c / _PuffWidth);
            float slope = _PuffHeight * 2.0 * (1.0 - t) / _PuffWidth;
            // Broad dome across the whole face so flat fronts curve enough to catch softbox reflections.
            // It uses a wide, unnormalized gradient: opposite slopes cancel toward a stroke's center
            // line, which rounds the ridge instead of folding the normal there. The footprint is half
            // the dome width, converted to UV through the planar mapping's UV-per-object-unit ratio.
            float uvPerUnit = length(fwidth(uv)) / max(length(fwidth(positionOS)), 1e-6);
            float2 domeOffset = max(0.5 * _FaceDomeWidth * uvPerUnit, _EdgeDistanceMap_TexelSize.x * 10.0).xx;
            float domeU = (SAMPLE_TEXTURE2D(_EdgeDistanceMap, sampler_EdgeDistanceMap, uv + float2(domeOffset.x, 0)).r
                         - SAMPLE_TEXTURE2D(_EdgeDistanceMap, sampler_EdgeDistanceMap, uv - float2(domeOffset.x, 0)).r)
                         * _DistanceRange / (2.0 * domeOffset.x);
            float domeV = (SAMPLE_TEXTURE2D(_EdgeDistanceMap, sampler_EdgeDistanceMap, uv + float2(0, domeOffset.y)).r
                         - SAMPLE_TEXTURE2D(_EdgeDistanceMap, sampler_EdgeDistanceMap, uv - float2(0, domeOffset.y)).r)
                         * _DistanceRange / (2.0 * domeOffset.y);
            // Planar UVs only describe the front and back faces; leave walls and bevels to the mesh.
            float3 axis = dot(_FrontAxis.xyz, _FrontAxis.xyz) > 1e-6 ? _FrontAxis.xyz : float3(0, 0, 1);
            float frontness = smoothstep(0.6, 0.9, abs(dot(normalWS, TransformObjectToWorldDir(axis))));
            float3 direction = gradD / max(gradLength, 1e-5);
            float tDome = saturate(c / _FaceDomeWidth);
            float domeSlope = _FaceDomeHeight * 2.0 * (1.0 - tDome) / _FaceDomeWidth;
            float3 domeGrad = domeU * gradU + domeV * gradV;
            return SafeNormalize(normalWS - (slope * direction * valid + domeSlope * domeGrad) * frontness);
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForwardOnly" }
            Cull Back ZWrite On
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #pragma multi_compile_fog
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #define _SPECULAR_SETUP 1
            #define _CLEARCOAT 1
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 positionOS : TEXCOORD2;
                float2 uv : TEXCOORD3;
                half fogFactor : TEXCOORD4;
                half3 vertexLighting : TEXCOORD5;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes IN)
            {
                Varyings OUT = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);
                VertexPositionInputs p = GetVertexPositionInputs(IN.positionOS.xyz);
                OUT.positionCS = p.positionCS;
                OUT.positionWS = p.positionWS;
                OUT.normalWS = TransformObjectToWorldNormal(IN.normalOS);
                OUT.positionOS = IN.positionOS.xyz;
                OUT.uv = IN.uv;
                OUT.fogFactor = ComputeFogFactor(p.positionCS.z);
                OUT.vertexLighting = VertexLighting(p.positionWS, OUT.normalWS);
                return OUT;
            }

            // Rounded-rectangle softbox fixed relative to the camera, so glossy white streaks
            // read even in a scene with a black sky and no reflection probes.
            half3 CeramicDiffuse(Light light, half3 n)
            {
                half wrapped = saturate((dot(n, light.direction) + _DiffuseWrap) / (1.0 + _DiffuseWrap));
                return light.color * wrapped * light.distanceAttenuation * light.shadowAttenuation;
            }
            half Softbox(float3 reflectionVS, float2 center, float2 halfSize)
            {
                float2 uv = reflectionVS.xy / max(reflectionVS.z, 0.05) - center;
                float dist = length(max(abs(uv) - halfSize, 0.0));
                return saturate(1.0 - dist * _StudioSharpness) * smoothstep(0.0, 0.2, reflectionVS.z);
            }

            half4 Frag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);
                float d = LetterEdgeDistance(IN.uv, IN.positionOS);
                float2 p = IN.positionOS.xy;

                float2 np = p * _NoiseScale;
                float warp = FBM(np + float2(3.1, 7.4));
                float mottle = FBM(np * 0.7 + warp * 1.5);
                float rough = ValueNoise(np * 5.0 + warp * 3.0);
                // The outline's inner boundary wanders with the watercolor noise.
                float dB = d + (mottle - 0.5) * 2.0 * _EdgeWobble + (rough - 0.5) * 2.0 * _EdgeRoughness;
                float outlineWidth = _OutlineWidth;

                // Body zones: nested arches in letter-normalized space (x -1..1, y 0..1 from the base).
                float2 halfExtent = max(0.5 * (_LetterBounds.zw - _LetterBounds.xy), 1e-4);
                float2 s = float2((p.x - (_LetterBounds.x + halfExtent.x)) / halfExtent.x,
                                  (p.y - _LetterBounds.y) / (2.0 * halfExtent.y));
                s.x += _ArchLean * s.y;
                float layerNoiseA = FBM(np * 0.9 + float2(31.7, 12.3));
                float layerNoiseB = FBM(np * 1.1 + float2(8.9, 27.4));
                float zone1 = ZoneMask(ArchRadius(s, _Zone1Size.xy) + (mottle - 0.5) * 2.0 * _ZoneWobble);
                float zone2 = max(ZoneMask(ArchRadius(s, _Zone2Size.xy) + (warp - 0.5) * 2.0 * _ZoneWobble), zone1);
                float zone3 = max(ZoneMask(ArchRadius(s, _Zone3Size.xy) + (layerNoiseA - 0.5) * 2.0 * _ZoneWobble), zone2);
                float zone4 = max(ZoneMask(ArchRadius(s, _Zone4Size.xy) + (layerNoiseB - 0.5) * 2.0 * _ZoneWobble), zone3);

                // Body base under the pink layers.
                half3 body = _BodyColor.rgb;
                body = lerp(body, _Zone4Color.rgb, zone4);
                body = lerp(body, _Zone3Color.rgb, zone3);
                body = lerp(body, _Zone2Color.rgb, zone2);
                body = lerp(body, _Zone1Color.rgb, zone1);

                // Outline: layers parallel to the silhouette (orange -> yellow -> green toward the inside).
                // Low-frequency noise shifts each layer's position, so layers swell and thin along the
                // edge like poured streams instead of breaking into islands.
                float across = saturate(d / max(outlineWidth, 1e-4));
                float2 fp = p * _OutlineFlowScale;
                float flowYellow = across + (FBM(fp + float2(21.7, 4.2)) - 0.5) * 2.0 * _OutlineFlowWobble;
                float flowGreen = across + (FBM(fp * 1.3 + float2(42.1, 17.9)) - 0.5) * 2.0 * _OutlineFlowWobble;
                // Coverage: slower noise lets each layer run for a stretch of the edge and then fade out.
                float yellowRun = smoothstep(-0.08, 0.08, FBM(fp * 0.6 + float2(63.2, 9.8)) - (0.7 - 0.4 * _OutlineYellowCoverage));
                float greenRun = smoothstep(-0.08, 0.08, FBM(fp * 0.6 + float2(14.5, 71.3)) - (0.7 - 0.4 * _OutlineGreenCoverage));
                float yellow = Between(flowYellow, _OutlineYellowStart, _OutlineYellowWidth, _OutlineFlowSoftness) * yellowRun * _OutlineYellow;
                float green = Between(flowGreen, _OutlineGreenStart, _OutlineGreenWidth, _OutlineFlowSoftness) * greenRun * _OutlineGreen;
                half3 outline = lerp(_OutlineColor.rgb, _OutlineColor2.rgb, yellow);
                outline = lerp(outline, _OutlineColor3.rgb, green);
                outline *= lerp(1.0 - _OutlineShade, 1.0, across);

                // Blurred hand-off from the outline to the body layers.
                float blur = max(_OutlineBlur, fwidth(d));
                float bodyMask = smoothstep(outlineWidth - blur, outlineWidth + blur, dB);
                half3 color = lerp(outline, body, bodyMask);

                // Watercolor granulation: two scales of speckle, strength set per zone.
                float grainAmount = lerp(_BodyGrain, _Zone4Grain, zone4);
                grainAmount = lerp(grainAmount, _Zone3Grain, zone3);
                grainAmount = lerp(grainAmount, _Zone2Grain, zone2);
                grainAmount = lerp(grainAmount, _Zone1Grain, zone1);
                grainAmount = lerp(_OutlineGrain, grainAmount, bodyMask);
                float g = ValueNoise(p * _GrainScale + warp * 4.0) * 0.6
                        + ValueNoise(p * _GrainScale * 2.7 + 11.3) * 0.4;
                half3 pigment = saturate(color * (1.0 + (g - 0.5) * 2.0 * grainAmount));

                // Glaze break: the glaze runs thin over the rims and the lighter body shows through.
                float rimThin = 1.0 - saturate(d / max(_PuffWidth, 1e-4));
                pigment = lerp(pigment, 1.0 - (1.0 - pigment) * 0.55, rimThin * rimThin * _GlazeBreak);

                // Ceramic surface: the edge distance inflates the flat faces so highlights roll off the rims.
                float3 n0 = NormalizeNormalPerPixel(IN.normalWS);
                float3 v = GetWorldSpaceNormalizeViewDir(IN.positionWS);
                float3 n = BumpNormal(IN.positionWS, PuffNormal(IN.uv, IN.positionOS, IN.positionWS, n0), (rough - 0.5) * _Wobble);

                InputData inputData = (InputData)0;
                inputData.positionWS = IN.positionWS;
                inputData.positionCS = IN.positionCS;
                inputData.normalWS = n;
                inputData.viewDirectionWS = v;
                #if defined(_MAIN_LIGHT_SHADOWS_SCREEN)
                    inputData.shadowCoord = ComputeScreenPos(TransformWorldToHClip(IN.positionWS));
                #else
                    inputData.shadowCoord = TransformWorldToShadowCoord(IN.positionWS);
                #endif
                inputData.fogCoord = IN.fogFactor;
                inputData.vertexLighting = IN.vertexLighting;
                inputData.bakedGI = SampleSH(n);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(IN.positionCS);
                inputData.shadowMask = half4(1, 1, 1, 1);

                // Ceramic body: wrapped Lambert plus ambient under the glaze, blended with the flat pigment
                // so the candy colors stay readable in dark scenes.
                Light mainLight = GetMainLight(inputData.shadowCoord, IN.positionWS, inputData.shadowMask);
                half3 lighting = CeramicDiffuse(mainLight, n) + inputData.bakedGI * _AmbientStrength;
                #if defined(_ADDITIONAL_LIGHTS)
                    #if USE_CLUSTER_LIGHT_LOOP
                        UNITY_LOOP for (uint lightIndex = 0; lightIndex < min(URP_FP_DIRECTIONAL_LIGHTS_COUNT, MAX_VISIBLE_LIGHTS); ++lightIndex)
                        {
                            Light light = GetAdditionalLight(lightIndex, IN.positionWS, inputData.shadowMask);
                            lighting += CeramicDiffuse(light, n);
                        }
                    #endif
                    uint count = GetAdditionalLightsCount();
                    LIGHT_LOOP_BEGIN(count)
                        Light light = GetAdditionalLight(lightIndex, IN.positionWS, inputData.shadowMask);
                        lighting += CeramicDiffuse(light, n);
                    LIGHT_LOOP_END
                #endif
                half3 diffuse = pigment * lerp(1.0, lighting, _DiffuseStrength);

                // Glaze: black albedo makes URP return only specular, clear coat and probe reflections.
                SurfaceData surface = (SurfaceData)0;
                surface.albedo = half3(0, 0, 0);
                surface.metallic = 0;
                surface.specular = half3(0.04, 0.04, 0.04);
                surface.smoothness = _Smoothness;
                surface.normalTS = half3(0, 0, 1);
                surface.occlusion = 1;
                surface.alpha = 1;
                surface.clearCoatMask = _Coat;
                surface.clearCoatSmoothness = _CoatSmoothness;

                float3 reflectionWS = reflect(-v, n);
                float3 reflectionVS = mul((float3x3)UNITY_MATRIX_V, reflectionWS);
                half softbox = Softbox(reflectionVS, float2(-0.55, 0.6), float2(0.9, 0.55) * _StudioSize)
                             + Softbox(reflectionVS, float2(0.75, 0.2), float2(0.18, 0.9) * _StudioSize) * 0.6;
                // Front softbox sits near the view axis, where camera-facing surfaces reflect.
                softbox += Softbox(reflectionVS, _FrontSoftboxCenter.xy, _FrontSoftboxSize.xy) * _FrontSoftbox / max(_StudioHighlight, 1e-3);
                // Glazed ceramic mirrors its surroundings at grazing angles (Schlick, F0 = 0.04):
                // a bright-above, dark-below studio gradient keeps that rim reflection in empty scenes.
                half fresnel = 0.04 + 0.96 * pow(1.0 - saturate(dot(n, v)), 5.0);
                half3 studio = lerp(half3(0.02, 0.02, 0.025), half3(1, 1, 1), smoothstep(-0.3, 0.9, reflectionWS.y));
                half3 glaze = UniversalFragmentPBR(inputData, surface).rgb * _SpecularStrength
                            + (studio * fresnel * _EnvReflection + softbox * _StudioHighlight) * _Coat;
                half3 color4 = diffuse * (1.0 - fresnel * _Coat) + glaze;
                return half4(MixFog(color4, IN.fogFactor), 1);
            }
            ENDHLSL
        }
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }
            ZWrite On ZTest LEqual ColorMask 0 Cull Back
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex ShadowVertex
            #pragma fragment EmptyFragment
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            float3 _LightDirection, _LightPosition;
            struct ShadowVaryings { float4 positionCS : SV_POSITION; UNITY_VERTEX_OUTPUT_STEREO };
            ShadowVaryings ShadowVertex(Attributes IN)
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                ShadowVaryings OUT = (ShadowVaryings)0;
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);
                float3 pos = TransformObjectToWorld(IN.positionOS.xyz);
                float3 normal = TransformObjectToWorldNormal(IN.normalOS);
                #if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
                    float3 direction = normalize(_LightPosition - pos);
                #else
                    float3 direction = _LightDirection;
                #endif
                OUT.positionCS = TransformWorldToHClip(ApplyShadowBias(pos, normal, direction));
                #if UNITY_REVERSED_Z
                    OUT.positionCS.z = min(OUT.positionCS.z, UNITY_NEAR_CLIP_VALUE * OUT.positionCS.w);
                #else
                    OUT.positionCS.z = max(OUT.positionCS.z, UNITY_NEAR_CLIP_VALUE * OUT.positionCS.w);
                #endif
                return OUT;
            }
            half4 EmptyFragment() : SV_Target { return 0; }
            ENDHLSL
        }
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }
            ZWrite On ColorMask R Cull Back
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex DepthVertex
            #pragma fragment DepthFragment
            #pragma multi_compile_instancing
            struct DepthVaryings { float4 positionCS : SV_POSITION; UNITY_VERTEX_OUTPUT_STEREO };
            DepthVaryings DepthVertex(Attributes IN)
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                DepthVaryings OUT = (DepthVaryings)0;
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                return OUT;
            }
            half DepthFragment(DepthVaryings IN) : SV_Target { return IN.positionCS.z; }
            ENDHLSL
        }
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode"="DepthNormalsOnly" }
            ZWrite On Cull Back
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex NormalsVertex
            #pragma fragment NormalsFragment
            #pragma multi_compile_instancing
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"
            struct NormalsVaryings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float3 positionOS : TEXCOORD2;
                float2 uv : TEXCOORD3;
                UNITY_VERTEX_OUTPUT_STEREO
            };
            NormalsVaryings NormalsVertex(Attributes IN)
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                NormalsVaryings OUT = (NormalsVaryings)0;
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.normalWS = TransformObjectToWorldNormal(IN.normalOS);
                OUT.positionOS = IN.positionOS.xyz;
                OUT.uv = IN.uv;
                return OUT;
            }
            half4 NormalsFragment(NormalsVaryings IN) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);
                float3 n = PuffNormal(IN.uv, IN.positionOS, IN.positionWS, NormalizeNormalPerPixel(IN.normalWS));
                #if defined(_GBUFFER_NORMALS_OCT)
                    float2 oct = PackNormalOctQuadEncode(n);
                    return half4(PackFloat2To888(saturate(oct * 0.5 + 0.5)), 0);
                #else
                    return half4(n, 0);
                #endif
            }
            ENDHLSL
        }
    }
    Fallback Off
}
