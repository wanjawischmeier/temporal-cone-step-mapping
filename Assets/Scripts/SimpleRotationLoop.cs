using UnityEngine;

public sealed class SimpleRotationLoop : MonoBehaviour
{
    [Header("Rotation Range")]
    [SerializeField] private Vector3 startRotation = new Vector3(0f, -5f, 0f);
    [SerializeField] private Vector3 endRotation = new Vector3(0f, 5f, 0f);

    [Header("Timing")]
    [Tooltip("Total number of frames for one full one-way sweep (Start -> End).")]
    [SerializeField, Min(1)] private int halfCycleFrames = 600;

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
        // Smoothly advance time without popping back to 0
        m_CurrentFrame++;
        ApplyRotation();
    }

    private void ApplyRotation()
    {
        // Calculate t smoothly ping-ponging in the range [0, 1]
        float t = Mathf.PingPong((float)m_CurrentFrame / halfCycleFrames, 1.0f);

        // Smooth step interpolation prevents sharp velocity spikes at endpoints
        float smoothT = Mathf.SmoothStep(0.0f, 1.0f, t);

        // Linearly interpolate between start and end rotation angles
        Vector3 currentEuler = Vector3.Lerp(startRotation, endRotation, smoothT);
        transform.localRotation = Quaternion.Euler(currentEuler);
    }
}