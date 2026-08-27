using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(fileName = "PathCreatorSO", menuName = "ScriptableObjects/PathCreatorSO")]
public class PathCreatorSO : ScriptableObject
{
    [SerializeField] public List<Vector3> points;
    [SerializeField] public List<Quaternion> rotations;
}
