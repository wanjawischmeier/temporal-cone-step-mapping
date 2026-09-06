using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

/// <summary>Reduces the cone-step debug target after this camera renders and shows its total.</summary>
[RequireComponent(typeof(Camera))]
public sealed class ConeStepCountDebugDisplay : MonoBehaviour
{
    [Header("Required")]
    [SerializeField] ComputeShader reductionShader;
    [SerializeField] TMP_Text totalStepText;

    [Header("Optional Heatmap")]
    [SerializeField] RawImage heatmapImage;
    [SerializeField] Material heatmapMaterial;
    [SerializeField, Min(1)] float heatmapMaximum = 64;
    [SerializeField] bool smoothHeatmap = false;
    [SerializeField, Range(1, 240)] int smoothFrameCount = 30;

    RenderTexture m_AccumTextureA;
    RenderTexture m_AccumTextureB;
    bool m_AccumIsAActive = true; // true => A currently holds the latest accumulated value
    const int AccumulatePassIndex = 1; // "Accumulate" pass in the heatmap shader

    Camera m_Camera;
    GraphicsBuffer m_TotalSteps;
    Material m_RuntimeHeatmapMaterial;
    bool m_ReadbackPending;
    int m_Kernel;

    void OnEnable()
    {
        m_Camera = GetComponent<Camera>();
        if (reductionShader != null)
            m_Kernel = reductionShader.FindKernel("AccumulateSteps");
        m_TotalSteps = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(uint));

        if (heatmapImage != null && heatmapMaterial != null)
        {
            m_RuntimeHeatmapMaterial = new Material(heatmapMaterial);
            heatmapImage.material = m_RuntimeHeatmapMaterial;
        }
        // Camera.onPostRender is a built-in-pipeline callback. The SRP event is
        // guaranteed to run after the render feature has populated its MRT target.
        RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
    }

    void OnDisable()
    {
        RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
        m_TotalSteps?.Release();
        m_TotalSteps = null;
        if (m_RuntimeHeatmapMaterial != null)
            Destroy(m_RuntimeHeatmapMaterial);
        m_RuntimeHeatmapMaterial = null;
        m_ReadbackPending = false;
        ReleaseAccumTextures();
    }

    void ReleaseAccumTextures()
    {
        if (m_AccumTextureA != null) { m_AccumTextureA.Release(); DestroyImmediate(m_AccumTextureA); m_AccumTextureA = null; }
        if (m_AccumTextureB != null) { m_AccumTextureB.Release(); DestroyImmediate(m_AccumTextureB); m_AccumTextureB = null; }
    }

    void EnsureAccumTextures(int width, int height)
    {
        if (m_AccumTextureA != null && m_AccumTextureA.width == width && m_AccumTextureA.height == height)
            return;

        ReleaseAccumTextures();
        var desc = new RenderTextureDescriptor(width, height, RenderTextureFormat.ARGBHalf, 0)
        {
            enableRandomWrite = false,
            msaaSamples = 1,
            sRGB = false
        };
        m_AccumTextureA = new RenderTexture(desc) { name = "ConeStepAccumA", hideFlags = HideFlags.HideAndDontSave };
        m_AccumTextureA.Create();
        m_AccumTextureB = new RenderTexture(desc) { name = "ConeStepAccumB", hideFlags = HideFlags.HideAndDontSave };
        m_AccumTextureB.Create();
        m_AccumIsAActive = true; // both start black; smoothing ramps up over ~smoothFrameCount frames
    }

    void OnEndCameraRendering(ScriptableRenderContext context, Camera camera)
    {
        if (camera != m_Camera || reductionShader == null || m_ReadbackPending)
            return;

        RenderTexture stepTexture = ConeStepMrtPass.GetStepCountTexture(camera);
        if (stepTexture == null)
            return; // Enable debugStepCount on ConeStepMrtRendererFeature first.

        if (m_RuntimeHeatmapMaterial != null)
        {
            m_RuntimeHeatmapMaterial.SetFloat("_MaxSteps", heatmapMaximum);
            m_RuntimeHeatmapMaterial.SetTexture("_StepCountTexture", stepTexture);

            if (smoothHeatmap)
            {
                EnsureAccumTextures(stepTexture.width, stepTexture.height);

                RenderTexture source = m_AccumIsAActive ? m_AccumTextureA : m_AccumTextureB;
                RenderTexture dest = m_AccumIsAActive ? m_AccumTextureB : m_AccumTextureA;

                m_RuntimeHeatmapMaterial.SetTexture("_PrevAccum", source);
                m_RuntimeHeatmapMaterial.SetFloat("_AccumAlpha", 1f / smoothFrameCount);
                m_RuntimeHeatmapMaterial.SetFloat("_MaxSteps", heatmapMaximum); // now also consumed by the Accumulate pass
                Graphics.Blit(stepTexture, dest, m_RuntimeHeatmapMaterial, AccumulatePassIndex);
                Graphics.Blit(stepTexture, dest, m_RuntimeHeatmapMaterial, AccumulatePassIndex);
                m_AccumIsAActive = !m_AccumIsAActive;

                m_RuntimeHeatmapMaterial.SetTexture("_SmoothedStepCountTexture", dest);
                m_RuntimeHeatmapMaterial.SetFloat("_UseSmoothing", 1f);
            }
            else
            {
                m_RuntimeHeatmapMaterial.SetFloat("_UseSmoothing", 0f);
                m_RuntimeHeatmapMaterial.SetFloat("_AccumAlpha", 1f);
            }

            heatmapImage.texture = stepTexture;
        }

        // Do not reset the buffer while its previous asynchronous readback is pending.
        m_TotalSteps.SetData(new uint[] { 0 });
        reductionShader.SetTexture(m_Kernel, "_StepCountTexture", stepTexture);
        reductionShader.SetBuffer(m_Kernel, "_TotalSteps", m_TotalSteps);
        reductionShader.GetKernelThreadGroupSizes(m_Kernel, out uint x, out uint y, out _);
        reductionShader.Dispatch(m_Kernel, Mathf.CeilToInt(stepTexture.width / (float)x),
            Mathf.CeilToInt(stepTexture.height / (float)y), 1);

        m_ReadbackPending = true;
        AsyncGPUReadback.Request(m_TotalSteps, request =>
        {
            m_ReadbackPending = false;
            if (!request.hasError && totalStepText != null)
                totalStepText.text = $"Cone-map steps: {request.GetData<uint>()[0]:N0}";
        });
    }
}
