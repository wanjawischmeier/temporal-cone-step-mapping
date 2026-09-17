#ifndef CONE_STEP_MARCH_INCLUDED
#define CONE_STEP_MARCH_INCLUDED

// Advances sc by one cone step and returns the new value.
// ds: normalized 3D ray direction, ds.xy horizontal (UV), ds.z vertical (height).
// dominantAxis: max(|ds.x|, |ds.y|) - the anisotropic analogue of length(ds.xy).
// remaining: vertical gap between the ray and the sampled surface height at
//            the CURRENT sc (positive while still above the surface).
// minStep: floor to guarantee forward progress against near-vertical cones.
float NextConeStep(float sc, float remaining, float coneRatio, float3 ds, float dominantAxis, float relax, float minStep)
{
    float denom = max(coneRatio * ds.z + dominantAxis, 1e-6);
    float step = remaining * coneRatio / denom;
    return sc + relax * max(minStep, step);
}

// --- Conservative cone stepping ---
// The conservative map guarantees the cone never touches the surface except
// at its own apex, so a plain step-and-converge loop can never overshoot
// through the surface.
ConeStepResult MarchConservative(
    float2 u0, float3 ds, float dominantAxis, float4 coneChannelMask,
    float4 heightMask, float startSc, float minStep,
    float4 historyData, bool isHistoryValid)
{
    ConeStepResult result;
    float sc = startSc;
    float error = 1.0;
    int iterations = (int) _MaxIterations;
    int i = 0;
    uint stepCount = 0;

    ReprojectionSeed bestSeed;
    bestSeed.uv = u0;
    bestSeed.height = 0.0;
    bestSeed.score = 0.0;
    bestSeed.t = 0.0;
    bestSeed.valid = false;

    // PRE-LOAD: Evaluate previous frame's seed before starting the loop.
    // If the loop skips over this seed's region, bestSeed will still hold it.
    if (isHistoryValid)
    {
        float2 prevApexUV = historyData.xy;
        float prevApexHeight = SampleHeight(prevApexUV, heightMask);
        
        // Reevaluate the seed's progress based on how far it actually advances THIS frame's ray
        float currentUtilityT = ds.z * startSc;
        ConsiderReprojectionSeed(bestSeed, u0, prevApexUV, prevApexHeight, currentUtilityT);
    }

    [loop]
    for (i = 0; i < iterations; i++)
    {
        stepCount++;
        float2 p = u0 + ds.xy * sc;
        float height = SampleHeight(p, heightMask);
            
        // Pass physical penetration depth ds.z * sc instead of loop counter
        ConsiderReprojectionSeed(bestSeed, u0, p, height, ds.z * sc);
            
        error = 1.0 - ds.z * sc - height;
        if (error <= _MinError)
            break;

        float coneRatio = SampleConeRatio(p, coneChannelMask);
        sc = NextConeStep(sc, error, coneRatio, ds, dominantAxis, _Relax, minStep);
    }

    result.uv = u0 + ds.xy * sc;
    result.t = ds.z * sc;
    result.error = error;
    result.wasHit = i < iterations;
    result.penetrated = false;
    result.steps = stepCount;
    // If no candidate earned a positive score, retain the historical behavior:
    // use this frame's real hit, with validity still controlled by wasHit.
    result.seedUV = bestSeed.valid ? bestSeed.uv : result.uv;
    result.seedHeight = bestSeed.valid ? bestSeed.height : SampleHeight(result.uv, heightMask);
    result.seedT = bestSeed.valid ? bestSeed.t : result.t; // NEW
    result.seedValid = bestSeed.valid || result.wasHit;
    return result;
}

// --- Relaxed cone stepping ---
// The relaxed/corrected map only guarantees AT MOST ONE penetration, so we
// march forward (allowed to overshoot into the surface once), then binary
// search back between the last safe sc and the first penetrating sc.
ConeStepResult MarchRelaxed(float2 u0, float3 ds, float dominantAxis, float4 coneChannelMask, float4 heightMask, float startSc, float minStep)
{
    ConeStepResult result;
    float sc = startSc;
    float scPrev = startSc;
    float error = 1.0;
    float errorPrev = 1.0;
    bool penetrated = false;
    int iterations = (int)_MaxIterations;
    int i = 0;
    uint stepCount = 0;

    [loop]
    for (i = 0; i < iterations; i++)
    {
        stepCount++;
        float2 p = u0 + ds.xy * sc;
        float height = SampleHeight(p, heightMask);
        error = 1.0 - ds.z * sc - height;

        if (error <= 0.0)
        {
            penetrated = true;
            break;
        }
        if (error <= _MinError) break;

        float coneRatio = SampleConeRatio(p, coneChannelMask);
        scPrev = sc;
        errorPrev = error;
        sc = NextConeStep(sc, error, coneRatio, ds, dominantAxis, _Relax, minStep);
    }

    if (penetrated)
    {
        float lo = scPrev, hi = sc;
        float loErr = errorPrev, hiErr = error;
        int binaryIterations = (int) _MaxBinaryIterations;

        [loop]
        for (int j = 0; j < binaryIterations; j++)
        {
            stepCount++;
            // False position: interpolate the zero-crossing instead of bisecting blindly.
            float t = loErr / max(loErr - hiErr, 1e-6);
            float mid = lerp(lo, hi, saturate(t));
            float2 pm = u0 + ds.xy * mid;
            float hMid = SampleHeight(pm, heightMask);
            float errMid = 1.0 - ds.z * mid - hMid;

            if (abs(errMid) <= _MinError || (hi - lo) < minStep)
            {
                lo = hi = mid;
                hiErr = errMid;
                break;
            }

            if (errMid > 0.0)
            {
                lo = mid;
                loErr = errMid;
            }
            else
            {
                hi = mid;
                hiErr = errMid;
            }
        }

        sc = hi;
        error = hiErr;
    }

    result.uv = u0 + ds.xy * sc;
    result.t = ds.z * sc;
    result.error = error;
    result.wasHit = penetrated || (i < iterations);
    result.penetrated = penetrated;
    result.steps = stepCount;
    // Relaxed-map cross-apex seeds are intentionally deferred. Keep the old
    // converged-hit representation; GetStartSc rejects relaxed history anyway.
    result.seedUV = result.uv;
    result.seedHeight = SampleHeight(result.uv, heightMask);
    result.seedValid = result.wasHit;
    result.seedT = result.t;
    return result;
}

#endif // CONE_STEP_MARCH_INCLUDED
