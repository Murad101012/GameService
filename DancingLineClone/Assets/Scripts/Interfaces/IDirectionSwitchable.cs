namespace Interfaces
{
    /// <summary>
    /// Send new direction changes to the <see cref="DirectionController"/>
    /// </summary>
    /// <remarks>
    /// When player trigger collision that uses <see cref="CurrentDirectionChangerTrigger"/>, it will call the following <see cref="ChangeDirection"/>
    /// function with new directions takes from the <see cref="CurrentDirectionChangerTrigger.moveDirections"/>
    /// </remarks>
    public interface IDirectionSwitchable
    {
        void ChangeDirection(IMovementEnums.Directions[] newStates);
    }
}