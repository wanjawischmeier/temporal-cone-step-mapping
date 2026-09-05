using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace ConeStepMapping.EditorTools
{
    /// <summary>
    /// Offline generator for the conservative and corrected relaxed cone maps from
    /// Bán et al., "Robust Cone Step Mapping" (2024).
    /// RGB(A) output: R = height, G = cone ratio (tan(alpha)).
    /// </summary>
    [CreateAssetMenu(menuName = "Cone Step Mapping/Cone Map Generator", fileName = "ConeMapGenerator")]
    public sealed class ConeMapGenerator : ScriptableObject
    {
        public enum Algorithm { Conservative, ExactRelaxed }
        public enum HeightChannel { Red, Green, Blue, Alpha }

        [Header("Input")]
        public Texture2D heightMap;
        [Tooltip("Assign Shaders/ConeMapGenerator.compute.")]
        public ComputeShader generatorComputeShader;
        public HeightChannel heightChannel = HeightChannel.Red;
        [Tooltip("The physical UV size of the height field. Keep (1,1) for a square tile. This is not an anisotropic cone map.")]
        public Vector2 uvScale = Vector2.one;

        [Header("Algorithm")]
        public Algorithm algorithm = Algorithm.ExactRelaxed;
        [Tooltip("Generiert eine anisotropische Cone Map (4 Ratios in RGBA). Exact Relaxed wird ignoriert.")]
        public bool anisotropic = false;
        [Tooltip("Required for conservative bilinear filtering, as described in the paper.")]
        public bool applyBilinearSafetyPass = true;
        [Min(1), Tooltip("Number of candidate texels tested per GPU dispatch. Smaller values update the progress UI more often.")]
        public int candidateBatchSize = 4096;

        [Header("Output")]
        [Tooltip("Project-relative folder. The generated Texture2D is saved here as an asset.")]
        public string outputFolder = "Assets/Textures/GeneratedConeMaps";
        public string outputName = "ConeMap";

        [NonSerialized] private RenderTexture workingMap;
        [NonSerialized] private RenderTexture safetyMap;
        [NonSerialized] private ComputeShader activeShader;
        [NonSerialized] private int generateKernel;
        [NonSerialized] private int initializeKernel;
        [NonSerialized] private int safetyKernel;
        [NonSerialized] private int candidateOffset;
        [NonSerialized] private int candidateTotal;
        [NonSerialized] private bool generating;

        public bool IsGenerating => generating;
        public float Progress => candidateTotal == 0 ? 0f : candidateOffset / (float)candidateTotal;
        public string ProgressLabel => generating ? $"Testing candidates {candidateOffset:N0} / {candidateTotal:N0}" : string.Empty;

        internal void Begin(ComputeShader generatorShader)
        {
            if (generating)
                throw new InvalidOperationException("A generation is already running.");
            if (heightMap == null)
                throw new InvalidOperationException("Assign a height map first.");
            if (!SystemInfo.supportsComputeShaders)
                throw new InvalidOperationException("This graphics device does not support compute shaders.");
            if (generatorShader == null)
                throw new InvalidOperationException("Assign Shaders/ConeMapGenerator.compute.");
            if (heightMap.width < 2 || heightMap.height < 2)
                throw new InvalidOperationException("The height map must be at least 2 x 2 texels.");
            if (uvScale.x <= 0f || uvScale.y <= 0f)
                throw new InvalidOperationException("UV Scale must be positive.");

            activeShader = generatorShader;
            initializeKernel = activeShader.FindKernel("Initialize");
            generateKernel = activeShader.FindKernel("GenerateBatch");
            safetyKernel = activeShader.FindKernel("BilinearSafetyPass");
            candidateOffset = 0;
            candidateTotal = heightMap.width * heightMap.height;

            ReleaseTemporaryTextures();
            workingMap = CreateMapRenderTexture("Cone Map (working)");
            activeShader.SetInts("_Size", heightMap.width, heightMap.height);
            activeShader.SetInt("_Anisotropic", anisotropic ? 1 : 0);
            activeShader.SetTexture(initializeKernel, "_HeightMap", heightMap);
            activeShader.SetTexture(initializeKernel, "_Result", workingMap);
            Dispatch(initializeKernel);
            generating = true;
        }

        /// <returns>true when all candidate texels have been evaluated.</returns>
        internal bool GenerateNextBatch()
        {
            if (!generating)
                return true;

            int count = Mathf.Min(Mathf.Max(1, candidateBatchSize), candidateTotal - candidateOffset);
            activeShader.SetInts("_Size", heightMap.width, heightMap.height);
            activeShader.SetInt("_Anisotropic", anisotropic ? 1 : 0);
            activeShader.SetVector("_UvScale", uvScale);
            activeShader.SetInt("_HeightChannel", (int)heightChannel);
            activeShader.SetInt("_Algorithm", (int)algorithm);
            activeShader.SetInt("_CandidateStart", candidateOffset);
            activeShader.SetInt("_CandidateCount", count);
            activeShader.SetTexture(generateKernel, "_HeightMap", heightMap);
            activeShader.SetTexture(generateKernel, "_Result", workingMap);
            Dispatch(generateKernel);
            candidateOffset += count;

            if (candidateOffset < candidateTotal)
                return false;

            if (applyBilinearSafetyPass)
            {
                safetyMap = CreateMapRenderTexture("Cone Map (bilinear-safe)");
                activeShader.SetInts("_Size", heightMap.width, heightMap.height);
                activeShader.SetInt("_Anisotropic", anisotropic ? 1 : 0);
                activeShader.SetTexture(safetyKernel, "_Source", workingMap);
                activeShader.SetTexture(safetyKernel, "_Result", safetyMap);
                Dispatch(safetyKernel);
            }
            return true;
        }

        internal Texture2D FinishAndSave()
        {
            if (!generating || candidateOffset != candidateTotal)
                throw new InvalidOperationException("Generation is not complete.");

            RenderTexture source = safetyMap != null ? safetyMap : workingMap;
            var texture = new Texture2D(source.width, source.height, TextureFormat.RGBAFloat, false, true)
            {
                name = outputName
            };
            RenderTexture previous = RenderTexture.active;
            try
            {
                RenderTexture.active = source;
                texture.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0, false);
                texture.Apply(false, false);
            }
            finally
            {
                RenderTexture.active = previous;
            }

            string folder = EnsureAssetFolder(outputFolder);
            string path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{SanitizeFileName(outputName)}.asset");
            AssetDatabase.CreateAsset(texture, path);
            AssetDatabase.SaveAssets();
            generating = false;
            ReleaseTemporaryTextures();
            Selection.activeObject = texture;
            return texture;
        }

        internal void Cancel()
        {
            generating = false;
            ReleaseTemporaryTextures();
        }

        private RenderTexture CreateMapRenderTexture(string textureName)
        {
            var descriptor = new RenderTextureDescriptor(heightMap.width, heightMap.height, GraphicsFormat.R32G32B32A32_SFloat, 0)
            {
                enableRandomWrite = true,
                msaaSamples = 1,
                sRGB = false,
                useMipMap = false,
                autoGenerateMips = false
            };
            var texture = new RenderTexture(descriptor) { name = textureName, hideFlags = HideFlags.HideAndDontSave };
            texture.Create();
            return texture;
        }

        private void Dispatch(int kernel)
        {
            activeShader.Dispatch(kernel, Mathf.CeilToInt(heightMap.width / 8f), Mathf.CeilToInt(heightMap.height / 8f), 1);
        }

        private void ReleaseTemporaryTextures()
        {
            Release(ref workingMap);
            Release(ref safetyMap);
        }

        private static void Release(ref RenderTexture texture)
        {
            if (texture == null) return;
            texture.Release();
            DestroyImmediate(texture);
            texture = null;
        }

        private static string EnsureAssetFolder(string requestedFolder)
        {
            string folder = string.IsNullOrWhiteSpace(requestedFolder) ? "Assets" : requestedFolder.Replace('\\', '/').TrimEnd('/');
            if (!folder.StartsWith("Assets", StringComparison.Ordinal) || (folder.Length > 6 && folder[6] != '/'))
                throw new InvalidOperationException("Output Folder must be Assets or a folder below Assets.");

            string[] parts = folder.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
            return folder;
        }

        private static string SanitizeFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "ConeMap";
            foreach (char c in System.IO.Path.GetInvalidFileNameChars()) value = value.Replace(c, '_');
            return value;
        }
    }
}
