using System;
using System.Collections;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Sweeps only _ReprojectionMarginWeight on a target material over a 1D range, with
/// _ReprojectionProgressWeight held fixed, and for each sample point records the resulting
/// total cone-step count for one frame. Writes a CSV suitable for the companion Python
/// plotting script (plot_cone_step_margin_sweep.py).
///
/// Structurally identical to ConeStepWeightSweep.cs, just collapsed to one axis. See that
/// file for the reasoning behind the synchronous-readback / history-reset-per-cell approach.
///
/// Not intended to run at the same time as ConeStepCountDebugDisplay or ConeStepWeightSweep on
/// the same camera - all three dispatch the same reduction shader against the same buffer
/// semantics, so pick one per session.
/// </summary>
[RequireComponent(typeof(Camera))]
public sealed class ConeStepMarginSweep : MonoBehaviour
{
    [Header("Required")]
    [SerializeField] ComputeShader reductionShader;
    [SerializeField] Material targetMaterial;
    [Tooltip("Camera whose ConeStepMrtRendererFeature step-count target will be read. Defaults to the camera on this object.")]
    [SerializeField] Camera targetCamera;

    [Header("Sweep Range")]
    [SerializeField] float marginWeightMin = 0f;
    [SerializeField] float marginWeightMax = 2f;
    [Tooltip("Held fixed for every sample in the sweep.")]
    [SerializeField] float fixedProgressWeight = 1f;
    [Tooltip("Number of sample points along the margin-weight axis.")]
    [SerializeField, Min(2)] int sampleCount = 21;

    [Header("Sampling")]
    [Tooltip("Frames to wait after applying new weights before reading the step-count texture, " +
             "to make sure the change has actually reached a rendered frame.")]
    [SerializeField, Min(1)] int warmupFrames = 2;
    [Tooltip("Optional: average the step count over this many consecutive frames per sample point " +
             "to smooth out frame-to-frame noise (camera jitter, etc). 1 = single sample.")]
    [SerializeField, Min(1)] int samplesPerPoint = 1;
    [Tooltip("Frames to wait between consecutive samples within the same point to avoid sampling correlated frames.")]
    [SerializeField, Min(0)] int sampleDelayFrames = 1;
    [Tooltip("Frames to wait after the render feature has cleared both history buffers for a point. " +
         "The point's own weights are already active during these frames.")]
    [SerializeField, Min(1)] int historyResetFrames = 1;

    [Header("Output")]
    [Tooltip("Relative to Application.persistentDataPath, unless rooted.")]
    [SerializeField] string outputFileName = "cone_step_margin_sweep.csv";
    [SerializeField] bool startOnPlay = true;
    [SerializeField] bool logProgressToConsole = true;

    const string MarginWeightProperty = "_ReprojectionMarginWeight";
    const string ProgressWeightProperty = "_ReprojectionProgressWeight";
    const string UseHistoryProperty = "_UseHistory";

    GraphicsBuffer m_TotalSteps;
    int m_Kernel;
    bool m_Running;

    string OutputPath =>
        Path.IsPathRooted(outputFileName) ? outputFileName : Path.Combine(Application.persistentDataPath, outputFileName);

    void OnEnable()
    {
        if (targetCamera == null)
            targetCamera = GetComponent<Camera>();

        if (startOnPlay)
            StartCoroutine(RunSweep());
    }

    void OnDisable()
    {
        m_TotalSteps?.Release();
        m_TotalSteps = null;
    }

    [ContextMenu("Run Sweep Now")]
    public void RunSweepManually()
    {
        if (!m_Running)
            StartCoroutine(RunSweep());
    }

    IEnumerator RunSweep()
    {
        yield return new WaitForSeconds(1f);

        if (reductionShader == null || targetMaterial == null || targetCamera == null)
        {
            Debug.LogError("[ConeStepMarginSweep] Missing reductionShader, targetMaterial, or targetCamera.");
            yield break;
        }

        m_Running = true;
        m_Kernel = reductionShader.FindKernel("AccumulateSteps");
        m_TotalSteps = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(uint));

        string path = OutputPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path));

        double bestSteps = double.MaxValue;
        float bestMargin = 0f;

        targetMaterial.SetFloat(ProgressWeightProperty, fixedProgressWeight);

        using (var writer = new StreamWriter(path, false))
        {
            writer.WriteLine("margin_weight,progress_weight,step_count");

            for (int mi = 0; mi < sampleCount; mi++)
            {
                float margin = Mathf.Lerp(marginWeightMin, marginWeightMax, sampleCount == 1 ? 0f : mi / (float)(sampleCount - 1));

                // Set this point's configuration before resetting. The reset is performed by
                // the render feature itself, clearing both ping-pong textures, rather than
                // merely disabling their use while still writing new (old-weight) seeds.
                targetMaterial.SetFloat(MarginWeightProperty, margin);
                targetMaterial.SetFloat(ProgressWeightProperty, fixedProgressWeight);
                targetMaterial.SetFloat(UseHistoryProperty, 1f);
                ConeStepMrtPass.RequestHistoryReset(targetCamera);
                for (int r = 0; r < historyResetFrames; r++)
                    yield return new WaitForEndOfFrame();

                // The history is now known clean and contains only seeds generated
                // under this point's weights. Let that state warm up before sampling.
                for (int w = 0; w < warmupFrames; w++)
                    yield return new WaitForEndOfFrame();

                double accumulated = 0;
                for (int s = 0; s < samplesPerPoint; s++)
                {
                    if (s > 0 && sampleDelayFrames > 0)
                    {
                        for (int d = 0; d < sampleDelayFrames; d++)
                            yield return new WaitForEndOfFrame();
                    }
                    else
                    {
                        yield return new WaitForEndOfFrame();
                    }

                    uint steps = 0;
                    yield return SampleStepCountOnce(result => steps = result);
                    accumulated += steps;
                }

                double avgSteps = accumulated / samplesPerPoint;
                writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "{0:R},{1:R},{2:R}", margin, fixedProgressWeight, avgSteps));
                writer.Flush();

                if (avgSteps < bestSteps)
                {
                    bestSteps = avgSteps;
                    bestMargin = margin;
                }

                if (logProgressToConsole)
                    Debug.Log($"[ConeStepMarginSweep] {mi + 1}/{sampleCount}  margin={margin:F3} (progress fixed={fixedProgressWeight:F3})  steps={avgSteps:N0}");
            }

            writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "# best: margin_weight={0:R}, progress_weight={1:R}, step_count={2:R}", bestMargin, fixedProgressWeight, bestSteps));
        }

        Debug.Log($"[ConeStepMarginSweep] Done. Best margin weight: {bestMargin:F4} (progress fixed={fixedProgressWeight:F4}) " +
                  $"-> {bestSteps:N0} steps. Wrote {path}");
        m_Running = false;
    }

    /// <summary>
    /// Dispatches the reduction shader and blocks (within the coroutine) until the readback of
    /// this single frame's step-count texture completes, then hands the value to onResult.
    /// </summary>
    IEnumerator SampleStepCountOnce(Action<uint> onResult)
    {
        RenderTexture stepTexture = ConeStepMrtPass.GetStepCountTexture(targetCamera);
        if (stepTexture == null)
        {
            Debug.LogWarning("[ConeStepMarginSweep] Step-count texture is null - " +
                              "enable debugStepCount on ConeStepMrtRendererFeature.");
            onResult(0);
            yield break;
        }

        m_TotalSteps.SetData(new uint[] { 0 });
        reductionShader.SetTexture(m_Kernel, "_StepCountTexture", stepTexture);
        reductionShader.SetBuffer(m_Kernel, "_TotalSteps", m_TotalSteps);
        reductionShader.GetKernelThreadGroupSizes(m_Kernel, out uint x, out uint y, out _);
        reductionShader.Dispatch(m_Kernel, Mathf.CeilToInt(stepTexture.width / (float)x),
            Mathf.CeilToInt(stepTexture.height / (float)y), 1);

        var request = AsyncGPUReadback.Request(m_TotalSteps);
        yield return new WaitUntil(() => request.done);

        if (request.hasError)
        {
            Debug.LogWarning("[ConeStepMarginSweep] Readback error, recording 0 for this sample.");
            onResult(0);
            yield break;
        }

        onResult(request.GetData<uint>()[0]);
    }
}
