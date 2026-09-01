namespace PhonesBridge.Services.Definitions;

public class WindowsActionEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public List<string> Folders { get; set; } = new();
    // priority per folder path (1 = highest)
    public Dictionary<string,int> FolderPriority { get; set; } = new();
    public List<string> ContentTypes { get; set; } = new(); // images, videos, files, audio (no videoThumb)
    public Dictionary<string,int> ContentPriority { get; set; } = new();
    public string Trigger { get; set; } = "onconnect";
    public bool Enabled { get; set; } = true;
    // per seen device toggle
    public Dictionary<string,bool> Devices { get; set; } = new(); // deviceName -> copy?
    public string DeviceTarget { get; set; } = "all"; //_devices list
    public bool UseGlobalConfig { get; set; } = false;
    public ActionConfig Overrides { get; set; } = new()
    {
        VideoFrameNormalized = 0f, // removed for windows
        ThumbnailQuality = 1.0f,
        VideoBitrateMultiplier = 1.0f,
        VideoResolutionMultiplier = 1.0f,
        BitrateCappingEnabled = false,
        BitrateCapMbps = 3.0f,
        ResolutionCeilingEnabled = false,
        ResolutionCeilingPx = 720
    };
    public bool RemoveFromWindowsAfterPush { get; set; } = true;
    // For FetchFiles variant (priority of files)
    public List<string> Files { get; set; } = new();
    public Dictionary<string,int> FilePriority { get; set; } = new();
    public string OwnerPc { get; set; } = ""; // which PC this action belongs to
}
