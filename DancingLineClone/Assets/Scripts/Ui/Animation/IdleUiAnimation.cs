using System;
using DataContainer;
using DG.Tweening;
using Interfaces;
using UnityEngine;

namespace Ui.Animation
{
    /// <summary>
    /// Animation sequences collection for Idle screen (Screen appear that waiting for player to click the screen and begin to play)
    /// </summary>
    public class IdleUiAnimation : MonoBehaviour, ILevelRegistryUser, IOnRestart, IOnCheckPoint, ILevelState
    {
        private ILevelRegistry _levelRegistry;
        [SerializeField] private CanvasGroup canvasGroup;
        private Sequence _canvasGroupOpacitySequence;
        [SerializeField] private LevelUiFlowSo levelUiFlow;

        private void Awake()
        {
            _levelRegistry.Register(this);
            
            _canvasGroupOpacitySequence = DOTween.Sequence();
            
            _canvasGroupOpacitySequence.Append(canvasGroup.DOFade(0, 0.5f).From(1f).
                OnComplete(() =>
                {
                    if (levelUiFlow != null)
                    {
                        levelUiFlow.PublishOnCanvasAnimationEnd();
                    }
                    else
                    {
                        Debug.LogWarning($"{name}: variable '{nameof(levelUiFlow)}' not set. Can't publish when Canvas animation end");
                    }
                }));
            _canvasGroupOpacitySequence.SetAutoKill(false);
            _canvasGroupOpacitySequence.Pause();
        }

        private void OnDestroy()
        {
            _levelRegistry.Unregister(this);
        }

        public void LevelRegistrySoSetter(ILevelRegistry levelRegistry)
        {
            _levelRegistry = levelRegistry;
        }

        public void OnLevelRestart()
        {
            _canvasGroupOpacitySequence.PlayBackwards();
        }
        
        public void OnLevelCheckPoint()
        {
            _canvasGroupOpacitySequence.PlayBackwards();
        }

        public void OnLevelStart()
        {
            _canvasGroupOpacitySequence.PlayForward();
        }

        public void OnLevelStop(){/*IT WILL BE EMPTY*/}
    }
}