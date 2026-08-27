using UnityEditor;
using UnityEngine;
using System.Text.RegularExpressions;

namespace Core
{
    [CustomEditor(typeof(LevelObjectAnimationController))]
    [CanEditMultipleObjects] 
    public class LevelObjectAnimationEditor : UnityEditor.Editor
    {
        private static readonly Regex PivotPattern = new Regex(@"^Pivot", RegexOptions.Compiled);
        private static readonly Regex WhitePattern = new Regex(@"^White", RegexOptions.Compiled);

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            if (targets == null || targets.Length == 0) return;

            EditorGUILayout.Space(15);
            EditorGUILayout.BeginHorizontal();
            
            // 1. AUTOMATION BUTTON
            GUI.backgroundColor = new Color(0.3f, 0.8f, 0.4f); // Clean Green
            if (GUILayout.Button("Automate Rotate Targets", GUILayout.Height(35)))
            {
                AutomateAllSelectedObjects();
            }
            
            // 2. FORCE REFRESH BUTTON
            GUI.backgroundColor = new Color(0.2f, 0.6f, 0.9f); // Clean Blue
            if (GUILayout.Button("Force Refresh UI", GUILayout.Width(130), GUILayout.Height(35)))
            {
                ExecuteHardRefresh();
            }
            
            EditorGUILayout.EndHorizontal();
            GUI.backgroundColor = Color.white; 
        }

        private void AutomateAllSelectedObjects()
        {
            int totalElementsChanged = 0;

            foreach (var targetObject in targets)
            {
                SerializedObject serializedTarget = new SerializedObject(targetObject);
                serializedTarget.Update();

                SerializedProperty animationDataProp = serializedTarget.FindProperty("animationData");
                if (animationDataProp == null || animationDataProp.arraySize == 0) continue;

                int elementsChangedInThisObject = 0;
                int totalCount = animationDataProp.arraySize;

                for (int i = 85; i < totalCount; i++)
                {
                    SerializedProperty element = animationDataProp.GetArrayElementAtIndex(i);
                    
                    // 👑 FIX: Filter matching types via the enum value (1 matches AnimationType.Rotate)
                    SerializedProperty typeProp = element.FindPropertyRelative("type");
                    if (typeProp.enumValueIndex != (int)LevelObjectAnimationController.AnimationType.Rotate)
                    {
                        continue; 
                    }

                    SerializedProperty transformProp = element.FindPropertyRelative("gameObjectTransform");
                    SerializedProperty targetTransformProp = element.FindPropertyRelative("targetTransform");

                    if (transformProp.objectReferenceValue == null) continue; 

                    Transform boundTransform = (Transform)transformProp.objectReferenceValue;
                    string objectName = boundTransform.name;

                    if (PivotPattern.IsMatch(objectName))
                    {
                        targetTransformProp.vector3Value = new Vector3(-1.83f, 0f, 0f);
                        elementsChangedInThisObject++;
                    }
                    else if (WhitePattern.IsMatch(objectName))
                    {
                        targetTransformProp.vector3Value = new Vector3(0f, 180f, 1.79f);
                        elementsChangedInThisObject++;
                    }
                }

                if (elementsChangedInThisObject > 0)
                {
                    serializedTarget.ApplyModifiedProperties();
                    totalElementsChanged += elementsChangedInThisObject;
                }
            }

            if (totalElementsChanged > 0)
            {
                ExecuteHardRefresh();
                Debug.Log($"<color=green><b>Success!</b></color> Automated {totalElementsChanged} rotation fields across all selected objects.");
            }
            else
            {
                Debug.LogWarning("Automation complete: 0 elements modified. Ensure target names match patterns AND their Type is explicitly set to Rotate.");
            }
        }

        private void ExecuteHardRefresh()
        {
            foreach (var targetObject in targets)
            {
                new SerializedObject(targetObject).Update();
            }
            
            GUI.FocusControl(null);
            
            Repaint();
            SceneView.RepaintAll();
            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
            
            Debug.Log("<color=cyan><b>Editor Layout Forced Refresh Execution Complete.</b></color>");
        }
    }
}