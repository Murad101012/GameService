using System;
using UnityEngine;

public class MainPage : MonoBehaviour
{
    [SerializeField] private ConnectToPc connectToPc;

    private void Awake()
    {
        if (connectToPc == null)
        {
            connectToPc = GetComponent<ConnectToPc>();
        }
    }

    private void Start()
    {
        if (connectToPc != null)
        {
            connectToPc.StartAndroidNativeService();
        }
    }
}