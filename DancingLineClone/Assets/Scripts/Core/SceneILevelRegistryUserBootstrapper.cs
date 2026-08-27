using DataContainer;
using Interfaces;
using UnityEngine;
 
namespace Core
{
    /// <summary>
    /// Inject <see cref="LevelRegistrySo"/> those are uses <see cref="ILevelRegistryUser"/>
    /// </summary>
    [DefaultExecutionOrder(-10)]
    public class SceneILevelRegistryUserBootstrapper : MonoBehaviour
    {
        [SerializeField] private LevelRegistrySo levelRegistrySo;
        [SerializeField] private bool useNewLevelRegistrySo;
        public MonoBehaviour[] gameObjectUsesLevelRegistryUser;
        
        private void Awake()
        {
            if (useNewLevelRegistrySo)
            {
                CreateRuntimeLevelRegistrySo();
            }
            else if (levelRegistrySo == null)
            {
                CreateRuntimeLevelRegistrySo();
                Debug.LogWarning($"{name}: {nameof(levelRegistrySo)} isn't assigned. " +
                                 $"Creating runtime {nameof(LevelRegistrySo)}. Classes are using {nameof(LevelRegistrySo)}" +
                                 $" under DontDestroyOnLoad can't use newly created {nameof(LevelRegistrySo)}.");
            }
                
            for (int i = 0; i < gameObjectUsesLevelRegistryUser.Length; i++)
            {
                if (gameObjectUsesLevelRegistryUser[i] is ILevelRegistryUser user)
                {
                    //levelRegistrySo implements ILevelRegistry
                    user.LevelRegistrySoSetter(levelRegistrySo);
                }
                else
                {
                    Debug.LogWarning($"{gameObjectUsesLevelRegistryUser[i]} is not implement ILevelRegistryUser interface, skipping");
                }
            }
        }

        private void CreateRuntimeLevelRegistrySo()
        {
            levelRegistrySo = ScriptableObject.CreateInstance<LevelRegistrySo>();
        }
    }
}