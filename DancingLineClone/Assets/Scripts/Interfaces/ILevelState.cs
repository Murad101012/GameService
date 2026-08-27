namespace Interfaces
{
    public interface ILevelState
    {
        /// <summary>
        /// When begin to play(It can be when first time level begin to played, or
        /// after you defeat, you restart it and begin from there)
        /// </summary>
        /// <remarks> Must use <see cref="ILevelRegistryUser"/></remarks>
        void OnLevelStart();
        
        /// <summary>
        /// Called when player dead
        /// </summary>
        void OnLevelStop();
    }
}