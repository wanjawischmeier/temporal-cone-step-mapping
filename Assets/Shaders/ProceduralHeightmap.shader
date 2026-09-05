Shader "Custom/ProceduralHeightmap"
{
    Properties
    {
        _MainTex ("Heightmap (A) & Color (RGB)", 2D) = "white" {}
        _ColorR ("Color for R Channel", Color) = (1,0,0,1)
        _ColorG ("Color for G Channel", Color) = (0,1,0,1)
        _ColorB ("Color for B Channel", Color) = (0,0,1,1)
        _ColorA ("Color for A Channel", Color) = (1,1,0,1)
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 100

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5 // Wichtig für SV_VertexID und moderne Shader-Features
            #include "UnityCG.cginc"

            sampler2D _MainTex, _ColorTex;
            fixed4 _ColorR, _ColorG, _ColorB, _ColorA;
            float _MinHeight;
            float _MaxHeight;
            float _PhysicalSize;
            int _Resolution;
            float4x4 _LocalToWorldMatrix;

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
            };

            v2f vert (uint id : SV_VertexID)
            {
                v2f o;
                
                // Wir haben (Resolution - 1) Quads in X- und Y-Richtung
                uint quadsX = _Resolution - 1;
                
                // Welches Quad zeichnen wir gerade? (6 Vertices pro Quad)
                uint quadID = id / 6;
                // Welcher Vertex innerhalb des Quads? (0 bis 5)
                uint vertInQuad = id % 6;
                
                // 2D-Index des aktuellen Quads
                uint qX = quadID % quadsX;
                uint qY = quadID / quadsX;
                
                // Lookup-Table für die 6 Vertices eines Quads (im Uhrzeigersinn für Front-Face Culling)
                // Dreieck 1: Oben-Links(0,1), Oben-Rechts(1,1), Unten-Links(0,0)
                // Dreieck 2: Unten-Links(0,0), Oben-Rechts(1,1), Unten-Rechts(1,0)
                uint2 offsets[6];
                offsets[0] = uint2(0, 1);
                offsets[1] = uint2(1, 1);
                offsets[2] = uint2(0, 0);
                offsets[3] = uint2(0, 0);
                offsets[4] = uint2(1, 1);
                offsets[5] = uint2(1, 0);
                
                // Absolute Grid-Position dieses Vertices berechnen
                uint2 gridPos = uint2(qX, qY) + offsets[vertInQuad];
                
                // In UV-Koordinaten umwandeln (0.0 bis 1.0)
                float2 uv = float2(gridPos) / float2(_Resolution - 1, _Resolution - 1);
                o.uv = uv;
                
                // Lokale X/Z Position berechnen (Zentriert um 0)
                float2 localXZ = (uv - 0.5) * _PhysicalSize;
                
                // Höhe auslesen (A-Kanal) und remappen
                float4 texData = tex2Dlod(_MainTex, float4(uv, 0, 0));
                float localY = lerp(_MinHeight, _MaxHeight, texData.a);
                
                // Lokale Vertex-Position
                float4 localPos = float4(localXZ.x, localY, localXZ.y, 1.0);
                
                // Transformation: Erst mit der Transform-Matrix des GameObjects, dann View-Projection
                float4 worldPos = mul(_LocalToWorldMatrix, localPos);
                o.vertex = mul(UNITY_MATRIX_VP, worldPos);
                
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 col = tex2D(_ColorTex, i.uv);

                fixed4 result =
                    col.r * _ColorR +
                    col.g * _ColorG +
                    col.b * _ColorB +
                    col.a * _ColorA;

                // Optional: Normalisierung, wenn du willst, dass die Summe der Gewichte 1 ergibt
                float sum = col.r + col.g + col.b + col.a;
                if (sum > 0) result /= sum;

                return result;
            }
            ENDCG
        }
    }
}