namespace Interfaces
{
    public interface IVictory
    {
        /// <summary>
        /// Scripts execute function when player won
        /// </summary>
        /// <remarks> Must use <see cref="ILevelRegistryUser"/></remarks>
        public void OnVictory();
    }
}