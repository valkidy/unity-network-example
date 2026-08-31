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
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        LOD 100

        Pass
        {
            Tags { "LightMode"="ForwardBase" }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            // Compiles variants for directional light shadows
            #pragma multi_compile_fwdbase
            
            #include "UnityCG.cginc"
            #include "AutoLight.cginc" // Contains shadow macros

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float3 worldPos : TEXCOORD1;
                float4 pos : SV_POSITION;
                // Declares internal coordinates needed to sample the shadow map
                SHADOW_COORDS(2)
            };

            fixed4 _ColorA;
            fixed4 _ColorB;
            float _CellSize;

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _ShadowColor;

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                
                // Coordinates calculation needed to map screen space/light space shadows
                TRANSFER_SHADOW(o);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // Sample unlit base texture
                fixed4 col = tex2D(_MainTex, i.uv);
                
                float cellSize = max(_CellSize, 0.0001);

                // World-space checkerboard on XZ plane.
                // Cell Size = 1 means each tile is 1 x 1 Unity world units.
                float2 cell = floor(i.worldPos.xz / cellSize);
                float checker = fmod(cell.x + cell.y, 2.0);
                checker = abs(checker);

                fixed4 checkerColor = lerp(_ColorA, _ColorB, checker);


                // Calculates shadow attenuation: 1.0 = fully lit, 0.0 = fully shadowed
                fixed attenuation = SHADOW_ATTENUATION(i);
                
                // Interpolate between the unlit texture and the shaded color tint based on shadow mapping
                fixed3 shadowResult = lerp(checkerColor.rgb * _ShadowColor.rgb, checkerColor.rgb, attenuation);
                
                return fixed4(shadowResult, checkerColor.a);
            }
            ENDCG
        }

        // Pass needed so this object can cast shadows onto other objects
        UsePass "Legacy Shaders/VertexLit/SHADOWCASTER"
    }
    Fallback "VertexLit"
}
