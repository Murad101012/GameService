using UnityEditor;
using UnityEngine;
using System.IO;

namespace Editor
{
    public abstract class MeshExporter
    {
        [MenuItem("Assets/Export All Child Meshes Separately", false, 10)]
        public static void ExportAllMeshes()
        {
            // Get the selected parent object in the Project/Hierarchy view
            GameObject selectedAsset = Selection.activeObject as GameObject;
            if (selectedAsset == null)
            {
                Debug.LogError("Please select an imported 3D model asset first.");
                return;
            }

            // Find the system path of the selected asset file
            string assetPath = AssetDatabase.GetAssetPath(Selection.activeObject);
            if (string.IsNullOrEmpty(assetPath))
            {
                Debug.LogError("Could not resolve the asset file path.");
                return;
            }

            // Find ALL MeshFilters inside the hierarchy
            MeshFilter[] meshFilters = selectedAsset.GetComponentsInChildren<MeshFilter>();
            if (meshFilters == null || meshFilters.Length == 0)
            {
                Debug.LogError($"No MeshFilters found inside {selectedAsset.name} or its children.");
                return;
            }

            // Get the folder path containing the FBX file
            string targetDirectory = Path.GetDirectoryName(assetPath);
            int exportCount = 0;

            // Loop through every single mesh found using a standard loop
            for (int i = 0; i < meshFilters.Length; i++)
            {
                MeshFilter filter = meshFilters[i];
                
                // Skip if the filter or its mesh data is empty/null
                if (filter == null || filter.sharedMesh == null)
                {
                    continue;
                }

                // Use the name of the specific child gameobject (e.g., "1", "2", "3")
                string childName = filter.gameObject.name;
                string newMeshPath = Path.Combine(targetDirectory, $"{childName}_PureMesh.mesh");

                // Isolate the vertex and index buffers from the original file container
                Mesh meshToSave = Object.Instantiate(filter.sharedMesh);

                // Save it directly as a native Unity binary file
                AssetDatabase.CreateAsset(meshToSave, newMeshPath);
                exportCount++;
            }

            // Batch save changes to the asset database for speed
            if (exportCount > 0)
            {
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                Debug.Log($"Successfully extracted {exportCount} pure individual mesh assets to: {targetDirectory}");
            }
        }
    }
}