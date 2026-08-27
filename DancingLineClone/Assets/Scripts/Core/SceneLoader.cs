using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;
using DataContainer;

namespace Core
{
    /// <summary>
    /// It's responsible for loading a Scene.
    /// </summary>
    /// <remarks>If a level working properly by playing directly, but throw null errors
    /// when loading that same level with LevelLoader, please check <see cref="IReady"/></remarks>
    public class SceneLoader : MonoBehaviour
    {
        private string _sceneNameInPreview;
        [SerializeField] private MenuOnLevelInPreviewChangeSo menuOnLevelInPreviewChange;
        [SerializeField] private CurrentlyLoadedSceneSo _currentlyLoadedSceneSo;
        [SerializeField] private SceneLoadStateEventSo sceneLoadStateEvent;
        private bool _sceneLoaderScreenFullyLoaded;
        private const float DurationToWaitSceneLoaded = 5f;
        

        private void OnEnable()
        {
            sceneLoadStateEvent.OnLoadingScreenFullyLoaded += LoadLevelAsync;
            menuOnLevelInPreviewChange.LevelPreviewChangeEvent += OnLevelPreviewChange;
        }

        private void Awake()
        {
            DontDestroyOnLoad(this);
            sceneLoadStateEvent.OnLoadingScreenFullyLoaded += OnLoadingScreenFullyLoaded;
        }

        private void Start()
        {
            ReturnToMenu();
        }

        private void OnDisable()
        {
            sceneLoadStateEvent.OnLoadingScreenFullyLoaded -= OnLevelPreviewChange;
            menuOnLevelInPreviewChange.LevelPreviewChangeEvent -= OnLevelPreviewChange;
            sceneLoadStateEvent.OnLoadingScreenFullyLoaded -= OnLoadingScreenFullyLoaded;
        }
        
        private async void LoadLevelAsync()
        {
            // Basic string check
            if (string.IsNullOrEmpty(_sceneNameInPreview)) 
            {
                Debug.LogWarning($"{name}: No scene name provided in preview");
                return;
            }

            // Build Settings check
            if (!Application.CanStreamedLevelBeLoaded(_sceneNameInPreview))
            {
                Debug.LogError($"{name}: Scene '{_sceneNameInPreview}' is not in Build Settings or doesn't exist");
                return;
            }

            SceneBeginToLoadEventInvoke();
            AsyncOperation op = SceneManager.LoadSceneAsync(_sceneNameInPreview);
            op.allowSceneActivation = false;
            
            //We're waiting until loadScreenAnimationFullyLoaded = true
            float timer = 0;
            while (!_sceneLoaderScreenFullyLoaded)
            {
                if (timer < DurationToWaitSceneLoaded)
                {
                    timer += Time.unscaledDeltaTime;
                }
                else
                {
                    Debug.LogWarning($"{name}: Loading screen isn't finished in setten time \"{DurationToWaitSceneLoaded}\". Beginning to load level without waiting Loading Screen ");
                    break;
                }
                await Task.Yield(); 
            }

            //When player only see loading screen, we're then allowing scene to load
            op.allowSceneActivation = true;

            //We check if level fully load
            while (!op.isDone)
            {
                await Task.Yield();
            }

            _currentlyLoadedSceneSo.loadedScene = menuOnLevelInPreviewChange.levelInPreview;
            
            _sceneLoaderScreenFullyLoaded = false;

            AfterSceneCompletelyLoadEventInvoke();
        }

        private void SceneBeginToLoadEventInvoke()
        {
            sceneLoadStateEvent.InvokeOnSceneBeginToLoad();
        }

        private void AfterSceneCompletelyLoadEventInvoke()
        {
            sceneLoadStateEvent.InvokeOnSceneFullyLoaded();
        }

        private void OnLevelPreviewChange()
        {
            _sceneNameInPreview = menuOnLevelInPreviewChange.levelInPreview.levelName;
        }
        
        private void OnLoadingScreenFullyLoaded()
        {
            _sceneLoaderScreenFullyLoaded = true;
        }

        public void ReturnToMenu()
        {
            _sceneNameInPreview = "Menu";
            sceneLoadStateEvent.InvokeOnPlayerClickedToLoadScene();
        }
    }
}