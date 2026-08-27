using DataContainer;
using DG.Tweening;
using Ui.ElementReferences;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ui.Controllers
{
    /// <summary>
    /// Responsible to control Menu's UI (e.g: changing behavior of an element (Interactable, Opacity, Enabled)) at level selection,
    /// acting as centralized hub for connecting buttons)
    /// </summary>
    /// <remarks> To prevent using direct string references to find a Visual Element in UIToolkit hierarchy,
    /// you must get references from <see cref="MenuUiElementReference"/> to prevent typos and get latest Visual Element
    /// names if their name changed</remarks>
    [RequireComponent(typeof(MenuUiElementReference))]
    [RequireComponent(typeof(UIDocument))]
    public class MenuUiController : MonoBehaviour
    {
        private UIDocument _uiDocument;
        private MenuUiElementReference _menuUiElementReference;
        [SerializeField] MenuOnLevelInPreviewChangeSo menuOnLevelInPreviewChangeSo;
        
        //Cache Scale and Crop
        private readonly BackgroundSize _backgroundSize = new BackgroundSize(BackgroundSizeType.Cover);
        private readonly BackgroundPosition _backgroundPosition = new BackgroundPosition(BackgroundPositionKeyword.Center);

        private Sequence _backgroundImageBlackFadeAnimation;
        
        private void OnEnable()
        {
            if (menuOnLevelInPreviewChangeSo == null)
            {
                Debug.LogWarning($"{name}: {nameof(menuOnLevelInPreviewChangeSo)} is null. Chancing name and background not possible");
                return;
            }
            menuOnLevelInPreviewChangeSo.LevelPreviewChangeEvent += OnLevelPreviewChange;
            
            menuOnLevelInPreviewChangeSo.BeginLevelPreviewChangeEvent += BlackScreenAnimation;
        }

        private void Awake()
        {
            _menuUiElementReference = GetComponent<MenuUiElementReference>();
        }

        private void BlackScreenAnimation(bool forward = true)
        {
            if (forward) _backgroundImageBlackFadeAnimation.Restart();
            else _backgroundImageBlackFadeAnimation.PlayBackwards();
        }
        
        private void Start()
        {
            if (!_menuUiElementReference.CheckFinished)
            {
                string updateTextOfClass = nameof(MenuUiElementReference);
                Debug.LogWarning($"{name}: checkFinished is false, can't get reference to UI elements. So, " +
                                 $"disabling the MenuUiController. (Tip: check if race-condition happen (Meaning if {name} begin" +
                                 $" first before {updateTextOfClass} finish it's Initialization(). " +
                                 "If problem is race-condition then proceed to use IReady interface if it suits to current situation.)");
                enabled = false;
                return;
            }

            if (!_menuUiElementReference.CheckResult)
            {
                Debug.LogWarning(
                    $"{name}: checkResult is false, can't get reference to UI elements. So, disabling the {name}.");
                enabled = false;
                return;
            }
            
            _backgroundImageBlackFadeAnimation = DOTween.Sequence();
            _backgroundImageBlackFadeAnimation.Append(DOTween.To(
                    () => _menuUiElementReference.BlackScreenReference.style.opacity.value,
                    x => _menuUiElementReference.BlackScreenReference.style.opacity = x, 
                    1, 
                    0.2f).From(0.7f)
            );

            _backgroundImageBlackFadeAnimation.SetAutoKill(false);
            _backgroundImageBlackFadeAnimation.Pause();
        }

        private void OnDisable()
        {
            menuOnLevelInPreviewChangeSo.LevelPreviewChangeEvent -= OnLevelPreviewChange;
            menuOnLevelInPreviewChangeSo.BeginLevelPreviewChangeEvent -= BlackScreenAnimation;
        }

        private void OnLevelPreviewChange()
        {
            //Chancing name
            _menuUiElementReference.LevelLabelReference.text =
                menuOnLevelInPreviewChangeSo.levelInPreview.levelName;
            
            //Chancing background-image
            _menuUiElementReference.Root.style.backgroundImage =
                menuOnLevelInPreviewChangeSo.levelInPreview.styleBackgroundBlurredLevelImage;
            
            //Since setting this in root class container ignored when we set background-image as style, we need to implement in C# manually
            /*By Gemini: Modern replacement for -unity-background-scale-mode: scale-and-crop */
            // 1. Force 'Cover' (Scale and Crop) via C#
            _menuUiElementReference.Root.style.backgroundSize = _backgroundSize;

            // 2. Force 'Center' alignment
            _menuUiElementReference.Root.style.backgroundPositionX = _backgroundPosition;
            _menuUiElementReference.Root.style.backgroundPositionY = _backgroundPosition;

        }
    }
}