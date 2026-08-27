using System.Collections;
using Interfaces;
using Player.States;
using UnityEngine;

namespace Player
{ 
    /// <summary>
    /// Trail comes right behind of player.
    /// </summary>
    /// <remarks>Must add as component to the Player.prefab</remarks>
    /// <remarks>Line works as creating pre-defined cubes and moving them with some delay by order</remarks>
    [RequireComponent(typeof(StateMachine))]
    public class Line : MonoBehaviour, IOnRestart, IOnCheckPoint, ILevelState, IOnDead, IVictory, ILevelRegistryUser
    {
        //CloneCube section for line effect
        [Header("Pool Settings")]
        [Tooltip("The prefab used for the trail segments.")]
        [SerializeField] private GameObject cloneCube;
        private GameObject[] _clonedCubes;
        [SerializeField] private int clonedCubesCount;
        private Transform _parentCubeClone;
        private int _currentTransformChangedCubeClone;
        private MaterialPropertyBlock _propBlock;
        
        [SerializeField] private float updateInterval;
        private WaitForSeconds _waitForSecondsCloneCube;
        
        private PositionRotationChangeCheckPointRestart _positionRotationChangeCheckPointRestart;

        private bool _playerOnGround = true;
        private ILevelRegistry _levelRegistry;

        private Coroutine _cloneCubesCoroutine;

        private void OnEnable()
        {
            // This function prevent Zig-Zag, cropped line when pressed to change direction.
            // It's directly put cube where player press
            PlayerMoveState.PlayerPressed += ChangeNextCloneCubePositionOnGoalPosition;

            //Stop Line drawing when player not on the ground (e.g. On air, Dead etc.)
            GroundStateChecker.OnGroundChange += OnGroundStateChange;
            
            /*Without these events, cubes can be misplaced on Restart/CheckPoint since,
            player position didn't update yet*/
            if (TryGetComponent(out _positionRotationChangeCheckPointRestart))
            {
                _positionRotationChangeCheckPointRestart.OnPlayerCheckPointComplete += Reset;
                _positionRotationChangeCheckPointRestart.OnPlayerRestartComplete += Reset;
                return;
            }
            Debug.LogWarning("Line: PositionRotationChangeCheckPointRestart not found. CloneCubes can be misaligned at CheckPoint and Restart.");
        }
        
        public void LevelRegistrySoSetter(ILevelRegistry levelRegistry)
        {
            _levelRegistry = levelRegistry;
        }

        private void Awake()
        {
            //Creating a new GameObject to add CloneCubes under it
            _parentCubeClone = new GameObject("CloneCubesParent").transform;
            
            _levelRegistry.Register(this);
            _propBlock = new MaterialPropertyBlock();
            
            _waitForSecondsCloneCube = new WaitForSeconds(updateInterval);
            InitializeCloneCubes();
        }

        private void OnDisable()
        {
            PlayerMoveState.PlayerPressed -= ChangeNextCloneCubePositionOnGoalPosition;
            GroundStateChecker.OnGroundChange -= OnGroundStateChange;

            if (_positionRotationChangeCheckPointRestart == null) return;
            _positionRotationChangeCheckPointRestart.OnPlayerCheckPointComplete -= Reset;
            _positionRotationChangeCheckPointRestart.OnPlayerRestartComplete -= Reset;
        }

        private void OnDestroy()
        {
            _levelRegistry.Unregister(this);
        }

        private void InitializeCloneCubes()
        {
            //Clone cubes create by amount of clonedCubesCount
            _clonedCubes = new GameObject[clonedCubesCount];
            for (int i = 0; i < _clonedCubes.Length; i++)
            {
                _clonedCubes[i] = Instantiate(cloneCube, _parentCubeClone);
                _clonedCubes[i].transform.position = transform.position;
                
                //Breaking SRP batch by overriding with an empty Property Block.
                //_clonedCubes[i].GetComponent<Renderer>().SetPropertyBlock(_propBlock);
            }
            
            //TODO: Move property block applying to player to other location
            //Also applying same Property Block to player cube itself
            //GetComponent<Renderer>().SetPropertyBlock(_propBlock);
        }

        /// <summary>
        /// Update _clonedCubes position based on player's transformation at <see cref="_waitForSecondsCloneCube"/> seconds
        /// </summary>
        IEnumerator TransformCloneCubes()
        {
            while (true)
            {
                ChangeNextCloneCubePositionOnGoalPosition();
                yield return _waitForSecondsCloneCube;
            }
        }

        private void StartTransformCloneCubes()
        {
            _cloneCubesCoroutine = StartCoroutine(TransformCloneCubes());
        }

        private void StopTransformCloneCubes()
        {
            if (_cloneCubesCoroutine != null)
            {
                StopCoroutine(_cloneCubesCoroutine);
            }
        }
        
        private void ChangeNextCloneCubePositionOnGoalPosition()
        {
            if (!_playerOnGround) return;
            //TODO: Performance may be increase by storing _clonedCubes transforms into an Array and referencing to that Array instead when chancing transform.position 
            _clonedCubes[_currentTransformChangedCubeClone].transform.position = transform.position;
            _currentTransformChangedCubeClone++;
            if (_currentTransformChangedCubeClone == _clonedCubes.Length)
            {
                _currentTransformChangedCubeClone = 0;
            }
        }

        private void OnGroundStateChange(bool groundState)
        {
            _playerOnGround = groundState;
        }

        public void OnLevelStart()
        {
            StartTransformCloneCubes();
        }

        public void OnLevelStop(){/*It will be empty*/}
        
        public void OnDead()
        {
            StopTransformCloneCubes();
        }
        
        public void OnVictory()
        {
            StopTransformCloneCubes();
        }
        
        public void OnLevelRestart()
        {
            if (_positionRotationChangeCheckPointRestart == null)
            {
                Reset();
            }
        }

        public void OnLevelCheckPoint()
        {
            if (_positionRotationChangeCheckPointRestart == null)
            {
                Reset();
            }
        }
        
        /// <summary>
        /// Functions are usually require same modify for both <see cref="IOnRestart"/> and <see cref="IOnCheckPoint"/>>
        /// </summary>
        private void Reset()
        {
            //Resetting all clone cubes transformation to where player is
            for (int i = 0; i < _clonedCubes.Length; i++)
            {
                _clonedCubes[i].transform.position = transform.position;
            }
        }
    }
}