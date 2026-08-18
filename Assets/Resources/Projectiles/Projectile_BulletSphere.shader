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
        }

        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Back

        Pass
        {
            CGPROGRAM

            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"

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

            float4 _InnerColor;
            float4 _OuterColor;
            float _OuterThickness;
            float _Softness;
            float _Glow;

            v2f vert(appdata v)
            {
                v2f o;

                o.vertex = UnityObjectToClipPos(v.vertex);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.worldNormal = UnityObjectToWorldNormal(v.normal);

                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 N = normalize(i.worldNormal);
                float3 V = normalize(_WorldSpaceCameraPos.xyz - i.worldPos);

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

            ENDCG
        }
    }
}