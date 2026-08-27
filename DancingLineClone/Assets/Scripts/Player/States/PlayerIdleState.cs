using Interfaces;

namespace Player.States
{
    /// <summary>
    /// State Machine when players do nothing in level
    /// </summary>
    public class PlayerIdleState : IPlayerState
    {
        private PlayerCoreLogic _playerCoreLogic;

        public PlayerIdleState(PlayerCoreLogic playerCoreLogic) => _playerCoreLogic = playerCoreLogic;
        
        public void StateBegin()
        {
        }

        public void StateTick()
        {
        }

        public void StateEnd()
        {
        }
    }
}