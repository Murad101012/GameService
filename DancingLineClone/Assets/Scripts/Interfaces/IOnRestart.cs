namespace Interfaces
{
    public interface IOnRestart
    {
        /// <summary>
        ///It's for restarting all transform of an object when such as Position, Rotation to
        /// where at the where it's set on beginning of level before begin to play it
        /// </summary>
        /// <remarks> Must use <see cref="ILevelRegistryUser"/></remarks>
        void OnLevelRestart();
    }
}