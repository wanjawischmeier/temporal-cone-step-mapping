using System;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

[RequireComponent(typeof(Camera))]
public sealed class ConeStepVelocityProfiler : MonoBehaviour
{
    public enum FrameTimeMode
    {
        CPU, GPU
    }

    [Header("Required Resources")]
    [SerializeField] ComputeShader reductionShader;
    [SerializeField] Material targetMaterial;
    [SerializeField] Camera targetCamera;

    [Header("Animation Path")]
    [SerializeField] Transform startTransform;
    [SerializeField] Transform endTransform;
    [SerializeField] bool animatePosition = true;
    [SerializeField] bool animateRotation = true;

    [Header("Sweep Configuration")]
    [Tooltip("The fastest animation speed (fewest frames to complete the path).")]
    [SerializeField, Min(2)] int minFrames = 10;
    [Tooltip("The slowest animation speed (most frames to complete the path).")]
    [SerializeField, Min(2)] int maxFrames = 200;
    [Tooltip("Number of distinct speeds to test between min and max frames.")]
    [SerializeField, Min(2)] int speedSteps = 20;
    [Tooltip("How many full animation runs to average together per speed setting.")]
    [SerializeField, Min(1)] int runsPerMeasurement = 4;

    [Header("Margin Weight Sweep")]
    [Tooltip("_ReprojectionMarginWeight paired with minFrames - i.e. at sweep index 0. " +
             "Leave marginWeightMin == marginWeightMax (default) to keep the weight fixed and " +
             "sweep only frame count, as before. To sweep margin weight instead of speed, set " +
             "minFrames == maxFrames and give marginWeightMin/Max a real range - both sweeps " +
             "share the same speedSteps index, so either (or both at once) can vary.")]
    [SerializeField] float marginWeightMin = 1f;
    [SerializeField] float marginWeightMax = 1f;

    [Header("Output")]
    [SerializeField] string outputFileName = "cone_step_velocity_sweep.csv";
    [SerializeField] bool startOnPlay = true;
    [SerializeField] FrameTimeMode frameTimeMode;

    const string UseHistoryProperty = "_UseHistory";
    const string MarginWeightProperty = "_ReprojectionMarginWeight";
    GraphicsBuffer m_TotalSteps;
    int m_Kernel;
    bool m_Running;

    private sealed class MeasurementResult
    {
        public double avgTotalSteps;
        public double avgTotalTimeMs;
    }

    string OutputPath => Path.IsPathRooted(outputFileName) ? outputFileName : Path.Combine(Application.persistentDataPath, outputFileName);

    void OnEnable()
    {
        if (targetCamera == null) targetCamera = GetComponent<Camera>();
        if (startOnPlay) StartCoroutine(RunProfiler());
    }

    void OnDisable()
    {
        m_TotalSteps?.Release();
        m_TotalSteps = null;
    }

    [ContextMenu("Run Profiler Now")]
    public void RunProfilerManually()
    {
        if (!m_Running) StartCoroutine(RunProfiler());
    }

    IEnumerator RunProfiler()
    {
        yield return new WaitForSeconds(1f);

        if (reductionShader == null || targetMaterial == null || startTransform == null || endTransform == null)
        {
            Debug.LogError("[ConeStepVelocityProfiler] Missing required references.");
            yield break;
        }

        m_Running = true;
        m_Kernel = reductionShader.FindKernel("AccumulateSteps");
        m_TotalSteps = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(uint));

        string path = OutputPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path));

        using (var writer = new StreamWriter(path, false))
        {
            writer.WriteLine("total_frames,speed,margin_weight,history_on_steps,history_off_steps,history_on_time_ms,history_off_time_ms");

            for (int i = 0; i < speedSteps; i++)
            {
                float t = speedSteps == 1 ? 0f : i / (float)(speedSteps - 1);
                int totalFrames = Mathf.RoundToInt(Mathf.Lerp(minFrames, maxFrames, t));
                float speed = 1.0f / totalFrames;
                float marginWeight = Mathf.Lerp(marginWeightMin, marginWeightMax, t);

                // Shared by both the history-on and history-off measurements below - the weight
                // only actually influences behavior while history is on, but we set it
                // unconditionally so the CSV's margin_weight column always reflects what was
                // configured for this row, regardless of history state.
                targetMaterial.SetFloat(MarginWeightProperty, marginWeight);

                var onResult = new MeasurementResult();
                yield return RunMeasurementSet(totalFrames, runsPerMeasurement, true, onResult);

                var offResult = new MeasurementResult();
                yield return RunMeasurementSet(totalFrames, runsPerMeasurement, false, offResult);

                writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "{0},{1:R},{2:R},{3:R},{4:R},{5:R},{6:R}",
                    totalFrames, speed, marginWeight, onResult.avgTotalSteps, offResult.avgTotalSteps, onResult.avgTotalTimeMs, offResult.avgTotalTimeMs));
                writer.Flush();

                double reduction = 1.0 - (onResult.avgTotalSteps / offResult.avgTotalSteps);
                Debug.Log($"[Profiler] Frames: {totalFrames} | Margin: {marginWeight:F3} | History: {onResult.avgTotalSteps:N0} vs {offResult.avgTotalSteps:N0} steps | Reduction: {reduction:P2}");
            }
        }

        Debug.Log($"[ConeStepVelocityProfiler] Done. Wrote results to {path}");
        m_Running = false;
    }

    IEnumerator RunMeasurementSet(int framesPerRun, int runs, bool useHistory, MeasurementResult result)
    {
        if (frameTimeMode == FrameTimeMode.CPU)
        {
            yield return RunMeasurementSetCPU(framesPerRun, runs, useHistory, result);
        }
        else
        {
            yield return RunMeasurementSetGPU(framesPerRun, runs, useHistory, result);
        }
    }

    IEnumerator RunMeasurementSetCPU(int framesPerRun, int runs, bool useHistory, MeasurementResult result)
    {
        targetMaterial.SetFloat(UseHistoryProperty, useHistory ? 1f : 0f);

        double accumulatedSteps = 0;
        double accumulatedTimeMs = 0;

        for (int r = 0; r < runs; r++)
        {
            SetCameraTransform(0f);
            ConeStepMrtPass.RequestHistoryReset(targetCamera);
            yield return new WaitForEndOfFrame();
            yield return new WaitForEndOfFrame();

            double runSteps = 0;
            double runTimeMs = 0;
            Stopwatch frameTimer = new Stopwatch();

            for (int frame = 1; frame <= framesPerRun; frame++)
            {
                float normalizedTime = (float)frame / framesPerRun;
                SetCameraTransform(normalizedTime);

                frameTimer.Restart();
                yield return new WaitForEndOfFrame();
                frameTimer.Stop();

                runTimeMs += frameTimer.Elapsed.TotalMilliseconds;

                uint stepCount = 0;
                yield return SampleStepCountOnce(res => stepCount = res);
                runSteps += stepCount;
            }

            accumulatedSteps += runSteps;
            accumulatedTimeMs += runTimeMs;
        }

        result.avgTotalSteps = accumulatedSteps / runs;
        result.avgTotalTimeMs = accumulatedTimeMs / runs;
    }

    IEnumerator RunMeasurementSetGPU(int framesPerRun, int runs, bool useHistory, MeasurementResult result)
    {
        // Force VSync off so we measure raw hardware speed
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = -1;

        targetMaterial.SetFloat(UseHistoryProperty, useHistory ? 1f : 0f);

        double accumulatedSteps = 0;
        double accumulatedTimeMs = 0;
        FrameTiming[] frameTimings = new FrameTiming[1];

        for (int r = 0; r < runs; r++)
        {
            SetCameraTransform(0f);
            ConeStepMrtPass.RequestHistoryReset(targetCamera);

            // Wait a couple frames for pipeline to stabilize
            yield return new WaitForEndOfFrame();
            yield return new WaitForEndOfFrame();

            double runSteps = 0;
            double runTimeMs = 0;

            for (int frame = 1; frame <= framesPerRun; frame++)
            {
                float normalizedTime = (float)frame / framesPerRun;
                SetCameraTransform(normalizedTime);

                yield return new WaitForEndOfFrame();

                // Ask Unity for the actual hardware GPU timing of the frame that just finished
                FrameTimingManager.CaptureFrameTimings();
                FrameTimingManager.GetLatestTimings(1, frameTimings);

                // gpuFrameTime is the actual time the GPU spent executing the frame
                runTimeMs += frameTimings[0].gpuFrameTime;

                uint stepCount = 0;
                yield return SampleStepCountOnce(res => stepCount = res);
                runSteps += stepCount;
            }

            accumulatedSteps += runSteps;
            accumulatedTimeMs += runTimeMs;
        }

        result.avgTotalSteps = accumulatedSteps / runs;
        result.avgTotalTimeMs = accumulatedTimeMs / runs;
    }

    void SetCameraTransform(float t)
    {
        if (animatePosition)
            transform.position = Vector3.Lerp(startTransform.position, endTransform.position, t);

        if (animateRotation)
            transform.rotation = Quaternion.Slerp(startTransform.rotation, endTransform.rotation, t);
    }

    IEnumerator SampleStepCountOnce(Action<uint> onResult)
    {
        RenderTexture stepTexture = ConeStepMrtPass.GetStepCountTexture(targetCamera);
        if (stepTexture == null)
        {
            onResult(0);
            yield break;
        }

        m_TotalSteps.SetData(new uint[] { 0 });
        reductionShader.SetTexture(m_Kernel, "_StepCountTexture", stepTexture);
        reductionShader.SetBuffer(m_Kernel, "_TotalSteps", m_TotalSteps);
        reductionShader.GetKernelThreadGroupSizes(m_Kernel, out uint x, out uint y, out _);
        reductionShader.Dispatch(m_Kernel, Mathf.CeilToInt(stepTexture.width / (float)x), Mathf.CeilToInt(stepTexture.height / (float)y), 1);

        var request = AsyncGPUReadback.Request(m_TotalSteps);
        yield return new WaitUntil(() => request.done);

        if (!request.hasError) onResult(request.GetData<uint>()[0]);
        else onResult(0);
    }
}