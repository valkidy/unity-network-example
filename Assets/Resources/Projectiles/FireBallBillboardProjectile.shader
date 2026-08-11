Shader "Unlit/FireEffect"
{
    Properties
    {
        _ShapeMask ("Shape Mask (Grayscale)", 2D) = "white" {}
        _CenterFocus ("Center Focus", Range(0.0, 1.0)) = 0.45
    }

    SubShader
    {
        Tags
        {
            // The billboard rebuilds the quad from the object pivot, so it needs
            // the real unity_ObjectToWorld. Batching pre-transforms the vertices
            // to world space and hands the shader an identity matrix instead,
            // which drags the quad around with the camera yaw. Keep this on.
            "DisableBatching" = "True"

            "RenderType" = "Transparent"
            "Queue" = "Transparent"
        }

        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off

        Pass
        {
            CGPROGRAM

            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"

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

            sampler2D _ShapeMask;
            float4 _ShapeMask_ST;

            float _CenterFocus;

            // v2f vert(appdata v)
            // {
            //     v2f o;
            //     o.vertex = UnityObjectToClipPos(v.vertex);
            //     o.uv = TRANSFORM_TEX(v.uv, _ShapeMask);
            //     return o;
            // }

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
                float2 scale = float2(length(unity_ObjectToWorld._m00_m10_m20),
                                      length(unity_ObjectToWorld._m01_m11_m21));

                // Span the quad in object space so the final transform is the
                // same UnityObjectToClipPos a plain unlit shader uses, and the
                // pivot comes from unity_ObjectToWorld untouched.
                float3 objectRight = mul((float3x3) unity_WorldToObject, worldRight);
                float3 objectUp = mul((float3x3) unity_WorldToObject, float3(0, 1, 0));

                float3 positionOS = objectRight * (v.vertex.x * scale.x)
                    + objectUp * (v.vertex.y * scale.y);

                o.vertex = UnityObjectToClipPos(positionOS);
                o.uv = TRANSFORM_TEX(v.uv, _ShapeMask);
                return o;
            }

                    
            float3 rgb2hsv(float3 c)
            {
                float4 K = float4(0.0, -1.0 / 3.0, 2.0 / 3.0, -1.0);
                float4 p = lerp(float4(c.bg, K.wz), float4(c.gb, K.xy), step(c.b, c.g));
                float4 q = lerp(float4(p.xyw, c.r), float4(c.r, p.yzx), step(p.x, c.r));

                float d = q.x - min(q.w, q.y);
                float e = 1.0e-10;

                return float3(abs(q.z + (q.w - q.y) / (6.0 * d + e)), d / (q.x + e), q.x);
            }

            float3 hsv2rgb(float3 c)
            {
                float4 K = float4(1.0, 2.0 / 3.0, 1.0 / 3.0, 3.0);
                float3 p = abs(frac(c.xxx + K.xyz) * 6.0 - K.www);
                return c.z * lerp(K.xxx, saturate(p - K.xxx), c.y);
            }

            float rand(float2 n)
            {
                return frac(sin(cos(dot(n,float2(12.9898, 12.1414)))) * 83758.5453);
            }

            float noise(float2 n)
            {
                const float2 d = float2(0.0, 1.0);
                float2 b = floor(n);
                float2 f = smoothstep(float2(0.0, 0.0), float2(1.0, 1.0), frac(n));
                return lerp(
                    lerp(
                        rand(b),
                        rand(b + d.yx),
                        f.x
                    ),
                    lerp(
                        rand(b + d.xy),
                        rand(b + d.yy),
                        f.x
                    ),
                    f.y
                );
            }

            float fbm(float2 n)
            {
                float total = 0.0;
                float amplitude = 1.0;

                for (int i = 0; i < 5; i++)
                {
                    total += noise(n) * amplitude;

                    n += n * 1.7;

                    amplitude *= 0.47;
                }

                return total;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                const float3 c1 = float3(0.5, 0.0, 0.1);
                const float3 c2 = float3(0.9, 0.1, 0.0);
                const float3 c3 = float3(0.2, 0.1, 0.7);
                const float3 c4 = float3(1.0, 0.9, 0.1);
                const float3 c5 = float3(0.1, 0.1, 0.1);
                const float3 c6 = float3(0.9, 0.9, 0.9);

                float iTime = _Time.y;

                //-----------------------------------
                // Centered UV
                //-----------------------------------
                float2 uv = i.uv;
                float2 centeredUV = (uv - 0.5) * 2.0;


                //-----------------------------------
                // Center focus
                //-----------------------------------

                float centerDistance = length(centeredUV);
                // 0 at center, approaches 1 outside
                float centerMask = saturate(centerDistance);

                // Compress coordinates toward center.
                // _CenterFocus = 0 -> original
                // _CenterFocus = 1 -> stronger concentration
                float centerScale = lerp(1.0, 0.45 + centerMask * 0.55, _CenterFocus);
                float2 localCoord = centeredUV * centerScale;

                //-----------------------------------
                // Flow direction
                //-----------------------------------
                float2 flowDir = float2(0.0, 1.0);
                float flowLength = max(length(flowDir), 0.0001);
                flowDir /= flowLength;

                //-----------------------------------
                // Original fire
                //-----------------------------------

                float2 speed = float2(2.0, 2.0);
                float shift = 1.327 + sin(iTime * 2.0) / 2.4;
                float dist = 3.5 - sin(iTime * 0.4) / 1.89;
                float2 p = localCoord * dist;

                //-----------------------------------
                // Original:
                //
                // p.x -= iTime / 1.1;
                //
                // Directional version
                //-----------------------------------

                p -= flowDir * iTime / 1.1;

                float q = fbm(p - iTime * 0.01 + 1.0 * sin(iTime) / 10.0);
                float qb = fbm(p - iTime * 0.002 + 0.1 * cos(iTime) / 5.0);
                float q2 = fbm(p - iTime * 0.44 - 5.0 * cos(iTime) / 7.0) - 6.0;
                float q3 = fbm(p - iTime * 0.9 - 10.0 * cos(iTime) / 30.0) - 4.0;
                float q4 = fbm(p - iTime * 2.0 - 20.0 * sin(iTime) / 20.0) + 2.0;
                q = (q + qb - 0.4 * q2 - 2.0 * q3 + 0.6 * q4) / 3.8;
                float2 r = float2(
                    fbm(p + q / 2.0 + iTime * speed.x - p.x - p.y),
                    fbm(p + q - iTime * speed.y)
                );

                float3 c = lerp(c1, c2, fbm(p + r))
                    + lerp(c3, c4, r.x)
                    - lerp(c5, c6, r.y);

                float3 color = c * cos(shift * uv.y);
                color += 0.05;
                color.r *= 0.8;

                float3 hsv = rgb2hsv(color);
                hsv.y *= hsv.z * 1.1;
                hsv.z *= hsv.y * 1.13;
                hsv.y = (2.2 - hsv.z * 0.9) * 1.20;
                color = hsv2rgb(hsv);

                //-----------------------------------
                // Shape mask
                //-----------------------------------
                float mask = tex2D(_ShapeMask, uv).r * r.x * r.y;
                return float4(color, saturate(2.0 * mask));
            }

            ENDCG
        }
    }
}