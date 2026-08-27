namespace Interfaces
{
    public interface IPlayerState
    {
        void StateBegin();
        void StateTick();
        void StateEnd();
    }
}