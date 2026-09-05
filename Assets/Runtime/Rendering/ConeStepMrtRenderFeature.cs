using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.Experimental.Rendering;

public class ConeStepMrtRendererFeature : ScriptableRendererFeature
{
    [Tooltip("Layer mask for objects to render with cone-step MRT.")]
    public LayerMask layerMask = -1; // Default: everything
    [Tooltip("Filter mode for the MRT render textures.")]
    public FilterMode filterMode = FilterMode.Bilinear;
    [Tooltip("Graphics format for the MRT render textures.")]
    public GraphicsFormat mrtGraphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
    public bool showDebug = false;

    ConeStepMrtPass m_ConeStepPass;

    public override void Create()
    {
        m_ConeStepPass = new ConeStepMrtPass();
        m_ConeStepPass.renderPassEvent = RenderPassEvent.BeforeRenderingOpaques;
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        m_ConeStepPass.Setup(layerMask, filterMode, mrtGraphicsFormat, showDebug);
        renderer.EnqueuePass(m_ConeStepPass);
    }

    protected override void Dispose(bool disposing)
    {
        m_ConeStepPass?.Cleanup();
    }
}
