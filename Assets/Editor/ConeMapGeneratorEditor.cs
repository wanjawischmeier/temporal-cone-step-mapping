using UnityEditor;
using UnityEngine;

namespace ConeStepMapping.EditorTools
{
    [CustomEditor(typeof(ConeMapGenerator))]
    public sealed class ConeMapGeneratorEditor : Editor
    {
        private ConeMapGenerator generator;
        private bool subscribed;

        private void OnEnable() => generator = (ConeMapGenerator)target;

        private void OnDisable()
        {
            Unsubscribe();
            EditorUtility.ClearProgressBar();
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUI.BeginDisabledGroup(generator.IsGenerating);
            DrawDefaultInspector();
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.Space();
            if (generator.IsGenerating)
            {
                EditorGUILayout.LabelField(generator.ProgressLabel);
                if (GUILayout.Button("Cancel Generation"))
                {
                    generator.Cancel();
                    Unsubscribe();
                    EditorUtility.ClearProgressBar();
                }
            }
            else if (GUILayout.Button("Generate Cone Map"))
            {
                try
                {
                    generator.Begin(generator.generatorComputeShader);
                    Subscribe();
                }
                catch (System.Exception exception)
                {
                    Debug.LogException(exception, generator);
                }
            }
            serializedObject.ApplyModifiedProperties();
        }

        private void Subscribe()
        {
            if (subscribed) return;
            EditorApplication.update += Tick;
            subscribed = true;
        }

        private void Unsubscribe()
        {
            if (!subscribed) return;
            EditorApplication.update -= Tick;
            subscribed = false;
        }

        private void Tick()
        {
            if (generator == null || !generator.IsGenerating)
            {
                Unsubscribe();
                EditorUtility.ClearProgressBar();
                return;
            }

            try
            {
                bool complete = generator.GenerateNextBatch();
                EditorUtility.DisplayProgressBar("Generating robust cone map", generator.ProgressLabel, generator.Progress);
                Repaint();
                if (!complete) return;

                Texture2D result = generator.FinishAndSave();
                Debug.Log($"Generated {generator.algorithm} cone map: {AssetDatabase.GetAssetPath(result)}", result);
                Unsubscribe();
                EditorUtility.ClearProgressBar();
            }
            catch (System.Exception exception)
            {
                generator.Cancel();
                Unsubscribe();
                EditorUtility.ClearProgressBar();
                Debug.LogException(exception, generator);
            }
        }
    }
}
