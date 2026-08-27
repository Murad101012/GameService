using DataContainer;
using Interfaces;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Core
{
    /// <summary>
    /// It helps to change States of Level with Interfaces using <see cref="LevelRegistrySo"/>
    /// </summary>
    public class LevelStateManager : MonoBehaviour, ILevelState, IOnRestart, IOnCheckPoint, IVictory, IOnDead
    {
        /// <summary>
        /// LevelRegistrySo must manually to this script
        /// </summary>
        [SerializeField] private LevelRegistrySo levelRegistrySo;
        [SerializeField] private LevelPropertiesSo levelPropertiesSo;
        [SerializeField] private GameObject levelBeginButton; //:TODO Find a better location for this 
        [SerializeField] private LevelEventHubSo levelEventHubSo;
        private DancingLineCloneInput _dancingLineCloneInput;
        private bool _defeatAnimationEndReadyToBeginToPlay = true;
        private bool _isVictory;
        
        private void OnEnable()
        {
            levelEventHubSo.OnPlayerDead += PlayerOnPlayerDead;
            levelEventHubSo.OnVictoryTriggered += SetTheVictory;
            levelEventHubSo.OnCheckpointBeginAnimationEnd += CheckpointTheLevel;
            levelEventHubSo.OnRestartBeginAnimationEnd += RestartTheLevel;
        }

        private void Awake()
        {
            levelRegistrySo.Register(this);
            _dancingLineCloneInput = new DancingLineCloneInput();
            _dancingLineCloneInput.OnLevelWaitToPlay.Enable();
            _dancingLineCloneInput.OnLevelWaitToPlay.BeginTheGame.performed += BeginTheGameOnPerformed;
            levelEventHubSo.OnRestartEndAnimationEnd += LevelEventHubSoOnOnRestartEndAnimationEnd;
        }

        private void LevelEventHubSoOnOnRestartEndAnimationEnd()
        {
            _defeatAnimationEndReadyToBeginToPlay = true;
        }

        private void OnDisable()
        {
            levelEventHubSo.OnPlayerDead -= PlayerOnPlayerDead;
            levelEventHubSo.OnVictoryTriggered -= SetTheVictory;
            levelEventHubSo.OnCheckpointBeginAnimationEnd -= CheckpointTheLevel;
            levelEventHubSo.OnRestartBeginAnimationEnd -= RestartTheLevel;
        }

        private void OnDestroy()
        {
            levelRegistrySo.Unregister(this);
            _dancingLineCloneInput.OnLevelWaitToPlay.Disable();
            _dancingLineCloneInput.OnLevelWaitToPlay.BeginTheGame.performed -= BeginTheGameOnPerformed;
        }

        public void OnStartTheGameButton()
        {
            if (!_defeatAnimationEndReadyToBeginToPlay) return;
            _dancingLineCloneInput.OnLevelWaitToPlay.BeginTheGame.performed -= BeginTheGameOnPerformed;
            StartTheGame();
        }
        
        private void BeginTheGameOnPerformed(InputAction.CallbackContext obj)
        {
            OnStartTheGameButton();
        }

        #region Triggers Interfaces
        public void StartTheGame()
        {
            levelRegistrySo.TriggerStartILevelState();
        }
        
        private void StopTheGame()
        {
            levelRegistrySo.TriggerStopILevelState();
        }

        private void CheckpointTheLevel()
        { 
            levelRegistrySo.TriggerOnCheckPoint();
        }

        private void RestartTheLevel()
        {
            levelRegistrySo.TriggerOnRestart();
        }
        
        private void SetTheVictory()
        {
            levelRegistrySo.TriggerOnVictory();
        }

        private void PlayerOnPlayerDead()
        {
            if (_isVictory) return;
            levelRegistrySo.TriggerOnDead();
        }
        
        #endregion
        
        public void OnLevelStart()
        {
            levelBeginButton.SetActive(false);
            _dancingLineCloneInput.OnLevelWaitToPlay.BeginTheGame.performed -= BeginTheGameOnPerformed;
        }

        public void OnLevelStop() {/*It will be empty*/}
        
        public void OnLevelRestart()
        {
            _isVictory = false;
            Reset();
        }

        public void OnLevelCheckPoint()
        {
            Reset();
        }
        
        /// <summary>
        /// Functions are usually require same modify for both <see cref="IOnRestart"/> and <see cref="IOnCheckPoint"/>>
        /// </summary>
        private void Reset()
        {
            levelBeginButton.SetActive(true);
            _dancingLineCloneInput.OnLevelWaitToPlay.BeginTheGame.performed += BeginTheGameOnPerformed;
        }

        public void OnVictory()
        {
            _isVictory = true;
        }

        public void OnDead()
        {
            _defeatAnimationEndReadyToBeginToPlay = false;
        }
    }
}