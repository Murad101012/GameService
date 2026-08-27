using System;
using DataContainer;
using Interfaces;
using UnityEngine;

namespace Gameplay
{
    /// <summary>
    /// It's for triggering the Victory that put on at the of level.
    /// Use VictoryTrigger.prefab
    /// </summary>
    public class VictoryTrigger: MonoBehaviour
    {
        [SerializeField] private LevelEventHubSo levelEventHubSo;
        
        private void OnTriggerEnter(Collider other)
        {
            if(other.TryGetComponent(out IVictory _)) levelEventHubSo.PublishVictory();
        }
    }
}