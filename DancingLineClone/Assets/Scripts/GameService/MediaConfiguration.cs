using UnityEngine.Scripting;

[Preserve]
public class MediaConfiguration
{
    // 0.0 is the very first frame, 1.0 is the very last frame.
    [Preserve]
    public float VideoFrameNormalized { get; set; } = 0.5f;

    [Preserve]
    public bool SendDebug { get; set; } = false;
}