using DataContainer;
using Gameplay;
using UnityEditor;
using UnityEngine;

namespace Editor
{
    /// <summary>
    /// It's automate blurring the texture (In low resolution) of Level preview image and assign to the background
    /// </summary>
    [CustomEditor(typeof(LevelPropertiesSo))]
    public class LevelPropertiesTextureAssigner : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            // Draw all the original variables (levelName, levelImage, etc.)
            DrawDefaultInspector();
            
            EditorGUILayout.Space();
            
            // Get a reference to the ScriptableObject this editor is looking at
            LevelPropertiesSo levelProperties = (LevelPropertiesSo)target;

            // Stylish button for your workflow
            GUI.backgroundColor = Color.cyan;
            if (GUILayout.Button("Bake Blur and Apply Texture", GUILayout.Height(30)))
            {
                // Call the method we just wrote in the ScriptableObject
                levelProperties.BakeAndSaveBlur();
            }
            GUI.backgroundColor = Color.white;
            
            // Helpful tip for the user (you)
            if (levelProperties.levelImage == null)
            {
                EditorGUILayout.HelpBox("Assign a Level Image before baking!", MessageType.Info);
            }
        }
    }
}