namespace Interfaces
{
    /// <summary>
    /// Functions those execute some logic when player dead
    /// </summary>
    /// <remarks> Must use <see cref="ILevelRegistryUser"/></remarks>
    public interface IOnDead
    {
        public void OnDead();
    }
}