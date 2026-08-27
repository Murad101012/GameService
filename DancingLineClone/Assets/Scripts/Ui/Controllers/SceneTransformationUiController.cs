using DataContainer;
using DG.Tweening;
using Ui.ElementReferences;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ui.Controllers
{
    /// <summary>
    /// Controls lifecycle of Loading screen assets
    /// </summary>
    [RequireComponent(typeof(SceneTransformationElementReference))]
    public class SceneTransformationUiController : MonoBehaviour
    {
        private SceneTransformationElementReference _sceneTransformationElementReference;
        private StyleFloat _opacityFloat;
        private Sequence _sequenceLoadScreen;
        [SerializeField] private SceneLoadStateEventSo sceneLoadStateEventSo;

        private void Awake()
        {
            _sceneTransformationElementReference = GetComponent<SceneTransformationElementReference>();
            sceneLoadStateEventSo.OnPlayerClickedToLoadLevel += LoadScreenAnimationForward;
            sceneLoadStateEventSo.OnSceneFullyLoaded += LoadScreenAnimationBackward;
        }

        private void Start()
        {
            
            if (!_sceneTransformationElementReference.CheckFinished)
            {
                Debug.LogWarning($"{name}: checkFinished is false, can't get reference to UI elements. So, " +
                                 $"disabling the {name}. (Tip: check if race-condition happen (Meaning if {name} begin" +
                                 $" first before {nameof(SceneTransformationElementReference)} finish it's Initialization(). " +
                                 "If problem is race-condition then proceed to use IReady interface if it suits to current situation.)");
                enabled = false;
                return;
            }

            if (!_sceneTransformationElementReference.CheckResult)
            {
                Debug.LogWarning(
                    $"{name}: checkResult is false, can't get reference to UI elements. So, disabling the {name}.");
                enabled = false;
                return;
            }

            _sequenceLoadScreen = DOTween.Sequence();
            
            _sequenceLoadScreen.Join(DOTween.To(
                () => _sceneTransformationElementReference.BlackLayerReference.style.opacity.value,
                x =>
                {
                    _opacityFloat.value = x;
                    _sceneTransformationElementReference.BlackLayerReference.style.opacity = _opacityFloat;
                },
                1, 
                0.8f
            ).SetEase(Ease.InQuad));
            _sequenceLoadScreen.Pause();
            _sequenceLoadScreen.SetAutoKill(false);

            _sequenceLoadScreen.AppendInterval(0.5f);
            _sequenceLoadScreen.OnComplete(() => sceneLoadStateEventSo.InvokeOnLoadingScreenFullyLoaded());
        }

        private void OnDestroy()
        {
            sceneLoadStateEventSo.OnPlayerClickedToLoadLevel -= LoadScreenAnimationForward;
            sceneLoadStateEventSo.OnSceneFullyLoaded -= LoadScreenAnimationBackward;
        }

        private void LoadScreenAnimationForward()
        {
            _sequenceLoadScreen.Restart();
        }

        private void LoadScreenAnimationBackward()
        {
            _sequenceLoadScreen.PlayBackwards();
        }
    }
}