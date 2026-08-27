using Unity.Cinemachine;
using UnityEngine;

namespace Camera
{
    /// <summary>
    /// Useful when chancing priority of a camera and make switch between CineMachine Cameras
    /// Can be uses with CameraPriorityChanger.prefab
    /// </summary>
    /// <remarks>All CameraPriorityChanger.prefabs must be child of same gameObject for
    /// properly using the Restart Feature for Camera with <see cref="RestartManager.cineMachineCamerasParent"/></remarks>
    public class CameraPriorityChanger : MonoBehaviour
    {
        /// <summary>
        /// Drag the camera you want to change its priority value
        /// </summary>
        [SerializeField] private CinemachineCamera zoneCamera;
        [SerializeField] private int activePriority = 1;

        private void OnTriggerEnter(Collider other)
        {
            if (other.CompareTag("Player"))
            {
                zoneCamera.Priority = activePriority;
            }
        }
    }
}