using UnityEngine;

public class UnblockFPS : MonoBehaviour
{
    private void Awake()
    {
        // 1. VSync must be disabled, otherwise it overrides targetFrameRate
        QualitySettings.vSyncCount = 0;

        // 2. Set target frame rate (-1 removes the cap entirely)
        // You can also set a specific high cap like 120 or 144 if preferred
        Application.targetFrameRate = 60;

        // 3. Optional: Keep screen awake during gameplay
        Screen.sleepTimeout = SleepTimeout.NeverSleep;
    }
}