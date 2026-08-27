using DataContainer;
using Interfaces;
using UnityEngine;

namespace Core.Gameplay
{
    /// <summary>
    /// Logic when player dead
    /// </summary>
    public class DefeatLogic : MonoBehaviour, IOnDead, ILevelRegistryUser
    {
       [SerializeField] private ProgressInCurrentLoadedLevelSo progressInCurrentLoadedLevelSo;
       
       private ILevelRegistry _levelRegistry;

       private void Awake()
       {
           _levelRegistry.Register(this);
       }

       private void OnDestroy()
       {
           _levelRegistry.Unregister(this);
       }

       public void OnDead()
       {
           if (progressInCurrentLoadedLevelSo != null)
           {
               //On dead, it calculates the progress (as %) player made by "playback time of current song * 100 / total length of current song"
               progressInCurrentLoadedLevelSo.progressInCurrentLoadedLevel = Mathf.Clamp(progressInCurrentLoadedLevelSo.playbackInAudioWhenPlayerDead * 100 / progressInCurrentLoadedLevelSo.audioDuration, 0f, 100f);
           }
           else
           {
               Debug.LogWarning($"{name}: {nameof(progressInCurrentLoadedLevelSo)} is null. Can't change progress in defeat screen that how much player progress");
           }
       }

       public void LevelRegistrySoSetter(ILevelRegistry levelRegistry)
       {
           _levelRegistry = levelRegistry;
       }
    }
}