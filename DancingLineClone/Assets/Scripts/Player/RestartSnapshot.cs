using UnityEngine;

namespace Player
{
    /// <summary>
    /// Saves the first position that player appears for using that reference as "Beginning position"
    /// </summary>
    public class RestartSnapshot: MonoBehaviour
    {
        public Vector3 FirstLevelBeginPosition { get; private set; }
        public Quaternion FirstLevelBeginRotation { get; private set; }

        private void Awake()
        {
            FirstLevelBeginPosition = transform.position;
            FirstLevelBeginRotation = transform.rotation;
        }
    }
}