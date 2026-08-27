using UnityEngine;

namespace Interfaces
{
    /// <summary>
    /// When player get into collision that have <see cref="CheckpointTrigger"/> script on it, it will get checkpoint information
    /// </summary>
    /// <remarks> To get informed, when player touch collision, look ProgressInCurrentLoadedLevelSo.OnCheckPointTrigger under interfaces</remarks>
    public interface ICheckPointReceiver
    {
        void CheckPointReceive(Transform transformPlayer);
    }
}