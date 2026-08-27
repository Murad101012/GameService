using System;
using UnityEngine;

namespace DataContainer
{
    [CreateAssetMenu(menuName = "ScriptableObjects/LevelUiFlow")]
    public class LevelUiFlowSo : ScriptableObject
    {
        //Defeat
        public event Action Defeat_OnRestartEndAnimationEnd;
        public event Action<bool> Defeat_OnPlayRestartBeginAnimation;
        
        //Victory
        public event Action Victory_OnRestartEndAnimationEnd;
        public event Action Victory_OnPlayRestartBeginAnimation;
        
        //Idle
        public event Action OnCanvasAnimationEnd;
        
        
        public void PublishDefeat_RestartEndAnimationEnd()
        {
            Defeat_OnRestartEndAnimationEnd?.Invoke();
        }

        public void PublishDefeat_PlayRestartBeginAnimation(bool isRestart)
        {
            Defeat_OnPlayRestartBeginAnimation?.Invoke(isRestart);
        }

        public void PublishVictory_RestartEndAnimationEnd()
        {
            Victory_OnRestartEndAnimationEnd?.Invoke();
        }

        public void PublishVictory_PlayRestartBeginAnimation()
        {
            Victory_OnPlayRestartBeginAnimation?.Invoke();
        }

        public void PublishOnCanvasAnimationEnd()
        {
            OnCanvasAnimationEnd?.Invoke();
        }
    }
}