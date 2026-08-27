using System.Collections.Generic;
using Core;
using Interfaces;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Editor
{
    /// <summary>
    /// Level Analyzer tool that find potential problems in level and giving detailed report with fix button
    /// </summary>
    public class LevelAnalyst : EditorWindow
    {
        private bool _problemFound;
        #region Classes are implement ILevelRegistryUser but not inside the list
        //We create an empty list that will contain all classes are using ILevelRegistryUser
        List<MonoBehaviour> _foundSceneILevelRegistryUserInLevel = new();
        //Add classes are uses ILevelRegistryUser but didn't add to sceneILevelRegistryUserBootstrapper.gameObjectUsesLevelRegistryUser
        List<MonoBehaviour> _iLevelRegistryUserMonoBehavioursAreNotInArray = new();
        private Vector2 _scrollPosForSceneILevelRegistryUser;
        #endregion
        

        
        // This adds a button to the top menu of Unity
        [MenuItem("Tools/Level analyst")]
        public static void ShowWindow()
        {
            // Get existing open window or if none, make a new one:
            GetWindow<LevelAnalyst>("Level analyst");
        }

        private void OnGUI()
        {
            if (GUILayout.Button("Analyse Level", GUILayout.Height(20)))
            {
                AnalysisLevel();
            }

            if (_iLevelRegistryUserMonoBehavioursAreNotInArray.Count > 0)
            {
                EditorGUILayout.BeginVertical("box");
                EditorGUILayout.HelpBox($"Following elements are uses {nameof(ILevelRegistryUser)}, but they not added to {nameof(SceneILevelRegistryUserBootstrapper)}.{nameof(SceneILevelRegistryUserBootstrapper.gameObjectUsesLevelRegistryUser)} array:", MessageType.Error);
                _scrollPosForSceneILevelRegistryUser = EditorGUILayout.BeginScrollView(_scrollPosForSceneILevelRegistryUser, GUILayout.Height(150));
                for (int i = 0; i < _iLevelRegistryUserMonoBehavioursAreNotInArray.Count; i++)
                {
                    EditorGUILayout.BeginHorizontal();
    
                    EditorGUILayout.LabelField($"{i + 1}) {_iLevelRegistryUserMonoBehavioursAreNotInArray[i].GetType().Name}");

                    if (GUILayout.Button("Show", GUILayout.Width(60)))
                    {
                        // 1. Highlight the object in the Hierarchy
                        EditorGUIUtility.PingObject(_iLevelRegistryUserMonoBehavioursAreNotInArray[i]);
        
                        // 2. Select the object so it appears in the Inspector
                        Selection.activeGameObject = _iLevelRegistryUserMonoBehavioursAreNotInArray[i].gameObject;
                    }

                    EditorGUILayout.EndHorizontal();
                }
                EditorGUILayout.EndScrollView();
                EditorGUILayout.EndVertical();
            }

            if (_problemFound)
            {
                if(GUILayout.Button("Fix-All", GUILayout.Height(40)))
                {
                    //Before begin to fix, we call AnalysisLevel() one more time to prevent Ghost Error/NoErrors
                    AnalysisLevel();
                    
                    //We check which ones are causing errors. Then we call the corresponding fix function
                    if (_iLevelRegistryUserMonoBehavioursAreNotInArray.Count > 0)
                    {
                        FixILevelRegistryUserNotContains();
                    }
                }
                else
                {
                    return;
                }
                
                //We do last check to see if all problems fixed
                AnalysisLevel();

                if (_iLevelRegistryUserMonoBehavioursAreNotInArray.Count > 0)
                {
                    return;
                }
                
                //If code execution come to at this point, meaning it didn't find any problem, so we revert _problemFound to default
                _problemFound = false;
            }
            else
            {
                GUI.backgroundColor = Color.green;
                EditorGUILayout.HelpBox($"No problems detected in {SceneManager.GetActiveScene().name}. Click \"Analyse Level\" button to validate the level", MessageType.Info);
                GUI.backgroundColor = Color.white;
            }
        }

        private void AnalysisLevel()
        {
            //Resetting values for fresh start
            ResetElements();
            
            //Then calling all known level analysis functions 
            GameObjectUsesLevelRegistryUserChecker();
        }

        private void FixILevelRegistryUserNotContains()
        {
            // 1. Create a "link" to the Bootstrapper's data
            SerializedObject so = new SerializedObject(FindFirstObjectByType(typeof(SceneILevelRegistryUserBootstrapper)) as SceneILevelRegistryUserBootstrapper);

            // 2. Find the specific array by its variable name in the code
            SerializedProperty arrayProp = so.FindProperty(nameof(SceneILevelRegistryUserBootstrapper.gameObjectUsesLevelRegistryUser));

            // 3. Expand the array to make room for new items
            int oldSize = arrayProp.arraySize;
            arrayProp.arraySize += _iLevelRegistryUserMonoBehavioursAreNotInArray.Count;

            // 4. Fill the new slots
            for (int i = 0; i < _iLevelRegistryUserMonoBehavioursAreNotInArray.Count; i++)
            {
                SerializedProperty element = arrayProp.GetArrayElementAtIndex(oldSize + i);
                element.objectReferenceValue = _iLevelRegistryUserMonoBehavioursAreNotInArray[i];
            }

            // 5. "Push" the changes back to the object and the SSD
            so.ApplyModifiedProperties();
        }

        private void ResetElements()
        {
            _foundSceneILevelRegistryUserInLevel.Clear();
            _iLevelRegistryUserMonoBehavioursAreNotInArray.Clear();
            _scrollPosForSceneILevelRegistryUser = Vector2.zero;
        }

        /// <summary>
        /// Finds classes those are implements <see cref="ILevelRegistryUser"/> but they're not added to the list (<see cref="SceneILevelRegistryUserBootstrapper.gameObjectUsesLevelRegistryUser"/>
        /// </summary>
        private void GameObjectUsesLevelRegistryUserChecker()
        {
            //We check if there is DI (Dependency Injection) added in the scene (Which it's SceneILevelRegistryUserBootstrapper as DI)
            SceneILevelRegistryUserBootstrapper sceneILevelRegistryUserBootstrapper = FindFirstObjectByType(typeof(SceneILevelRegistryUserBootstrapper)) as SceneILevelRegistryUserBootstrapper;
            if (sceneILevelRegistryUserBootstrapper == null)
            {
                Debug.LogError("Could not find SceneILevelRegistryUserBootstrapper");
                return;
            }
            
            //Gets all classes in the scene
            MonoBehaviour[] allMonoBehaviours = FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
            
            //We check which ones are using ILevelRegistryUser
            for (int i = 0; i < allMonoBehaviours.Length; i++)
            {
                //If true we add to the list
                if (allMonoBehaviours[i] is ILevelRegistryUser)
                {
                    _foundSceneILevelRegistryUserInLevel.Add(allMonoBehaviours[i]);
                }
            }

            //Check which classes it's contain/not contation those are using ILevelRegistryUser in sceneILevelRegistryUserBootstrapper.gameObjectUsesLevelRegistryUser
            for (int i = 0; i < _foundSceneILevelRegistryUserInLevel.Count; i++)
            {
                bool foundMatch = false;
                for (int j = 0; j < sceneILevelRegistryUserBootstrapper.gameObjectUsesLevelRegistryUser.Length; j++)
                {
                    //If found match, it means it already contain this class inside of it's list, so we skip this
                    if (_foundSceneILevelRegistryUserInLevel[i] ==
                        sceneILevelRegistryUserBootstrapper.gameObjectUsesLevelRegistryUser[j])
                    {
                        foundMatch = true;
                        break;
                    }
                }
                
                //If it's not found match, we add to list that it not contains this class that using ILevelRegistry, but it's not in the sceneILevelRegistryUserBootstrapper.gameObjectUsesLevelRegistryUser
                if (!foundMatch)
                {
                    _iLevelRegistryUserMonoBehavioursAreNotInArray.Add(_foundSceneILevelRegistryUserInLevel[i]);
                    _problemFound = true;
                }
            }
        }
    }
}