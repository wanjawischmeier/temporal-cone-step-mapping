using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

// Deliberately structured to match ParallaxMrtPass as closely as possible - the
// generic MRT/history-buffer machinery doesn't depend on which shader is being
// drawn, only on the ShaderTagId used to filter the renderer list. This is kept
// as a separate pass/feature (rather than reusing ParallaxMrtPass directly) so
// the two shaders can be enabled independently and never cross-draw each
// other's objects. If you don't need that separation, you can instead just tag
// ConeStepMapping.shader's Pass with LightMode = "MRTOnly" and reuse the
// existing ParallaxMrtPass/ParallaxMrtRendererFeature unmodified.
public class ConeStepMrtPass : ScriptableRenderPass
{
    class MrtPassData
    {
        public RendererListHandle rendererListHandle;
        public TextureHandle previousFrame;
        public Matrix4x4 prevViewProjMatrix;
    }

    class CameraHistory
    {
        public RTHandle RT_A;
        public RTHandle RT_B;
        public int passIndex;
        public Matrix4x4 prevViewProjMatrix = Matrix4x4.identity;
        public bool isFirstFrame = true;
    }

    static readonly ShaderTagId kShaderTagId = new ShaderTagId("ConeStepMRT");

    LayerMask m_LayerMask;
    Dictionary<Camera, CameraHistory> m_Histories = new Dictionary<Camera, CameraHistory>();
    FilterMode m_FilterMode;
    GraphicsFormat m_MrtGraphicsFormat;
    bool m_ShowDebug;

    public void Setup(LayerMask layerMask, FilterMode filterMode, GraphicsFormat mrtGraphicsFormat, bool showDebug = false)
    {
        m_LayerMask = layerMask;
        m_FilterMode = filterMode;
        m_MrtGraphicsFormat = mrtGraphicsFormat;
        m_ShowDebug = showDebug;
    }

    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
    {
        var resourceData = frameData.Get<UniversalResourceData>();
        var cameraData = frameData.Get<UniversalCameraData>();
        var renderingData = frameData.Get<UniversalRenderingData>();
        var lightData = frameData.Get<UniversalLightData>();

        var camera = cameraData.camera;
        if (!m_Histories.TryGetValue(camera, out var history))
        {
            history = new CameraHistory();
            m_Histories[camera] = history;
        }

        var colorDesc = cameraData.cameraTargetDescriptor;
        colorDesc.depthBufferBits = 0;
        colorDesc.graphicsFormat = GraphicsFormat.R8G8B8A8_UNorm;

        bool isRenderToTexture = true; // since we render to MRT texture
        Matrix4x4 viewMatrix = camera.worldToCameraMatrix;
        Matrix4x4 projMatrix = GL.GetGPUProjectionMatrix(camera.projectionMatrix, isRenderToTexture);
        Matrix4x4 currentViewProj = projMatrix * viewMatrix;

        if (history.isFirstFrame)
        {
            history.prevViewProjMatrix = currentViewProj;
            history.isFirstFrame = false;
        }

        // Save the previous matrix for the shader, then update the history with the current one
        Matrix4x4 prevViewProj = history.prevViewProjMatrix;
        history.prevViewProjMatrix = currentViewProj;

        bool needsResize = history.RT_A == null ||
                           history.RT_A.rt.width != colorDesc.width ||
                           history.RT_A.rt.height != colorDesc.height;

        if (needsResize)
        {
            history.RT_A?.Release();
            history.RT_B?.Release();

            history.RT_A = RTHandles.Alloc(colorDesc.width, colorDesc.height,
                colorFormat: m_MrtGraphicsFormat,
                filterMode: m_FilterMode,
                name: $"ConeStepMRT_A_{camera.name}");
            history.RT_B = RTHandles.Alloc(colorDesc.width, colorDesc.height,
                colorFormat: m_MrtGraphicsFormat,
                filterMode: m_FilterMode,
                name: $"ConeStepMRT_B_{camera.name}");
        }

        // By using an internal pass index per camera instead of Time.frameCount,
        // we guarantee the buffers swap every time this camera renders, even if
        // the game is paused or we are in Edit mode. See ParallaxMrtPass for the
        // rationale (this comment is preserved verbatim from that pass).
        history.passIndex = (history.passIndex + 1) % 2;
        bool useTextureA = history.passIndex == 0;

        var readTexture = useTextureA ? history.RT_A : history.RT_B;
        var writeTexture = useTextureA ? history.RT_B : history.RT_A;

        var previousFrame = renderGraph.ImportTexture(readTexture);
        var mrt1 = renderGraph.ImportTexture(writeTexture);

        using (var builder = renderGraph.AddRasterRenderPass<MrtPassData>("Cone Step MRT Pass", out var passData))
        {
            passData.previousFrame = previousFrame;
            passData.prevViewProjMatrix = prevViewProj;

            builder.AllowPassCulling(false);
            builder.AllowGlobalStateModification(true);

            var sortingCriteria = cameraData.defaultOpaqueSortFlags;
            var drawingSettings = RenderingUtils.CreateDrawingSettings(kShaderTagId,
                renderingData, cameraData, lightData, sortingCriteria);
            drawingSettings.overrideMaterialPassIndex = 0;

            var filteringSettings = new FilteringSettings(RenderQueueRange.all, m_LayerMask);

            var renderListParams = new RendererListParams(renderingData.cullResults, drawingSettings, filteringSettings);
            passData.rendererListHandle = renderGraph.CreateRendererList(renderListParams);
            builder.UseRendererList(passData.rendererListHandle);

            // Read from previous frame
            builder.UseTexture(passData.previousFrame, AccessFlags.Read);

            // Write to current frame (different texture)
            builder.SetRenderAttachment(resourceData.activeColorTexture, 0);
            builder.SetRenderAttachment(mrt1, 1);
            builder.SetRenderAttachmentDepth(resourceData.activeDepthTexture, AccessFlags.Write);

            builder.SetRenderFunc(static (MrtPassData data, RasterGraphContext rgContext) =>
            {
                rgContext.cmd.SetGlobalTexture("_PreviousFrame", data.previousFrame);
                rgContext.cmd.SetGlobalMatrix("_CustomPrevViewProjMatrix", data.prevViewProjMatrix);
                rgContext.cmd.DrawRendererList(data.rendererListHandle);
            });
        }
    }

    public void Cleanup()
    {
        foreach (var history in m_Histories.Values)
        {
            history.RT_A?.Release();
            history.RT_B?.Release();
        }
        m_Histories.Clear();
    }
}
