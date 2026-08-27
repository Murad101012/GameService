using System;
using Interfaces;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Player.States
{
    /// <summary>
    /// Handles the player's movement logic, input-based rotation, and ground detection
    /// while the player is in the active 'Moving' state.
    /// </summary>
    /// <remarks> It's belong to </remarks>
    public class PlayerMoveState : IPlayerState, ILevelState, IOnDead
    {
        private readonly PlayerCoreLogic _playerCoreLogic;
        private ILevelRegistry _levelRegistrySo;

        private Transform _movementTransform;
        private bool _switchOrder;
        private DirectionController _directionController;
        private readonly DancingLineCloneInput _dancingLineCloneInput = new(); //Cached

        /// <remarks> This boolean duplicates from
        /// <see cref="DirectionController.CurrentDirectionsAsQuaternions"/></remarks>
        private Quaternion[] _currentDirectionsAsQuaternions;
        /// <remarks> This boolean duplicates from <see cref="GroundStateChecker._onGround"/></remarks>
        private bool _onGround = true;
        
        public PlayerMoveState(PlayerCoreLogic playerCoreLogic, ILevelRegistry registry) 
        {
            _playerCoreLogic = playerCoreLogic;
            _levelRegistrySo = registry;
        }
        
        public static event Action PlayerPressed;
        
        public void StateBegin()
        {
            _dancingLineCloneInput.Player.Enable();
            
            _movementTransform = _playerCoreLogic.transform;
            
            /*As soon as game begin script get the first Direction information since, with OnGroundChange event,
            it's miss the very first event since PlayerMoveState not MonoBehaviour and can't use OnEnable*/
            _directionController = _playerCoreLogic.GetComponent<DirectionController>();
            _currentDirectionsAsQuaternions = _directionController.CurrentDirectionsAsQuaternions;
            
            GroundStateChecker.OnGroundChange += OnGroundStateChangeUpdater;
            
            _levelRegistrySo.Register(this);
        }
        
        public void StateTick()
        {
            MovePlayerForwardZIndex();
        }
        
        /// <remarks>
        /// Moving done by only where Z axis pointing, not by chancing player's rotation.
        /// </remarks>
        //TODO: Change transform.position to RigidBody for prevent teleport and transform to Update()
        private void MovePlayerForwardZIndex()
        {
            _movementTransform.position += _movementTransform.forward * _playerCoreLogic.PlayerStatsSo.speed * Time.deltaTime;
        }
        
        /// <summary>
        /// Player cube rotation change in runtime based on Space button and state of <see cref="_onGround"/>
        /// </summary>
        private void SwitchOrder(InputAction.CallbackContext context)
        {
            if (!_onGround) return;
            //Switch between 0 and 1 currentStates with _switchOrder boolean
            if (!_switchOrder)
            {
                /*If next direction will be assigned to _movementTransform.rotation already same as current
                    direction of player going, then it will be assigned the other direction instead of based _switchOrder.
                    This problem comes when in CurrentDirectionChangerTrigger overlap one of directions those player already have.*/
                if (_movementTransform.rotation == _currentDirectionsAsQuaternions[0])
                {
                    _movementTransform.rotation = _currentDirectionsAsQuaternions[1];
                }
                else
                {
                    _switchOrder = true;
                    _movementTransform.rotation = _currentDirectionsAsQuaternions[0];
                }
            }
            else
            {
                /*Same for this also. If player already going to _currentDirectionsAsQuaternions[1],
                     assigned _currentDirectionsAsQuaternions[1] again make player doesn't change direction
                     despite player pressed, so assign _currentDirectionsAsQuaternions[0] instead*/
                if (_movementTransform.rotation == _currentDirectionsAsQuaternions[1])
                {
                    _movementTransform.rotation = _currentDirectionsAsQuaternions[0];
                }
                else
                {
                    _switchOrder = false;
                    _movementTransform.rotation = _currentDirectionsAsQuaternions[1];
                }
            }
            PlayerPressed?.Invoke();
        }
        
        private void OnDirectionChange(Quaternion[] newStatesAsQuaternions)
        {
            _currentDirectionsAsQuaternions[0] = newStatesAsQuaternions[0];
            _currentDirectionsAsQuaternions[1] = newStatesAsQuaternions[1];
        }

        private void OnGroundStateChangeUpdater(bool currentState)
        {
            _onGround = currentState;
        }
        
 
        public void StateEnd()
        {
            GroundStateChecker.OnGroundChange -= OnGroundStateChangeUpdater;
            _dancingLineCloneInput.Player.ChangeDirection.performed -= SwitchOrder;

            /*Chancing _onGround = true, because when _onGround fired true when game begins,
            PlayerMoveState didn't subs to GroundStateChecker.OnGroundChange yet and since this
            script not destroyed but cached, _onGround must be begin as true when this state
            call to use again*/
            _onGround = true;
            
            _levelRegistrySo.Unregister(this);
            _dancingLineCloneInput.Player.Disable();
        }

        public void OnDead()
        {
            _dancingLineCloneInput.Player.ChangeDirection.performed -= SwitchOrder;
        }

        public void OnLevelStart()
        {
            //New Input System
            _dancingLineCloneInput.Player.ChangeDirection.performed += SwitchOrder;
        }

        public void OnLevelStop(){/*IT WILL BE EMPTY*/}
        
    }
}