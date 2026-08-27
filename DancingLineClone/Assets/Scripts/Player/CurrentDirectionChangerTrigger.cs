using Interfaces;
using UnityEngine;

namespace Player
{
    /// <summary>
    /// Chancing state based on what set at moveStates array <see cref="moveDirections"/>
    /// </summary>
    /// <remarks>
    /// Place the "StateChangerPrefab" in front of the player. 
    /// The transition occurs via <see cref="OnTriggerEnter"/>.
    /// </remarks>
    public class CurrentDirectionChangerTrigger : MonoBehaviour
    {
        [SerializeField] private IMovementEnums.Directions[] moveDirections = new IMovementEnums.Directions[2];
        
        private void OnTriggerEnter(Collider other)
        {
            IDirectionSwitchable directionSwitchable = other.GetComponent<IDirectionSwitchable>();
            if (directionSwitchable != null)
            {
                directionSwitchable.ChangeDirection(moveDirections);
            }
        }
    }
}