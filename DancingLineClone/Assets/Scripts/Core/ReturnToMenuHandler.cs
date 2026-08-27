using System;
using UnityEngine;

namespace Core
{
    /// <summary>
    /// Listens <see cref="ReturnToMenuEvent"/> to send signal <see cref="SceneLoader"/> for loading back to Menu scene
    /// </summary>
    [RequireComponent(typeof(SceneLoader))]
    public class ReturnToMenuHandler : MonoBehaviour
    {
        private SceneLoader _sceneLoader;

        private void OnEnable()
        {
            ReturnToMenuEvent.OnReturnToMenu += OnReturnMenu;
        }

        private void Awake()
        {
            _sceneLoader = GetComponent<SceneLoader>();
        }

        private void OnDisable()
        {
            ReturnToMenuEvent.OnReturnToMenu -= OnReturnMenu;
        }

        private void OnReturnMenu()
        {
            _sceneLoader.ReturnToMenu();
        }
    }
}