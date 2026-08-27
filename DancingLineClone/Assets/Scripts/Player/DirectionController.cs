using System.Collections.Generic;
using Interfaces;
using UnityEngine;

namespace Player
{
    /// <summary>
    /// Current move directions update in <see cref="CurrentDirections"/>
    /// </summary>
    /// <remarks>
    /// For get the latest directions, <see cref="PlayerCoreLogic"/> script must take current directions from <see cref="CurrentDirections"/>
    /// </remarks>
    public class DirectionController : MonoBehaviour, IDirectionSwitchable, IOnCheckPoint, ILevelRegistryUser
    {
        
        public IMovementEnums.Directions[] CurrentDirections { get; private set; } = new IMovementEnums.Directions[2];
        public Quaternion[] CurrentDirectionsAsQuaternions { get; private set; } = new Quaternion[2];
        private Dictionary<IMovementEnums.Directions, Quaternion> _directionDictionary = new(4);
        
        private CheckPointSnapshot _checkPointSnapshot;
        private ILevelRegistry _levelRegistry;
        
        private void PassStageChangePlayer()
        {
            //1.Transform Directions enum to Quaternions to find where to player must rotate
            CurrentDirectionsAsQuaternions[0] = _directionDictionary[CurrentDirections[0]];
            CurrentDirectionsAsQuaternions[1] = _directionDictionary[CurrentDirections[1]];
        }
        

        private void Awake()
        {
            _levelRegistry.Register(this);
            FirstInitialize();
            if (!TryGetComponent(out _checkPointSnapshot))
            {
                Debug.LogWarning("DirectionController: CheckPointSnapshot not found. Players direction will not be updated when it begin the level again from the last checkpoint");
            }
        }

        private void OnDestroy()
        {
            _levelRegistry.Unregister(this);
        }

        private void FirstInitialize()
        {
            // Define the rotation based on state
            _directionDictionary.Add(IMovementEnums.Directions.Forward, Quaternion.identity);
            _directionDictionary.Add(IMovementEnums.Directions.Backward, Quaternion.Euler(0, 180, 0));
            _directionDictionary.Add(IMovementEnums.Directions.Left, Quaternion.Euler(0, -90, 0));
            _directionDictionary.Add(IMovementEnums.Directions.Right, Quaternion.Euler(0, 90, 0));
        }
        
        /// <summary>
        /// Current directions are change here by <see cref="CurrentDirectionChangerTrigger.OnTriggerEnter"/> firing
        /// </summary>
        public void ChangeDirection(IMovementEnums.Directions[] newStates)
        {
            CurrentDirections[0] = newStates[0];
            CurrentDirections[1] = newStates[1];
            
            //Passing new direction changes to Movement.cs
            PassStageChangePlayer();
        }

        public void OnLevelCheckPoint()
        {
            if (_checkPointSnapshot != null)
            {
                ChangeDirection(_checkPointSnapshot.CheckPointDirections);
            }
        }

        public void LevelRegistrySoSetter(ILevelRegistry levelRegistry)
        {
            _levelRegistry = levelRegistry;
        }
    }
}