using UnityEngine;

public sealed class SimpleRotationLoop : MonoBehaviour
{
    [Header("Rotation Range")]
    [SerializeField] private Vector3 startRotation = Vector3.zero;
    [SerializeField] private Vector3 endRotation = new Vector3(0f, 360f, 0f);

    [Header("Timing")]
    [Tooltip("Total number of frames for one full loop cycle.")]
    [SerializeField, Min(1)] private int totalFrames = 60;

    private int m_CurrentFrame = 0;

    private void OnEnable()
    {
        m_CurrentFrame = 0;
        ApplyRotation();
    }

    private void Start()
    {
        Application.targetFrameRate = 140;
        QualitySettings.vSyncCount = 0;
    }

    private void Update()
    {
        // Advance by 1 frame
        m_CurrentFrame = (m_CurrentFrame + 1) % totalFrames;
        ApplyRotation();
    }

    private void ApplyRotation()
    {
        // Calculate t in range [0, 1)
        float t = (float)m_CurrentFrame / totalFrames;

        // Linearly interpolate between start and end rotation angles
        Vector3 currentEuler = Vector3.Lerp(startRotation, endRotation, t);
        transform.localRotation = Quaternion.Euler(currentEuler);
    }
}