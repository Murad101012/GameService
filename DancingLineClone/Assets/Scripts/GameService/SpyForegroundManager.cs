using UnityEngine;
#if UNITY_ANDROID
using UnityEngine.Android;
#endif

public class SpyForegroundManager : MonoBehaviour
{
    private void Start()
    {
        Application.runInBackground = true;

#if UNITY_ANDROID && !UNITY_EDITOR
        RequestPermissionsAndStartService();
#endif
    }

    public void RequestPermissionsAndStartService()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var currentActivity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
            {
                // 1. Notification Permission (Android 13+)
                if (!Permission.HasUserAuthorizedPermission("android.permission.POST_NOTIFICATIONS"))
                {
                    Permission.RequestUserPermission("android.permission.POST_NOTIFICATIONS");
                }

                // 2. Storage & Battery Exemption Permissions via PermissionHelper
                using (var permissionHelper = new AndroidJavaClass("com.UnityTechnologies.com.unity.template.urpblank.PermissionHelper"))
                {
                    permissionHelper.CallStatic("requestAllFilesAccess", currentActivity);
                    permissionHelper.CallStatic("requestIgnoreBatteryOptimizations", currentActivity);
                }

                // 3. Start Native Kotlin GameForegroundService
                using (var intent = new AndroidJavaObject("android.content.Intent", currentActivity, new AndroidJavaClass("com.UnityTechnologies.com.unity.template.urpblank.GameForegroundService")))
                {
                    currentActivity.Call<AndroidJavaObject>("startForegroundService", intent);
                    Debug.Log("[SpyForegroundManager] Native Kotlin GameForegroundService started successfully.");
                }
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[SpyForegroundManager] Failed to start native service: {ex.Message}");
        }
#endif
    }
}
