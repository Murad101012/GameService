using DataContainer;
using Interfaces;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Ui.Controllers
{
    /// <summary>
    /// Responsible for managing life cycle of Defeat.prefab's gameobjects
    /// </summary>
    public class DefeatUiController : MonoBehaviour, IOnRestart, IOnCheckPoint, IOnDead, ILevelRegistryUser
    {
        [Header("UI References")]
        [SerializeField] private GameObject defeatScreen;
        [SerializeField] private Button checkPointButton;
        [SerializeField] private Button backToMenuButton;
        [SerializeField] private TextMeshProUGUI levelNameText;
        [SerializeField] private TextMeshProUGUI levelProgressText;
        /// <remarks>
        /// Duplicates from <see cref="CheckPointSnapshot._checkPointTriggered"/>
        /// </remarks>
        private bool _checkPointTriggered;
        private ILevelRegistry _levelRegistry;
        
        [Header("")]
        [SerializeField] private CurrentlyLoadedSceneSo currentlyLoadedSceneSo;
        [SerializeField] private ProgressInCurrentLoadedLevelSo progressInCurrentLoadedLevelSo;
        [SerializeField] private LevelUiFlowSo levelUiFlowSo;
        
        //Input
        private DancingLineCloneInput _dancingLineCloneInput;
        
        private void OnEnable()
        {
            progressInCurrentLoadedLevelSo.OnCheckPointTrigger += RefreshCheckPointButtonState;

            //It's require for smooth DefeatScreen disabling, without preventing Scaling down animation
            if(levelUiFlowSo != null) levelUiFlowSo.Defeat_OnRestartEndAnimationEnd += Reset;
        }

        private void Awake()
        {
            levelNameText.text = currentlyLoadedSceneSo.loadedScene.levelName;
            _dancingLineCloneInput = new DancingLineCloneInput();
            _dancingLineCloneInput.OnDeadScreen.Enable();
            
            _levelRegistry.Register(this);
        }
        
        private void OnDisable()
        {
            progressInCurrentLoadedLevelSo.OnCheckPointTrigger -= RefreshCheckPointButtonState;
            if(levelUiFlowSo != null) levelUiFlowSo.Defeat_OnRestartEndAnimationEnd -= Reset;
        }

        private void OnDestroy()
        {
            _levelRegistry.Unregister(this);
            _dancingLineCloneInput.OnDeadScreen.Disable();
            _dancingLineCloneInput.OnDeadScreen.CheckPoint.performed -= CheckPointOnPerformed;
            _dancingLineCloneInput.OnDeadScreen.Restart.performed -= RestartOnPerformed;
        }

        private void RefreshCheckPointButtonState() 
        {
            if (checkPointButton != null)
                checkPointButton.interactable = true;
            else Debug.LogWarning($"{name}: No checkpoint button found");
        }

        public void OnDead()
        {
            defeatScreen.SetActive(true);
            ChangeLevelProgressText(progressInCurrentLoadedLevelSo.progressInCurrentLoadedLevel);
            
            _dancingLineCloneInput.OnDeadScreen.CheckPoint.performed += CheckPointOnPerformed;
            _dancingLineCloneInput.OnDeadScreen.Restart.performed += RestartOnPerformed;
        }

        public void Reset()
        {
            defeatScreen.SetActive(false);
        }

        public void OnLevelRestart()
        {
            _checkPointTriggered = false;
            checkPointButton.interactable = _checkPointTriggered;
            NullCheckDefeatUiAnimationRewindEvent();
        }

        public void OnLevelCheckPoint()
        {
            NullCheckDefeatUiAnimationRewindEvent();
        }

        private void NullCheckDefeatUiAnimationRewindEvent()
        {
            if (levelUiFlowSo == null)
            {
                Reset();
                Debug.LogWarning($"{name}: DefeatUiController: {nameof(levelUiFlowSo)} is null, " +
                                 $"bypassing animation");
            }
        }
        
        public void LevelRegistrySoSetter(ILevelRegistry levelRegistry)
        {
            _levelRegistry = levelRegistry;
        }
        
        private void RestartOnPerformed(InputAction.CallbackContext obj)
        {
            OnLevelRestartButton();
        }

        private void CheckPointOnPerformed(InputAction.CallbackContext obj)
        {
            OnLevelCheckPointButton();
        }

        public void OnLevelRestartButton()
        {
            _dancingLineCloneInput.OnDeadScreen.CheckPoint.performed -= CheckPointOnPerformed;
            _dancingLineCloneInput.OnDeadScreen.Restart.performed -= RestartOnPerformed;
            levelUiFlowSo.PublishDefeat_PlayRestartBeginAnimation(true);
        }

        public void OnLevelCheckPointButton()
        {
            _dancingLineCloneInput.OnDeadScreen.CheckPoint.performed -= CheckPointOnPerformed;
            _dancingLineCloneInput.OnDeadScreen.Restart.performed -= RestartOnPerformed;
            levelUiFlowSo.PublishDefeat_PlayRestartBeginAnimation(false);
        }

        public void ChangeLevelProgressText(float progress)
        {
            levelProgressText.text = $"Progress: {progress:F0}%";
        }
    }
}