using System;
using DataContainer;
using Interfaces;
using Unity.Cinemachine;
using UnityEngine;

namespace Camera
{
    /// <summary>
    /// It will reset all cameras priority to zero except the CineMachine Camera at the beginning of the level
    /// </summary>
    [RequireComponent(typeof(CinemachineBrain))]
    public class RestartManager : MonoBehaviour, IOnRestart, ILevelState, ILevelRegistryUser
    {
        private CinemachineBrain _cineMachineBrain;
        private CinemachineCamera[] _cameras;
        [SerializeField] private Transform cineMachineCamerasParent;
        [SerializeField] private CinemachineCamera cameraAtBeginning;
        private ILevelRegistry _levelRegistry;
        [SerializeField] private SceneLoadStateEventSo sceneLoadStateEventSo;
        private int[] _cinemachineCamerasBeginningIndexs;

        private void Awake()
        {
            _levelRegistry.Register(this);
            if(sceneLoadStateEventSo != null) sceneLoadStateEventSo.OnSceneFullyLoaded += Initialization;
            else
            {
                Debug.LogWarning($"{name}: sceneLoadStateEventSo variable is null, manually calling Initialization");
                Initialization();
            }

            _cineMachineBrain = GetComponent<CinemachineBrain>();

            if (cineMachineCamerasParent != null) return;
            Debug.LogWarning("Camera/RestartManager: cineMachineCamerasParent is null, disabling the Restart feature for Camera");
            enabled = false;
        }

#if UNITY_EDITOR
        private void Start()
        {
            //If camera length is 0 or didn't initialized and if this is in Unity Editor, in some chance developer playing the level,
            //bypassing the initilization and directly playing the level, so we call the initialization manually (if this is the really what the issue)
            if (_cameras?.Length == 0 || _cameras == null)
            {
                Initialization();
            }
        }
#endif

        private void OnDestroy()
        {
            _levelRegistry.Unregister(this);
            sceneLoadStateEventSo.OnSceneFullyLoaded -= Initialization;
        }

        public void OnLevelRestart()
        {
            if (_cameras?.Length == 0 || _cameras == null) return;
            _cineMachineBrain.enabled = false;

            //Resetting all CineMachine Camera component's priority value to default state
            for (int i = 0; i < _cameras.Length; i++)
            {
                _cameras[i].Priority = _cinemachineCamerasBeginningIndexs[i];
            }
            
            _cineMachineBrain.enabled = true;
        }
        
        public void Initialization()
        {
            //Getting all CineMachine cameras under parent and loading to _cameras variable
            _cameras = cineMachineCamerasParent.GetComponentsInChildren<CinemachineCamera>(true);
            if (_cameras.Length == 0)
            {
                Debug.LogWarning("Camera/RestartManager: cineMachineCamerasParent doesn't have children" +
                                 " with CineMachineCamera component,  disabling the Restart feature for Camera");
                enabled = false;
                return;
            }
            
            //Taking all cinemachine cameras priorty at the beginning those default
            _cinemachineCamerasBeginningIndexs = new int[_cameras.Length];
            for (int i = 0; i < _cameras.Length; i++)
            {
                _cinemachineCamerasBeginningIndexs[i] = _cameras[i].Priority;
            }
        }

        public void OnLevelStart()
        {
            /*Since ActiveVirtualCamera is not initialized yet In Awake() and Start(), executing this code cause always  return null exception
              error, because CinemachineCamera isn't ready yet. So, instead it gets the camera that active as soon as player begin to
              play the level*/          
            if (cameraAtBeginning == null)
            {
                cameraAtBeginning = (CinemachineCamera)_cineMachineBrain.ActiveVirtualCamera;
                Debug.LogWarning($"{name}: '{nameof(cameraAtBeginning)}' was not assigned in the Inspector. " +
                                 $"Auto-assigned to '{cameraAtBeginning.name}'. " +
                                 "(Tip: Assign this manually to avoid performance overhead of auto-detection.)");
            }
        }

        public void OnLevelStop() {/*It will be empty*/}
        
        public void LevelRegistrySoSetter(ILevelRegistry levelRegistry)
        {
            _levelRegistry = levelRegistry;
        }
    }
}