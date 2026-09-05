using UnityEngine;

[ExecuteAlways]
public class ProceduralHeightmapDrawer : MonoBehaviour
{
    [Header("Data")]
    public Texture2D heightmapTexture;
    public Texture2D colorTexture;
    public Material proceduralMaterial;

    [Header("Scaling")]
    public float minHeight = 0f;
    public float maxHeight = 10f;
    public float physicalSize = 10f;

    [Header("Mesh Settings")]
    [Range(2, 2048)]
    [Tooltip("Anzahl der Vertices pro Kante. (Z.B. 1024 -> 1 Million Quads!)")]
    public int resolution = 200;

    void Update()
    {
        if (proceduralMaterial == null || heightmapTexture == null)
            return;

        // Eigenschaften an das Material übergeben
        proceduralMaterial.SetTexture("_MainTex", heightmapTexture);
        proceduralMaterial.SetTexture("_ColorTex", colorTexture);
        proceduralMaterial.SetFloat("_MinHeight", minHeight);
        proceduralMaterial.SetFloat("_MaxHeight", maxHeight);
        proceduralMaterial.SetFloat("_PhysicalSize", physicalSize);
        proceduralMaterial.SetInt("_Resolution", resolution);

        // Die Transformation des GameObjects übergeben (Position, Rotation, Scale)
        proceduralMaterial.SetMatrix("_LocalToWorldMatrix", transform.localToWorldMatrix);

        // Anzahl der Vertices berechnen: (Breite-1) * (Höhe-1) * 6 Vertices pro Quad
        int quadsX = resolution - 1;
        int quadsY = resolution - 1;
        int vertexCount = quadsX * quadsY * 6;

        // Bounding Box für Frustum Culling berechnen
        Bounds bounds = new Bounds(transform.position, new Vector3(physicalSize, maxHeight * 2f, physicalSize));

        // Den Draw-Call direkt auf der GPU abfeuern
        Graphics.DrawProcedural(
            proceduralMaterial,
            bounds,
            MeshTopology.Triangles,
            vertexCount,
            1, // Instance Count
            null, // Camera (null = alle Kameras)
            null, // MaterialPropertyBlock
            UnityEngine.Rendering.ShadowCastingMode.Off,
            false, // Receive Shadows
            gameObject.layer
        );
    }
}