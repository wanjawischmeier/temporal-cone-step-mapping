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
        public RTHandle stepCount;
        public int passIndex;
        public Matrix4x4 prevViewProjMatrix = Matrix4x4.identity;
        public bool isFirstFrame = true;
    }

    static readonly ShaderTagId kShaderTagId = new ShaderTagId("ConeStepMRT");

    LayerMask m_LayerMask;
    static readonly Dictionary<Camera, CameraHistory> s_Histories = new Dictionary<Camera, CameraHistory>();
    static readonly HashSet<Camera> s_HistoryResetRequests = new HashSet<Camera>();
    FilterMode m_FilterMode;
    GraphicsFormat m_MrtGraphicsFormat;
    bool m_ShowDebug;
    bool m_DebugStepCount;

    public void Setup(LayerMask layerMask, FilterMode filterMode, GraphicsFormat mrtGraphicsFormat, bool showDebug = false, bool debugStepCount = false)
    {
        m_LayerMask = layerMask;
        m_FilterMode = filterMode;
        m_MrtGraphicsFormat = mrtGraphicsFormat;
        m_ShowDebug = showDebug;
        m_DebugStepCount = debugStepCount;
    }

    /// <summary>Returns this camera's per-pixel step-count target while debugging is enabled.</summary>
    public static RenderTexture GetStepCountTexture(Camera camera)
    {
        return camera != null && s_Histories.TryGetValue(camera, out var history) && history.stepCount != null
            ? history.stepCount.rt : null;
    }

    public static RenderTexture GetMrt1Texture(Camera camera)
    {
        if (camera == null || !s_Histories.TryGetValue(camera, out var history))
            return null;
        bool useTextureA = history.passIndex == 0;
        var written = useTextureA ? history.RT_A : history.RT_B; // flip both branches if this checks out backwards
        return written?.rt;
    }

    /// <summary>
    /// Invalidates both ping-pong history targets before this camera's next cone
    /// pass. The request is executed inside the render graph, so it remains
    /// correctly ordered with the pass that subsequently reads the history.
    /// </summary>
    public static void RequestHistoryReset(Camera camera)
    {
        if (camera != null)
            s_HistoryResetRequests.Add(camera);
    }

    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
    {
        var resourceData = frameData.Get<UniversalResourceData>();
        var cameraData = frameData.Get<UniversalCameraData>();
        var renderingData = frameData.Get<UniversalRenderingData>();
        var lightData = frameData.Get<UniversalLightData>();

        var camera = cameraData.camera;
        if (!s_Histories.TryGetValue(camera, out var history))
        {
            history = new CameraHistory();
            s_Histories[camera] = history;
        }

        // A reset must also reset the reprojection matrix baseline. Otherwise a
        // newly invalidated history texture could later be paired with a matrix
        // that predates the reset.
        bool resetHistory = s_HistoryResetRequests.Remove(camera);

        var colorDesc = cameraData.cameraTargetDescriptor;
        colorDesc.depthBufferBits = 0;
        colorDesc.graphicsFormat = GraphicsFormat.R8G8B8A8_UNorm;

        // Get the exact View matrix URP is using (handles Camera Relative Rendering automatically)
        Matrix4x4 viewMatrix = cameraData.GetViewMatrix();

        // Get the projection matrix and format it for GPU texture rendering
        Matrix4x4 projMatrix = GL.GetGPUProjectionMatrix(cameraData.GetProjectionMatrix(), true);

        Matrix4x4 currentViewProj = projMatrix * viewMatrix;

        if (history.isFirstFrame || resetHistory)
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
                filterMode: FilterMode.Point,
                name: $"ConeStepMRT_A_{camera.name}");
            history.RT_B = RTHandles.Alloc(colorDesc.width, colorDesc.height,
                colorFormat: m_MrtGraphicsFormat,
                filterMode: FilterMode.Point,
                name: $"ConeStepMRT_B_{camera.name}");
        }

        if (m_DebugStepCount && (history.stepCount == null ||
                                 history.stepCount.rt.width != colorDesc.width ||
                                 history.stepCount.rt.height != colorDesc.height))
        {
            history.stepCount?.Release();
            history.stepCount = RTHandles.Alloc(colorDesc.width, colorDesc.height,
                colorFormat: GraphicsFormat.R32_UInt,
                filterMode: FilterMode.Point,
                name: $"ConeStepCount_{camera.name}");
        }

        // By using an internal pass index per camera instead of Time.frameCount,
        // we guarantee the buffers swap every time this camera renders, even if
        // the game is paused or we are in Edit mode. See ParallaxMrtPass for the
        // rationale (this comment is preserved verbatim from that pass).
        history.passIndex = (history.passIndex + 1) % 2;
        bool useTextureA = history.passIndex == 0;

        var readTexture = useTextureA ? history.RT_A : history.RT_B;
        var writeTexture = useTextureA ? history.RT_B : history.RT_A;

        // Instead of separate raster passes that bind these textures as attachments
        // (which the render-graph compiler then merges with the main MRT pass, causing
        // the same RTHandle to be bound at two different attachment indices at once),
        // clear them via the import's load action. No extra attachment binding = no
        // merge conflict.
        var readImportParams = new ImportResourceParams
        {
            clearOnFirstUse = resetHistory,
            clearColor = Color.clear,
            discardOnLastUse = false
        };
        var writeImportParams = new ImportResourceParams
        {
            clearOnFirstUse = true,
            clearColor = Color.clear,
            discardOnLastUse = false
        };

        var previousFrame = renderGraph.ImportTexture(readTexture, readImportParams);
        var mrt1 = renderGraph.ImportTexture(writeTexture, writeImportParams);

        TextureHandle stepCountTexture = default;
        if (m_DebugStepCount)
        {
            var stepCountImportParams = new ImportResourceParams
            {
                clearOnFirstUse = true, // always clear per frame, debug-only cost
                clearColor = Color.clear,
                discardOnLastUse = false
            };
            stepCountTexture = renderGraph.ImportTexture(history.stepCount, stepCountImportParams);
        }

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
            if (m_DebugStepCount)
                builder.SetRenderAttachment(stepCountTexture, 2);
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
        foreach (var history in s_Histories.Values)
        {
            history.RT_A?.Release();
            history.RT_B?.Release();
            history.stepCount?.Release();
        }
        s_Histories.Clear();
        s_HistoryResetRequests.Clear();
    }

    class DebugClearPassData { }
    class HistoryClearPassData { }
}
