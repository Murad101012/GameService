using DataContainer;
using UnityEngine;

namespace Player
{
    /// <summary>
    /// It's central point for ObjectSo, tracking Dead (and other interfaces) and add additional information,
    /// about player to <see cref="DataContainer"/> (e.g <see cref="ProgressInCurrentLoadedLevelSo"/>
    /// </summary>
    [RequireComponent(typeof(StateMachine))]
    public class PlayerCoreLogic : MonoBehaviour
    {
        [field: SerializeField] public PlayerStatsSo PlayerStatsSo { get; private set; }
        [SerializeField] private LevelEventHubSo levelEventHubSo;
        [SerializeField] private ProgressInCurrentLoadedLevelSo progressInCurrentLoadedLevelSo;
        
        private void OnEnable()
        {
            GroundStateChecker.OnNonGroundChange += OnNonGroundStateChangeUpdater;
        }

        private void Awake()
        {
            if (PlayerStatsSo == null)
            {
                Debug.LogWarning(
                    $"ObjectStatsSo is not assigned, using dummy ObjectStatsSo with default values for {name}");
                PlayerStatsSo = ScriptableObject.CreateInstance<PlayerStatsSo>();
                PlayerStatsSo.speed = 10;
            }

            if (progressInCurrentLoadedLevelSo == null)
            {
                Debug.LogWarning($"{name}: variable {progressInCurrentLoadedLevelSo} is null. Can't add player location");
            }
            else
            {
                progressInCurrentLoadedLevelSo.playerTransform = transform;
            }
        }


        private void OnDisable()
        {
            GroundStateChecker.OnNonGroundChange -= OnNonGroundStateChangeUpdater;
        }

        private void OnNonGroundStateChangeUpdater(bool currentState)
        {
            if (currentState)
            {
                if (levelEventHubSo == null)
                {
                    Debug.LogWarning($"{nameof(levelEventHubSo)} isn't assigned, can't invoke dead");
                    return;
                }
                
                levelEventHubSo.PublishPlayerDead();
            }
        }
    }
}