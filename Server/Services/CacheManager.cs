using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using PhonesBridge.Services.Definitions;

namespace PhonesBridge.Services;

public class CacheManager
{
    private static readonly string CacheBaseDir = Path.Combine("Services", "DataContainers", "Cache");

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".avi", ".webm", ".3gp"
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".flac", ".aac", ".ogg", ".wma", ".m4a"
    };

    static CacheManager()
    {
        Directory.CreateDirectory(CacheBaseDir);
    }

    public static string GetDeviceCacheDir(string deviceId)
    {
        string dir = Path.Combine(CacheBaseDir, deviceId);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void UpdateDeviceAccessTime(string deviceId)
    {
        string dir = GetDeviceCacheDir(deviceId);
        File.WriteAllText(Path.Combine(dir, "last_accessed.txt"), DateTime.UtcNow.ToString("O"));
    }

    public static DateTime GetDeviceAccessTime(string deviceId)
    {
        string dir = Path.Combine(CacheBaseDir, deviceId);
        string file = Path.Combine(dir, "last_accessed.txt");
        if (!File.Exists(file))
            return DateTime.MinValue;

        string content = File.ReadAllText(file).Trim();
        if (DateTime.TryParse(content, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime dt))
            return dt;
        return DateTime.MinValue;
    }

    private static readonly string CacheHashVersion = "v5-thumb-size-cap";

    private static string HashPath(string input)
    {
        using var sha256 = SHA256.Create();
        byte[] hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(CacheHashVersion + input));
        return Convert.ToHexString(hash).Substring(0, 16).ToUpperInvariant();
    }

    /// <summary>
    /// Canonical relative-path form used for cache keys going forward: leading/trailing
    /// slashes are stripped so the Android agent's "/" prefixed children ("", "/", "/DCIM")
    /// and the offline explorer's slash-free paths ("", "DCIM", "DCIM/Camera") hash identically.
    /// </summary>
    private static string NormalizeRelPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        return path.Trim().Trim('/');
    }

    /// <summary>
    /// Ordered cache-key variants for a relative path: the normalized (canonical) form
    /// first, then the slash-prefixed form. Used on reads so both clean keys written by
    /// new code and legacy leading-slash keys written by older runs resolve correctly.
    /// </summary>
    private static IEnumerable<string> RelPathVariants(string path)
    {
        string norm = NormalizeRelPath(path);
        yield return norm;
        if (norm.Length > 0) yield return "/" + norm;
    }

    /// <summary>
    /// Builds the per-frame file key for a normalized frame value (e.g. 0.10 -&gt; "0010").
    /// The key is zero-padded so frame entries sort in numeric order on disk.
    /// </summary>
    public static string FrameSuffix(float frame)
    {
        int key = Math.Clamp((int)Math.Round(frame * 100f), 0, 1000);
        return $"F{key:D4}";
    }

    /// <summary>
    /// Returns the cache entry name for a specific frame of a video. Videos are cached
    /// once per frame so every downloaded frame keeps its own thumbnail; images (frame &lt; 0)
    /// keep a single entry keyed by the base path hash.
    /// </summary>
    public static string? GetFrameEntryName(string relativePath, float frame)
    {
        if (frame < 0f) return null;
        return $"{HashPath(relativePath)}_{FrameSuffix(frame)}";
    }

    /// <summary>
    /// Decides whether an existing cached thumbnail already satisfies the currently
    /// requested settings. A cache entry is suitable when its recorded quality is at
    /// least as high as the requested quality (a higher-quality thumbnail is always
    /// acceptable) and, for videos, the frame it was captured at matches the requested
    /// frame (unless the frame is unknown). Entries with unknown quality (-1) are
    /// trusted as-is so legacy caches are never wiped by a settings change.
    /// </summary>
    public static bool IsThumbnailSuitable(CacheMetadata? meta, float effectiveFrame, float effectiveQuality, bool isVideo)
    {
        if (meta == null) return false;
        if (meta.Quality < 0f) return true;
        if (effectiveQuality > meta.Quality + 0.001f) return false;
        if (isVideo && meta.FrameNormalized >= 0f && Math.Abs(meta.FrameNormalized - effectiveFrame) > 0.001f) return false;
        return true;
    }

    public bool TryGetCachedThumbnail(string deviceId, string relativePath, float frame, float quality, out byte[]? data, out CacheMetadata? metadata)
    {
        data = null;
        metadata = null;
        string dir = GetDeviceCacheDir(deviceId);

        foreach (string variant in RelPathVariants(relativePath))
        {
            string? frameEntry = frame >= 0f ? $"{HashPath(variant)}_{FrameSuffix(frame)}" : null;
            string entryName = frameEntry ?? HashPath(variant);

            if (ReadCacheEntry(deviceId, dir, entryName, out data, out metadata))
                return true;

            // Legacy single-entry video cache: stored under the base hash without a frame suffix.
            if (frame >= 0f)
            {
                string baseHash = HashPath(variant);
                if (!baseHash.Equals(entryName, StringComparison.OrdinalIgnoreCase)
                    && ReadCacheEntry(deviceId, dir, baseHash, out byte[]? legacyData, out CacheMetadata? legacyMeta)
                    && legacyMeta != null && legacyMeta.FrameNormalized >= 0f
                    && Math.Abs(legacyMeta.FrameNormalized - frame) <= 0.005f)
                {
                    data = legacyData;
                    metadata = legacyMeta;
                    return true;
                }
            }

            // No explicit frame requested (still images, or callers that pass -1). If no base
            // entry exists but per-frame video entries do, fall back to the best cached frame so
            // videos stay browsable offline regardless of the configured default frame.
            if (frame < 0f)
            {
                string baseHash = HashPath(variant);
                string? bestEntry = FindBestFrameEntry(dir, baseHash);
                if (bestEntry != null && ReadCacheEntry(deviceId, dir, bestEntry, out data, out metadata))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reads only the cached thumbnail metadata of a file (no image bytes) and returns
    /// its best recorded quality. Videos with multiple cached frames report the highest
    /// quality among them so the sort is stable regardless of which frame was captured.
    /// Files with no cached thumbnail yield -1.
    /// </summary>
    public float GetBestCachedThumbnailQuality(string deviceId, string relativePath)
    {
        string dir = GetDeviceCacheDir(deviceId);
        float best = -1f;

        foreach (string variant in RelPathVariants(relativePath))
        {
            string baseHash = HashPath(variant);

            TryAddQuality(Path.Combine(dir, $"{baseHash}.json"));

            foreach (string jsonPath in Directory.GetFiles(dir, $"{baseHash}_F*.json"))
                TryAddQuality(jsonPath);
        }

        return best;

        void TryAddQuality(string jsonPath)
        {
            try
            {
                if (!File.Exists(jsonPath)) return;
                var meta = JsonSerializer.Deserialize<CacheMetadata>(
                    File.ReadAllText(jsonPath),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (meta != null && meta.Quality > best)
                    best = meta.Quality;
            }
            catch { }
        }
    }

    private static bool ReadCacheEntry(string deviceId, string dir, string entryName, out byte[]? data, out CacheMetadata? metadata)
    {
        data = null;
        metadata = null;
        string datPath = Path.Combine(dir, $"{entryName}.dat");
        string jsonPath = Path.Combine(dir, $"{entryName}.json");

        if (!File.Exists(datPath) || !File.Exists(jsonPath))
            return false;

        try
        {
            data = File.ReadAllBytes(datPath);
            string json = File.ReadAllText(jsonPath);
            metadata = JsonSerializer.Deserialize<CacheMetadata>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (metadata == null)
            {
                data = null;
                return false;
            }
            UpdateDeviceAccessTime(deviceId);
            return true;
        }
        catch
        {
            data = null;
            metadata = null;
            return false;
        }
    }

    /// <summary>
    /// Scans a device cache directory for per-frame video entries of the given base hash
    /// and returns the entry name of the highest-quality cached frame (ties -&gt; most recent).
    /// </summary>
    private static string? FindBestFrameEntry(string dir, string baseHash)
    {
        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        string? best = null;
        float bestQuality = float.MinValue;
        DateTime bestTime = DateTime.MinValue;

        foreach (string jsonPath in Directory.GetFiles(dir, $"{baseHash}_F*.json"))
        {
            try
            {
                string json = File.ReadAllText(jsonPath);
                var meta = JsonSerializer.Deserialize<CacheMetadata>(json, jsonOptions);
                if (meta == null) continue;
                if (meta.Quality > bestQuality + 0.0001f
                    || (Math.Abs(meta.Quality - bestQuality) <= 0.0001f && meta.CachedTimeUtc > bestTime))
                {
                    best = Path.GetFileNameWithoutExtension(jsonPath);
                    bestQuality = meta.Quality;
                    bestTime = meta.CachedTimeUtc;
                }
            }
            catch { }
        }
        return best;
    }

    /// <summary>
    /// Compares two cached frames of the same video: higher quality wins, ties -&gt; most recent.
    /// </summary>
    private static bool IsBetterFrame(CacheMetadata a, CacheMetadata b)
    {
        if (a.Quality > b.Quality + 0.0001f) return true;
        if (Math.Abs(a.Quality - b.Quality) <= 0.0001f && a.CachedTimeUtc > b.CachedTimeUtc) return true;
        return false;
    }

    /// <summary>
    /// Lists every cached frame (with quality) for a video, sorted lowest-to-highest.
    /// Includes the legacy base entry so old single-frame caches remain switchable.
    /// Duplicate frames (legacy base + re-saved per-frame entry) collapse to the best one.
    /// </summary>
    public List<CachedVideoFrame> GetCachedVideoFrames(string deviceId, string relativePath)
    {
        var result = new SortedDictionary<int, CachedVideoFrame>();
        string dir = Path.Combine(CacheBaseDir, deviceId);
        if (!Directory.Exists(dir)) return new List<CachedVideoFrame>();

        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        void Add(CacheMetadata? meta)
        {
            if (meta == null || meta.FrameNormalized < 0f) return;
            int key = (int)Math.Round(meta.FrameNormalized * 100f);
            if (result.TryGetValue(key, out var cur))
            {
                if (meta.Quality < cur.Quality - 0.0001f) return;
                if (Math.Abs(meta.Quality - cur.Quality) <= 0.0001f && meta.CachedTimeUtc <= cur.CachedTimeUtc) return;
            }
            result[key] = new CachedVideoFrame { FrameNormalized = meta.FrameNormalized, Quality = meta.Quality, CachedTimeUtc = meta.CachedTimeUtc };
        }

        // Probe both the canonical (no-slash) and legacy slash-prefixed hashes so
        // frame entries written under either key form resolve.
        foreach (string variant in RelPathVariants(relativePath))
        {
            string baseHash = HashPath(variant);

            foreach (string jsonPath in Directory.GetFiles(dir, $"{baseHash}_F*.json"))
            {
                try
                {
                    string json = File.ReadAllText(jsonPath);
                    Add(JsonSerializer.Deserialize<CacheMetadata>(json, jsonOptions));
                }
                catch { }
            }

            // Legacy single-frame cache entry (base hash, no suffix).
            string legacyJsonPath = Path.Combine(dir, $"{baseHash}.json");
            try
            {
                if (File.Exists(legacyJsonPath))
                {
                    string json = File.ReadAllText(legacyJsonPath);
                    Add(JsonSerializer.Deserialize<CacheMetadata>(json, jsonOptions));
                }
            }
            catch { }
        }

        return result.Values.ToList();
    }

    public void SaveCachedThumbnail(string deviceId, string relativePath, string fileName, byte[] data, long originalLength, string mediaType, float frame = -1f, float quality = -1f, long originalPreviewLength = 0)
    {
        string? frameEntry = GetFrameEntryName(relativePath, frame);
        string entryName = frameEntry ?? HashPath(relativePath);
        string dir = GetDeviceCacheDir(deviceId);
        string datPath = Path.Combine(dir, $"{entryName}.dat");
        string jsonPath = Path.Combine(dir, $"{entryName}.json");

        File.WriteAllBytes(datPath, data);

        var metadata = new CacheMetadata
        {
            RelativePath = relativePath,
            FileName = fileName,
            OriginalLength = originalLength,
            ProcessedLength = data.Length,
            MediaType = mediaType,
            CachedTimeUtc = DateTime.UtcNow,
            FrameNormalized = frame,
            Quality = quality,
            OriginalPreviewLength = originalPreviewLength
        };

        string json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(jsonPath, json);
        UpdateDeviceAccessTime(deviceId);
    }

    // ── Full-file cache (actual video / audio / other file bytes) ────────────
    // Full files live in their own namespace (FULL_<hash>.dat) so a file's
    // original bytes never collide with its thumbnail cache entries.

    public static string FullFileEntryName(string relativePath) => $"FULL_{HashPath(NormalizeRelPath(relativePath))}";

    public static bool IsFullFileEntry(string entryName) =>
        entryName.StartsWith("FULL_", StringComparison.OrdinalIgnoreCase);

    private static string FullFileDatPath(string deviceId, string relativePath) =>
        Path.Combine(GetDeviceCacheDir(deviceId), $"{FullFileEntryName(relativePath)}.dat");

    private static string FullFileJsonPath(string deviceId, string relativePath) =>
        Path.Combine(GetDeviceCacheDir(deviceId), $"{FullFileEntryName(relativePath)}.json");

    /// <summary>Saves a fully received file into the offline cache as one buffer.</summary>
    public void SaveCachedFile(
        string deviceId, string relativePath, string fileName, byte[] data, long originalLength,
        string mediaType, float frame = -1f, float quality = -1f, float bitrateMultiplier = -1f,
        float resolutionMultiplier = -1f, int bitrateCapBps = -1, int resolutionCeilingPx = -1)
    {
        CacheMetadata metadata = new()
        {
            RelativePath = relativePath,
            FileName = fileName,
            OriginalLength = originalLength,
            ProcessedLength = data.Length,
            MediaType = mediaType,
            CachedTimeUtc = DateTime.UtcNow,
            FrameNormalized = frame,
            Quality = quality,
            IsFullFile = true,
            BitrateMultiplier = bitrateMultiplier,
            ResolutionMultiplier = resolutionMultiplier,
            BitrateCapBps = bitrateCapBps,
            ResolutionCeilingPx = resolutionCeilingPx
        };

        string dir = GetDeviceCacheDir(deviceId);
        File.WriteAllBytes(FullFileDatPath(deviceId, relativePath), data);
        File.WriteAllText(FullFileJsonPath(deviceId, relativePath),
            JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
        UpdateDeviceAccessTime(deviceId);
    }

    /// <summary>
    /// Opens a temporary .part file for progressively streaming a full file onto
    /// disk while it transfers from the phone. Returns null if a newer/equal entry
    /// already exists for this exact variant (callers should skip the transfer).
    /// </summary>
    public string? BeginStreamedFile(
        string deviceId, string relativePath, bool allowReplace)
    {
        string dir = GetDeviceCacheDir(deviceId);
        string part = Path.Combine(dir, $"{FullFileEntryName(relativePath)}.part");
        if (File.Exists(part))
            File.Delete(part);
        return part;
    }

    /// <summary>
    /// Commits a fully streamed file (renames the .part temp into the cache entry
    /// and writes its metadata). Partial/interrupted transfers should call
    /// AbortFullFile instead so nothing half-written becomes available offline.
    /// </summary>
    public void CompleteFullFile(string partPath, string deviceId, string relativePath, string fileName,
        long transferredBytes, long originalLength, string mediaType, float frame = -1f, float quality = -1f,
        float bitrateMultiplier = -1f, float resolutionMultiplier = -1f, int bitrateCapBps = -1, int resolutionCeilingPx = -1)
    {
        string dat = FullFileDatPath(deviceId, relativePath);
        File.Move(partPath, dat, overwrite: true);

        var metadata = new CacheMetadata
        {
            RelativePath = relativePath,
            FileName = fileName,
            OriginalLength = originalLength,
            ProcessedLength = transferredBytes,
            MediaType = mediaType,
            CachedTimeUtc = DateTime.UtcNow,
            FrameNormalized = frame,
            Quality = quality,
            IsFullFile = true,
            BitrateMultiplier = bitrateMultiplier,
            ResolutionMultiplier = resolutionMultiplier,
            BitrateCapBps = bitrateCapBps,
            ResolutionCeilingPx = resolutionCeilingPx
        };

        File.WriteAllText(FullFileJsonPath(deviceId, relativePath),
            JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
        UpdateDeviceAccessTime(deviceId);
    }

    public void DeleteFullFile(string deviceId, string relativePath)
    {
        foreach (var p in new[] { FullFileDatPath(deviceId, relativePath), FullFileJsonPath(deviceId, relativePath) })
        {
            if (File.Exists(p)) { try { File.Delete(p); } catch { } }
        }
    }

    /// <summary>Whether a full file cache entry exists for a relative path.</summary>
    public bool HasFullFile(string deviceId, string relativePath)
    {
        string dir = GetDeviceCacheDir(deviceId);
        foreach (string variant in RelPathVariants(relativePath))
        {
            string name = FullFileEntryName(variant);
            string dat = Path.Combine(dir, $"{name}.dat");
            string json = Path.Combine(dir, $"{name}.json");
            if (File.Exists(dat) && File.Exists(json)) return true;
        }
        return false;
    }

    /// <summary>Full on-disk path for an existing full-file entry, or null.</summary>
    public string? GetFullFilePath(string deviceId, string relativePath)
    {
        string dir = GetDeviceCacheDir(deviceId);
        foreach (string variant in RelPathVariants(relativePath))
        {
            string name = FullFileEntryName(variant);
            string dat = Path.Combine(dir, $"{name}.dat");
            string json = Path.Combine(dir, $"{name}.json");
            if (File.Exists(dat) && File.Exists(json)) return dat;
        }
        return null;
    }

    /// <summary>Loads the saved settings metadata for a full-file entry, or null.</summary>
    public CacheMetadata? GetFullFileMetadata(string deviceId, string relativePath)
    {
        string dir = GetDeviceCacheDir(deviceId);
        foreach (string variant in RelPathVariants(relativePath))
        {
            string json = Path.Combine(dir, $"{FullFileEntryName(variant)}.json");
            if (!File.Exists(json)) continue;
            try
            {
                return JsonSerializer.Deserialize<CacheMetadata>(File.ReadAllText(json),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch { return null; }
        }
        return null;
    }

    /// <summary>
    /// Whether an existing cached full file already satisfies the requested
    /// settings (never downgrade). A cached file produced with a higher bitrate or
    /// resolution multiplier, or under looser caps, is always acceptable.
    /// </summary>
    public static bool IsFullFileSuitable(CacheMetadata? meta, float reqBrm, float reqResm, int reqBrCapBps, int reqCeilPx)
    {
        if (meta == null || !meta.IsFullFile) return false;
        if (reqBrm > 0f && meta.BitrateMultiplier > 0f && meta.BitrateMultiplier + 0.001f < reqBrm) return false;
        if (reqResm > 0f && meta.ResolutionMultiplier > 0f && meta.ResolutionMultiplier + 0.001f < reqResm) return false;
        if (reqBrCapBps > 0 && meta.BitrateCapBps > 0 && meta.BitrateCapBps + 1 < reqBrCapBps) return false;
        if (reqCeilPx > 0 && meta.ResolutionCeilingPx > 0 && meta.ResolutionCeilingPx < reqCeilPx) return false;
        return true;
    }

    public List<CacheDeviceInfo> GetAllCachedDevices(ConnectionManager cm)
    {
        if (!Directory.Exists(CacheBaseDir))
            return new List<CacheDeviceInfo>();

        var results = new List<CacheDeviceInfo>();
        foreach (string deviceDir in Directory.GetDirectories(CacheBaseDir))
        {
            string deviceId = Path.GetFileName(deviceDir);
            var info = GetCacheDeviceInfo(deviceId, cm);
            if (info != null)
                results.Add(info);
        }
        return results;
    }

    public CacheDeviceInfo? GetCacheDeviceInfo(string deviceId, ConnectionManager cm)
    {
        string dir = Path.Combine(CacheBaseDir, deviceId);
        if (!Directory.Exists(dir))
            return null;

        string deviceName = deviceId;
        string nameFile = Path.Combine(dir, "device_name.txt");
        if (File.Exists(nameFile))
        {
            deviceName = File.ReadAllText(nameFile).Trim();
        }
        else if (cm.TryGetDeviceIdentity(deviceId, out var identity) && identity != null)
        {
            deviceName = identity.DeviceName;
        }

        long totalSizeBytes = 0;
        long imageSizeBytes = 0;
        long videoSizeBytes = 0;
        long audioSizeBytes = 0;
        long fileSizeBytes = 0;
        int totalFileCount = 0;
        int imageFileCount = 0;
        int videoFileCount = 0;
        int audioFileCount = 0;
        int fileFileCount = 0;
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string jsonPath in Directory.GetFiles(dir, "*.json"))
        {
            string fileName = Path.GetFileName(jsonPath);
            if (fileName.StartsWith("folder_", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                string json = File.ReadAllText(jsonPath);
                var meta = JsonSerializer.Deserialize<CacheMetadata>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (meta == null) continue;

                // Multiple cached frames of the same video are stored as separate entries;
                // count the source file only once so stats are not inflated.
                string key = string.IsNullOrEmpty(meta.RelativePath) ? fileName : meta.RelativePath;
                if (!seenPaths.Add(key))
                    continue;

                string ext = Path.GetExtension(meta.FileName ?? "");
                // Report the ACTUAL bytes stored on the PC — the fetched thumbnail for
                // images/videos, the transcoded size for transcoded videos, or the raw
                // size when a video was downloaded natively — not the phone's original.
                long storedBytes = meta.ProcessedLength > 0 ? meta.ProcessedLength : meta.OriginalLength;
                totalSizeBytes += storedBytes;
                totalFileCount++;

                if (ImageExtensions.Contains(ext)) {
                    imageSizeBytes += storedBytes;
                    imageFileCount++;
                }
                else if (VideoExtensions.Contains(ext)) {
                    videoSizeBytes += storedBytes;
                    videoFileCount++;
                }
                else if (AudioExtensions.Contains(ext)) {
                    audioSizeBytes += storedBytes;
                    audioFileCount++;
                }
                else {
                    fileSizeBytes += storedBytes;
                    fileFileCount++;
                }
            }
            catch
            {
            }
        }

        return new CacheDeviceInfo
        {
            DeviceId = deviceId,
            DeviceName = deviceName,
            TotalSizeBytes = totalSizeBytes,
            ImageSizeBytes = imageSizeBytes,
            VideoSizeBytes = videoSizeBytes,
            AudioSizeBytes = audioSizeBytes,
            FileSizeBytes = fileSizeBytes,
            TotalFileCount = totalFileCount,
            ImageFileCount = imageFileCount,
            VideoFileCount = videoFileCount,
            AudioFileCount = audioFileCount,
            FileFileCount = fileFileCount,
            LastAccessedUtc = GetDeviceAccessTime(deviceId)
        };
    }

    private static readonly object _deviceNameLock = new();

    public void SaveDeviceName(string deviceId, string deviceName)
    {
        string filePath = Path.Combine(GetDeviceCacheDir(deviceId), "device_name.txt");
        lock (_deviceNameLock)
        {
            // Skip write if name hasn't changed
            try { if (File.Exists(filePath) && File.ReadAllText(filePath).Trim() == deviceName) return; } catch { }
            for (int retry = 0; retry < 5; retry++)
            {
                try
                {
                    File.WriteAllText(filePath, deviceName);
                    return;
                }
                catch (IOException) { Thread.Sleep(50); }
            }
        }
    }

    public string? TryResolveDeviceId(string deviceNameOrId)
    {
        if (!Directory.Exists(CacheBaseDir))
            return null;

        string directDir = Path.Combine(CacheBaseDir, deviceNameOrId);
        if (Directory.Exists(directDir))
            return deviceNameOrId;

        foreach (string deviceDir in Directory.GetDirectories(CacheBaseDir))
        {
            string nameFile = Path.Combine(deviceDir, "device_name.txt");
            if (File.Exists(nameFile))
            {
                string name = File.ReadAllText(nameFile).Trim();
                if (name.Equals(deviceNameOrId, StringComparison.OrdinalIgnoreCase))
                    return Path.GetFileName(deviceDir);
            }
        }
        return null;
    }

    public void ClearCache()
    {
        if (Directory.Exists(CacheBaseDir))
            Directory.Delete(CacheBaseDir, true);
        Directory.CreateDirectory(CacheBaseDir);
    }

    public void ClearCache(string deviceId, string? mediaType = null)
    {
        if (string.IsNullOrEmpty(mediaType))
        {
            ClearDeviceCache(deviceId);
            return;
        }
        ClearDeviceMediaType(deviceId, mediaType);
    }

    public void ClearDeviceCache(string deviceId)
    {
        string dir = Path.Combine(CacheBaseDir, deviceId);
        if (Directory.Exists(dir))
            Directory.Delete(dir, true);
    }

    public void ClearDeviceMediaType(string deviceId, string? mediaType)
    {
        if (string.IsNullOrEmpty(mediaType))
        {
            ClearDeviceCache(deviceId);
            return;
        }

        string dir = Path.Combine(CacheBaseDir, deviceId);
        if (!Directory.Exists(dir))
            return;

        foreach (string jsonPath in Directory.GetFiles(dir, "*.json"))
        {
            string jsonFileName = Path.GetFileName(jsonPath);
            if (jsonFileName.StartsWith("folder_", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                string json = File.ReadAllText(jsonPath);
                var meta = JsonSerializer.Deserialize<CacheMetadata>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (meta != null && meta.MediaType.Equals(mediaType, StringComparison.OrdinalIgnoreCase))
                {
                    string hash = Path.GetFileNameWithoutExtension(jsonPath);
                    string datPath = Path.Combine(dir, $"{hash}.dat");
                    if (File.Exists(datPath))
                        File.Delete(datPath);
                    if (File.Exists(jsonPath))
                        File.Delete(jsonPath);
                }
            }
            catch
            {
            }
        }
    }

    public string? FindMostRecentDevice(List<string> deviceIds)
    {
        string? mostRecent = null;
        DateTime mostRecentTime = DateTime.MinValue;

        foreach (string deviceId in deviceIds)
        {
            DateTime accessTime = GetDeviceAccessTime(deviceId);
            if (accessTime > mostRecentTime)
            {
                mostRecentTime = accessTime;
                mostRecent = deviceId;
            }
        }
        return mostRecent;
    }

    public int MergeMultiple(List<string> sourceIds, string targetId)
    {
        int total = 0;
        foreach (string sourceId in sourceIds)
        {
            if (MergeCache(sourceId, targetId))
                total++;
        }
        return total;
    }

    public bool MergeCache(string oldDeviceId, string newDeviceId)
    {
        string oldDir = Path.Combine(CacheBaseDir, oldDeviceId);
        string newDir = GetDeviceCacheDir(newDeviceId);

        if (!Directory.Exists(oldDir))
            return false;

        if (Path.GetFullPath(oldDir).Equals(Path.GetFullPath(newDir), StringComparison.OrdinalIgnoreCase))
            return true; // same directory — nothing to merge or delete

        var oldFiles = Directory.GetFiles(oldDir);
        foreach (string oldFilePath in oldFiles)
        {
            string fileName = Path.GetFileName(oldFilePath);
            if (fileName.Equals("device_name.txt", StringComparison.OrdinalIgnoreCase))
                continue;

            string newFilePath = Path.Combine(newDir, fileName);

            if (!File.Exists(newFilePath))
            {
                File.Copy(oldFilePath, newFilePath);
            }
            else
            {
                var oldInfo = new FileInfo(oldFilePath);
                var newInfo = new FileInfo(newFilePath);
                if (oldInfo.Length > newInfo.Length)
                {
                    File.Copy(oldFilePath, newFilePath, true);
                }
            }
        }

        // The old device cache has been fully merged into the target — remove its
        // directory so only the latest device ID remains in the cache list and
        // offline browser.
        try
        {
            Directory.Delete(oldDir, true);
            /* Console.WriteLine($"[CacheManager] Merged and removed old device cache: {oldDeviceId}"); */
        }
        catch (Exception ex)
        {
            /* Console.WriteLine($"[CacheManager] Merge of {oldDeviceId} completed but old directory could not be deleted: {ex.Message}"); */
        }

        return true;
    }

    public byte[]? ExportDeviceCache(string deviceId)
    {
        return ExportDeviceCacheLive(deviceId, null, null).GetAwaiter().GetResult();
    }

    public async Task<byte[]?> ExportDeviceCacheLive(string deviceId, ConnectionManager? cm, IHubContext<CommunicateSignalR>? hubContext)
    {
        string dir = Path.Combine(CacheBaseDir, deviceId);
        if (!Directory.Exists(dir)) return null;

        string deviceName = deviceId;
        string nameFile = Path.Combine(dir, "device_name.txt");
        if (File.Exists(nameFile))
            deviceName = File.ReadAllText(nameFile).Trim();

        var allFolders = new HashSet<string>();
        var folderJsonMap = new Dictionary<string, string>();
        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        foreach (var jsonPath in Directory.GetFiles(dir, "folder_*.json"))
        {
            try
            {
                string raw = File.ReadAllText(jsonPath);
                var fe = JsonSerializer.Deserialize<FolderCacheEntry>(raw, jsonOptions);
                if (fe == null) continue;
                string folderPath = (fe.FolderPath ?? "").Replace('\\', '/').Trim('/');
                string hash = Path.GetFileNameWithoutExtension(jsonPath)["folder_".Length..];
                folderJsonMap[folderPath] = hash;

                if (!string.IsNullOrEmpty(folderPath))
                {
                    var parts = folderPath.Split('/');
                    for (int i = 1; i <= parts.Length; i++)
                        allFolders.Add(string.Join("/", parts.Take(i)));
                }
            }
            catch { }
        }

        if (cm != null && hubContext != null)
        {
            string? connId = null;
            foreach (var d in cm.GetAllDevices())
            {
                if (d.DeviceId.Equals(deviceId, StringComparison.OrdinalIgnoreCase))
                {
                    connId = d.ConnectionId;
                    break;
                }
            }
            if (connId != null)
            {
                await WalkPhoneTree(hubContext, connId, "", allFolders, 3);
            }
        }

        var hashToMeta = new Dictionary<string, CacheMetadata>();
        foreach (var jsonPath in Directory.GetFiles(dir, "*.json"))
        {
            if (Path.GetFileName(jsonPath).StartsWith("folder_")) continue;
            try
            {
                string raw = File.ReadAllText(jsonPath);
                var meta = JsonSerializer.Deserialize<CacheMetadata>(raw, jsonOptions);
                if (meta != null)
                    hashToMeta[Path.GetFileNameWithoutExtension(jsonPath)] = meta;
            }
            catch { }
        }

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            var detailsEntry = zip.CreateEntry("details.txt", CompressionLevel.Fastest);
            using (var w = new StreamWriter(detailsEntry.Open()))
            {
                w.WriteLine($"DeviceName: {deviceName}");
                w.WriteLine($"DeviceId: {deviceId}");
                w.WriteLine($"ExportedUtc: {DateTime.UtcNow:O}");
            }

            var usedNames = new HashSet<string>();

            // For videos with multiple cached frames, pick the "default" frame (highest
            // quality, ties -> most recent) to also export under the plain filename.
            var bestFrameByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in hashToMeta)
            {
                if (kvp.Value.FrameNormalized < 0f) continue;
                string rp = (kvp.Value.RelativePath ?? "").Replace('\\', '/');
                if (!bestFrameByPath.TryGetValue(rp, out var curHash) || IsBetterFrame(kvp.Value, hashToMeta[curHash]))
                    bestFrameByPath[rp] = kvp.Key;
            }

            foreach (var datPath in Directory.GetFiles(dir, "*.dat"))
            {
                string hash = Path.GetFileNameWithoutExtension(datPath);
                if (!hashToMeta.TryGetValue(hash, out var meta)) continue;
                if (string.IsNullOrEmpty(meta.RelativePath)) continue;

                string rp = meta.RelativePath.Replace('\\', '/');
                int lastSlash = rp.LastIndexOf('/');
                string folder = lastSlash >= 0 ? rp[..lastSlash] : "";
                bool isVideoFrame = meta.FrameNormalized >= 0f;

                // Video frame thumbnails are exported under a distinct, human-readable name
                // (video.mp4_Frame0.30.jpg) so every cached frame survives the round-trip.
                string exportPath;
                if (isVideoFrame)
                {
                    string frameLabel = meta.FrameNormalized.ToString("0.00", CultureInfo.InvariantCulture);
                    exportPath = string.IsNullOrEmpty(folder)
                        ? $"{meta.FileName}_Frame{frameLabel}.jpg"
                        : $"{folder}/{meta.FileName}_Frame{frameLabel}.jpg";
                }
                else
                {
                    exportPath = string.IsNullOrEmpty(folder) ? meta.FileName : $"{folder}/{meta.FileName}";
                }

                string orig = exportPath;
                int dup = 1;
                while (!usedNames.Add(exportPath))
                {
                    string bn = Path.GetFileNameWithoutExtension(orig);
                    string ext = Path.GetExtension(orig);
                    string parent = Path.GetDirectoryName(orig)?.Replace('\\', '/') ?? "";
                    exportPath = string.IsNullOrEmpty(parent) ? $"{bn}_{dup}{ext}" : $"{parent}/{bn}_{dup}{ext}";
                    dup++;
                }

                var fileBytes = File.ReadAllBytes(datPath);
                var entry = zip.CreateEntry(exportPath, CompressionLevel.Fastest);
                using (var a = new MemoryStream(fileBytes))
                using (var b = entry.Open()) { a.CopyTo(b); }

                // Also export the "default" frame under the plain filename so imports and
                // galleries keep finding a plain video thumbnail.
                if (isVideoFrame
                    && bestFrameByPath.TryGetValue(rp, out var bestHash)
                    && bestHash.Equals(hash, StringComparison.OrdinalIgnoreCase))
                {
                    string plainPath = string.IsNullOrEmpty(folder) ? meta.FileName : $"{folder}/{meta.FileName}";
                    string plainOrig = plainPath;
                    int plainDup = 1;
                    while (!usedNames.Add(plainPath))
                    {
                        string pbn = Path.GetFileNameWithoutExtension(plainOrig);
                        string pext = Path.GetExtension(plainOrig);
                        string pparent = Path.GetDirectoryName(plainOrig)?.Replace('\\', '/') ?? "";
                        plainPath = string.IsNullOrEmpty(pparent) ? $"{pbn}_{plainDup}{pext}" : $"{pparent}/{pbn}_{plainDup}{pext}";
                        plainDup++;
                    }
                    var pEntry = zip.CreateEntry(plainPath, CompressionLevel.Fastest);
                    using (var a = new MemoryStream(fileBytes))
                    using (var b = pEntry.Open()) { a.CopyTo(b); }
                }

                var rEntry = zip.CreateEntry($"_raw/{Path.GetFileName(datPath)}", CompressionLevel.Fastest);
                using (var a = new MemoryStream(fileBytes))
                using (var b = rEntry.Open()) { a.CopyTo(b); }
            }

            foreach (var jsonPath in Directory.GetFiles(dir, "*.json"))
            {
                var rEntry = zip.CreateEntry($"_raw/{Path.GetFileName(jsonPath)}", CompressionLevel.Fastest);
                var jb = File.ReadAllBytes(jsonPath);
                using (var a = new MemoryStream(jb))
                using (var b = rEntry.Open()) { a.CopyTo(b); }
            }

            foreach (var folder in allFolders)
            {
                if (!string.IsNullOrEmpty(folder))
                    zip.CreateEntry(folder + "/", CompressionLevel.NoCompression);
            }
        }
        return ms.ToArray();
    }

    private async Task WalkPhoneTree(IHubContext<CommunicateSignalR> hubContext, string connId, string path, HashSet<string> folders, int depth)
    {
        if (depth <= 0) return;
        try
        {
            var result = await hubContext.Clients.Client(connId)
                .InvokeCoreAsync<FileNodeResultDto>("GetPathInfo", new object[] { path, false }, CancellationToken.None);
            if (result?.Children == null) return;

            foreach (var child in result.Children)
            {
                if (!child.IsDirectory) continue;
                string childPath = string.IsNullOrEmpty(path) ? child.Name : $"{path}/{child.Name}";
                childPath = childPath.Replace('\\', '/');
                if (folders.Add(childPath))
                {
                    await WalkPhoneTree(hubContext, connId, childPath, folders, depth - 1);
                }
            }
        }
        catch { }
    }

    public CacheImportResult ImportDeviceCache(byte[] zipData)
    {
        using var ms = new MemoryStream(zipData);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);

        string? deviceId = null;
        string? deviceName = null;

        var detailsEntry = zip.GetEntry("details.txt");
        if (detailsEntry != null)
        {
            using var reader = new StreamReader(detailsEntry.Open());
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.StartsWith("DeviceId: ")) deviceId = line["DeviceId: ".Length..].Trim();
                else if (line.StartsWith("DeviceName: ")) deviceName = line["DeviceName: ".Length..].Trim();
            }
        }

        // Fallback: generate a new ID if details.txt is missing
        if (string.IsNullOrEmpty(deviceId))
            deviceId = Guid.NewGuid().ToString();
        if (string.IsNullOrEmpty(deviceName))
            deviceName = "Imported_" + DateTime.UtcNow.ToString("yyyyMMdd");

        SaveDeviceName(deviceId, deviceName);

        string deviceDir = GetDeviceCacheDir(deviceId);

        // Check for _raw/ directory (export format with raw cache data)
        var rawEntries = zip.Entries.Where(e => e.FullName.StartsWith("_raw/") && !e.FullName.EndsWith("/")).ToList();
        if (rawEntries.Count > 0)
        {
            int imported = 0;
            foreach (var entry in rawEntries)
            {
                try { entry.ExtractToFile(Path.Combine(deviceDir, Path.GetFileName(entry.FullName)), true); imported++; } catch { }
            }
            return new CacheImportResult { Success = true, DeviceId = deviceId, Message = $"Imported {imported} files for '{deviceName ?? deviceId}'." };
        }

        bool hasMetaFiles = zip.Entries.Any(e => e.Name.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase));
        bool hasRawJson = zip.Entries.Any(e => e.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && !e.Name.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase));

        if (hasMetaFiles)
        {
            var metaEntries = zip.Entries.Where(e => e.Name.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var metaEntry in metaEntries)
            {
                try
                {
                    var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    CacheMetadata? meta;
                    using (var metaStream = metaEntry.Open())
                    {
                        meta = JsonSerializer.Deserialize<CacheMetadata>(metaStream, jsonOptions);
                    }
                    if (meta == null) continue;

                    string hash = HashPath(meta.RelativePath);
                    string jsonPath = Path.Combine(deviceDir, $"{hash}.json");
                    string datName = metaEntry.Name.Replace(".meta.json", ".dat", StringComparison.OrdinalIgnoreCase);

                    var datEntry = zip.GetEntry(datName);
                    if (datEntry == null)
                    {
                        string fullDatName = metaEntry.FullName.Replace(".meta.json", ".dat", StringComparison.OrdinalIgnoreCase);
                        datEntry = zip.GetEntry(fullDatName);
                    }

                    if (datEntry != null)
                    {
                        string datPath = Path.Combine(deviceDir, $"{hash}.dat");
                        using var datStream = datEntry.Open();
                        using var fs = File.Create(datPath);
                        datStream.CopyTo(fs);

                        meta.ProcessedLength = new FileInfo(datPath).Length;
                        meta.CachedTimeUtc = DateTime.UtcNow;
                        string json = JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true });
                        File.WriteAllText(jsonPath, json);
                    }
                }
                catch { }
            }
        }
        else
        {
            var jsonEntries = zip.Entries.Where(e => e.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && !e.Name.StartsWith("folder_")).ToList();
            foreach (var jsonEntry in jsonEntries)
            {
                try
                {
                    string hash = Path.GetFileNameWithoutExtension(jsonEntry.Name);
                    string jsonPath = Path.Combine(deviceDir, $"{hash}.json");
                    string datName = $"{hash}.dat";

                    var datEntry = zip.GetEntry(datName) ?? zip.Entries.FirstOrDefault(e => e.Name.Equals(datName, StringComparison.OrdinalIgnoreCase));

                    if (datEntry != null)
                    {
                        string datPath = Path.Combine(deviceDir, $"{hash}.dat");
                        using var datStream = datEntry.Open();
                        using var fs = File.Create(datPath);
                        datStream.CopyTo(fs);

                        using var jsonStream = jsonEntry.Open();
                        using var reader = new StreamReader(jsonStream);
                        string jsonContent = reader.ReadToEnd();
                        File.WriteAllText(jsonPath, jsonContent);
                    }
                }
                catch { }
            }
        }

        UpdateDeviceAccessTime(deviceId);
        return new CacheImportResult { Success = true, Message = "Cache imported successfully.", DeviceId = deviceId };
    }

    public void EnforceCacheLimit(int maxLimitMb)
    {
        if (!Directory.Exists(CacheBaseDir))
            return;

        long maxLimitBytes = (long)maxLimitMb * 1024 * 1024;
        if (maxLimitBytes <= 0)
            return;

        var datFiles = new List<(string DatPath, string JsonPath, DateTime LastAccess)>();
        foreach (string deviceDir in Directory.GetDirectories(CacheBaseDir))
        {
            foreach (string datPath in Directory.GetFiles(deviceDir, "*.dat"))
            {
                string hash = Path.GetFileNameWithoutExtension(datPath);
                string jsonPath = Path.Combine(deviceDir, $"{hash}.json");
                DateTime lastAccess = File.GetLastAccessTimeUtc(datPath);
                datFiles.Add((datPath, jsonPath, lastAccess));
            }
        }

        datFiles = datFiles.OrderBy(f => f.LastAccess).ToList();

        long currentTotalSize = datFiles.Sum(f =>
        {
            try { return new FileInfo(f.DatPath).Length; }
            catch { return 0; }
        });

        for (int i = 0; i < datFiles.Count && currentTotalSize > maxLimitBytes; i++)
        {
            var (datPath, jsonPath, _) = datFiles[i];
            try
            {
                long size = new FileInfo(datPath).Length;
                if (File.Exists(datPath))
                    File.Delete(datPath);
                if (File.Exists(jsonPath))
                    File.Delete(jsonPath);
                currentTotalSize -= size;
            }
            catch { }
        }
    }

    public void SaveFolderCache(string deviceId, string folderPath, FileNodeResultDto nodeInfo)
    {
        string normalized = NormalizeRelPath(folderPath);
        string hash = HashPath(normalized);
        string dir = GetDeviceCacheDir(deviceId);
        string jsonPath = Path.Combine(dir, $"folder_{hash}.json");

        var deduped = new Dictionary<string, FileNodeDto>(StringComparer.OrdinalIgnoreCase);
        if (nodeInfo.Children != null)
        {
            foreach (var c in nodeInfo.Children)
                deduped[NormalizeRelPath(c.RelativePath)] = c;
        }
        var entry = new FolderCacheEntry
        {
            FolderPath = normalized,
            CachedTimeUtc = DateTime.UtcNow,
            Exists = nodeInfo.Exists,
            CurrentNode = nodeInfo.CurrentNode,
            Children = deduped.Values.ToList()
        };

        string json = JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true });
        File.WriteAllText(jsonPath, json);
        UpdateDeviceAccessTime(deviceId);
    }

    /// <summary>
    /// Merges the children the client actually requested into the folder cache.
    /// Only the paginated pages the user browsed are ever stored — files that were
    /// never requested are never written to the offline cache.
    /// Existing cached entries for the same path are preserved across page requests.
    /// </summary>
    public void MergeFolderCache(string deviceId, string folderPath, FileNodeDto? currentNode, List<FileNodeDto> children)
    {
        string normalized = NormalizeRelPath(folderPath);
        var existing = GetFolderCache(deviceId, normalized);

        var merged = new Dictionary<string, FileNodeDto>(StringComparer.OrdinalIgnoreCase);
        if (existing?.Children != null)
        {
            foreach (var c in existing.Children)
                merged[NormalizeRelPath(c.RelativePath)] = c;
        }
        foreach (var c in children)
            merged[NormalizeRelPath(c.RelativePath)] = c;

        var entry = new FolderCacheEntry
        {
            FolderPath = normalized,
            CachedTimeUtc = DateTime.UtcNow,
            Exists = true,
            CurrentNode = currentNode ?? existing?.CurrentNode,
            Children = merged.Values.ToList()
        };

        string hash = HashPath(normalized);
        string dir = GetDeviceCacheDir(deviceId);
        string jsonPath = Path.Combine(dir, $"folder_{hash}.json");
        string json = JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true });
        File.WriteAllText(jsonPath, json);
        UpdateDeviceAccessTime(deviceId);
    }

    public FolderCacheEntry? GetFolderCache(string deviceId, string folderPath)
    {
        string dir = Path.Combine(CacheBaseDir, deviceId);

        foreach (string variant in RelPathVariants(folderPath))
        {
            string hash = HashPath(variant);
            string jsonPath = Path.Combine(dir, $"folder_{hash}.json");
            if (!File.Exists(jsonPath)) continue;

            try
            {
                string json = File.ReadAllText(jsonPath);
                var entry = JsonSerializer.Deserialize<FolderCacheEntry>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                UpdateDeviceAccessTime(deviceId);
                return entry;
            }
            catch
            {
                return null;
            }
        }
        return null;
    }

    public bool HasFolderCache(string deviceId, string folderPath)
    {
        string dir = Path.Combine(CacheBaseDir, deviceId);
        foreach (string variant in RelPathVariants(folderPath))
        {
            string hash = HashPath(variant);
            string jsonPath = Path.Combine(dir, $"folder_{hash}.json");
            if (File.Exists(jsonPath)) return true;
        }
        return false;
    }

    /// <summary>
    /// Counts the cached children of a folder — including everything inside its
    /// sub-folders (recursive) — broken down by media type (images / videos /
    /// audio / other). Used by the offline explorer's folder hover popup.
    /// </summary>
    public FolderChildCounts GetFolderChildCounts(string deviceId, string folderPath)
    {
        return GetFolderChildCountsRecursive(deviceId, folderPath, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    private FolderChildCounts GetFolderChildCountsRecursive(string deviceId, string folderPath, HashSet<string> visited)
    {
        var counts = new FolderChildCounts();
        var folder = GetFolderCache(deviceId, folderPath);
        if (folder?.Children == null)
            return counts;

        foreach (var c in folder.Children)
        {
            if (c.IsDirectory)
            {
                // Recurse into sub-folders. Protect against symlink-style loops
                // (a folder appearing as a child of itself) so we never overflow.
                if (visited.Add(c.RelativePath))
                {
                    var sub = GetFolderChildCountsRecursive(deviceId, c.RelativePath, visited);
                    counts.Images += sub.Images;
                    counts.Videos += sub.Videos;
                    counts.Audio  += sub.Audio;
                    counts.Other  += sub.Other;
                }
                continue;
            }

            string ext = Path.GetExtension(c.Name);
            if (ImageExtensions.Contains(ext))       counts.Images++;
            else if (VideoExtensions.Contains(ext))  counts.Videos++;
            else if (AudioExtensions.Contains(ext))  counts.Audio++;
            else                                      counts.Other++;
        }
        return counts;
    }

    public List<string> GetCachedFolderPaths(string deviceId)
    {
        var paths = new List<string>();
        string dir = Path.Combine(CacheBaseDir, deviceId);
        if (!Directory.Exists(dir))
            return paths;

        foreach (string jsonPath in Directory.GetFiles(dir, "folder_*.json"))
        {
            try
            {
                string json = File.ReadAllText(jsonPath);
                var entry = JsonSerializer.Deserialize<FolderCacheEntry>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (entry != null && !string.IsNullOrEmpty(entry.FolderPath))
                    paths.Add(entry.FolderPath);
            }
            catch { }
        }
        return paths;
    }

    public List<string> GetAllCachedPaths(string deviceId)
    {
        return GetCachedFolderPaths(deviceId);
    }

    /// <summary>
    /// Searches the offline folder cache for files/folders whose name contains the query.
    /// Used by Offline Explorer search when the device is not connected.
    /// </summary>
    public List<FileNodeDto> SearchCache(string deviceId, string query, string? startPath, bool recursive, int maxResults)
    {
        var results = new List<FileNodeDto>();
        if (string.IsNullOrWhiteSpace(query)) return results;
        string normalizedQuery = query.Trim();
        string normalizedStart = NormalizeRelPath(startPath ?? "");
        bool hasStart = !string.IsNullOrEmpty(normalizedStart);
        string startPrefix = hasStart ? normalizedStart + "/" : "";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string dir = Path.Combine(CacheBaseDir, deviceId);
        if (!Directory.Exists(dir)) return results;
        foreach (string jsonPath in Directory.GetFiles(dir, "folder_*.json"))
        {
            if (results.Count >= maxResults) break;
            try
            {
                string json = File.ReadAllText(jsonPath);
                var entry = JsonSerializer.Deserialize<FolderCacheEntry>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (entry?.Children == null) continue;
                foreach (var child in entry.Children)
                {
                    if (results.Count >= maxResults) break;
                    if (child == null || string.IsNullOrEmpty(child.Name)) continue;
                    if (child.Name.IndexOf(normalizedQuery, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    string rel = child.RelativePath ?? "";
                    string normRel = NormalizeRelPath(rel);
                    if (hasStart)
                    {
                        if (recursive)
                        {
                            if (!normRel.Equals(normalizedStart, StringComparison.OrdinalIgnoreCase) &&
                                !normRel.StartsWith(startPrefix, StringComparison.OrdinalIgnoreCase))
                                continue;
                        }
                        else
                        {
                            string parent = "";
                            int slash = normRel.LastIndexOf('/');
                            if (slash >= 0) parent = normRel.Substring(0, slash);
                            if (!parent.Equals(normalizedStart, StringComparison.OrdinalIgnoreCase))
                                continue;
                        }
                    }
                    string dedupKey = string.IsNullOrEmpty(normRel) ? child.Name : normRel;
                    if (!seen.Add(dedupKey)) continue;
                    results.Add(child);
                }
            }
            catch { }
        }
        return results;
    }

}

public class CacheMetadata
{
    public string RelativePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public long OriginalLength { get; set; }
    public long ProcessedLength { get; set; }
    public long OriginalPreviewLength { get; set; }
    public string MediaType { get; set; } = "";
    public DateTime CachedTimeUtc { get; set; }
    public float FrameNormalized { get; set; } = -1f;
    public float Quality { get; set; } = -1f;

    // Full-file cache entries (actual video / audio / other file bytes).
    // Records the transcode/quality settings that produced them so suitability
    // can be re-checked on every fetch (same rule as thumbnails: never downgrade).
    public bool IsFullFile { get; set; }
    public float BitrateMultiplier { get; set; } = -1f;
    public float ResolutionMultiplier { get; set; } = -1f;
    public int BitrateCapBps { get; set; } = -1;
    public int ResolutionCeilingPx { get; set; } = -1;
}

public class CachedVideoFrame
{
    public float FrameNormalized { get; set; }
    public float Quality { get; set; } = -1f;
    public DateTime CachedTimeUtc { get; set; }
}

public class CacheDeviceInfo
{
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public long TotalSizeBytes { get; set; }
    public long ImageSizeBytes { get; set; }
    public long VideoSizeBytes { get; set; }
    public long AudioSizeBytes { get; set; }
    public long FileSizeBytes { get; set; }
    public int TotalFileCount { get; set; }
    public int ImageFileCount { get; set; }
    public int VideoFileCount { get; set; }
    public int AudioFileCount { get; set; }
    public int FileFileCount { get; set; }
    public DateTime LastAccessedUtc { get; set; }
}

public class FolderCacheEntry
{
    public string FolderPath { get; set; } = "";
    public DateTime CachedTimeUtc { get; set; }
    public bool Exists { get; set; }
    public FileNodeDto? CurrentNode { get; set; }
    public List<FileNodeDto> Children { get; set; } = new();
}

public class CacheImportResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string? DeviceId { get; set; }
}

public class FolderChildCounts
{
    public int Images { get; set; }
    public int Videos { get; set; }
    public int Audio { get; set; }
    public int Other { get; set; }
}