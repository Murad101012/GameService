using DataContainer;
using Interfaces;
using UnityEngine;
using UnityEngine.UI;

namespace Ui.Controllers
{
    /// <summary>
    /// Control the lifecycle of Idle Ui's element
    /// </summary>
    public class IdleUiController : MonoBehaviour, IOnRestart, IOnCheckPoint, ILevelRegistryUser, ILevelState
    {
        private ILevelRegistry _levelRegistry;
        [SerializeField] private GameObject idleCanvas;
        [SerializeField] private Button backToMenuButton;
        [SerializeField] private LevelUiFlowSo levelUiFlowSo;
        private bool _onIdle = true;

        private void Awake()
        {
            _levelRegistry.Register(this);
        }

        private void OnEnable()
        {
            if (levelUiFlowSo != null)
            {
                levelUiFlowSo.OnCanvasAnimationEnd += DisableIdleCanvas;
            }
        }

        private void OnDisable()
        {
            if (levelUiFlowSo != null)
            {
                levelUiFlowSo.OnCanvasAnimationEnd -= DisableIdleCanvas;
            }
        }

        private void OnDestroy()
        {
            _levelRegistry.Unregister(this);
        }
        
        private void DisableIdleCanvas()
        {
            if (!_onIdle)
            {
                idleCanvas.SetActive(false);
            }
        }

        public void OnLevelRestart()
        {
            ActivateIdleCanvas();
        }
        
        public void OnLevelCheckPoint()
        {
            ActivateIdleCanvas();
        }

        private void ActivateIdleCanvas()
        {
            backToMenuButton.interactable = true;
            _onIdle = true;
            idleCanvas.SetActive(true);
        }

        public void LevelRegistrySoSetter(ILevelRegistry levelRegistry)
        {
            _levelRegistry = levelRegistry;
        }

        public void OnLevelStart()
        {
            backToMenuButton.interactable = false;
            _onIdle = false;
            if (levelUiFlowSo == null)
            {
                Debug.LogWarning($"{name}: {nameof(levelUiFlowSo)} is null. " +
                                 $"Disabling the IdleUiCanvas without waiting idle animation complete when {nameof(OnLevelStart)}");
                idleCanvas.SetActive(false);
            }
        }

        public void OnLevelStop(){/*It will be empty*/ }
    }
}