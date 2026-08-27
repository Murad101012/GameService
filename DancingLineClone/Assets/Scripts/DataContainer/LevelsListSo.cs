using UnityEngine;

namespace DataContainer
{
    /// <summary> Add/Remove Levels </summary>
    /// <remarks>
    /// It's make easier to modify Levels inside the list without directly modifying UXML
    /// </remarks>
    [CreateAssetMenu(menuName = "ScriptableObjects/LevelsListSo")]
    public class LevelsListSo : ScriptableObject
    {
        public LevelPropertiesSo[] levelPropertiesSo;
        [HideInInspector] public int levelPropertiesLength;

        private void OnValidate()
        {
            levelPropertiesLength = levelPropertiesSo.Length;
        }
    }
}