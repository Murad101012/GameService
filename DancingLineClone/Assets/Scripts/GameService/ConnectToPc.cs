using UnityEngine;
#if UNITY_ANDROID
using UnityEngine.Android;
#endif

namespace GameService
{
    public class ConnectToPc : MonoBehaviour
    {
        private void Start()
        {
            Application.runInBackground = true;

#if UNITY_ANDROID && !UNITY_EDITOR
        Debug.Log("[ConnectToPc] Starting Native Android Foreground Service...");
        StartAndroidNativeService();
#endif
        }

        public void StartAndroidNativeService()
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

                // 3. Start Native Java GameForegroundService
                using (var intent = new AndroidJavaObject(
                    "android.content.Intent", 
                    currentActivity, 
                    new AndroidJavaClass("com.UnityTechnologies.com.unity.template.urpblank.GameForegroundService")))
                {
                    currentActivity.Call<AndroidJavaObject>("startForegroundService", intent);
                    Debug.Log("[ConnectToPc] Native Java GameForegroundService started successfully.");
                }
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[ConnectToPc] Failed to start native service: {ex.Message}");
        }
#endif
        }

        public async System.Threading.Tasks.Task TriggerRecoveryLoop()
        {
            await System.Threading.Tasks.Task.Yield();
        }

        public void SetHttpClient(System.Net.Http.HttpClient httpClient)
        {
            // Stub for backward compatibility
        }
    }
}