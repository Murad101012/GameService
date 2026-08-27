using System;
using Interfaces;
using UnityEngine;

namespace Player
{
    /// <summary>
    /// It's responsible to change player's position and rotation at CheckPoint and Restart
    /// </summary>
    /// <remarks>Without <see cref="CheckPointSnapshot"/>, this script became useless</remarks>
    public class PositionRotationChangeCheckPointRestart : MonoBehaviour, IOnCheckPoint, IOnRestart, ILevelRegistryUser
    {
        private CheckPointSnapshot _checkPointSnapshot;
        private RestartSnapshot _restartSnapshot;
        
        public event Action OnPlayerCheckPointComplete;
        public event Action OnPlayerRestartComplete;
        private ILevelRegistry _levelRegistry;

        private void Awake()
        {
            _levelRegistry.Register(this);
            
            if (!TryGetComponent(out _checkPointSnapshot))
            {
                Debug.LogWarning("PositionRotationChangeCheckPointRestart: CheckPointSnapshot not found, " +
                                 "player's position/rotation can't be change the last state on " +
                                 "CheckPointTrigger if player begin the level from the last checkPoint.");
            }

            if (!TryGetComponent(out _restartSnapshot))
            {
                Debug.LogWarning("PositionRotationChangeCheckPointRestart: RestartSnapshot not found, " +
                                 "player's position/rotation can't be change when" +
                                 " it restart the level after dead");
            }
        }

        private void OnDestroy()
        {
            _levelRegistry.Unregister(this);
        }

        public void OnLevelCheckPoint()
        {
            if (_checkPointSnapshot == null) return;
            transform.position = _checkPointSnapshot.CheckpointPosition;
            transform.rotation = _checkPointSnapshot.CheckpointRotation;
            OnPlayerCheckPointComplete?.Invoke();
        }

        public void OnLevelRestart()
        {
            if (_restartSnapshot == null) return;
            transform.position = _restartSnapshot.FirstLevelBeginPosition;
            transform.rotation = _restartSnapshot.FirstLevelBeginRotation;
            OnPlayerRestartComplete?.Invoke();
        }

        public void LevelRegistrySoSetter(ILevelRegistry levelRegistry)
        {
            _levelRegistry = levelRegistry;
        }
    }
}