using System;
using Interfaces;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Core
{
    /// <summary>
    /// Script to communicate with event (<see cref="OnReturnToMenu"/>) to <see cref="SceneLoader"/> for load Menu scene back
    /// </summary>
    /// <remarks>It's used in Ui prefabs on Level such as Defeat Ui/Victory Ui etc. where when player click on
    /// BackToMenuButton.prefab this script invoke and <see cref="ReturnToMenuHandler"/> send signal to <see cref="SceneLoader"/>
    /// for load the menu</remarks>
    public class ReturnToMenuEvent : MonoBehaviour, ILevelRegistryUser, IOnDead, ILevelState, IOnRestart, IOnCheckPoint
    {
        public static event Action OnReturnToMenu;
        private Button _returnToMenuButton;
        private ILevelRegistry _levelRegistry;
        private DancingLineCloneInput _dancingLineCloneInput;
        
        private void OnEnable()
        {
            _returnToMenuButton.onClick.AddListener(ReturnToMenu);
        }

        private void Awake()
        {
            _levelRegistry.Register(this);
            _returnToMenuButton = GetComponent<Button>();
            _dancingLineCloneInput = new DancingLineCloneInput();
            _dancingLineCloneInput.OnLevelWaitToPlay.Enable();
            _dancingLineCloneInput.OnLevelWaitToPlay.BackToMenu.performed += BackToMenuOnPerformed;
        }

        private void OnDestroy()
        {
            _levelRegistry.Unregister(this);
            _dancingLineCloneInput.OnLevelWaitToPlay.Disable();
        }

        private void OnDisable()
        {
            _returnToMenuButton.onClick.RemoveListener(ReturnToMenu);
        }

        private void ReturnToMenu()
        {
            OnReturnToMenu?.Invoke();
        }

        public void OnDead()
        {
            _dancingLineCloneInput.OnDeadScreen.BackToMenu.performed += BackToMenuOnPerformed;
        }

        public void LevelRegistrySoSetter(ILevelRegistry levelRegistry)
        {
            _levelRegistry = levelRegistry;
        }

        public void OnLevelStart()
        {
            _dancingLineCloneInput.OnLevelWaitToPlay.BackToMenu.performed -= BackToMenuOnPerformed;
        }

        private void BackToMenuOnPerformed(InputAction.CallbackContext context)
        {
            ReturnToMenu();
        }

        public void OnLevelStop(){/*IT WILL BE EMPTY*/}
        public void OnLevelRestart()
        {
            _dancingLineCloneInput.OnLevelWaitToPlay.BackToMenu.performed += BackToMenuOnPerformed;
        }

        public void OnLevelCheckPoint()
        {
            _dancingLineCloneInput.OnLevelWaitToPlay.BackToMenu.performed += BackToMenuOnPerformed;

        }
    }
}