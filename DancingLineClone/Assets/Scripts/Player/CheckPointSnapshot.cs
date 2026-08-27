using System;
using DataContainer;
using Interfaces;
using UnityEngine;

namespace Player
{
    /// <summary>
    /// When a checkpoint triggered, it takes snapshot of the properties of player.
    /// It behaves as read-only class
    /// </summary>
    //TODO: Do better naming since it's only change player transform not whole game's CheckPoint parameters
    public class CheckPointSnapshot : MonoBehaviour, ICheckPointReceiver, IOnRestart, ILevelRegistryUser
    {
        
        public Vector3 CheckpointPosition { get; private set; }
        public Quaternion CheckpointRotation { get; private set; }
        
        private DirectionController _directionController;
        public IMovementEnums.Directions[] CheckPointDirections { get; private set; } = new IMovementEnums.Directions[2];
        
        private bool _checkPointTriggered;
        private ILevelRegistry _levelRegistry;
        [SerializeField] private ProgressInCurrentLoadedLevelSo progressInCurrentLoadedLevelSo;

        private void Awake()
        {
            _levelRegistry.Register(this);

            if (!TryGetComponent(out _directionController))
            {
                Debug.LogWarning("CheckPointSnapshot: DirectionController not found, can't capture Directions at CheckPoint trigger");
            }
        }

        private void OnDestroy()
        {
            _levelRegistry.Unregister(this);
        }

        /// <summary>
        /// This function called when <see cref="Gameplay.CheckpointTrigger"/> triggered
        /// </summary>
        public void CheckPointReceive(Transform transformPlayer)
        {
            //Setting only X and Z positions of trigger but keeping Y same as player's it's position
            //CheckpointPosition = new Vector3(transformPlayer.position.x, transform.position.y, transformPlayer.position.z);
            
            CheckpointPosition = transform.position;
            CheckpointRotation = transform.rotation;
            
            //Getting directions
            if (_directionController != null)
            {
                CheckPointDirections = _directionController.CurrentDirections.Clone() as IMovementEnums.Directions[];
            }
            
            _checkPointTriggered = true;
            progressInCurrentLoadedLevelSo.PublishCheckPointerTrigger();
        }
        
        
        /// <summary>
        /// Restart function is here for if player Restart the level instead of continue from checkpoint,
        /// this function remove the checkpoint object got and reset to 0
        /// </summary>
        /// <remarks>Main Restarting happen at <see cref="RestartSnapshot"/></remarks>
        public void OnLevelRestart()
        {
            if (!_checkPointTriggered) return;
            CheckpointPosition = Vector3.zero;
            CheckpointRotation = Quaternion.identity;
            CheckPointDirections = Array.Empty<IMovementEnums.Directions>();
            _checkPointTriggered = false;
        }
        
        public void LevelRegistrySoSetter(ILevelRegistry levelRegistry)
        {
            _levelRegistry = levelRegistry;
        }
    }
}