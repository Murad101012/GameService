using System;
using DataContainer;
using DG.Tweening;
using Interfaces;
using UnityEngine;
using UnityEngine.UI;

namespace Ui.Animation
{
    /// <summary>
    /// Animations for Victory.prefab
    /// </summary>
    /// <remarks>Since animations of Victory and defeat is same with minor changes, code is duplicated from <see cref="DefeatUiAnimation"/></remarks>
    public class VictoryUiAnimation : MonoBehaviour, IVictory, IOnRestart, ILevelRegistryUser
    {
        [SerializeField] private RectTransform backgroundRect;
        [SerializeField] private CanvasGroup victoryCanvasGroup;
        [SerializeField] private CanvasGroup elementsCanvasGroup;
        [SerializeField] private Image backgroundImage;
        [SerializeField] private GameObject victoryRoot;
        private bool _animationWorking;
        
        //Getting beginning values to reset back
        private Vector2 _backgroundRectBeginningOffsetMinValues;
        private Vector2 _backgroundRectBeginningOffsetMaxValues;
        private Color _backgroundImageBeginningColor;

        private Sequence _victorySequence;
        private readonly float _victoryBackgroundImageEndPosition = 200f;
        private readonly float _victoryBackgroundImageBeginningPosition = 1000f;
        private readonly float _victoryAnimationDuration = 0.5f;
        private Vector2 _victoryBackgroundImagePositionCurrent;
        
        //Stage 1 
        private Sequence _restartBeginSequence;
        private readonly float _restartBeginBackgroundImageBottomEndPosition = -500f;
        private readonly float _restartBeginBackgroundImageBottomBeginningPosition = 200;

        private Sequence _restartEndSequence;
        //Top - Stage 2 (Removing the black screen after restart fully done)
        private readonly float _restartEndBackgroundImageTopEndPosition = -1500;
        private readonly float _restartEndBackgroundImageTopBeginningPosition = 400;
        
        private readonly float _restartAnimationDuration = 0.7f;
        private Vector3 _restartBackgroundImagePositionCurrent;
        private Color _restartBackgroundImageEndColor = new (1, 1, 1, 1);
        private Color _restartBackgroundImageBeginningColor = new (1, 1, 1, 0.86274f);
        
        private ILevelRegistry _levelRegistry;

        [SerializeField] private LevelEventHubSo levelEventHubSo;
        [SerializeField] private LevelUiFlowSo levelUiFlowSo;

        private void Awake()
        {
            _backgroundRectBeginningOffsetMinValues = backgroundRect.offsetMin;
            _backgroundRectBeginningOffsetMaxValues = backgroundRect.offsetMax;
            _backgroundImageBeginningColor = backgroundImage.color;
            
            _levelRegistry.Register(this);
            if (levelUiFlowSo != null)
            {
                levelUiFlowSo.Victory_OnPlayRestartBeginAnimation += PlayRestartBeginAnimation;
            }
            InitializeSequence();
        }

        private void InitializeSequence()
        {
            // Create the sequence and ensure it doesn't destroy itself so we can play it backwards
            _victorySequence = DOTween.Sequence();
            _restartBeginSequence = DOTween.Sequence();
            _restartEndSequence = DOTween.Sequence();

            _victorySequence.AppendCallback(() => backgroundRect.gameObject.SetActive(true));
                
            _victorySequence.Join(DOTween.To(
                () => backgroundRect.offsetMin.y,
                y =>
                {
                    _victoryBackgroundImagePositionCurrent.x = backgroundRect.offsetMin.x;
                    _victoryBackgroundImagePositionCurrent.y = y;
                    backgroundRect.offsetMin = _victoryBackgroundImagePositionCurrent;
                },
                _victoryBackgroundImageEndPosition, 
                _victoryAnimationDuration
            ).From(_victoryBackgroundImageBeginningPosition, false).SetEase(Ease.OutBack));


            _victorySequence.Join(victoryCanvasGroup.DOFade(1f, _victoryAnimationDuration).From(0f, false));

            _victorySequence.SetAutoKill(false);
            _victorySequence.Pause();
            
            
            _restartBeginSequence.Join(DOTween.To(() => backgroundRect.offsetMin.y, y =>
            {
                _restartBackgroundImagePositionCurrent.x = backgroundRect.offsetMin.x;
                _restartBackgroundImagePositionCurrent.y = y;
                backgroundRect.offsetMin = _restartBackgroundImagePositionCurrent;
            }, _restartBeginBackgroundImageBottomEndPosition, _restartAnimationDuration)
                .From(_restartBeginBackgroundImageBottomBeginningPosition, false).SetEase(Ease.InBack).OnComplete(() => levelEventHubSo.PublishRestartBeginAnimationEnd()));
            
            _restartBeginSequence.Join(backgroundImage.DOColor(_restartBackgroundImageEndColor, _restartAnimationDuration).From(_restartBackgroundImageBeginningColor, false));
            _restartBeginSequence.Join(elementsCanvasGroup.DOFade(0f, _restartAnimationDuration).From(1f, false));

            
            _restartBeginSequence.SetAutoKill(false);
            _restartBeginSequence.Pause();

            _restartEndSequence.AppendInterval(.5f);
            _restartEndSequence.Append(DOTween.To(() => backgroundRect.offsetMax.y,
                y =>
                {
                    _restartBackgroundImagePositionCurrent.x = backgroundRect.offsetMax.x;
                    _restartBackgroundImagePositionCurrent.y = y;
                    backgroundRect.offsetMax = _restartBackgroundImagePositionCurrent;
                }, _restartEndBackgroundImageTopEndPosition, _restartAnimationDuration).From(_restartEndBackgroundImageTopBeginningPosition, false).
                SetEase(Ease.InBack));

            _restartEndSequence.Append(backgroundImage.DOColor(Color.clear, _restartAnimationDuration).From(_restartBackgroundImageEndColor, false).OnComplete (() =>
            {
                if (levelUiFlowSo != null)
                {
                    levelUiFlowSo.PublishVictory_RestartEndAnimationEnd();
                }
                else
                {
                    Debug.Log($"{name}: variable '{nameof(levelUiFlowSo)}' is null. Can't publish event that restart animation end");
                }
                ResetAnimationValues();
            }));

            _restartEndSequence.SetAutoKill(false);
            _restartEndSequence.Pause();
        }

        private void ResetAnimationValues()
        {
            backgroundRect.offsetMin = _backgroundRectBeginningOffsetMinValues;
            backgroundRect.offsetMax = _backgroundRectBeginningOffsetMaxValues;
            backgroundImage.color = _backgroundImageBeginningColor;
            backgroundImage.gameObject.SetActive(false);
            elementsCanvasGroup.alpha = 1;
            _animationWorking = false;
        }

        public void OnVictory()
        {
            _victorySequence.Restart();
        }
        
        public void OnLevelRestart()
        {
            _restartEndSequence.Restart();
        }

        private void OnDestroy()
        {
            _levelRegistry.Unregister(this);
            if (levelUiFlowSo != null)
            {
                levelUiFlowSo.Victory_OnPlayRestartBeginAnimation -= PlayRestartBeginAnimation;
            }
            // Clean up the tween memory
            _victorySequence?.Kill();
            _restartEndSequence?.Kill();
            _restartBeginSequence?.Kill();
        }

        private void PlayRestartBeginAnimation()
        {
            if (!_animationWorking)
            {
                _restartBeginSequence.Restart();
                _animationWorking = true;
            }
        }

        public void LevelRegistrySoSetter(ILevelRegistry levelRegistry)
        {
            _levelRegistry = levelRegistry;
        }
    }
}