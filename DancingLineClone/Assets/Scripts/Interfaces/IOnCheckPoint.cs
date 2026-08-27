namespace Interfaces
{
    public interface IOnCheckPoint
    {
        /// <summary>
        /// When player choose continue the level from the last checkpoint, classes which use this
        /// interfaces must implement <see cref="IOnCheckPoint"/>
        /// </summary>
        /// <remarks> Must use <see cref="ILevelRegistryUser"/></remarks>
        void OnLevelCheckPoint();
    }
}