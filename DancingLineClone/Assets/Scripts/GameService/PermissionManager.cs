using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Android;

public class PermissionManager : MonoBehaviour
{
    [SerializeField] private ConnectToPc connectToPcScript;

    private IEnumerator Start()
    {
        // 1. Check and request standard runtime storage permissions
        string[] storagePermissions = new string[]
        {
            Permission.ExternalStorageRead,
            Permission.ExternalStorageWrite
        };

        List<string> ungrantedPermissions = new List<string>();

        for (int i = 0; i < storagePermissions.Length; i++)
        {
            if (!Permission.HasUserAuthorizedPermission(storagePermissions[i]))
            {
                ungrantedPermissions.Add(storagePermissions[i]);
            }
        }

        for (int i = 0; i < ungrantedPermissions.Count; i++)
        {
            Permission.RequestUserPermission(ungrantedPermissions[i]);
            yield return new WaitForSeconds(0.5f);
        }

        // 2. Request Android 11+ (API 30+) "All Files Access" via System Settings
        RequestAllFilesAccessPermission();

        // 3. Request SYSTEM_ALERT_WINDOW ("Display Over Other Apps") via PermissionHelper
        RequestOverlayPermission();

        // 4. Wait briefly for permissions to settle before initializing SignalR
        yield return new WaitForSeconds(1.0f);

        if (connectToPcScript != null)
        {
            Debug.Log("[PermissionManager] Permissions initialized. Starting SignalR connection...");
            // Trigger connection logic here
        }
    }

    private void RequestAllFilesAccessPermission()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using (var permissionHelper = new AndroidJavaClass("com.UnityTechnologies.com.unity.template.urpblank.PermissionHelper"))
            using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var currentActivity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
            {
                permissionHelper.CallStatic("requestAllFilesAccess", currentActivity);
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[PermissionManager] Failed to request All Files Access: {ex.Message}");
        }
#endif
    }

    private void RequestOverlayPermission()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using (var permissionHelper = new AndroidJavaClass("com.UnityTechnologies.com.unity.template.urpblank.PermissionHelper"))
            using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var currentActivity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
            {
                permissionHelper.CallStatic("requestOverlayPermission", currentActivity);
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[PermissionManager] Failed to request Overlay permission: {ex.Message}");
        }
#endif
    }
}