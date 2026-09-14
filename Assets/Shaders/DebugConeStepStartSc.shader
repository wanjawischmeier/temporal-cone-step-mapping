Shader "Hidden/DebugConeStepStartSc"
{
    Properties
    {
        _MainTex ("Debug Tex (MRT1)", 2D) = "black" {}
        [Toggle] _ShowValue ("Show Value (vs Bool)", Float) = 0
        _NormalizeMin ("Normalize Min", Float) = 0.0
        _NormalizeMax ("Normalize Max", Float) = 1.0
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }
        Cull Off ZWrite Off ZTest Always
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            float _ShowValue;
            float _NormalizeMin;
            float _NormalizeMax;

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings { float4 positionHCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = IN.uv;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                // .z currently holds startSc per your temporary debug write.
                float startSc = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv).z;

                if (_ShowValue > 0.5)
                {
                    float t = saturate((startSc - _NormalizeMin) / max(_NormalizeMax - _NormalizeMin, 1e-6));
                    return half4(t.xxx, 1);
                }

                bool historyUsed = startSc != 0.0;
                return historyUsed ? half4(0, 1, 0, 1) : half4(1, 0, 0, 1);
            }
            ENDHLSL
        }
    }
}