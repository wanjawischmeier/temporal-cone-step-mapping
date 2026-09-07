using System;
using System.Collections;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Sweeps _ReprojectionMarginWeight / _ReprojectionProgressWeight on a target material over a
/// grid, and for each cell records the resulting total cone-step count for one frame.
/// Writes a CSV suitable for the companion Python plotting script.
///
/// Unlike ConeStepCountDebugDisplay, this deliberately reads back SYNCHRONOUSLY (the coroutine
/// blocks on each request) rather than fire-and-forget. A sweep needs each step-count sample
/// paired unambiguously with the weights that produced it; overlapping async requests across
/// changing weights would risk attributing a readback to the wrong grid cell.
///
/// Not intended to run at the same time as ConeStepCountDebugDisplay on the same camera - both
/// dispatch the same reduction shader against the same buffer semantics, so pick one per session.
/// </summary>
[RequireComponent(typeof(Camera))]
public sealed class ConeStepWeightSweep : MonoBehaviour
{
    [Header("Required")]
    [SerializeField] ComputeShader reductionShader;
    [SerializeField] Material targetMaterial;
    [Tooltip("Camera whose ConeStepMrtRendererFeature step-count target will be read. Defaults to the camera on this object.")]
    [SerializeField] Camera targetCamera;

    [Header("Sweep Range")]
    [SerializeField] float marginWeightMin = 0f;
    [SerializeField] float marginWeightMax = 2f;
    [SerializeField] float progressWeightMin = 0f;
    [SerializeField] float progressWeightMax = 2f;
    [Tooltip("Number of sample points per axis. Total samples = gridResolution^2.")]
    [SerializeField, Min(2)] int gridResolution = 11;

    [Header("Sampling")]
    [Tooltip("Frames to wait after applying new weights before reading the step-count texture, " +
             "to make sure the change has actually reached a rendered frame.")]
    [SerializeField, Min(1)] int warmupFrames = 2;
    [Tooltip("Optional: average the step count over this many consecutive frames per grid cell " +
             "to smooth out frame-to-frame noise (camera jitter, etc). 1 = single sample.")]
    [SerializeField, Min(1)] int samplesPerCell = 1;
    [Tooltip("Frames to wait between consecutive samples within the same cell to avoid sampling correlated frames.")]
    [SerializeField, Min(0)] int sampleDelayFrames = 1;
    [Tooltip("Frames to hold _UseHistory off between cells, to flush stale reprojection state from " +
         "the previous cell's weights before sampling this one. Does NOT run between samples " +
         "within the same cell - the running history across those is the point.")]
    [SerializeField, Min(1)] int historyResetFrames = 1;

    [Header("Output")]
    [Tooltip("Relative to Application.persistentDataPath, unless rooted.")]
    [SerializeField] string outputFileName = "cone_step_sweep.csv";
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
        if (reductionShader == null || targetMaterial == null || targetCamera == null)
        {
            Debug.LogError("[ConeStepWeightSweep] Missing reductionShader, targetMaterial, or targetCamera.");
            yield break;
        }

        m_Running = true;
        m_Kernel = reductionShader.FindKernel("AccumulateSteps");
        m_TotalSteps = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(uint));

        string path = OutputPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path));

        double bestSteps = double.MaxValue;
        float bestMargin = 0f;
        float bestProgress = 0f;
        int totalCells = gridResolution * gridResolution;
        int cellIndex = 0;

        using (var writer = new StreamWriter(path, false))
        {
            writer.WriteLine("margin_weight,progress_weight,step_count");

            for (int mi = 0; mi < gridResolution; mi++)
            {
                float margin = Mathf.Lerp(marginWeightMin, marginWeightMax, gridResolution == 1 ? 0f : mi / (float)(gridResolution - 1));

                for (int pi = 0; pi < gridResolution; pi++)
                {
                    float progress = Mathf.Lerp(progressWeightMin, progressWeightMax, gridResolution == 1 ? 0f : pi / (float)(gridResolution - 1));

                    // Flush the previous cell's accumulated reprojection state before sampling this one,
                    // so every cell starts from the same clean baseline rather than inheriting whatever
                    // history built up under the last cell's weights.
                    targetMaterial.SetFloat(UseHistoryProperty, 0f);
                    for (int r = 0; r < historyResetFrames; r++)
                        yield return new WaitForEndOfFrame();
                    targetMaterial.SetFloat(UseHistoryProperty, 1f);

                    targetMaterial.SetFloat(MarginWeightProperty, margin);
                    targetMaterial.SetFloat(ProgressWeightProperty, progress);

                    // Let the new weights actually reach a rendered frame before we sample.
                    for (int w = 0; w < warmupFrames; w++)
                        yield return new WaitForEndOfFrame();

                    double accumulated = 0;
                    for (int s = 0; s < samplesPerCell; s++)
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

                    double avgSteps = accumulated / samplesPerCell;
                    writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "{0:R},{1:R},{2:R}", margin, progress, avgSteps));
                    writer.Flush();

                    if (avgSteps < bestSteps)
                    {
                        bestSteps = avgSteps;
                        bestMargin = margin;
                        bestProgress = progress;
                    }

                    cellIndex++;
                    if (logProgressToConsole)
                        Debug.Log($"[ConeStepWeightSweep] {cellIndex}/{totalCells}  margin={margin:F3} progress={progress:F3}  steps={avgSteps:N0}");
                }
            }

            writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "# best: margin_weight={0:R}, progress_weight={1:R}, step_count={2:R}", bestMargin, bestProgress, bestSteps));
        }

        Debug.Log($"[ConeStepWeightSweep] Done. Best weights: margin={bestMargin:F4} progress={bestProgress:F4} " +
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
            Debug.LogWarning("[ConeStepWeightSweep] Step-count texture is null - " +
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
            Debug.LogWarning("[ConeStepWeightSweep] Readback error, recording 0 for this sample.");
            onResult(0);
            yield break;
        }

        onResult(request.GetData<uint>()[0]);
    }
}
