using System;
using UnityEngine;

namespace DataContainer
{
    /// <summary>
    /// Prevent race-condition cause from not fully initialized GameObject between scenes load 
    /// </summary>
    /// <remarks>
    /// When a Scene load, gameobjects are not wait until the scene they belong to fully load. This cause race-condition
    /// where X gameobject may have reference to Y gameobject but since scene not fully loaded and Y gameobject didn't had
    /// a chance to initialize itself, when X gameobject try to Y gameobject it can't get reference since it's not fully ready.
    /// Usually all gameobjects SEEMS fully loaded when the scene also fully loaded, so this Scriptable Object created based on this.
    /// When Scene fully loaded <see cref="SceneLoader"/>, <see cref="OnSceneFullyLoaded"/> will invoke. Gameobjects those are
    /// must postphone their "Start/Awake" to the after fully loaded scene in new scene will listen to this event and initialize
    /// their code when it's trigger.
    /// </remarks>
    [CreateAssetMenu(menuName = "ScriptableObjects/SceneFullyLoadedEventSo")]
    public class SceneLoadStateEventSo : ScriptableObject
    {
        public event Action OnPlayerClickedToLoadLevel;
        public event Action OnSceneFullyLoaded;
        public event Action OnSceneBeginToLoad;
        public event Action OnLoadingScreenFullyLoaded;

        public void InvokeOnSceneBeginToLoad()
        {
            OnSceneBeginToLoad?.Invoke();
        }

        public void InvokeOnSceneFullyLoaded()
        {
            OnSceneFullyLoaded?.Invoke();
        }

        public void InvokeOnLoadingScreenFullyLoaded()
        {
            OnLoadingScreenFullyLoaded?.Invoke();
        }

        public void InvokeOnPlayerClickedToLoadScene()
        {
            OnPlayerClickedToLoadLevel?.Invoke();
        }
    }
}