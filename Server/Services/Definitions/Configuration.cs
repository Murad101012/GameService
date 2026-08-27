namespace PhonesBridge.Services.Definitions
{
    public class Configuration
    {
        // 0.0 is the very first frame, 1.0 is the very last frame.
        public float VideoFrameNormalized { get; set; } = 0.1f;

        public bool SendDebug { get; set; } = true;

        // Thumbnail quality for both images and videos in the file list (0.00 = lowres, 1.00 = raw/highest quality)
        public float ThumbnailQuality { get; set; } = 0.00f;

        // Video playback quality (0.00 = 144p downscale, 1.00 = native resolution)
        // This only affects actual video playback (lightbox/download), NOT video thumbnails in the list
        public float VideoQuality { get; set; } = 0.00f;

        // Video transcode: bitrate multiplier (0.00 = minimum, 1.00 = native/original — never above)
        public float VideoBitrateMultiplier { get; set; } = 0.50f;

        public float VideoResolutionMultiplier { get; set; } = 1.00f;

        // Hard cap: bitrate ceiling (enabled by default, value in Mbps)
        public bool BitrateCappingEnabled { get; set; } = true;
        public float BitrateCapMbps { get; set; } = 3.0f;

        // Hard cap: resolution ceiling (enabled by default, max 720p)
        public bool ResolutionCeilingEnabled { get; set; } = true;
        public int ResolutionCeilingPx { get; set; } = 720;

        // Bandwidth / Speed limit in KB/s (0 = unlimited)
        public int SpeedLimitKbps { get; set; } = 0;

        // Keep the phone screen awake while the app is running (like watching a video).
        public bool KeepScreenOn { get; set; } = false;

        // Phone parallel file-processing worker cores. Values:
        // "Low" (2 cores) | "Medium" (half of cores) | "High" (75% of cores) | "Absolute" (all cores)
        public string ParallelWork { get; set; } = "Medium";

        // ── Connection routing ─────────────────────────────────────────────────
        // Per-channel data path choice: "P2P" (direct link when available) or
        // "Domain" (Cloudflare relay). Each channel falls back to the other side
        // automatically when its chosen path is unavailable, so these are
        // preferences, not hard guarantees.
        public string FileRoute   { get; set; } = "P2P";    // File browse chunks + downloads
        public string ConfigRoute { get; set; } = "Domain"; // Config push/pull
        public string DebugRoute  { get; set; } = "Domain"; // Debug stream → PC console
    }
}