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

float TryIntersectCone(float2 apexUV, float apexHeight, float2 rayUV, float3 ds, bool takeFurthest)
{
    float bestSc = takeFurthest ? -1.0 : 1e20;
    bool found = false;

    [unroll]
    for (int face = 0; face < 4; ++face)
    {
        float candidateSc;
        if (TryIntersectConeFace(apexUV, apexHeight, rayUV, ds, face, candidateSc))
        {
            found = true;
            bestSc = takeFurthest ? max(bestSc, candidateSc) : min(bestSc, candidateSc);
        }
    }

    return found ? bestSc : 0.0;
}

float GetStartSc(float4 historyData, float2 prevScreenUV, float2 rayUV, float3 ds, float4 heightMask, bool isHistoryValid, bool didCameraMove)
{
    if (!isHistoryValid)
        return 0.0;

    if (!didCameraMove)
    {
        // historyData.zw = previous frame's exact converged hit UV. It sits on
        // the true surface, essentially tangent to this frame's ray (assuming
        // truly static camera), not chosen for margin like the mode-1 seed.
        // The generic ConeCeiling(rayUV) pre-check is unreliable this far from
        // the apex, so we skip it and instead take the FURTHEST of the (up to)
        // two valid face crossings, which bracket the near-tangent touch point.
        float2 apexUV = historyData.zw;
        float apexHeight = SampleHeight(apexUV, heightMask);
        float startSc = TryIntersectCone(apexUV, apexHeight, rayUV, ds, true);

        // Defensive: unlike mode 1, we didn't prove ray origin starts above
        // this apex's bound, so verify we haven't jumped past the real
        // surface before trusting startSc.
        if (startSc > 0.0)
        {
            float2 p = rayUV + ds.xy * startSc;
            float h = SampleHeight(p, heightMask);
            if (1.0 - ds.z * startSc - h < 0.0)
                startSc = 0.0;
        }
        return startSc;
    }

    float2 apexUV = historyData.xy;
    float apexHeight = SampleHeight(apexUV, heightMask);

    if (1.0 <= ConeCeiling(apexUV, apexHeight, rayUV))
        return 0.0;

    return TryIntersectCone(apexUV, apexHeight, rayUV, ds, false);
}

#endif // CONE_STEP_REPROJECTION_INCLUDED
