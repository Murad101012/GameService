namespace PhonesBridge.Services.Definitions;

/// <summary>
/// A user-defined automated "Action". Currently only "fetch" actions exist,
/// which recursively download a selected folder's contents into the offline cache.
/// Config flows: see AGENTS.md "Config flow" for the global config sync — action
/// overrides below are PC-side only and are passed to the phone explicitly at
/// fetch time (frame/quality overrides, transcode bitrate/caps args).
/// </summary>
public class ActionEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>User-facing name. Defaults to the action type when empty.</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Action type. "fetch" recursively downloads folders ("Fetch the folder");
    /// "fetchFiles" downloads an explicit list of files ("Fetch the Files").
    /// </summary>
    public string Type { get; set; } = "fetch";

    /// <summary>Storage-relative folder paths to fetch (e.g. "DCIM", "DCIM/Camera"). Only used by Type "fetch".</summary>
    public List<string> Folders { get; set; } = new();

    /// <summary>Explicit storage-relative file paths to fetch. Only used by Type "fetchFiles".</summary>
    public List<string> Files { get; set; } = new();

    /// <summary>
    /// For Type "fetchFiles": how videos are handled — "thumbnail" (Video
    /// Thumbnail-only) or "actual" (Video Actual, full file). Images always fetch
    /// a thumbnail; audio/other files always fetch their full bytes.
    /// </summary>
    public string VideoFetchType { get; set; } = "thumbnail";

    /// <summary>
    /// Optional binding to a single device (by device ID). Empty = runs against any
    /// device. Actions created from "Explore Offline" are bound to that device.
    /// </summary>
    public string? DeviceId { get; set; }

    /// <summary>
    /// How the action selects its target device in the Actions wizard:
    /// "none" = unbound (manual run picks any connected device / on-connect runs for
    /// every new device), "all" = fires on every connected device at once,
    /// "device" = bound to a single DeviceId (which may currently be offline).
    /// </summary>
    public string DeviceTarget { get; set; } = "none";

    /// <summary>What to download: "images", "videoThumb", "videoActual", "files", "audio". Only used by Type "fetch".</summary>
    public List<string> ContentTypes { get; set; } = new();

    /// <summary>"manual" (user calls it) or "onconnect" (runs automatically when a new device connects).</summary>
    public string Trigger { get; set; } = "manual";

    /// <summary>Whether this action may run.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>true = inherit the device's current config; false = use Overrides.</summary>
    public bool UseGlobalConfig { get; set; } = true;

    /// <summary>
    /// How many image (thumbnail) downloads run at once: "default" (5 per the
    /// original behaviour), "custom" (ImageConcurrency), or "unlimited".
    /// </summary>
    public string ImageConcurrencyMode { get; set; } = "default";
    public int ImageConcurrency { get; set; } = 5;

    /// <summary>
    /// How many heavy downloads (video thumbs, full videos, audio, files) run at
    /// once: "default" (1 per the original behaviour), "custom" (HeavyConcurrency),
    /// or "unlimited".
    /// </summary>
    public string HeavyConcurrencyMode { get; set; } = "default";
    public int HeavyConcurrency { get; set; } = 1;

    public ActionConfig Overrides { get; set; } = new();
}

public class ActionConfig
{
    public float VideoFrameNormalized { get; set; } = 0.10f;
    public float ThumbnailQuality { get; set; } = 0.00f;
    public float VideoBitrateMultiplier { get; set; } = 0.50f;
    public float VideoResolutionMultiplier { get; set; } = 1.00f;
    public bool BitrateCappingEnabled { get; set; } = true;
    public float BitrateCapMbps { get; set; } = 3.0f;
    public bool ResolutionCeilingEnabled { get; set; } = true;
    public int ResolutionCeilingPx { get; set; } = 720;
}

public record EnabledRequest(bool Enabled);

public record RunActionRequest(string? Device, bool? AllDevices = null);