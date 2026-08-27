using System;
using System.Collections.Generic;
using DataContainer;
using DG.Tweening;
using Interfaces;
using UnityEngine;

namespace Core
{
    public class LevelObjectAnimationController : MonoBehaviour, ILevelRegistryUser, IOnDead, ILevelState, IOnRestart, IOnCheckPoint
    {
        public enum AnimationType
        {
            Move,
            Rotate,
            Scale,
            Shake
        }

        public enum AnimationLifetime
        {
            OneTime,
            Repeat,
            RepeatUntilOutsideOfDistance,
            Custom
        }

        public enum TriggerType
        {
            CollisionTrigger,
            WithPrevious,
            TriggerWhenInsideOfDistance,
            AfterPrevious,
            SoundtrackTimeline
        }
        
        [Serializable]
        public struct AnimationData
        {
            [Header("Object Animation")]
            public Transform gameObjectTransform;
            [Header("Type")]
            public AnimationType type;
            [Header("Target")]
            [HideInInspector] public Vector3 beginning;
            public Vector3 targetTransform;
            [Header("AnimationLifeTime")]
            public AnimationLifetime lifetime;
            public float duration;
            public int repeatCount;
            public float distanceAreaStop;
            [Header("Trigger")]
            public TriggerType triggerType;
            public float triggerTypeValue;
            [Space(10)] public float delay;
            [HideInInspector] public bool delayFinished;
            [HideInInspector] public bool proceed; //It's need for if this Animation Data proceed next sequences (Like play WithPrevious etc.)
        }

        public struct DistanceCheck
        {
            public ushort animationIndex;
            public float distanceArea;
        }

        public struct AnimationDelay
        {
            public ushort animationIndex;
            public float delay;
            public bool full;
        }
        
        [SerializeField] private AnimationData[] animationData;
        [SerializeField] private LevelObjectAnimationSo levelObjectAnimationSo;
        [SerializeField] private ProgressInCurrentLoadedLevelSo progressInCurrentLoadedLevelSo;
        
        //Value written separate for cache-hit
        private Sequence[] _animationSequences;
        private DistanceCheck[] _animationTriggerTypesDistance;
        private DistanceCheck[] _animationLifeTimeDistance;
        
        //Delay
        private AnimationDelay[] _animationDelay;
        private AnimationDelay[] _animationDelaySnapshot;
        private ushort _animationDelayArrayLength;
        private ushort _animationDelaySnapshotLength;
        [SerializeField] private ushort animationDelayLengthLimit = 200;
        
        //PlaybackTime
        private float _songPlaybackTime;
        private float _songPlaybackTimeSnapshotOnCheckpointTrigger;
        private ushort[] _animationTriggerTypeSoundtrackTimelineIndexList;
        [SerializeField] private ushort animationTriggerTypeSoundtrackTimeLimit = 500;
        private ushort _animationTriggerTypeSoundtrackTimeArrayLength;
        private ushort _currentSoundtrackTimelineIndexWaiting;
        private ushort _currentSoundtrackTimelineIndexWaitingSnapshotOnCheckpointTrigger;


        private bool _beginAnimation;
        
        private ILevelRegistry _levelRegistry;

        private void Awake()
        {
            _animationDelay = new AnimationDelay[animationDelayLengthLimit];
            _animationDelaySnapshot = new AnimationDelay[animationDelayLengthLimit];
            _animationTriggerTypeSoundtrackTimelineIndexList = new ushort[animationTriggerTypeSoundtrackTimeLimit];
            
            levelObjectAnimationSo.OnAnimationCollisionTrigger += PlayAnimation;
            progressInCurrentLoadedLevelSo.OnCheckPointTrigger += OnCheckPointTrigger;
            _levelRegistry.Register(this);

            AnimationSequencePrepare();
        }

        private void AnimationSequencePrepare()
        {
            DOTween.Init(true, true, LogBehaviour.ErrorsOnly).SetCapacity(700, 700);
            _animationSequences = new Sequence[animationData.Length];
            
            List<ushort> tempListOfTriggerTypeDistance = new List<ushort>();
            List<ushort> tempListOfLifeTimeDistance = new List<ushort>();
            
            for (ushort i = 0; i < animationData.Length; i++)
            { 
                //Getting direct reference
                ref AnimationData data = ref animationData[i];
                ref Sequence sequence = ref _animationSequences[i];
                
                //Before we begin, we check the null
                if (data.gameObjectTransform == null)
                {
                    Debug.LogError($"{name}: gameObjectTransform is null at index {i}. Disabling the script");
                    enabled = false;
                    return;
                }
        
                sequence = DOTween.Sequence().Pause();
                sequence.SetAutoKill(false);
        
                switch (data.type)
                {
                    case AnimationType.Move:
                        data.beginning = data.gameObjectTransform.localPosition;
                        sequence.Append(data.gameObjectTransform.DOLocalMove(data.targetTransform, data.duration).SetEase(Ease.Linear));
                        break;
        
                    case AnimationType.Rotate:
                        data.beginning = data.gameObjectTransform.localEulerAngles;
                        sequence.Append(data.gameObjectTransform.DOLocalRotate(data.targetTransform, data.duration).SetEase(Ease.Linear));
                        break;
        
                    case AnimationType.Scale:
                        data.beginning = data.gameObjectTransform.localScale;
                        sequence.Append(data.gameObjectTransform.DOScale(data.targetTransform, data.duration).SetEase(Ease.Linear));
                        break;
                    case AnimationType.Shake:
                        sequence.Append(data.gameObjectTransform.DOShakePosition(data.duration));
                        break;
                }

                switch (data.lifetime)
                {
                    case AnimationLifetime.OneTime:
                        break;
                    case AnimationLifetime.Repeat:
                        sequence.SetLoops(-1, LoopType.Yoyo);
                        break;
                    case AnimationLifetime.Custom:
                        sequence.SetLoops(data.repeatCount, LoopType.Yoyo);
                        break;
                    case AnimationLifetime.RepeatUntilOutsideOfDistance:
                        tempListOfLifeTimeDistance.Add(i);
                        break;
                }

                switch (data.triggerType)
                {
                    case TriggerType.TriggerWhenInsideOfDistance:
                        tempListOfTriggerTypeDistance.Add(i);
                        break;
                    case TriggerType.SoundtrackTimeline:
                        if (animationTriggerTypeSoundtrackTimeLimit <= _animationTriggerTypeSoundtrackTimeArrayLength)
                        {
                            Debug.LogWarning($"{name}: Limit for {nameof(animationTriggerTypeSoundtrackTimeLimit)} is {animationTriggerTypeSoundtrackTimeLimit}, " +
                                             "but it exceed it. No more animation based on time will not player. " +
                                             $"\n(TIP): Increase the limit {nameof(animationTriggerTypeSoundtrackTimeLimit)} " +
                                             "or decrease the amount of animation those are TriggerType is SoundtrackTimeline");
                            break;
                        }
                        _animationTriggerTypeSoundtrackTimelineIndexList[_animationTriggerTypeSoundtrackTimeArrayLength] = i;
                        _animationTriggerTypeSoundtrackTimeArrayLength++;
                        break;
                }
                
            }

            //Distance caching to array separately for Trigger Type
            _animationTriggerTypesDistance = new DistanceCheck[tempListOfTriggerTypeDistance.Count];
            for (int i = 0; i < tempListOfTriggerTypeDistance.Count; i++)
            {
                _animationTriggerTypesDistance[i].animationIndex = tempListOfTriggerTypeDistance[i];
                _animationTriggerTypesDistance[i].distanceArea = animationData[_animationTriggerTypesDistance[i].animationIndex].triggerTypeValue;
            }
            
            //Distance caching to array separately for Life Time
            _animationLifeTimeDistance = new DistanceCheck[tempListOfLifeTimeDistance.Count];
            for (int i = 0; i < tempListOfLifeTimeDistance.Count; i++)
            {
                _animationLifeTimeDistance[i].animationIndex = tempListOfLifeTimeDistance[i];
                _animationLifeTimeDistance[i].distanceArea = animationData[_animationLifeTimeDistance[i].animationIndex].distanceAreaStop;
            }
            
            //Sorting arrays in incremental order to prevent unnecessary if check to find index in update to reduce CPU cycle
            // (WRITTEN BY GEMINI AI) Sort the index pointers based on the underlying target values inside the structural dataset
            Array.Sort(_animationTriggerTypeSoundtrackTimelineIndexList, 0, _animationTriggerTypeSoundtrackTimeArrayLength, 
                Comparer<ushort>.Create((x, y) => animationData[x].triggerTypeValue.CompareTo(animationData[y].triggerTypeValue)));

        }
        
        
        private void Update()
        {
            if (_beginAnimation)
            {
                #region Delay Check-Up section
                if (_animationDelayArrayLength > 0)
                {
                    for (int i = _animationDelayArrayLength - 1; i >= 0; i--)
                    {
                        _animationDelay[i].delay -= Time.deltaTime;
            
                        if (_animationDelay[i].delay <= 0f)
                        {
                            ushort animIndex = _animationDelay[i].animationIndex;
                
                            ref AnimationData data = ref animationData[animIndex];
                        
                            data.delayFinished = true;
                
                            PlayAnimation(animIndex);
                            RemoveFromAnimationDelay((ushort)i);
                        }
                    }
                }
                #endregion

                
                #region SoundtrackTimeline Check-Up section
                /*Instead of grabbing song playback timeline via the SO from GlobalAudioPlayer
                (Which is C++ object wrapped with C#), we manually calculate it inside C# since 
                we know when song begin (LevelStart) and stop (OnDead)*/
                _songPlaybackTime += Time.deltaTime;
                while (_currentSoundtrackTimelineIndexWaiting < _animationTriggerTypeSoundtrackTimeArrayLength && 
                       animationData[_animationTriggerTypeSoundtrackTimelineIndexList[_currentSoundtrackTimelineIndexWaiting]].triggerTypeValue <= _songPlaybackTime)
                {
                    PlayAnimation(_animationTriggerTypeSoundtrackTimelineIndexList[_currentSoundtrackTimelineIndexWaiting]);
                    _currentSoundtrackTimelineIndexWaiting++;
                }
                #endregion
                
            }
        }

        private void OnDestroy()
        {
            levelObjectAnimationSo.OnAnimationCollisionTrigger -= PlayAnimation;
            progressInCurrentLoadedLevelSo.OnCheckPointTrigger -= OnCheckPointTrigger;
            _levelRegistry.Unregister(this);
        }

        /// <summary>
        /// Plays animation from based on <see cref="animationData"/>
        /// </summary>
        /// <param name="index">Animation Index from <see cref="animationData"/></param>
        private void PlayAnimation(ushort index)
        {
            if (index >= animationData.Length)
            {
                Debug.LogWarning("AnimationData index out of range: " + index);
                return;
            }

            ref AnimationData rootData = ref animationData[index];

            // If the triggered animation has a valid delay remaining, we add to the list
            if (rootData.delay > 0f && !rootData.delayFinished)
            {
                AddToAnimationDelay(index, rootData.delay);
                ProcessTheAnimation(index);
            }
            else
            {
                // No delay present: Fire the sequence
                _animationSequences[index].Restart();
                ProcessTheAnimation(index);
                ResetAnimationCell(index);
            }
        }

        private void ProcessTheAnimation(ushort index)
        {
            ref AnimationData rootData = ref animationData[index];
            //If an animation with delayed already execute the code below, this prevents to execute again cause double animation add
            if (rootData.proceed)
            {
                return;
            }
            
            //We return if trigger type is "WithPrevious", because it means in previous animations we already add it (Look "WITH PREVIOUS" below)
            if (rootData.triggerType == TriggerType.WithPrevious)
            {
                return;
            }

            float currentDelayCount = rootData.delay;
                
            // ==============
            // WITH PREVIOUS
            // ==============
            // Scan elements down the line to find linked sequences
            for (int i = index + 1; i < animationData.Length; i++)
            {
                ref AnimationData nextData = ref animationData[i];

                // If the next item is bound to the previous trigger layout, we add it
                if (nextData.triggerType == TriggerType.WithPrevious)
                {
                    if (nextData.delay > 0f && !nextData.delayFinished)
                    {
                        currentDelayCount += nextData.delay;
                        AddToAnimationDelay((ushort)i, currentDelayCount);
                    }
                    else
                    {
                        _animationSequences[i].Restart();
                    }
                }
                else
                {
                    // If, the next is not "With Previous" trigger, we stop scanning forward for this animation (rootData)
                    break;
                }
            }
            
            rootData.proceed = true;
        }

        //Resets the states of animations those are begin after they called to play
        private void ResetAnimationCell(ushort index)
        {
            animationData[index].proceed = false;
            animationData[index].delayFinished = false;
        }

        private void AddToAnimationDelay(ushort index, float delay)
        {
            if (_animationDelayArrayLength < animationDelayLengthLimit)
            {
                _animationDelayArrayLength++;
                _animationDelay[_animationDelayArrayLength - 1].animationIndex = index;
                _animationDelay[_animationDelayArrayLength - 1].delay = delay;
                _animationDelay[_animationDelayArrayLength - 1].full = true;
            }
            else
            {
                Debug.LogWarning("Can't add new AnimationDelay, it's pass the limit");
            }
        }

        private void RemoveFromAnimationDelay(ushort indexInAnimationDelay)
        {
            if (_animationDelay[indexInAnimationDelay].full)
            {
                _animationDelay[indexInAnimationDelay].animationIndex = _animationDelay[_animationDelayArrayLength - 1].animationIndex;
                _animationDelay[indexInAnimationDelay].delay = _animationDelay[_animationDelayArrayLength - 1].delay;

                _animationDelay[_animationDelayArrayLength - 1].full = false;
                _animationDelayArrayLength--;
            }
            else
            {
                Debug.LogWarning("Slot is empty, don't have animation to remove");
            }
        }

        public void LevelRegistrySoSetter(ILevelRegistry levelRegistry)
        {
            _levelRegistry = levelRegistry;
        }

        public void OnDead()
        {
            _beginAnimation = false;
        }

        public void OnCheckPointTrigger()
        {
            #region Delay
            _animationDelaySnapshotLength = _animationDelayArrayLength;
    
            // Copy only the active slots into the snapshot
            for (int i = 0; i < _animationDelayArrayLength; i++)
            {
                _animationDelaySnapshot[i] = _animationDelay[i];
            }
            #endregion

            #region PlaybackTimeline

            _songPlaybackTimeSnapshotOnCheckpointTrigger = _songPlaybackTime;
            _currentSoundtrackTimelineIndexWaitingSnapshotOnCheckpointTrigger = _currentSoundtrackTimelineIndexWaiting;

            #endregion
        }

        public void OnLevelStart()
        {
            _beginAnimation = true;
        }

        public void OnLevelStop(){/*IT WILL BE EMPTY*/}
        public void OnLevelRestart()
        {
            #region Delay
            _animationDelayArrayLength = 0; 

            // You still need this loop to let old animations trigger again on the new run
            for (ushort i = 0; i < animationData.Length; i++)
            {
                animationData[i].delayFinished = false;
                animationData[i].proceed = false;
            }
            #endregion

            #region PlaybackTimeline

            _songPlaybackTime = 0;
            _songPlaybackTimeSnapshotOnCheckpointTrigger = 0;
            _currentSoundtrackTimelineIndexWaitingSnapshotOnCheckpointTrigger = 0;
            _currentSoundtrackTimelineIndexWaiting = 0;
            
            #endregion

            ResetAllAnimationSequences();
        }

        public void OnLevelCheckPoint()
        {
            #region Delay
            _animationDelayArrayLength = _animationDelaySnapshotLength;
    
            // Restore only the slots that were active at the checkpoint
            for (int i = 0; i < _animationDelaySnapshotLength; i++)
            {
                _animationDelay[i] = _animationDelaySnapshot[i];
            }
            #endregion

            #region PlaybackTimeline
            
            _songPlaybackTime = _songPlaybackTimeSnapshotOnCheckpointTrigger;
            _currentSoundtrackTimelineIndexWaiting = _currentSoundtrackTimelineIndexWaitingSnapshotOnCheckpointTrigger;

            #endregion
            
            
            //Cache:
            ushort trueAnimIndex;

            for (ushort i = 0; i < _animationTriggerTypeSoundtrackTimeArrayLength; i++)
            {
                trueAnimIndex = _animationTriggerTypeSoundtrackTimelineIndexList[i];
                
                if (i < _currentSoundtrackTimelineIndexWaiting)
                {
                    _animationSequences[trueAnimIndex].Complete();
                }
                else
                {
                    ResetAnimationSequences(trueAnimIndex);
                }
        
                animationData[trueAnimIndex].proceed = false;
                animationData[trueAnimIndex].delayFinished = false;
            }
        }

        private void ResetAllAnimationSequences()
        {
            for (ushort i = 0; i < _animationSequences.Length; i++)
            {
                ResetAnimationSequences(i);
            }
        }

        private void ResetAnimationSequences(ushort index)
        {
            _animationSequences[index].Complete();
            _animationSequences[index].Rewind();
        }
    }
}