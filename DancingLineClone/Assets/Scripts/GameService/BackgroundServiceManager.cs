using UnityEngine;

public class BackgroundServiceManager : MonoBehaviour
{
    private void Start()
    {
        Application.runInBackground = true;
        var spyManager = GetComponent<SpyForegroundManager>();
        if (spyManager == null)
        {
            spyManager = gameObject.AddComponent<SpyForegroundManager>();
        }
    }
}