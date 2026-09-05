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
            m_RuntimeHeatmapMaterial.SetTexture("_StepCountTexture", stepTexture);
            m_RuntimeHeatmapMaterial.SetFloat("_MaxSteps", heatmapMaximum);
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
