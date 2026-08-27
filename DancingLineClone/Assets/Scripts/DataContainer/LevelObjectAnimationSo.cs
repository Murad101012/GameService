using System;
using UnityEngine;

namespace DataContainer
{
    [CreateAssetMenu(menuName = "ScriptableObjects/LevelObjectAnimation")]
    public class LevelObjectAnimationSo : ScriptableObject
    {
        public event Action<ushort> OnAnimationCollisionTrigger;
        
        public void PublishAnimationTrigger(ushort animationTrigger)
        {
            OnAnimationCollisionTrigger?.Invoke(animationTrigger);
        }
    }
}