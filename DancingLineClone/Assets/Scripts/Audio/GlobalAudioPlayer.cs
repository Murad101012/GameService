using System;
using DataContainer;
using DG.Tweening;
using Interfaces;
using UnityEngine;

namespace Audio
{
    /// <summary>
    /// All sounds in the game going through this script
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public class GlobalAudioPlayer : MonoBehaviour, ILevelState, IOnDead, IOnCheckPoint
    {
        private AudioSource _audioSource;
        [SerializeField] private MenuOnLevelInPreviewChangeSo menuOnLevelInPreviewChangeSo;
        [SerializeField] private LevelRegistrySo levelRegistrySo;
        [SerializeField] private SceneLoadStateEventSo sceneLoadStateEvent;
        [SerializeField] private ProgressInCurrentLoadedLevelSo progressInCurrentLoadedLevelSo;
        private float _audioPlaybackTimeSnapshotOnTrigger;

        private void OnEnable()
        {
            if (menuOnLevelInPreviewChangeSo == null)
            {
                Debug.LogWarning($"{name}: {nameof(menuOnLevelInPreviewChangeSo)} is null. Can't access audio when level preview change");
                return;
            }

            if (progressInCurrentLoadedLevelSo == null)
            {
                Debug.LogWarning($"{name}: {nameof(progressInCurrentLoadedLevelSo)} is null. Can't write information about audio that where currently is playing on");
            }
            menuOnLevelInPreviewChangeSo.LevelPreviewChangeEvent += OnLevelPreviewChange;
            sceneLoadStateEvent.OnSceneBeginToLoad += OnSceneBeginToLoad;
            menuOnLevelInPreviewChangeSo.BeginLevelPreviewChangeEvent += MenuOnLevelInPreviewChangeSoOnBeginLevelPreviewChangeEvent;
            progressInCurrentLoadedLevelSo.OnCheckPointTrigger += OnCheckPointTrigger;
        }

        private void MenuOnLevelInPreviewChangeSoOnBeginLevelPreviewChangeEvent(bool obj)
        {
            if (obj) StopSound(true, 0.5f);
            else PlaySound();
        }

        private void Awake()
        {
            _audioSource = GetComponent<AudioSource>();
            
            levelRegistrySo.Register(this);
        }

        private void Update()
        {
            progressInCurrentLoadedLevelSo.audioPlaybackTime = _audioSource.time;
        }

        private void OnDisable()
        {
            menuOnLevelInPreviewChangeSo.LevelPreviewChangeEvent -= OnLevelPreviewChange;
            sceneLoadStateEvent.OnSceneBeginToLoad -= OnSceneBeginToLoad;
            menuOnLevelInPreviewChangeSo.BeginLevelPreviewChangeEvent -= MenuOnLevelInPreviewChangeSoOnBeginLevelPreviewChangeEvent;
            progressInCurrentLoadedLevelSo.OnCheckPointTrigger -= OnCheckPointTrigger;
        }

        private void OnDestroy()
        {
            levelRegistrySo.Unregister(this);
        }

        private void OnSceneBeginToLoad()
        {
            StopSound();
        }
        
        
        private void InsertClip(AudioClip clip)
        {
            _audioSource.clip = clip;
            progressInCurrentLoadedLevelSo.audioDuration = _audioSource.clip.length;
        }

        private void PlaySound(bool delay = false)
        {
            _audioSource.DOKill();
            
            _audioSource.volume = 1;
            if(_audioSource.clip != null) _audioSource.Play();
            else{Debug.LogWarning("GlobalAudioPlayer: No clip found to play");}
        }
        

        private void StopSound(bool fading = true, float duration = 3f)
        {
            _audioSource.DOKill();
            
            if (!fading) _audioSource.Stop();
            else _audioSource.DOFade(0, duration).OnComplete(_audioSource.Stop);
        }

        public void OnLevelStart()
        {
            PlaySound();
        }

        public void OnLevelStop()
        {
            StopSound();
        }

        public void OnDead()
        {
            progressInCurrentLoadedLevelSo.playbackInAudioWhenPlayerDead = _audioSource.time;
            StopSound();
        }

        private void OnLevelPreviewChange()
        {
            if (menuOnLevelInPreviewChangeSo.levelInPreview.levelSound == null) return;
            InsertClip(menuOnLevelInPreviewChangeSo.levelInPreview.levelSound);
            if(!menuOnLevelInPreviewChangeSo.playerCurrentlyChangeLevelPreview) PlaySound(true);
        }

        private void OnCheckPointTrigger()
        {
            _audioPlaybackTimeSnapshotOnTrigger = _audioSource.time;
        }

        public void OnLevelCheckPoint()
        {
            StopSound(false);
            _audioSource.time = _audioPlaybackTimeSnapshotOnTrigger;
        }
    }
}