Shader "Hidden/ConeStepMapping/Step Count Heatmap"
{
    Properties
    {
        [MainTexture] _MainTex("Main Texture", 2D) = "white" {} // unused, but required for URP
        _MaxSteps("Heatmap Maximum", Float) = 64
        _SmoothedStepCountTexture("Smoothed Step Count (internal)", 2D) = "black" {}
        _UseSmoothing("Use Smoothing", Float) = 0
        _PrevAccum("Previous Accum (internal)", 2D) = "black" {}
        _AccumAlpha("Accum Alpha (internal)", Float) = 1
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Off
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings { float4 positionHCS : SV_POSITION; float2 uv : TEXCOORD0; };
            Texture2D<uint> _StepCountTexture;
            float4 _StepCountTexture_TexelSize;
            float _MaxSteps;
            Texture2D<half4> _SmoothedStepCountTexture;
            float _UseSmoothing;

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.positionHCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.uv;
                return output;
            }

            float3 Heat(float t)
            {
                return saturate(float3(1.5 - abs(4.0 * t - 3.0),
                                       1.5 - abs(4.0 * t - 2.0),
                                       1.5 - abs(4.0 * t - 1.0)));
            }

            half4 frag(Varyings input) : SV_Target
            {
                uint2 pixel = min((uint2)(saturate(input.uv) * _StepCountTexture_TexelSize.zw),
                                  (uint2)_StepCountTexture_TexelSize.zw - 1);

                if (_UseSmoothing > 0.5)
                    return _SmoothedStepCountTexture.Load(int3(pixel, 0));

                uint steps = _StepCountTexture.Load(int3(pixel, 0));
                return half4(Heat(saturate(steps / max(_MaxSteps, 1.0))), 1.0);
            }
            ENDHLSL
        }

        Pass
        {
            Name "Accumulate"
            ZWrite Off
            ZTest Always
            Cull Off
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment fragAccumulate
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings { float4 positionHCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Texture2D<uint> _StepCountTexture;
            float4 _StepCountTexture_TexelSize;
            Texture2D<half4> _PrevAccum;
            float _AccumAlpha;
            float _MaxSteps;

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.positionHCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.uv;
                return output;
            }

            float3 Heat(float t)
            {
                return saturate(float3(1.5 - abs(4.0 * t - 3.0),
                                       1.5 - abs(4.0 * t - 2.0),
                                       1.5 - abs(4.0 * t - 1.0)));
            }

            half4 fragAccumulate(Varyings input) : SV_Target
            {
                uint2 pixel = min((uint2)(saturate(input.uv) * _StepCountTexture_TexelSize.zw),
                                  (uint2)_StepCountTexture_TexelSize.zw - 1);
                uint steps = _StepCountTexture.Load(int3(pixel, 0));
                half4 currentColor = half4(Heat(saturate(steps / max(_MaxSteps, 1.0))), 1.0);
                half4 prevColor = _PrevAccum.Load(int3(pixel, 0));
                return lerp(prevColor, currentColor, _AccumAlpha);
            }
            ENDHLSL
        }
    }
}
