namespace PhonesBridge.Services.Definitions;

public record ConnectionIdToPc(string DeviceName, string? DeviceId = null, string Platform = "Android");

public record DeviceIdentity(string ConnectionId, string DeviceName, string DeviceId, string Platform = "Android");

public record UpdatePayloadDto(string TargetDevice, string VersionId, string Content, bool IsAllDevices = false, string? Mode = null, string? FileName = null,
    bool AutoSnapshot = false, string? SnapshotName = null);

public record BackupInfoDto(string VersionId, string CreatedAt, string FilePath, long SizeBytes);

public record RollbackRequestDto(string TargetDevice, string VersionId, string? ExeName = null);

public record PurgeBackupsRequestDto(string TargetDevice);

public record ServiceStateDto(string Name, int Status, string TaskName);

public record ServiceControlRequestDto(string TargetDevice, string ExeName, string Action);

public record FileNodeDto(
    string Name,
    string RelativePath,
    bool IsDirectory,
    long Length,
    long OriginalLength,
    DateTime LastWriteTimeUtc,
    long OriginalPreviewLength = 0,
    // Optional enrichment for Windows roots / shell entries. Devices that don't
    // send them simply fall back to the defaults.
    string? EntryKind = null,        // disk | usb | networkdrive | cdrom | ram | mtp | mtpfile | network
    bool Disabled = false,           // greyed-out entry (no media, locked MTP device, ...)
    string? AccessWarning = null,    // tooltip/warning shown for disabled entries
    long FreeBytes = 0,
    long TotalBytes = 0,
    // True for required folders inside the App Data root (e.g. Transcoded
    // Videos): browsable, but the UI must not offer delete for them.
    bool Protected = false
);

public record FileNodeResultDto(
    bool Exists,
    FileNodeDto CurrentNode,
    List<FileNodeDto> Children,
    int TotalFileCount = 0
);

public record SaveFolderCacheRequest(
    FileNodeDto? CurrentNode,
    List<FileNodeDto>? Children
);