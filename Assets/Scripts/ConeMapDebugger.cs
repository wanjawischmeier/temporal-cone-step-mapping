using UnityEngine;

[ExecuteAlways]
public class ConeMapDebugger : MonoBehaviour
{
    [Header("Maps & Data")]
    [Tooltip("Textur, die im A-Kanal die Höhe speichert. Muss 'Read/Write Enabled' sein!")]
    public Texture2D heightMap;
    [Tooltip("Cone Map Textur. Muss 'Read/Write Enabled' sein!")]
    public Texture2D coneMap;

    public float minHeight = 0f;
    public float maxHeight = 10f;
    public float physicalSize = 10f;

    [Header("Cone Modus")]
    public bool isAnisotropic = false;

    [Header("Manual Start Cone")]
    public bool showCone = true;
    [Range(0f, 1f)] public float startU = 0.5f;
    [Range(0f, 1f)] public float startV = 0.5f;

    [Header("Main Ray")]
    public bool showMainRay = true;
    [Tooltip("Startpunkt des Rays")]
    public Transform mainRayOrigin;
    [Tooltip("Zielpunkt des Rays (definiert die Richtung)")]
    public Transform mainRayTarget;

    [Header("Cone Stepping")]
    [Tooltip("Anzahl der Raymarching Schritte entlang der Intersections")]
    [Range(0, 50)] public int coneSteps = 0;
    [Tooltip("Schritt der aktuell visualisiert wird (bei 0 alle)")]
    [Range(0, 50)] public int visualizationConeStep = 0;
    public Color firstConeColor = Color.blue;
    public Color otherConeColor = new Color(0f, 0.5f, 0.5f, 1f); // Dark Cyan
    public Color intersectionColor = Color.cyan;

    [Header("Reference Ray (Nur visuell)")]
    public bool showRefRay = false;
    public Transform refRayOrigin;
    public Transform refRayTarget;
    public Color refRayColor = Color.cyan;

    private void OnDrawGizmos()
    {
        if (coneMap == null || heightMap == null) return;

        // Sicherheits-Check
        try
        {
            coneMap.GetPixel(0, 0);
            heightMap.GetPixel(0, 0);
        }
        catch
        {
            Debug.LogWarning("ConeMapDebugger: HeightMap und ConeMap müssen 'Read/Write Enabled' sein!");
            return;
        }

        Gizmos.matrix = transform.localToWorldMatrix;
        Matrix4x4 worldToLocal = transform.worldToLocalMatrix;

        float heightScale = Mathf.Max(maxHeight - minHeight, 0.0001f);
        float sphereRadius = physicalSize * 0.005f;

        DrawReferenceRay(worldToLocal);

        Vector2 currentUV = new Vector2(startU, startV);
        Color currentConeColor = firstConeColor;

        Vector3 currentRayOrigin = Vector3.zero;
        Vector3 currentRayDir = Vector3.forward;
        bool hasRay = mainRayOrigin != null && mainRayTarget != null;

        if (hasRay && showMainRay)
        {
            currentRayOrigin = worldToLocal.MultiplyPoint3x4(mainRayOrigin.position);
            Vector3 localTar = worldToLocal.MultiplyPoint3x4(mainRayTarget.position);
            currentRayDir = (localTar - currentRayOrigin).normalized;

            Gizmos.color = Color.red;
            Gizmos.DrawRay(currentRayOrigin, currentRayDir * physicalSize * 2f);
        }

        // Cone Stepping Loop
        for (int step = 0; step <= coneSteps; step++)
        {
            float normHeight = heightMap.GetPixelBilinear(currentUV.x, currentUV.y).a;
            Color texData = coneMap.GetPixelBilinear(currentUV.x, currentUV.y);

            Vector3 apex = new Vector3(
                (currentUV.x - 0.5f) * physicalSize,
                Mathf.Lerp(minHeight, maxHeight, normHeight),
                (currentUV.y - 0.5f) * physicalSize
            );

            float coneDrawHeight = Mathf.Max(maxHeight - apex.y, physicalSize * 0.25f);
            bool shouldDrawDetailed = step == visualizationConeStep || coneSteps == 0;
            bool hit = false;
            float t = 0f;
            Vector3 relOrigin = currentRayOrigin - apex;

            if (isAnisotropic)
            {
                // R=X+, G=X-, B=Z+, A=Z-
                Vector4 rAniso = new Vector4(texData.r, texData.g, texData.b, texData.a) * (physicalSize / heightScale);

                if (showCone || step > 0)
                    DrawWirePyramid(apex, rAniso, coneDrawHeight, currentConeColor, shouldDrawDetailed);

                if (hasRay && showMainRay)
                    hit = IntersectPyramid(relOrigin, currentRayDir, rAniso, out t);
            }
            else
            {
                // Fallback Isotropisch (wir lesen der Einfachheit halber den G-Kanal)
                float rIso = texData.g * (physicalSize / heightScale);

                if (showCone || step > 0)
                    DrawWireCone(apex, rIso, coneDrawHeight, currentConeColor, shouldDrawDetailed);

                if (hasRay && showMainRay)
                    hit = IntersectCone(relOrigin, currentRayDir, rIso, out t);
            }

            if (!hasRay || !showMainRay) break;

            if (hit)
            {
                if (t < 0.0001f) t = 0.001f; // Epsilon-Verschiebung gegen Endlosschleifen

                Vector3 hitPointLocal = currentRayOrigin + currentRayDir * t;
                Gizmos.color = intersectionColor;
                Gizmos.DrawSphere(hitPointLocal, sphereRadius);

                currentRayOrigin = hitPointLocal;
                currentUV = new Vector2(
                    (hitPointLocal.x / physicalSize) + 0.5f,
                    (hitPointLocal.z / physicalSize) + 0.5f
                );

                if (currentUV.x < 0f || currentUV.x > 1f || currentUV.y < 0f || currentUV.y > 1f) break;
                currentConeColor = otherConeColor;
            }
            else
            {
                break; // Ray verfehlt den Kegel/die Pyramide
            }
        }
    }

    private void DrawReferenceRay(Matrix4x4 worldToLocal)
    {
        if (showRefRay && refRayOrigin != null && refRayTarget != null)
        {
            Gizmos.color = refRayColor;
            Vector3 locRefOri = worldToLocal.MultiplyPoint3x4(refRayOrigin.position);
            Vector3 locRefTar = worldToLocal.MultiplyPoint3x4(refRayTarget.position);
            Vector3 locRefDir = (locRefTar - locRefOri).normalized;
            Gizmos.DrawRay(locRefOri, locRefDir * physicalSize * 2f);
        }
    }

    #region Isotropisch (Kegel)

    private void DrawWireCone(Vector3 apex, float R, float height, Color color, bool detailed)
    {
        Gizmos.color = color;
        int segments = 24;
        float radius = R * height;
        Vector3 centerTop = apex + Vector3.up * height;

        Vector3 prev = centerTop + new Vector3(radius, 0, 0);
        for (int i = 1; i <= segments; i++)
        {
            float angle = i * Mathf.PI * 2f / segments;
            Vector3 next = centerTop + new Vector3(Mathf.Cos(angle) * radius, 0, Mathf.Sin(angle) * radius);
            Gizmos.DrawLine(prev, next);
            if (i % 6 == 0 || detailed) Gizmos.DrawLine(apex, next);
            prev = next;
        }
    }

    private bool IntersectCone(Vector3 origin, Vector3 dir, float R, out float t)
    {
        t = -1f;
        float a = dir.x * dir.x + dir.z * dir.z - R * R * dir.y * dir.y;
        float b = 2f * (origin.x * dir.x + origin.z * dir.z - R * R * origin.y * dir.y);
        float c = origin.x * origin.x + origin.z * origin.z - R * R * origin.y * origin.y;

        if (Mathf.Abs(a) < 1e-6f)
        {
            if (Mathf.Abs(b) < 1e-6f) return false;
            float t0 = -c / b;
            if (t0 > 0f && (origin.y + t0 * dir.y) >= 0f) { t = t0; return true; }
            return false;
        }

        float delta = b * b - 4f * a * c;
        if (delta < 0f) return false;

        float sqrtDelta = Mathf.Sqrt(delta);
        float t1 = (-b - sqrtDelta) / (2f * a);
        float t2 = (-b + sqrtDelta) / (2f * a);

        float bestT = float.MaxValue;
        if (t1 > 0f && (origin.y + t1 * dir.y) >= 0f) bestT = Mathf.Min(bestT, t1);
        if (t2 > 0f && (origin.y + t2 * dir.y) >= 0f) bestT = Mathf.Min(bestT, t2);

        if (bestT == float.MaxValue) return false;
        t = bestT;
        return true;
    }

    #endregion

    #region Anisotropisch (4-seitige Pyramide)

    private void DrawWirePyramid(Vector3 apex, Vector4 R, float height, Color color, bool detailed)
    {
        Gizmos.color = color;
        Vector3 centerTop = apex + Vector3.up * height;

        // Eckpunkte an der Oberseite (height)
        Vector3 pXpZp = centerTop + new Vector3(R.x * height, 0, R.z * height);
        Vector3 pXmZp = centerTop + new Vector3(-R.y * height, 0, R.z * height);
        Vector3 pXmZm = centerTop + new Vector3(-R.y * height, 0, -R.w * height);
        Vector3 pXpZm = centerTop + new Vector3(R.x * height, 0, -R.w * height);

        // Rand oben
        Gizmos.DrawLine(pXpZp, pXmZp);
        Gizmos.DrawLine(pXmZp, pXmZm);
        Gizmos.DrawLine(pXmZm, pXpZm);
        Gizmos.DrawLine(pXpZm, pXpZp);

        // Kanten zum Apex
        Gizmos.DrawLine(apex, pXpZp);
        Gizmos.DrawLine(apex, pXmZp);
        Gizmos.DrawLine(apex, pXmZm);
        Gizmos.DrawLine(apex, pXpZm);

        if (detailed)
        {
            // Achsen-Mittelpunkte auf den Flächen (wie ein Kreuz in der Pyramide)
            Gizmos.DrawLine(apex, centerTop + new Vector3(R.x * height, 0, 0));
            Gizmos.DrawLine(apex, centerTop + new Vector3(-R.y * height, 0, 0));
            Gizmos.DrawLine(apex, centerTop + new Vector3(0, 0, R.z * height));
            Gizmos.DrawLine(apex, centerTop + new Vector3(0, 0, -R.w * height));
        }
    }

    private bool IntersectPyramid(Vector3 origin, Vector3 dir, Vector4 R, out float bestT)
    {
        bestT = float.MaxValue;

        // Normale Vektoren der 4 Pyramiden-Flächen (zeigen nach innen, Gleichung: P * N = 0)
        // x - R.x * y = 0 -> Normal: (1, -R.x, 0)
        CheckPlaneIntersect(origin, dir, new Vector3(1f, -R.x, 0f), R, ref bestT); // +X
        CheckPlaneIntersect(origin, dir, new Vector3(-1f, -R.y, 0f), R, ref bestT); // -X
        CheckPlaneIntersect(origin, dir, new Vector3(0f, -R.z, 1f), R, ref bestT); // +Z
        CheckPlaneIntersect(origin, dir, new Vector3(0f, -R.w, -1f), R, ref bestT); // -Z

        return bestT != float.MaxValue;
    }

    private void CheckPlaneIntersect(Vector3 origin, Vector3 dir, Vector3 planeNormal, Vector4 R, ref float bestT)
    {
        float denom = Vector3.Dot(dir, planeNormal);
        if (Mathf.Abs(denom) < 1e-6f) return; // Strahl verläuft parallel zur Pyramidenfläche

        float t = -Vector3.Dot(origin, planeNormal) / denom;
        if (t <= 1e-5f) return; // Intersection liegt hinter dem Ray

        Vector3 p = origin + t * dir;

        if (p.y < -1e-4f) return; // Intersection muss über dem Apex liegen

        // Prüfen, ob der Hit-Punkt p innerhalb der Grenzen der anderen Pyramidenflächen liegt
        // (Epsilon für kleine Floating-Point Ungenauigkeiten)
        float eps = 1e-3f * p.y;
        if (p.x <= R.x * p.y + eps &&
           -p.x <= R.y * p.y + eps &&
            p.z <= R.z * p.y + eps &&
           -p.z <= R.w * p.y + eps)
        {
            if (t < bestT) bestT = t;
        }
    }

    #endregion
}