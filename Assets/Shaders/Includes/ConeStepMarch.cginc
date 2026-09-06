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
ConeStepResult MarchConservative(float2 u0, float3 ds, float dominantAxis, float4 coneChannelMask, float4 heightMask, float startSc, float minStep)
{
    ConeStepResult result;
    float sc = startSc;
    float error = 1.0;
    int iterations = (int)_MaxIterations;
    int i = 0;
    uint stepCount = 0;
    float2 historyUV = u0;
    float historyHeight = SampleHeight(u0, heightMask);

    [loop]
    for (i = 0; i < iterations; i++)
    {
        stepCount++;
        float2 p = u0 + ds.xy * sc;
        float height = SampleHeight(p, heightMask);
        // Binary-refinement samples are not cone apices. Only primary cone
        // steps can seed a cross-apex reprojection shortcut.
        if (i <= _HistoryStepIndex)
        {
            historyUV = p;
            historyHeight = height;
        }
        error = 1.0 - ds.z * sc - height;
        if (error <= _MinError) break;

        float coneRatio = SampleConeRatio(p, coneChannelMask);
        sc = NextConeStep(sc, error, coneRatio, ds, dominantAxis, _Relax, minStep);
    }

    result.uv = u0 + ds.xy * sc;
    result.t = ds.z * sc;
    result.error = error;
    result.wasHit = i < iterations;
    result.penetrated = false;
    result.steps = stepCount;
    result.historyUV = historyUV;
    result.historyHeight = historyHeight;
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
    float2 historyUV = u0;
    float historyHeight = SampleHeight(u0, heightMask);

    [loop]
    for (i = 0; i < iterations; i++)
    {
        stepCount++;
        float2 p = u0 + ds.xy * sc;
        float height = SampleHeight(p, heightMask);
        if (i <= _HistoryStepIndex)
        {
            historyUV = p;
            historyHeight = height;
        }
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
        float hiErr = error;
        int binaryIterations = (int)_MaxBinaryIterations;

        [loop]
        for (int j = 0; j < binaryIterations; j++)
        {
            stepCount++;
            float mid = 0.5 * (lo + hi);
            float2 pm = u0 + ds.xy * mid;
            float hMid = SampleHeight(pm, heightMask);
            float errMid = 1.0 - ds.z * mid - hMid;

            if (abs(errMid) <= _MinError)
            {
                lo = hi = mid;
                hiErr = errMid;
                break;
            }

            if (errMid > 0.0) { lo = mid; }
            else { hi = mid; hiErr = errMid; }
        }

        sc = hi;   // land on/just past the surface rather than back above it
        error = hiErr;
    }

    result.uv = u0 + ds.xy * sc;
    result.t = ds.z * sc;
    result.error = error;
    result.wasHit = penetrated || (i < iterations);
    result.penetrated = penetrated;
    result.steps = stepCount;
    result.historyUV = historyUV;
    result.historyHeight = historyHeight;
    return result;
}

#endif // CONE_STEP_MARCH_INCLUDED
