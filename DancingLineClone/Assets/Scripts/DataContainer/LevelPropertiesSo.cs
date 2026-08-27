using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.UIElements;

namespace DataContainer
{
    /// <summary>
    /// This ScriptableObject includes properties for level in both.. in level selection
    /// and while in the game
    /// </summary>
    /// <remarks>It's capabilities increased with LevelPropertiesEditor.cs and recommend to use
    /// "Fetch Data from current active scene" button under LevelProperties's inspector</remarks>
    [CreateAssetMenu(menuName = "ScriptableObjects/LevelProperties")]
    public class LevelPropertiesSo : ScriptableObject
    {
        //TODO: Change to Addressable type loading
        public string levelName;
        [SerializeField] public Sprite levelImage;
        public Texture2D bakedBlurredTexture;
        private int _idLevelImage;
        public AudioClip levelSound;
        public StyleBackground styleBackgroundLevelImage;
        public StyleBackground styleBackgroundBlurredLevelImage;
        public AssetReference sceneLevel;

        //As soon as levelImage add from inspector, thi
        private void OnValidate()
        {
            if (levelImage != null && levelImage.GetHashCode() != _idLevelImage)
            {
                _idLevelImage = levelImage.GetHashCode();
            }

            if (styleBackgroundLevelImage != null || styleBackgroundBlurredLevelImage != null)
            {
                styleBackgroundLevelImage = new StyleBackground(levelImage);
                styleBackgroundBlurredLevelImage = new StyleBackground(bakedBlurredTexture);
            }
        }
        
        /// <summary>
        /// This script written by GEMINI AI
        /// </summary>
#if UNITY_EDITOR
        [ContextMenu("Bake Blur and Assign")]
        public void BakeAndSaveBlur()
        {
            if (levelImage == null) return;

            // 1. Generate the texture
            Texture2D blurred = BakeBlur(levelImage, 2, 2);

            // 2. Determine paths
            string soPath = UnityEditor.AssetDatabase.GetAssetPath(this);
            string directory = System.IO.Path.GetDirectoryName(soPath);
            string fileName = this.name + "_Blurred.png";
            string targetPath = System.IO.Path.Combine(directory, fileName);
            // Convert system path to Unity project path for AssetDatabase
            string unityPath = targetPath.Replace(System.IO.Path.DirectorySeparatorChar, '/');

            // 3. Save to Disk
            byte[] bytes = blurred.EncodeToPNG();
            System.IO.File.WriteAllBytes(targetPath, bytes);
            
            // 4. Refresh and Import
            UnityEditor.AssetDatabase.ImportAsset(unityPath);

            // 5. Setup Import Settings (Crucial for UI quality)
            UnityEditor.TextureImporter importer = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEditor.TextureImporter>(unityPath);
            if (importer != null)
            {
                importer.textureType = UnityEditor.TextureImporterType.Default;
                importer.sRGBTexture = true; // Ensure colors look correct
                importer.alphaSource = UnityEditor.TextureImporterAlphaSource.None;
                importer.SaveAndReimport();
            }

            // 6. AUTOMATIC ASSIGNMENT
            // Load the newly created asset and assign it to the slot
            bakedBlurredTexture = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(unityPath);
            
            // 7. Mark the ScriptableObject as "Dirty" so Unity knows to save the new reference
            UnityEditor.EditorUtility.SetDirty(this);
            UnityEditor.AssetDatabase.SaveAssets();

            Debug.Log($"Billed Baked & Assigned: {unityPath}");
            DestroyImmediate(blurred);
        }
#endif
        
        /// <summary>
        /// This script written by GEMINI AI
        /// </summary>
        private Texture2D BakeBlur(Sprite sprite, int blurSize, int scale)
{
    // Ensure scale is at least 1 to avoid division by zero
    scale = Mathf.Max(1, scale);
    
    Texture2D source = sprite.texture;
    Rect r = sprite.rect;

    // 1. Calculate aspect-ratio safe dimensions
    int targetW = Mathf.RoundToInt(r.width / scale);
    int targetH = Mathf.RoundToInt(r.height / scale);

    // 2. Extract the slice from the sheet (RGBA32 to keep original colors for now)
    Texture2D cropped = new Texture2D((int)r.width, (int)r.height, TextureFormat.RGBA32, false);
    Color[] slicePixels = source.GetPixels((int)r.x, (int)r.y, (int)r.width, (int)r.height);
    cropped.SetPixels(slicePixels);
    cropped.Apply();

    // 3. Downscale using RenderTexture to target dimensions
    // We use RGB565 here for the final output to save 50% RAM
    Texture2D blurred = new Texture2D(targetW, targetH, TextureFormat.RGB565, false);
    RenderTexture rt = RenderTexture.GetTemporary(targetW, targetH);
    
    Graphics.Blit(cropped, rt);
    RenderTexture.active = rt;
    blurred.ReadPixels(new Rect(0, 0, targetW, targetH), 0, 0);
    blurred.Apply();
    
    RenderTexture.active = null;
    RenderTexture.ReleaseTemporary(rt);

    // 4. Perform the Blur Loop
    Color[] pixels = blurred.GetPixels();
    Color[] resultPixels = new Color[pixels.Length];

    for (int y = 0; y < targetH; y++)
    {
        for (int x = 0; x < targetW; x++)
        {
            float red = 0, g = 0, b = 0;
            int count = 0;
            for (int ky = -blurSize; ky <= blurSize; ky++)
            {
                for (int kx = -blurSize; kx <= blurSize; kx++)
                {
                    int nx = Mathf.Clamp(x + kx, 0, targetW - 1);
                    int ny = Mathf.Clamp(y + ky, 0, targetH - 1);
                    Color c = pixels[ny * targetW + nx];
                    red += c.r; g += c.g; b += c.b;
                    count++;
                }
            }
            resultPixels[y * targetW + x] = new Color(red / count, g / count, b / count, 1.0f);
        }
    }

    blurred.SetPixels(resultPixels);
    blurred.Apply();

    // 5. Cleanup
    if (Application.isEditor) DestroyImmediate(cropped);
    else Destroy(cropped);

    return blurred;
}

    }
}