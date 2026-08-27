using System;
using UnityEngine;

namespace DataContainer
{
    [CreateAssetMenu(menuName = "ScriptableObjects/LevelEventHub")]
    public class LevelEventHubSo : ScriptableObject
    {
        // Signals coming FROM the game features
        public event Action OnPlayerDead;
        public event Action OnVictoryTriggered;
        public event Action OnRestartBeginAnimationEnd;
        public event Action OnRestartEndAnimationEnd;
        public event Action OnCheckpointBeginAnimationEnd;

        // Methods for features to call
        public void PublishPlayerDead() => OnPlayerDead?.Invoke();
        public void PublishVictory() => OnVictoryTriggered?.Invoke();
        public void PublishRestartBeginAnimationEnd() => OnRestartBeginAnimationEnd?.Invoke();
        public void PublishRestartEndAnimationEnd() => OnRestartEndAnimationEnd?.Invoke();
        public void PublishCheckpointBeginAnimationEnd() => OnCheckpointBeginAnimationEnd?.Invoke();
    }
}