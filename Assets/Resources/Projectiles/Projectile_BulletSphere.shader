Shader "Unlit/BulletSphere"
{
    Properties
    {
        _InnerColor ("Inner Color", Color) = (1,1,1,1)
        _OuterColor ("Outer Color", Color) = (0.05,0.1,1,1)
        _OuterThickness ("Outer Thickness", Range(0.01,1.0)) = 0.3
        _Softness ("Softness", Range(0.001,0.5)) = 0.05
        _Glow ("Glow", Range(0.0,5.0)) = 1.5
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
        Cull Back

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
                float3 normal : NORMAL;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float3 worldPos : TEXCOORD0;
                float3 worldNormal : TEXCOORD1;
            };

            // Every non-texture material property has to live in this buffer, otherwise
            // the SRP Batcher rejects the shader.
            CBUFFER_START(UnityPerMaterial)
                float4 _InnerColor;
                float4 _OuterColor;
                float _OuterThickness;
                float _Softness;
                float _Glow;
            CBUFFER_END

            v2f vert(appdata v)
            {
                v2f o;

                o.vertex = TransformObjectToHClip(v.vertex.xyz);
                o.worldPos = TransformObjectToWorld(v.vertex.xyz);
                o.worldNormal = TransformObjectToWorldNormal(v.normal);

                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                float3 N = normalize(i.worldNormal);
                float3 V = normalize(GetCameraPositionWS() - i.worldPos);

                float ndv = abs(dot(N, V));

                float fresnel = 1.0 - ndv;

                float threshold = 1.0 - _OuterThickness;

                float outerMask = smoothstep(
                    threshold - _Softness,
                    threshold + _Softness,
                    fresnel
                );

                float4 col = lerp(
                    _InnerColor,
                    _OuterColor,
                    outerMask
                );

                col.rgb *= _Glow;

                return col;
            }

            ENDHLSL
        }
    }
}
