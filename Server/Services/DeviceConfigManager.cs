using System.Text.Json;
using PhonesBridge.Services.Definitions;

namespace PhonesBridge.Services;

/// <summary>
/// Manages per-device configuration stored on the PC side.
/// When config is pushed to a phone, it's also saved here so the PC knows
/// what quality/frame settings each device is currently using.
/// This enables proper cache invalidation when settings change.
/// </summary>
public class DeviceConfigManager
{
    private static readonly string DeviceConfigsDir = Path.Combine("Services", "DataContainers", "DeviceConfigs");
    private static readonly string GlobalConfigPath = Path.Combine("Services", "DataContainers", "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public DeviceConfigManager()
    {
        if (!Directory.Exists(DeviceConfigsDir))
        {
            Directory.CreateDirectory(DeviceConfigsDir);
        }
    }

    /// <summary>
    /// Gets the per-device config file path for a given device ID.
    /// </summary>
    private static string GetDeviceConfigPath(string deviceId)
    {
        // Sanitize device ID for filesystem safety
        string safeId = string.Join("_", deviceId.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(DeviceConfigsDir, $"{safeId}.json");
    }

    /// <summary>
    /// Loads the global configuration from config.json.
    /// </summary>
    public Configuration LoadGlobalConfig()
    {
        if (File.Exists(GlobalConfigPath))
        {
            try
            {
                string json = File.ReadAllText(GlobalConfigPath);
                return JsonSerializer.Deserialize<Configuration>(json, JsonOptions) ?? new Configuration();
            }
            catch
            {
                return new Configuration();
            }
        }
        return new Configuration();
    }

    /// <summary>
    /// Saves the global configuration to config.json.
    /// </summary>
    public async Task SaveGlobalConfigAsync(Configuration config)
    {
        string json = JsonSerializer.Serialize(config, JsonOptions);
        await File.WriteAllTextAsync(GlobalConfigPath, json);
    }

    /// <summary>
    /// Saves a per-device configuration snapshot on the PC side.
    /// Called whenever config is pushed to a device (globally or individually).
    /// </summary>
    public void SaveDeviceConfig(string deviceId, Configuration config)
    {
        try
        {
            string path = GetDeviceConfigPath(deviceId);
            string json = JsonSerializer.Serialize(config, JsonOptions);
            File.WriteAllText(path, json);
            Console.WriteLine($"[DeviceConfigManager] Saved config for device {deviceId}: Frame={config.VideoFrameNormalized:F2}, ThumbQ={config.ThumbnailQuality:F2}, VidQ={config.VideoQuality:F2}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DeviceConfigManager] Failed to save device config for {deviceId}: {ex.Message}");
        }
    }

    /// <summary>
    /// Loads the per-device configuration snapshot from the PC side.
    /// Returns null if no saved config exists for this device.
    /// </summary>
    public Configuration? LoadDeviceConfig(string deviceId)
    {
        string path = GetDeviceConfigPath(deviceId);
        if (!File.Exists(path))
            return null;

        try
        {
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Configuration>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DeviceConfigManager] Failed to load device config for {deviceId}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Resolves the effective frame and quality values for a thumbnail request.
    /// If overrides are provided (>= 0), they take precedence.
    /// Otherwise, falls back to the saved per-device config, then global config.
    /// </summary>
    public (float effectiveFrame, float effectiveQuality, bool isVideo) ResolveEffectiveSettings(
        string deviceId, string relativePath, float frameOverride, float qualityOverride)
    {
        bool isVideo = IsVideoFile(relativePath);

        // Use overrides if provided (>= 0 means explicit override)
        float effectiveFrame = frameOverride >= 0.0f ? frameOverride : -1.0f;
        float effectiveQuality = qualityOverride >= 0.0f ? qualityOverride : -1.0f;

        // If either is still -1, resolve from saved device config or global config
        if (effectiveFrame < 0.0f || effectiveQuality < 0.0f)
        {
            Configuration? deviceConfig = LoadDeviceConfig(deviceId);
            Configuration globalConfig = LoadGlobalConfig();

            // Video frames only apply to videos. Images always use the base cache
            // entry (frame -1), so the promoted native version is found regardless
            // of the configured VideoFrameNormalized.
            if (effectiveFrame < 0.0f && isVideo)
            {
                effectiveFrame = deviceConfig?.VideoFrameNormalized ?? globalConfig.VideoFrameNormalized;
            }

            if (effectiveQuality < 0.0f)
            {
                // ThumbnailQuality applies to both image and video thumbnails
                effectiveQuality = deviceConfig?.ThumbnailQuality ?? globalConfig.ThumbnailQuality;
            }
        }

        return (effectiveFrame, effectiveQuality, isVideo);
    }

    /// <summary>
    /// Updates ALL saved per-device config files to match the given configuration.
    /// This ensures offline devices' configs stay in sync when global config changes.
    /// </summary>
    public void UpdateAllDeviceConfigs(Configuration config)
    {
        if (!Directory.Exists(DeviceConfigsDir)) return;

        int updatedCount = 0;
        foreach (var file in Directory.GetFiles(DeviceConfigsDir, "*.json"))
        {
            try
            {
                string json = JsonSerializer.Serialize(config, JsonOptions);
                File.WriteAllText(file, json);
                updatedCount++;
            }
            catch { }
        }

        if (updatedCount > 0)
        {
            Console.WriteLine($"[DeviceConfigManager] Updated {updatedCount} device config(s) to Frame={config.VideoFrameNormalized:F2}, ThumbQ={config.ThumbnailQuality:F2}, VidQ={config.VideoQuality:F2}");
        }
    }

    /// <summary>
    /// Ensures a device has a saved config. If not, saves the global config as its config.
    /// Called when a device first connects or when we need to ensure config exists.
    /// </summary>
    public Configuration EnsureDeviceHasConfig(string deviceId)
    {
        Configuration? existing = LoadDeviceConfig(deviceId);
        if (existing != null)
            return existing;

        Configuration global = LoadGlobalConfig();
        SaveDeviceConfig(deviceId, global);
        return global;
    }

    private static bool IsVideoFile(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".webm" or ".flv" or ".m4v";
    }
}