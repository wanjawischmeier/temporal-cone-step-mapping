#ifndef CONE_STEP_MAPPING_COMMON_INCLUDED
#define CONE_STEP_MAPPING_COMMON_INCLUDED

// --- Structs ---

struct Attributes
{
    float4 positionOS : POSITION;
    float2 uv : TEXCOORD0;
    float3 normalOS : NORMAL;
    float4 tangentOS : TANGENT;
};

struct Varyings
{
    float4 positionHCS : SV_POSITION;
    float2 uv : TEXCOORD0;
    float3 tangentViewDir : TEXCOORD1;
    float4 screenPos : TEXCOORD2;
    float4 prevScreenPos : TEXCOORD3;
    float3 positionWS : TEXCOORD4;
    float3 normalWS : TEXCOORD5;
    float3 tangentWS : TEXCOORD6;
    float3 bitangentWS : TEXCOORD7;
};

struct FragmentOutput
{
    half4 color0 : SV_Target0;
    half4 color1 : SV_Target1;
    uint stepCount : SV_Target2;
    float depth : SV_Depth;
};

// Result of a cone-step march. "t" mirrors the old shader's "depth" field:
// fraction of full penetration along the ray (0 = surface entry, 1 = deepest).
struct ConeStepResult
{
    float2 uv;
    float t;
    float error;
    bool wasHit;
    bool penetrated; // relaxed mode only, diagnostic
    uint steps;
    float2 seedUV;
    float seedHeight;
    bool seedValid;
};

// Candidate retained while conservative marching. It is deliberately separate
// from the converged hit: its sole purpose is to seed next frame's shortcut.
struct ReprojectionSeed
{
    float2 uv;
    float height;
    float score;
    bool valid;
};

// --- Textures & Samplers ---

TEXTURE2D(_BaseMap);
SAMPLER(sampler_BaseMap);

TEXTURE2D(_HeightMap);
SAMPLER(sampler_HeightMap);

TEXTURE2D(_ConeMap);
SAMPLER(sampler_ConeMap);
float4 _ConeMap_TexelSize; // auto-filled by Unity: x=1/width, y=1/height

TEXTURE2D(_PreviousFrame);
SAMPLER(sampler_PreviousFrame);

// --- Constant Buffers ---

CBUFFER_START(UnityPerMaterial)
    half4 _BaseColor;
    float4 _BaseMap_ST;
    float _HeightChannel;
    float _ParallaxScale, _OrthoSize, _ParallaxOffset;
    float _MinError;
    float _MaxIterations;
    float _MaxBinaryIterations;
    float _Relax;
    float _UseRelaxedCone, _UseHistory;
    float _ReprojectionMarginWeight;
    float _ReprojectionProgressWeight;
    float _UseTestTexture;
CBUFFER_END

// Declared outside the per-material CBUFFER to stay SRP-batcher compatible,
// matching how _CustomPrevViewProjMatrix is set up in the existing MRT shader.
float4x4 _CustomPrevViewProjMatrix;

// --- Helper Functions ---

float3 ComputeTangentViewDir(Attributes IN)
{
    float3 binormalOS = cross(IN.normalOS, IN.tangentOS.xyz) * IN.tangentOS.w;
    float3x3 objectToTangent = float3x3(IN.tangentOS.xyz, binormalOS, IN.normalOS);

    float3 cameraPosOS = TransformWorldToObject(_WorldSpaceCameraPos.xyz);
    float3 viewDirOS = cameraPosOS - IN.positionOS.xyz;

    return mul(objectToTangent, viewDirOS);
}

float4 ComputePrevScreenPos(Attributes IN)
{
    float4 worldPos = mul(UNITY_MATRIX_M, float4(IN.positionOS.xyz, 1.0));
    float4 prevClipPos = mul(_CustomPrevViewProjMatrix, worldPos);
    return ComputeScreenPos(prevClipPos);
}

float4 GetHeightChannelMask()
{
    if (_HeightChannel < 0.5) return float4(1, 0, 0, 0);
    if (_HeightChannel < 1.5) return float4(0, 1, 0, 0);
    if (_HeightChannel < 2.5) return float4(0, 0, 1, 0);
    return float4(0, 0, 0, 1);
}

float SampleHeight(float2 uv, float4 heightMask)
{
    float4 c = SAMPLE_TEXTURE2D_LOD(_HeightMap, sampler_HeightMap, uv, 0);
    return dot(c, heightMask);
}

// Picks the one channel of the quad-directional cone map that bounds the
// current ray direction. Since the ray direction is fixed for the whole
// march, this only needs to be computed once per pixel, outside the loop.
// R = +X, G = -X, B = +Y, A = -Y (matches RobustConeMapGenerator.compute).
float4 GetConeChannelMask(float2 rayDirXY)
{
    bool xDominant = abs(rayDirXY.x) >= abs(rayDirXY.y);
    if (xDominant)
        return rayDirXY.x > 0 ? float4(1, 0, 0, 0) : float4(0, 1, 0, 0);
    else
        return rayDirXY.y > 0 ? float4(0, 0, 1, 0) : float4(0, 0, 0, 1);
}

float SampleConeRatio(float2 uv, float4 coneChannelMask)
{
    float4 c = SAMPLE_TEXTURE2D_LOD(_ConeMap, sampler_ConeMap, uv, 0);
    return dot(c, coneChannelMask);
}

// GenerateBatch initializes an anisotropic channel to this value and only
// lowers it when a limiting texel exists. Such a channel has no useful cone.
static const float kUnwrittenConeRatio = 1000.0;

void ConsiderReprojectionSeed(inout ReprojectionSeed best, float2 originUV,
    float2 apexUV, float apexHeight, int iteration)
{
    float2 apexToOrigin = originUV - apexUV;
    float ratio = SampleConeRatio(apexUV, GetConeChannelMask(apexToOrigin));
    if (ratio >= kUnwrittenConeRatio)
        return;

    float dominantDistance = max(abs(apexToOrigin.x), abs(apexToOrigin.y));
    
    // Calculate the raw margin without clamping it to 0
    float margin = 1.0 - (apexHeight + dominantDistance / max(ratio, 1e-6));
    
    // HARD REJECT: If the cone ceiling breaches the Z=1.0 plane, it is 
    // mathematically guaranteed to fail next frame's GetStartSc rejection check.
    if (margin <= 0.0)
        return;

    float progress = iteration / max(_MaxIterations, 1.0);
    float score = _ReprojectionMarginWeight * margin + _ReprojectionProgressWeight * progress;

    if (score > 0.0 && (!best.valid || score > best.score))
    {
        best.uv = apexUV;
        best.height = apexHeight;
        best.score = score;
        best.valid = true;
    }
}

#endif // CONE_STEP_MAPPING_COMMON_INCLUDED
