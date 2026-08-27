using System.Collections.Generic;
using Interfaces;
using UnityEngine;

namespace DataContainer
{
    /// <summary>
    /// Centralized register point for interfaces such as <see cref="IOnRestart"/>, <see cref="IOnDead"/> and etc.
    /// (look for the following List for all interfaces).
    /// </summary>
    /// <remarks>For scripts be able to find <see cref="LevelRegistrySo"/> to register/unregister themselves,
    /// please look into <see cref="ILevelRegistryUser"/> and <see cref="SceneILevelRegistryUserBootstrapper"/></remarks>
    [CreateAssetMenu(menuName = "ScriptableObjects/LevelRegistry")]
    public class LevelRegistrySo : ScriptableObject, ILevelRegistry
    {
        private List<ILevelState> _levelStates = new();
        private List<IOnCheckPoint> _onCheckPoints = new();
        private List<IOnRestart> _onRestarts = new();
        private List<IVictory> _victories = new();
        private List<IOnDead> _deads = new();
        
        private void OnEnable()
        {
            _levelStates.Clear();
            _onCheckPoints.Clear();
            _onRestarts.Clear();
        }

        //TODO: Add Safe check to be sure not a same script register/unregister itself more than once
        public void Register<T>(T entity)
        {
            if (entity is ILevelState state) _levelStates.Add(state);
            if (entity is IOnCheckPoint checkPoint) _onCheckPoints.Add(checkPoint);
            if (entity is IOnRestart restart) _onRestarts.Add(restart);
            if (entity is IVictory victory) _victories.Add(victory);
            if (entity is IOnDead dead) _deads.Add(dead);
        }

        public void Unregister<T>(T entity)
        {
            if (entity is ILevelState state) _levelStates.Remove(state);
            if (entity is IOnCheckPoint checkPoint) _onCheckPoints.Remove(checkPoint);
            if (entity is IOnRestart restart) _onRestarts.Remove(restart);
            if (entity is IVictory victory) _victories.Remove(victory);
            if (entity is IOnDead dead) _deads.Remove(dead);
        }
        
        //In here we can trigger all the interfaces we want those scrips are registered themselves

        #region ILevelState Methods
        public void TriggerStartILevelState()
        {
            for (int i = 0; i < _levelStates.Count; i++)
                _levelStates[i].OnLevelStart();
        }

        public void TriggerStopILevelState()
        {
            for (int i = 0; i < _levelStates.Count; i++)
                _levelStates[i].OnLevelStop();
        }
        #endregion

        #region IOnCheckPoint Methods
        public void TriggerOnCheckPoint()
        {
            for (int i = 0; i < _onCheckPoints.Count; i++)
            {
                _onCheckPoints[i].OnLevelCheckPoint();
            }
        }
        #endregion

        #region IOnRestart Methods
        public void TriggerOnRestart()
        {
            for (int i = 0; i < _onRestarts.Count; i++)
            {
                _onRestarts[i].OnLevelRestart();
            }
        }
        #endregion

        #region IVictory Methods

        public void TriggerOnVictory()
        {
            for (int i = 0; i < _victories.Count; i++)
            {
                _victories[i].OnVictory();
            }
        }
        #endregion
        
        #region IVictory Methods

        public void TriggerOnDead()
        {
            for (int i = 0; i < _deads.Count; i++)
            {
                _deads[i].OnDead();
            }
        }
        #endregion
        
    }
}

