#ifndef CONE_STEP_REPROJECTION_INCLUDED
#define CONE_STEP_REPROJECTION_INCLUDED

// --- Reprojection hook ---
// Not enabled by default (_UseHistory = 0). When enabled, reprojects the
// previous frame's converged UV onto THIS frame's ray direction to get a
// candidate starting sc, and compares its error against a fresh sc = 0
// start, keeping whichever is closer to the surface. This mirrors
// IterativeParallax.shader's GetStartUV, adapted to sc-space.
//
// What is intentionally NOT implemented here (left for you to add):
// disocclusion/rejection heuristics, confidence-based blending, and
// history validity beyond the screen-space bounds check below.
float GetStartSc(float4 historyData, float2 prevScreenUV, float2 u0, float3 ds, float4 heightMask)
{
    bool isHistoryValid = _UseHistory > 0.5 &&
                           prevScreenUV.x >= 0.0 && prevScreenUV.x <= 1.0 &&
                           prevScreenUV.y >= 0.0 && prevScreenUV.y <= 1.0 &&
                           historyData.w > 0.5;

    if (!isHistoryValid) return 0.0;

    // Project the historical UV back onto this frame's ray direction to
    // recover an equivalent sc. ds.xy is not unit length in UV terms alone
    // (it's part of the normalized 3D ray), so divide out its own length.
    float2 historyUV = historyData.xy;
    float denom = max(dot(ds.xy, ds.xy), 1e-8);
    float scHistory = max(0.0, dot(historyUV - u0, ds.xy) / denom);

    float2 pFresh = u0;
    float2 pHistory = u0 + ds.xy * scHistory;

    float errorFresh = 1.0 - SampleHeight(pFresh, heightMask);
    float errorHistory = 1.0 - ds.z * scHistory - SampleHeight(pHistory, heightMask);

    // Only take the history start if it's still above the surface (positive
    // error) and better than starting fresh - otherwise fall back to sc = 0.
    if (errorHistory > 0.0 && errorHistory < errorFresh) return scHistory;
    return 0.0;
}

#endif // CONE_STEP_REPROJECTION_INCLUDED
