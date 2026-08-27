using System;
using UnityEngine;

namespace DataContainer
{
    /// <summary>
    /// Source of truth for information about what achieved and how much progressed
    /// </summary>
    [CreateAssetMenu(menuName = "ScriptableObjects/ProgressInCurrentLoadedLevel")]
    public class ProgressInCurrentLoadedLevelSo : ScriptableObject
    {
        //Finding the progress
        public float audioDuration;
        public float audioPlaybackTime;
        public float playbackInAudioWhenPlayerDead;
        public float progressInCurrentLoadedLevel;

        public Transform playerTransform;
            
        public event Action OnCheckPointTrigger;

        public void PublishCheckPointerTrigger()
        {
            OnCheckPointTrigger?.Invoke();
        }
        
        //For future reference
        public int collectedCoins;
        public int collectedCrowns;
    }
}