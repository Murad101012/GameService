using UnityEngine;

namespace DataContainer
{
    /// <summary>
    /// Stats of player than can be unique for each different levels
    /// </summary>
    [CreateAssetMenu(menuName = "ScriptableObjects/ObjectStats")]
    public class PlayerStatsSo : ScriptableObject
    {
        [Header("Movement")]
        public int speed;
        
        [Header("Level Initialization - First Beginning")]
        public Vector3 firstLevelBeginPosition;
        public Quaternion firstLevelBeginRotation;
    }
}