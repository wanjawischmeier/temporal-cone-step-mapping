using UnityEngine;
using UnityEngine.UI;

[ExecuteAlways]
public class DebugConeStepView : MonoBehaviour
{
    [SerializeField] public Camera targetCamera;
    public RawImage rawImage;
    public Shader debugShader;

    [Tooltip("Off = boolean history-used view. On = normalized startSc value view.")]
    public bool showValue = false;
    public float normalizeMin = 0.0f;
    public float normalizeMax = 1.0f;

    Material m_Material;

    public void SetShowValue(bool value) => showValue = value;

    void OnEnable()
    {
        if (debugShader == null)
            debugShader = Shader.Find("Hidden/DebugConeStepStartSc");

        if (m_Material == null && debugShader != null)
            m_Material = new Material(debugShader) { hideFlags = HideFlags.HideAndDontSave };

        if (rawImage != null && m_Material != null)
            rawImage.material = m_Material;
    }

    void OnDisable()
    {
        if (m_Material != null)
        {
            if (Application.isPlaying) Destroy(m_Material);
            else DestroyImmediate(m_Material);
            m_Material = null;
        }
    }

    void Update()
    {
        if (targetCamera == null || rawImage == null || m_Material == null)
            return;

        var mrt1 = ConeStepMrtPass.GetMrt1Texture(targetCamera);
        if (mrt1 == null)
            return;

        rawImage.texture = mrt1;
        m_Material.SetFloat("_ShowValue", showValue ? 1f : 0f);
        m_Material.SetFloat("_NormalizeMin", normalizeMin);
        m_Material.SetFloat("_NormalizeMax", normalizeMax);
    }
}