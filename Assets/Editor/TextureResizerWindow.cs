using System.IO;
using UnityEditor;
using UnityEngine;

public class TextureResizerWindow : EditorWindow
{
    private Texture2D sourceTexture;
    private int targetWidth = 1024;
    private int targetHeight = 1024;
    private FilterMode filterMode = FilterMode.Bilinear;

    [MenuItem("Window/Custom/Texture Resizer")]
    public static void ShowWindow()
    {
        GetWindow<TextureResizerWindow>("Texture Resizer");
    }

    private void OnGUI()
    {
        GUILayout.Label("Texture Upscaler / Resizer", EditorStyles.boldLabel);
        EditorGUILayout.Space();

        EditorGUI.BeginChangeCheck();
        sourceTexture = (Texture2D)EditorGUILayout.ObjectField("Source Texture", sourceTexture, typeof(Texture2D), false);
        if (EditorGUI.EndChangeCheck() && sourceTexture != null)
        {
            // Auto-fill target dimensions to 2x upscale by default
            targetWidth = sourceTexture.width * 2;
            targetHeight = sourceTexture.height * 2;
        }

        if (sourceTexture != null)
        {
            EditorGUILayout.HelpBox($"Original Size: {sourceTexture.width} x {sourceTexture.height}\nFormat: {sourceTexture.format}", MessageType.Info);
        }

        targetWidth = EditorGUILayout.IntField("Target Width", targetWidth);
        targetHeight = EditorGUILayout.IntField("Target Height", targetHeight);
        filterMode = (FilterMode)EditorGUILayout.EnumPopup("Filter Mode", filterMode);

        EditorGUILayout.Space();

        GUI.enabled = sourceTexture != null && targetWidth > 0 && targetHeight > 0;
        if (GUILayout.Button("Resize and Save", GUILayout.Height(30)))
        {
            ProcessTexture();
        }
        GUI.enabled = true;
    }

    private void ProcessTexture()
    {
        string assetPath = AssetDatabase.GetAssetPath(sourceTexture);
        if (string.IsNullOrEmpty(assetPath))
        {
            Debug.LogError("[TextureResizer] Source texture is not a saved asset.");
            return;
        }

        // 1. Ensure we can read the pixels
        Texture2D readableSource = GetReadableTexture(sourceTexture);
        if (readableSource == null) return;

        // 2. Create the target texture matching the EXACT format of the source
        Texture2D resized = new Texture2D(targetWidth, targetHeight, sourceTexture.format, sourceTexture.mipmapCount > 1);

        // 3. Process pixels on the CPU to ensure the format isn't polluted by RenderTexture conversions
        for (int y = 0; y < targetHeight; y++)
        {
            for (int x = 0; x < targetWidth; x++)
            {
                float u = (float)x / (targetWidth - 1);
                float v = (float)y / (targetHeight - 1);

                Color c;
                if (filterMode == FilterMode.Bilinear)
                {
                    c = readableSource.GetPixelBilinear(u, v);
                }
                else
                {
                    int px = Mathf.Clamp(Mathf.RoundToInt(u * (readableSource.width - 1)), 0, readableSource.width - 1);
                    int py = Mathf.Clamp(Mathf.RoundToInt(v * (readableSource.height - 1)), 0, readableSource.height - 1);
                    c = readableSource.GetPixel(px, py);
                }

                // SetPixel automatically handles packing the Color back into the underlying R8/R16/RGBA format
                resized.SetPixel(x, y, c);
            }
        }
        resized.Apply();

        // 4. Save to Disk
        string directory = Path.GetDirectoryName(assetPath);
        string extension = Path.GetExtension(assetPath).ToLower();
        string filename = Path.GetFileNameWithoutExtension(assetPath);
        string newPath = Path.Combine(directory, $"{filename}_{targetWidth}x{targetHeight}{extension}");

        if (extension == ".asset")
        {
            AssetDatabase.CreateAsset(resized, newPath);
            AssetDatabase.SaveAssets();
            Debug.Log($"[TextureResizer] Saved resized asset to: {newPath}");
        }
        else if (extension == ".png")
        {
            try
            {
                byte[] bytes = resized.EncodeToPNG();
                File.WriteAllBytes(newPath, bytes);
                AssetDatabase.ImportAsset(newPath);
                Debug.Log($"[TextureResizer] Saved resized PNG to: {newPath}");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[TextureResizer] Format {sourceTexture.format} cannot be encoded to PNG. Saving as .asset instead. Error: {e.Message}");
                newPath = Path.Combine(directory, $"{filename}_{targetWidth}x{targetHeight}.asset");
                AssetDatabase.CreateAsset(resized, newPath);
                AssetDatabase.SaveAssets();
            }
        }
        else
        {
            Debug.LogWarning($"[TextureResizer] Unsupported extension '{extension}' for direct byte-save. Defaulting to .asset.");
            newPath = Path.Combine(directory, $"{filename}_{targetWidth}x{targetHeight}.asset");
            AssetDatabase.CreateAsset(resized, newPath);
            AssetDatabase.SaveAssets();
        }

        // Cleanup temporary readable texture if we generated one via Blit
        if (readableSource != sourceTexture)
        {
            DestroyImmediate(readableSource);
        }
    }

    private Texture2D GetReadableTexture(Texture2D source)
    {
        if (source.isReadable) return source;

        // Try to fix it via the importer first (Standard for .png / .tga files)
        string path = AssetDatabase.GetAssetPath(source);
        TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer != null)
        {
            importer.isReadable = true;
            importer.SaveAndReimport();
            return source;
        }

        // If it's an .asset it might not have an importer. Fallback to extracting via RenderTexture
        RenderTexture rt = RenderTexture.GetTemporary(source.width, source.height, 0, source.graphicsFormat);
        Graphics.Blit(source, rt);

        Texture2D readable = new Texture2D(source.width, source.height, source.format, false);
        RenderTexture.active = rt;
        readable.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
        readable.Apply();

        RenderTexture.active = null;
        RenderTexture.ReleaseTemporary(rt);
        return readable;
    }
}