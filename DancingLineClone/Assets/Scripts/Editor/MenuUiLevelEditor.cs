using System;
using System.IO;
using System.Text.RegularExpressions;
using DataContainer;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Editor
{
    /// <summary>
    /// Automate adding new levels information to the UI Carousel
    /// </summary>
    /// <remarks>This script works that gets each levels information assigned to <see cref="MenuUiLevelList.levelPropertiesSo"/> list
    /// and add information of each <see cref="LevelPropertiesSo"/> to the .uxml file of MenuUi (MenuDocument.uxml) with Regex</remarks>
    public class MenuUiLevelEditor  : EditorWindow
    {
        [MenuItem("Tools/Menu Ui Level Editor")]
        public static void ShowWindow()
        {
            // Get existing open window or if none, make a new one:
            GetWindow<MenuUiLevelEditor>("Menu Ui Level Editor");
        }
        
        [SerializeField] private LevelsListSo levelList;
        [SerializeField] private UIDocument menuUiDocument;
        
        public void CreateGUI()
        {
            SerializedObject so = new SerializedObject(this);

            // 1. Create Property Fields
            var levelListField = new PropertyField(so.FindProperty(nameof(levelList)), "Levels List");
            var menuUiField = new PropertyField(so.FindProperty(nameof(menuUiDocument)), "Menu UI Document");

            // 2. Bind them
            levelListField.Bind(so);
            menuUiField.Bind(so);

            // 3. Add them to root
            rootVisualElement.Add(levelListField);
            rootVisualElement.Add(menuUiField);

            // 4. Create the HelpBox (Replacement for EditorGUILayout.HelpBox)
            // We hide it by default and show it if levelList is null
            HelpBox helpBox = new HelpBox("Controller not assigned!", HelpBoxMessageType.Error);
            helpBox.style.display = levelList == null ? DisplayStyle.Flex : DisplayStyle.None;
            rootVisualElement.Add(helpBox);

            // 5. Create the Refresh Button
            Button refreshButton = new Button(() => RefreshLevels()) { text = "Refresh Levels From List" };
            refreshButton.SetEnabled(levelList != null);
            rootVisualElement.Add(refreshButton);

            // 6. Logic to update button/helpbox state when the SerializedObject changes
            levelListField.RegisterValueChangeCallback(evt => {
                bool isNull = evt.changedProperty.objectReferenceValue == null;
                helpBox.style.display = isNull ? DisplayStyle.Flex : DisplayStyle.None;
                refreshButton.SetEnabled(!isNull);
            });
        }

        private string GetUiDocumentAddress()
        {
            if (levelList == null)
            {
                return "0";
            }

            string relativePath;

            if (menuUiDocument != null)
            {
                // 1. Get the Unity Relative Path (e.g., "Assets/Menu.uxml")
                relativePath = AssetDatabase.GetAssetPath(menuUiDocument.visualTreeAsset);

                // 2. Get the Project Root (e.g., "/home/user/MyGame/")
                // Application.dataPath is ".../Assets", so we go one level up
                string projectRoot = Path.GetDirectoryName(Application.dataPath);

                // 3. Combine them to get the "Surgical" path for the SSD
                return Path.Combine(projectRoot, relativePath);
            }

            Debug.LogWarning($"{nameof(LevelsListSo)}: I couldn't find UIDocument under my parent");
            return "0";
        }

        private void CreateBackup(string sourceFilePath)
        {
            try
            {
                if (!File.Exists(sourceFilePath)) return;

                // 1. Establish the clean tracking project folder boundaries
                string relativeBackupDir = "Assets/Scripts/Ui/Menu/MenuDocumentCheckpoints";
                string projectRoot = Path.GetDirectoryName(Application.dataPath);
                string absoluteBackupFolder = Path.Combine(projectRoot, relativeBackupDir);

                // 2. If the directory layer doesn't exist, safely generate it
                if (!Directory.Exists(absoluteBackupFolder))
                {
                    Directory.CreateDirectory(absoluteBackupFolder);
                }

                // 3. Calculate file data parameters
                string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(sourceFilePath);
                string extension = Path.GetExtension(sourceFilePath);
                
                // Format details: e.g., "MenuDocument - 20260529_195710.uxml"
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string backupFileName = $"{fileNameWithoutExtension} - {timestamp}{extension}";
                string finalBackupPath = Path.Combine(absoluteBackupFolder, backupFileName);

                // 4. Perform direct storage copy pass
                File.Copy(sourceFilePath, finalBackupPath, true);
                
                Debug.Log($"<color=#4CAF50><b>[UXML Backup Success]:</b></color> Snapshot saved safely to: {relativeBackupDir}/{backupFileName}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"MenuUiLevelEditor: Failed to create automatic file checkpoint backup. Exception: {ex.Message}");
            }
        }

        private void RefreshLevels()
        {
            if (levelList.levelPropertiesSo == null) return;

            string fullPathOfUiDocument = GetUiDocumentAddress();
            if (fullPathOfUiDocument == "0") return;

            // 👑 THE BACKUP TRIGGER: Safely run a copy loop BEFORE we let Regex touch the content
            CreateBackup(fullPathOfUiDocument);

            string originalXml = File.ReadAllText(fullPathOfUiDocument);

            string buttonsXml = "";
            for (int i = 0; i < levelList.levelPropertiesSo.Length; i++)
            {
                var level = levelList.levelPropertiesSo[i];
                string assetPath = AssetDatabase.GetAssetPath(level.levelImage);

                // If there is no image, we skip the style to avoid "Invalid Path" errors
                string styleString = "";
                if (!string.IsNullOrEmpty(assetPath))
                {
                    // Added the missing '); at the end of the URL
                    styleString = $"style=\"background-image: url('project://database/{assetPath}');\"";
                }

                buttonsXml += $"\n\n                <ui:VisualElement name=\"{level.levelName}\" class=\"btn-level\" >\n                    <ui:VisualElement name=\"Btn_Background\" class=\"btn-level-image\" {styleString} />\n                </ui:VisualElement>";
            }

            buttonsXml += "\n\n";

            File.WriteAllText(fullPathOfUiDocument, Regex.Replace(originalXml,
                @"(?<=<ui:VisualElement name=""Cont_Carousel""[^>]*>).*?(?=</ui:VisualElement >)", buttonsXml,
                RegexOptions.Singleline));

            AssetDatabase.Refresh();
        }
    }
}