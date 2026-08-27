using Core;
using DataContainer;
using Interfaces;
using Player;
using Player.States;
using UnityEditor;
using UnityEngine;

namespace Editor
{
    /// <summary>
    /// Automate path creation
    /// </summary>
    /// <example> 1. Starts the game, 2. Create new PathCreatorSo, 3. Click on Record, 4. When finished stop the record, 5. Click on "Bake PathCreatorSO" </example>
    public class PathCreator : EditorWindow, ILevelState, ILevelRegistryUser
    {
        private Vector2 _scrollPos;
        private PathCreatorSO _pathCreatorSo;
        private string _recordString;
        private bool _onRecord;
        private Color _originalColor;
        private Transform _playerTransform;
        private float _pathWidth = 1.0f;
        private ILevelRegistry _levelRegistry;
        
        private void OnEnable()
        {
            _originalColor = GUI.backgroundColor;
            EditorApplication.playModeStateChanged += WhenGameBegin;
        
            // This tells Unity to call the Scene drawing function
            SceneView.duringSceneGui += OnSceneGUI;
        }
        private void OnDisable()
        {
            EditorApplication.playModeStateChanged -= WhenGameBegin;
            SceneView.duringSceneGui -= OnSceneGUI;
        }

        private void WhenGameBegin(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode && _onRecord)
            {
                _playerTransform = GameObject.FindGameObjectWithTag("Player").transform;
                //_levelRegistry = GameObject.FindFirstObjectByType<LevelRegistrySo>();
                //_levelRegistry.Register(this);
            }
        }

        // This adds a button to the top menu of Unity
        [MenuItem("Tools/Path Creator")]
        public static void ShowWindow()
        {
            // Get existing open window or if none, make a new one:
            GetWindow<PathCreator>("Path Creator");
        }

        private void OnGUI()
        {
            EditorGUILayout.BeginVertical("box");
        
        
            _pathCreatorSo = (PathCreatorSO)EditorGUILayout.ObjectField("Path SO", _pathCreatorSo, typeof(PathCreatorSO), false);
            if (GUILayout.Button("Create new PathCreator"))
            {
                _pathCreatorSo = CreateInstance<PathCreatorSO>();
                AssetDatabase.CreateAsset(_pathCreatorSo, "Assets/PathCreator/PathCreatorSO/PathCreator.asset");
            }

            if (!_onRecord)
            {
                GUI.backgroundColor = _originalColor;
                _recordString = "Record player movements";
            }
            else
            {
                GUI.backgroundColor = Color.red;
                _recordString = "Recording...";
            }
            if (GUILayout.Button(_recordString, GUILayout.Height(30)) && PathCreatorSoChecker())
            {
                if (!_onRecord)
                {
                    _onRecord = true;
                    PlayerMoveState.PlayerPressed += PlayerMovementClick;
                }
                else
                {
                    _onRecord = false;
                    PlayerMoveState.PlayerPressed -= PlayerMovementClick;
                }
            }

            GUI.backgroundColor = _originalColor;
        
            if (GUILayout.Button("Reset PathCreatorSO"))
            {
                if (PathCreatorSoChecker())
                {
                    _pathCreatorSo.points.Clear();
                    _pathCreatorSo.rotations.Clear();
                }
            }
        
            //Didn't used PathCreatorSoChecker, otherwise it would spawn Debug.LogWarning in the console
            if (_pathCreatorSo != null)
            {
                EditorGUILayout.LabelField(_pathCreatorSo.name, EditorStyles.boldLabel,  GUILayout.Height(30));
                // 2. Create a "Wrapper" for our ScriptableObject
                SerializedObject so = new SerializedObject(_pathCreatorSo);
        
                // 3. Find the specific lists inside the SO
                SerializedProperty pointsProp = so.FindProperty("points");
                SerializedProperty rotationsProp = so.FindProperty("rotations");

                // 4. Create a ScrollView so the window doesn't get cut off
                _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

                // 5. Draw the lists (True means "include children" like X, Y, Z values)
                EditorGUILayout.PropertyField(pointsProp, true);
                EditorGUILayout.PropertyField(rotationsProp, true);

                EditorGUILayout.EndScrollView();

                // 6. Save any changes made by the artist back to the SSD
                so.ApplyModifiedProperties();
            }
            else
            {
                EditorGUILayout.LabelField("Inside of PathCreatorSO will be shown in here after you assign it",  EditorStyles.boldLabel,  GUILayout.Height(30));
            }
        
            _pathWidth = EditorGUILayout.FloatField("Path Width", _pathWidth);
            if (_pathCreatorSo != null)
            {
                if (_pathCreatorSo.points.Count > 1)
                {
                    EditorGUI.BeginDisabledGroup(false);
                }
                else
                {
                    EditorGUI.BeginDisabledGroup(true);
                }
            }
            else
            {
                EditorGUI.BeginDisabledGroup(true);
            }
            if (GUILayout.Button("Bake PathCreatorSO"))
            {
                GameObject oldPath = GameObject.Find("BakedPath_" + _pathCreatorSo.name);
                if (oldPath != null) 
                {
                    DestroyImmediate(oldPath); // Use DestroyImmediate in Editor scripts
                }
                if (PathCreatorSoChecker())
                {
                    BakeMesh();
                }
            }
            EditorGUI.BeginDisabledGroup(false);

            EditorGUILayout.EndVertical();
        }

        private bool PathCreatorSoChecker()
        {
            if (_pathCreatorSo != null)
            {
                return true;
            }
            Debug.LogWarning("You haven't assigned PathCreator");
            return false;
        }

        private void PlayerMovementClick()
        {
            if (PathCreatorSoChecker())
            {
                _pathCreatorSo.points.Add(_playerTransform.position);
                _pathCreatorSo.rotations.Add(_playerTransform.rotation);
            
                //For make sure it's save on disk
                EditorUtility.SetDirty(_pathCreatorSo);
                //AssetDatabase.SaveAssets();
            }
        }
    
        private void OnSceneGUI(SceneView sceneView)
        {
            if (_pathCreatorSo == null || _pathCreatorSo.points.Count < 2)
                return;

            // Set the color for our "Ghost Path"
            Handles.color = Color.cyan;

            // Use a for loop to draw lines between all recorded points
            for (int i = 0; i < _pathCreatorSo.points.Count - 1; i++)
            {
                Vector3 start = _pathCreatorSo.points[i];
                Vector3 end = _pathCreatorSo.points[i + 1];

                // Draw a solid line between points
                Handles.DrawLine(start, end, 2.0f);

                // Optional: Draw a small box at each turn point
                Handles.DrawWireCube(start, Vector3.one * 0.2f);
            }
        }
    
        /// <remarks>This function planned, drawn by me (Author) and explained to AI to generate CODE,
        /// so Code not belong to Author but AI where actual math also done by AI not the author.
        /// Author only made planning and explanation to AI,
        /// How logic must be with vertices, lines, faces(triangles)</remarks>>
        private void BakeMesh()
        {
            GameObject oldPath = GameObject.Find("BakedPath_" + _pathCreatorSo.name);
            if (oldPath != null) DestroyImmediate(oldPath);

            GameObject pathObject = new GameObject("BakedPath_" + _pathCreatorSo.name);
            MeshFilter mf = pathObject.AddComponent<MeshFilter>();
            MeshRenderer mr = pathObject.AddComponent<MeshRenderer>();
            mr.material = new Material(Shader.Find("Sprites/Default"));

            Mesh mesh = new Mesh();
            int pointCount = _pathCreatorSo.points.Count;

            Vector3[] vertices = new Vector3[pointCount * 2];
            int[] triangles = new int[(pointCount - 1) * 6];
            float halfWidth = _pathWidth / 2f;

            for (int i = 0; i < pointCount; i++)
            {
                Vector3 pos = _pathCreatorSo.points[i];
                Vector3 forward;

                if (i == 0) 
                {
                    forward = (_pathCreatorSo.points[i + 1] - pos).normalized;
                }
                else if (i == pointCount - 1) 
                {
                    forward = (pos - _pathCreatorSo.points[i - 1]).normalized;
                }
                else 
                {
                    Vector3 dirIn = (pos - _pathCreatorSo.points[i - 1]).normalized;
                    Vector3 dirOut = (_pathCreatorSo.points[i + 1] - pos).normalized;
                    // IMPORTANT: Normalize the average so it doesn't "shrink" the side calculation
                    forward = (dirIn + dirOut).normalized;
                }

                // Calculate 'Side' perpendicular to 'Forward'
                Vector3 side = new Vector3(-forward.z, 0, forward.x); 

                // Fix the Miter Scale: 
                // This math ensures the width is consistent even at 90-degree turns.
                // For Dancing Line's 90-degree turns, the scale factor is 1 / cos(45 degrees)
                float miterScale = 1f;
                if (i > 0 && i < pointCount - 1)
                {
                    Vector3 dirIn = (pos - _pathCreatorSo.points[i - 1]).normalized;
                    // The dot product helps us find how much to shrink the side to keep width uniform
                    miterScale = 1f / Mathf.Max(0.1f, Vector3.Dot(side, new Vector3(-(pos - _pathCreatorSo.points[i-1]).normalized.z, 0, (pos - _pathCreatorSo.points[i-1]).normalized.x)));
                }

                // Apply the corrected halfWidth
                vertices[i * 2] = pos - (side * halfWidth * miterScale);
                vertices[i * 2 + 1] = pos + (side * halfWidth * miterScale);
            }

            // Updated triangle loop to flip the face UP
            int t = 0;
            for (int i = 0; i < pointCount - 1; i++)
            {
                int v = i * 2;

                // Triangle 1 (Clockwise: 0 -> 1 -> 2)
                triangles[t++] = v;
                triangles[t++] = v + 1; // Swapped
                triangles[t++] = v + 2; // Swapped

                // Triangle 2 (Clockwise: 1 -> 3 -> 2)
                triangles[t++] = v + 1;
                triangles[t++] = v + 3;
                triangles[t++] = v + 2;
            }
    
            // 1. Create the UV array (same size as vertices)
            Vector2[] uvs = new Vector2[vertices.Length];
            float distanceTravelled = 0f;

            for (int i = 0; i < pointCount; i++)
            {
                // Calculate how far we are from the start to repeat the texture correctly
                if (i > 0)
                {
                    distanceTravelled += Vector3.Distance(_pathCreatorSo.points[i], _pathCreatorSo.points[i - 1]);
                }

                // Left vertex gets U=0, Right gets U=1. V is the distance.
                uvs[i * 2] = new Vector2(0, distanceTravelled);
                uvs[i * 2 + 1] = new Vector2(1, distanceTravelled);
            }

            // 2. Assign to the mesh
            mesh.uv = uvs;

            mesh.vertices = vertices;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            mf.mesh = mesh;
    
            // --- PHYSICS SETUP ---
            // Add the collider if it doesn't exist, or grab the existing one
            MeshCollider mc = pathObject.GetComponent<MeshCollider>();
            if (mc == null) mc = pathObject.AddComponent<MeshCollider>();

            // This is the secret for procedural meshes:
            mc.sharedMesh = null; // Clear old reference to force a "re-cook"
            mc.sharedMesh = mesh;
    
            // Ensure the physics engine treats it as a solid floor
            mc.cookingOptions = MeshColliderCookingOptions.EnableMeshCleaning | 
                                MeshColliderCookingOptions.WeldColocatedVertices;
    
            // Set the layer so your Raycast sees it
            pathObject.layer = LayerMask.NameToLayer("Ground");
    
            // --- PERSISTENCE SETUP ---
            // Check if a folder exists, if not, create it
            if (!AssetDatabase.IsValidFolder("Assets/BakedPaths"))
            {
                AssetDatabase.CreateFolder("Assets","BakedPaths");
            }

            // Create a unique path for the mesh file
            string meshPath = $"Assets/BakedPaths/{_pathCreatorSo.name}_Mesh.asset";
    
            // Save the mesh data as a real file on your SSD
            AssetDatabase.CreateAsset(mesh, meshPath);
            AssetDatabase.SaveAssets();

            Debug.Log($"Mesh saved permanently to: {meshPath}");

            Debug.Log("Path Baked with Collision!");
        }

        public void OnLevelStart()
        {
            if (_onRecord)
            {
                _pathCreatorSo.points.Add(_playerTransform.position);
                _pathCreatorSo.rotations.Add(_playerTransform.rotation);
            }
        }

        public void OnLevelStop()
        {
            //_levelRegistry.Unregister(this);
        }
        
        public void LevelRegistrySoSetter(ILevelRegistry levelRegistry)
        {
            _levelRegistry = levelRegistry;
        }
    }
}