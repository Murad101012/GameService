using Interfaces;
using Player.States;
using UnityEngine;

namespace Player
{
    /// <summary>
    /// It's responsible for if player in be <see cref="PlayerIdleState"/>,
    /// <see cref="PlayerMoveState"/> or <see cref="PlayerDeadState"/>
    /// </summary>
    [RequireComponent(typeof(PlayerCoreLogic))]
    public class StateMachine : MonoBehaviour, IOnRestart, IOnCheckPoint, ILevelState, IOnDead, IVictory, ILevelRegistryUser
    {
        private IPlayerState _currentState;
        private PlayerCoreLogic _playerCoreLogic;
        
        //Caching to current States
        private IPlayerState _idleState;
        private IPlayerState _moveState;

        //It's public for states
        public ILevelRegistry _levelRegistry;

        private void Awake()
        {
            _levelRegistry.Register(this);

            _playerCoreLogic = GetComponent<PlayerCoreLogic>();
            
            //Initializing Caches
            _idleState = new PlayerIdleState(_playerCoreLogic);
            _moveState = new PlayerMoveState(_playerCoreLogic, _levelRegistry);
            
            //Setting player state default to PlayerMoveState
            ChangeStateIdle();
        }
        
        private void Update()
        {
            _currentState?.StateTick();
        }
        
        private void OnDestroy()
        {
            _levelRegistry.Unregister(this);
            // This ensures the static event is unsubscribed
            _currentState?.StateEnd();
        }

        private void ChangeState(IPlayerState newState)
        {
            _currentState?.StateEnd();
            _currentState = newState;
            _currentState.StateBegin();
        }

        #region StateChanges

        private void ChangeStateMove()
        {
            ChangeState(_moveState);
        }

        public void OnDead()
        {
            ChangeStateIdle();
        }
        
        private void ChangeStateIdle()
        {
            ChangeState(_idleState);
        }
        #endregion

        public void OnLevelRestart()
        {
           ChangeStateIdle();
        }

        public void OnLevelCheckPoint()
        {
            ChangeStateIdle();
        }
        
        public void OnVictory()
        {
            ChangeStateIdle();
        }

        public void OnLevelStart()
        {
            ChangeStateMove();
        }

        public void OnLevelStop() {/*EMPTY*/}
        
        public void LevelRegistrySoSetter(ILevelRegistry levelRegistry)
        {
            _levelRegistry = levelRegistry;
        }
    }
}