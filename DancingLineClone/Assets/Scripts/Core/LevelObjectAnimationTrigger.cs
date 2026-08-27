using DataContainer;
using UnityEngine;

namespace Core
{
    [RequireComponent(typeof(BoxCollider))]
    public class LevelObjectAnimationTrigger : MonoBehaviour
    {
        [SerializeField] private ushort levelObjectAnimationDataIndex;
        [SerializeField] private LevelObjectAnimationSo levelObjectAnimationSo;
        
        private void OnTriggerEnter(Collider other)
        {
            if (other.CompareTag("Player"))
            {
                levelObjectAnimationSo.PublishAnimationTrigger(levelObjectAnimationDataIndex);
            }
        }
    }
}