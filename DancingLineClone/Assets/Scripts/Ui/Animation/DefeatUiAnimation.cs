using System;
using DataContainer;
using DG.Tweening;
using Interfaces;
using UnityEngine;
using UnityEngine.UI;

namespace Ui.Animation
{
    /// <summary>
    /// Animations for Defeat.prefab using DOTween
    /// </summary>
    public class DefeatUiAnimation : MonoBehaviour, IOnRestart, IOnCheckPoint, IOnDead, ILevelRegistryUser
    {
        [SerializeField] private RectTransform backgroundRect;
        [SerializeField] private CanvasGroup defeatCanvasGroup;
        [SerializeField] private CanvasGroup elementsCanvasGroup;
        [SerializeField] private Image backgroundImage;
        [SerializeField] private GameObject defeatRoot;
        [SerializeField] private Button button;
        private bool _animationWorking;
        private bool _isPlayerRestart;
        
        //Getting beginning values to reset back
        private Vector2 _backgroundRectBeginningOffsetMinValues;
        private Vector2 _backgroundRectBeginningOffsetMaxValues;
        private Color _backgroundImageBeginningColor;

        private Sequence _defeatSequence;
        private readonly float _defeatBackgroundImageEndPosition = 200f;
        private readonly float _defeatBackgroundImageBeginningPosition = 1000f;
        private readonly float _defeatAnimationDuration = 0.5f;
        private Vector2 _defeatBackgroundImagePositionCurrent;
        
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
                levelUiFlowSo.Defeat_OnPlayRestartBeginAnimation += PlayRestartBeginAnimation;
            }
            else
            {
                Debug.LogWarning($"{name}: variable '{nameof(levelUiFlowSo)}' is null. Can't listen to when play restart animation");
            }
            
            InitializeSequence();
        }

        private void InitializeSequence()
        {
            // Create the sequence and ensure it doesn't destroy itself so we can play it backwards
            _defeatSequence = DOTween.Sequence();
            _restartBeginSequence = DOTween.Sequence();
            _restartEndSequence = DOTween.Sequence();

            _defeatSequence.AppendCallback(() => backgroundRect.gameObject.SetActive(true));
                
            _defeatSequence.Join(DOTween.To(
                () => backgroundRect.offsetMin.y,
                y =>
                {
                    _defeatBackgroundImagePositionCurrent.x = backgroundRect.offsetMin.x;
                    _defeatBackgroundImagePositionCurrent.y = y;
                    backgroundRect.offsetMin = _defeatBackgroundImagePositionCurrent;
                },
                _defeatBackgroundImageEndPosition, 
                _defeatAnimationDuration
            ).From(_defeatBackgroundImageBeginningPosition, false).SetEase(Ease.OutBack));


            _defeatSequence.Join(defeatCanvasGroup.DOFade(1f, _defeatAnimationDuration).From(0f, false));

            _defeatSequence.SetAutoKill(false);
            _defeatSequence.Pause();
            
            
            _restartBeginSequence.Join(DOTween.To(() => backgroundRect.offsetMin.y, y =>
            {
                _restartBackgroundImagePositionCurrent.x = backgroundRect.offsetMin.x;
                _restartBackgroundImagePositionCurrent.y = y;
                backgroundRect.offsetMin = _restartBackgroundImagePositionCurrent;
            }, _restartBeginBackgroundImageBottomEndPosition, _restartAnimationDuration)
                .From(_restartBeginBackgroundImageBottomBeginningPosition, false).SetEase(Ease.InBack).OnComplete(() =>
                {
                    if (_isPlayerRestart) levelEventHubSo.PublishRestartBeginAnimationEnd();
                    else levelEventHubSo.PublishCheckpointBeginAnimationEnd();
                }));
            
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
                    levelUiFlowSo.PublishDefeat_RestartEndAnimationEnd();
                    levelEventHubSo.PublishRestartEndAnimationEnd();
                }
                else
                {
                    Debug.LogWarning($"{name}: variable '{nameof(levelUiFlowSo)}' is null.");
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

        public void OnDead()
        {
            _defeatSequence.Restart();
        }

        public void OnLevelRestart()
        {
            _restartEndSequence.Restart();
        }

        public void OnLevelCheckPoint()
        {
            _restartEndSequence.Restart();
        }

        private void OnDestroy()
        {
            _levelRegistry.Unregister(this);
            if (levelUiFlowSo != null)
            {
                levelUiFlowSo.Defeat_OnPlayRestartBeginAnimation -= PlayRestartBeginAnimation;
            }
            // Clean up the tween memory
            _defeatSequence?.Kill();
            _restartEndSequence?.Kill();
            _restartBeginSequence?.Kill();
        }

        private void PlayRestartBeginAnimation(bool isRestart)
        {
            if (!_animationWorking)
            {
                _restartBeginSequence.Restart();
                _animationWorking = true;
                _isPlayerRestart = isRestart;
            }
        }

        public void LevelRegistrySoSetter(ILevelRegistry levelRegistry)
        {
            _levelRegistry = levelRegistry;
        }
    }
}