#ifndef CONE_STEP_REPROJECTION_INCLUDED
#define CONE_STEP_REPROJECTION_INCLUDED

// Conservative, anisotropic cross-apex reprojection. The conservative
// generator bounds every texel from every apex using the directional
// dominant-axis metric, so a prior primary-march apex can safely seed the
// current ray. Relaxed maps do not carry this guarantee.

float4 ConeFaceMask(int face)
{
    if (face == 0) return float4(1, 0, 0, 0); // +X
    if (face == 1) return float4(0, 1, 0, 0); // -X
    if (face == 2) return float4(0, 0, 1, 0); // +Y
    return float4(0, 0, 0, 1);                // -Y
}

bool IsInsideConeFace(float2 delta, int face)
{
    if (face == 0) return delta.x >= 0.0 && delta.x >= abs(delta.y);
    if (face == 1) return delta.x <= 0.0 && -delta.x >= abs(delta.y);
    if (face == 2) return delta.y >= 0.0 && delta.y >= abs(delta.x);
    return delta.y <= 0.0 && -delta.y >= abs(delta.x);
}

// Evaluates the pyramid ceiling using the face selected from apex -> point,
// never from the current ray direction.
float ConeCeiling(float2 apexUV, float apexHeight, float2 pointUV)
{
    float2 delta = pointUV - apexUV;
    int face;
    if (abs(delta.x) >= abs(delta.y)) face = delta.x >= 0.0 ? 0 : 1;
    else face = delta.y >= 0.0 ? 2 : 3;

    float ratio = SampleConeRatio(apexUV, ConeFaceMask(face));
    float distance = face < 2 ? abs(delta.x) : abs(delta.y);
    return apexHeight + distance / max(ratio, 1e-6);
}

// Returns a ray/face crossing only if that crossing lies in the matching
// directional wedge. All four faces are tested: the ray may change dominant
// axis before it reaches the historical pyramid.
bool TryIntersectConeFace(float2 apexUV, float apexHeight, float2 rayUV, float3 ds, int face, out float intersectionSc)
{
    float2 delta0 = rayUV - apexUV;
    bool xFace = face < 2;
    float sign = (face == 0 || face == 2) ? 1.0 : -1.0;
    float signedDelta0 = sign * (xFace ? delta0.x : delta0.y);
    if (signedDelta0 < 0.0)
        return false;
    
    float signedRayDelta = sign * (xFace ? ds.x : ds.y);
    float ratio = SampleConeRatio(apexUV, ConeFaceMask(face));

    // 1 - ds.z * sc = apexHeight +
    //                  (signedDelta0 + signedRayDelta * sc) / ratio.
    // A positive denominator means the ray approaches this face.
    float safeRatio = max(ratio, 1e-6);
    float denominator = ds.z + signedRayDelta / safeRatio;
    float numerator = 1.0 - apexHeight - signedDelta0 / safeRatio;
    intersectionSc = numerator / denominator;

    if (ratio <= 1e-6 || denominator <= 1e-6 || intersectionSc <= 0.0 || !isfinite(intersectionSc))
        return false;

    return IsInsideConeFace(delta0 + ds.xy * intersectionSc, face);
}

float TryIntersectCone(float2 apexUV, float apexHeight, float2 rayUV, float3 ds)
{
    float startSc = 1e20;
    
    [unroll]
    for (int face = 0; face < 4; ++face)
    {
        float candidateSc;
        if (TryIntersectConeFace(apexUV, apexHeight, rayUV, ds, face, candidateSc))
            startSc = min(startSc, candidateSc);
    }
    
    return startSc >= 1e19 ? 0.0 : startSc;
}

float GetStartSc(float4 historyData, float2 prevScreenUV, float2 rayUV, float3 ds, float4 heightMask, bool isHistoryValid, bool didCameraMove)
{
    if (!isHistoryValid)
        return 0.0;

    float2 apexUV = historyData.xy;
    // The history target may be bilinearly filtered. Its interpolated B value
    // is useful to inspect, but does not necessarily belong to its interpolated
    // UV. Resampling makes the apex height and cone sample an exact pair.
    float apexHeight = SampleHeight(apexUV, heightMask);

    // If the ray origin is not above this globally conservative ceiling, it
    // cannot skip any section of the ray safely.
    if (1.0 <= ConeCeiling(apexUV, apexHeight, rayUV))
        return 0.0;

    return TryIntersectCone(apexUV, apexHeight, rayUV, ds);
}

#endif // CONE_STEP_REPROJECTION_INCLUDED
