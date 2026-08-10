Shader "Unlit/Terrain"
{
    Properties
    {
        _ColorA ("Color A", Color) = (0.75, 0.75, 0.75, 1)
        _ColorB ("Color B", Color) = (0.25, 0.25, 0.25, 1)

        // 1.0 = one checker cell per Unity world unit.
        _CellSize ("Cell Size", Float) = 1.0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
        }

        LOD 100

        Pass
        {
            CGPROGRAM

            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
            };

            struct v2f
            {
                float3 worldPos : TEXCOORD0;
                UNITY_FOG_COORDS(1)
                float4 vertex : SV_POSITION;
            };

            fixed4 _ColorA;
            fixed4 _ColorB;
            float _CellSize;

            v2f vert(appdata v)
            {
                v2f o;

                o.vertex = UnityObjectToClipPos(v.vertex);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;

                UNITY_TRANSFER_FOG(o, o.vertex);

                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // Prevent division by zero.
                float cellSize = max(_CellSize, 0.0001);

                // Use world-space XZ coordinates.
                // With Cell Size = 1, each cell is 1x1 Unity units.
                float2 cell = floor(i.worldPos.xz / cellSize);

                float checker = fmod(cell.x + cell.y, 2.0);

                // fmod may return a negative value for negative coordinates.
                checker = abs(checker);

                fixed4 col = lerp(_ColorA, _ColorB, checker);

                UNITY_APPLY_FOG(i.fogCoord, col);

                return col;
            }

            ENDCG
        }
    }
}