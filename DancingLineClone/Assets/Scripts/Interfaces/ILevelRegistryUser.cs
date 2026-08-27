namespace Interfaces
{
    /// <summary>
    /// Helps <see cref="SceneILevelRegistryUserBootstrapper"/> inject <see cref="LevelRegistrySo"/> classes are need in runtime
    /// </summary>
    /// <example>Add this interface to script that needs to use interfaces such as <see cref="IOnRestart"/>, <see cref="IOnDead"/>.
    /// And add those scripts to the <see cref="SceneILevelRegistryUserBootstrapper.gameObjectUsesLevelRegistryUser"/></example> list
    /// or just LevelAnalyst tool to automatically add to the list. After that, in runtime Bootstrapper will inject <see cref="LevelRegistrySo"/>
    /// scripts are under the list and scripts can register themselves.
    public interface ILevelRegistryUser
    {
        void LevelRegistrySoSetter(ILevelRegistry levelRegistry);
    }
}