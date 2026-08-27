using DataContainer;
using Interfaces;
using UnityEngine;
using UnityEngine.UI;

namespace Ui.Controllers
{
    /// <summary>
    /// Controls the lifetime of gameobjects of Victory Ui prefab
    /// </summary>
    public class VictoryUiController : MonoBehaviour, IVictory, IOnRestart, ILevelRegistryUser
    {
        [Header("UI References")]
        [SerializeField] private GameObject victoryScreen;
        [SerializeField] private Button restartButton;
        private ILevelRegistry _levelRegistry;
        
        [SerializeField] private LevelUiFlowSo levelUiFlow;
        
        private void OnEnable()
        {
            _levelRegistry.Register(this);
            
            if(levelUiFlow != null) levelUiFlow.Victory_OnRestartEndAnimationEnd += Reset;
        }

        private void OnDisable()
        {
            _levelRegistry.Unregister(this);
            if(levelUiFlow != null) levelUiFlow.Victory_OnRestartEndAnimationEnd -= Reset;
        }

        public void OnVictory()
        {
            victoryScreen.SetActive(true);
        }

        public void OnLevelRestart()
        {
            NullCheckDefeatUiAnimationRewindEvent();
        }
        public void Reset()
        {
            victoryScreen.SetActive(false);
        }
        
        private void NullCheckDefeatUiAnimationRewindEvent()
        {
            if (levelUiFlow == null)
            {
                Reset();
                Debug.LogWarning($"{name}: VictoryUiController: {nameof(levelUiFlow)} is null, " +
                                 $"bypassing animation");
            }
        }
        
        public void OnLevelRestartButton()
        {
            levelUiFlow.PublishVictory_PlayRestartBeginAnimation();
        }
        
        public void LevelRegistrySoSetter(ILevelRegistry levelRegistry)
        {
            _levelRegistry = levelRegistry;
        }
    }
}