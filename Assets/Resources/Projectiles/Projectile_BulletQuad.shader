Shader "Unlit/BulletQuad"
{
    Properties
    {
        _InnerColor ("Inner Color", Color) = (1,1,1,1)
        _OuterColor ("Outer Color", Color) = (0.05,0.15,1,1)

        _MaskTex ("Mask Texture", 2D) = "white" {}
        _MaskThreshold ("Mask Threshold", Range(0.0,1.0)) = 0.5
        _MaskSoftness ("Mask Softness", Range(0.001,0.5)) = 0.05
        _InvertMask ("Invert Mask", Range(0.0,1.0)) = 0.0

        _Glow ("Glow", Range(0.0,5.0)) = 1.0

        _CenterFocus ("Center Focus", Range(0.0, 1.0)) = 0.45
    }

    SubShader
    {
        Tags
        {
            "Queue"="Transparent"
            "RenderType"="Transparent"
            "RenderPipeline"="UniversalPipeline"
        }

        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off

        Pass
        {
            Name "ForwardUnlit"
            Tags { "LightMode"="UniversalForward" }

            HLSLPROGRAM

            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            TEXTURE2D(_MaskTex);
            SAMPLER(sampler_MaskTex);

            // Every non-texture material property has to live in this buffer, otherwise
            // the SRP Batcher rejects the shader.
            CBUFFER_START(UnityPerMaterial)
                float4 _InnerColor;
                float4 _OuterColor;

                float4 _MaskTex_ST;

                float _MaskThreshold;
                float _MaskSoftness;
                float _InvertMask;
                float _Glow;
                float _CenterFocus;
            CBUFFER_END

            v2f vert(appdata v)
            {
                v2f o;

                // Camera forward in world space, flattened so the quad only
                // yaws and never leans when the camera pitches.
                float3 cameraForward = -UNITY_MATRIX_I_V._m02_m12_m22;
                cameraForward.y = 0.0;
                float cameraForwardLengthSquared = dot(cameraForward, cameraForward);
                // Looking straight down leaves no horizontal facing direction.
                cameraForward = cameraForwardLengthSquared > 1e-8
                    ? cameraForward * rsqrt(cameraForwardLengthSquared)
                    : float3(0, 0, 1);

                float3 worldRight = cross(float3(0, 1, 0), cameraForward);

                // Keep the object's scale, drop only its rotation.
                float2 scale = float2(length(GetObjectToWorldMatrix()._m00_m10_m20),
                                      length(GetObjectToWorldMatrix()._m01_m11_m21));

                // Span the quad in object space so the final transform is the
                // same TransformObjectToHClip a plain unlit shader uses, and the
                // pivot comes from the object-to-world matrix untouched.
                float3 objectRight = mul((float3x3) GetWorldToObjectMatrix(), worldRight);
                float3 objectUp = mul((float3x3) GetWorldToObjectMatrix(), float3(0, 1, 0));

                float3 positionOS = objectRight * (v.vertex.x * scale.x)
                    + objectUp * (v.vertex.y * scale.y);

                o.vertex = TransformObjectToHClip(positionOS);
                o.uv = TRANSFORM_TEX(v.uv, _MaskTex);
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                float4 maskTexel = SAMPLE_TEXTURE2D(_MaskTex, sampler_MaskTex, i.uv);

                float guide = maskTexel.r;
                guide = lerp(guide, 1.0 - guide, _InvertMask);

                float coverage = maskTexel.a;

                float innerMask = smoothstep(
                    _MaskThreshold - _MaskSoftness,
                    _MaskThreshold + _MaskSoftness,
                    guide
                );

                float outerMask = (1.0 - innerMask) * coverage;
                innerMask *= coverage;

                float4 col = 0.0;
                col += _InnerColor * innerMask;
                col += _OuterColor * outerMask;

                col.rgb *= _Glow;
                col.a = coverage * max(col.a, max(innerMask, outerMask));

                return col;
            }

            ENDHLSL
        }
    }
}
