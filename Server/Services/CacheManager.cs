using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
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

    public static string GetDeviceDbPath(string deviceId)
    {
        // New location: inside device folder (Cache/{deviceId}/{deviceId}.db) as requested
        string dir = GetDeviceCacheDir(deviceId);
        string newPath = Path.Combine(dir, deviceId + ".db");
        // Migrate old location (Cache/{deviceId}.db) to new if exists
        string oldPath = Path.Combine(CacheBaseDir, deviceId + ".db");
        if (File.Exists(oldPath) && !File.Exists(newPath))
        {
            try { File.Move(oldPath, newPath); } catch {}
            foreach (var ext in new[] { "-wal", "-shm" })
            {
                string oldWal = oldPath + ext;
                string newWal = newPath + ext;
                if (File.Exists(oldWal) && !File.Exists(newWal))
                    try { File.Move(oldWal, newWal); } catch {}
            }
        }
        return newPath;
    }

    private static readonly ConcurrentDictionary<string, bool> _schemaEnsured = new();
    // Serializes first-time schema creation per db file. Without this, 60+
    // unlimited-lane workers racing GetConnection on a fresh file partial-create
    // the schema (only thumbnails+index win) and poison _schemaEnsured forever.
    private static readonly ConcurrentDictionary<string, object> _schemaLocks = new();

    /// <summary>
    /// Forgets schema-ensured state for one db file (used after wiping it), so
    /// the next GetConnection recreates the full schema instead of assuming it.
    /// </summary>
    private static void DropEnsureFlags(string dbPath)
    {
        _schemaEnsured.TryRemove(dbPath, out _);
        _schemaEnsured.TryRemove(dbPath + "_pragma", out _);
    }
    private static SqliteConnection GetConnection(string deviceId, bool createIfNotExists = true)
    {
        string dbPath = GetDeviceDbPath(deviceId);
        bool isNew = !File.Exists(dbPath);
        if (isNew && createIfNotExists)
        {
            string legacyDir = Path.Combine(CacheBaseDir, deviceId);
            if (Directory.Exists(legacyDir) && Directory.GetFiles(legacyDir, "*.json").Length > 0)
            {
            }
        }
        var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        // Wait on locked DB instead of failing instantly: fetch workers write
        // concurrently, and the default busy timeout (0) turns every collision
        // into an immediate SQLITE_BUSY error (silently swallowed -> "fetched"
        // files that were never saved).
        try { using var busy = conn.CreateCommand(); busy.CommandText = "PRAGMA busy_timeout=15000;"; busy.ExecuteNonQuery(); } catch {}
        // WAL for concurrent SYSTEM + Kestrel - only set once per db file, not per connection
        string pragmaKey = dbPath + "_pragma";
        if (!_schemaEnsured.ContainsKey(pragmaKey))
        {
            try { using var cmd = conn.CreateCommand(); cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;"; cmd.ExecuteNonQuery(); _schemaEnsured[pragmaKey] = true; } catch {}
        }
        object gate = _schemaLocks.GetOrAdd(dbPath, _ => new object());
        lock (gate)
        {
            if (isNew)
            {
                EnsureSchema(conn);
                _schemaEnsured[dbPath] = true;
                string legacyDir = Path.Combine(CacheBaseDir, deviceId);
                if (Directory.Exists(legacyDir))
                {
                    try { MigrateLegacyDirToDb(deviceId, legacyDir, conn); } catch {}
                }
            }
            else if (!_schemaEnsured.ContainsKey(dbPath))
            {
                EnsureSchema(conn);
                _schemaEnsured[dbPath] = true;
            }
        }
        return conn;
    }

    private static void EnsureSchema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS thumbnails(
                relativePath TEXT NOT NULL,
                frame INTEGER NOT NULL,
                quality REAL NOT NULL,
                data BLOB NOT NULL,
                originalLength INTEGER,
                processedLength INTEGER,
                originalPreviewLength INTEGER,
                mediaType TEXT,
                fileName TEXT,
                cachedTimeUtc TEXT,
                PRIMARY KEY(relativePath, frame)
            );
            CREATE INDEX IF NOT EXISTS idx_thumb_quality ON thumbnails(relativePath, quality);
            CREATE TABLE IF NOT EXISTS fullFiles(
                relativePath TEXT PRIMARY KEY,
                fileName TEXT,
                data BLOB,
                originalLength INTEGER,
                processedLength INTEGER,
                mediaType TEXT,
                cachedTimeUtc TEXT,
                frame REAL,
                quality REAL,
                bitrateMultiplier REAL,
                resolutionMultiplier REAL,
                bitrateCapBps INTEGER,
                resolutionCeilingPx INTEGER
            ) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS folders(
                folderPath TEXT PRIMARY KEY,
                json TEXT NOT NULL,
                cachedTimeUtc TEXT
            ) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS deviceMeta(
                key TEXT PRIMARY KEY,
                value TEXT
            ) WITHOUT ROWID;
        ";
        cmd.ExecuteNonQuery();
    }

    private static void MigrateLegacyDirToDb(string deviceId, string legacyDir, SqliteConnection conn)
    {
        if (!Directory.Exists(legacyDir)) return;
        var files = Directory.GetFiles(legacyDir, "*.json");
        if (files.Length == 0) return;
        using var tx = conn.BeginTransaction();
        foreach (var jsonPath in files)
        {
            string fileName = Path.GetFileName(jsonPath);
            if (fileName.StartsWith("folder_", StringComparison.OrdinalIgnoreCase)) continue;
            if (fileName.StartsWith("FULL_", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    string json = File.ReadAllText(jsonPath);
                    var meta = JsonSerializer.Deserialize<CacheMetadata>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (meta == null) continue;
                    string datPath = Path.Combine(legacyDir, Path.GetFileNameWithoutExtension(jsonPath) + ".dat");
                    if (!File.Exists(datPath)) continue;
                    byte[] data = File.ReadAllBytes(datPath);
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "INSERT OR REPLACE INTO fullFiles(relativePath, fileName, data, originalLength, processedLength, mediaType, cachedTimeUtc, frame, quality, bitrateMultiplier, resolutionMultiplier, bitrateCapBps, resolutionCeilingPx) VALUES($p, $fn, $d, $ol, $pl, $mt, $ct, $fr, $q, $brm, $resm, $brcap, $resceil)";
                    cmd.Parameters.AddWithValue("$p", meta.RelativePath ?? "");
                    cmd.Parameters.AddWithValue("$fn", meta.FileName ?? "");
                    cmd.Parameters.AddWithValue("$d", data);
                    cmd.Parameters.AddWithValue("$ol", meta.OriginalLength);
                    cmd.Parameters.AddWithValue("$pl", meta.ProcessedLength);
                    cmd.Parameters.AddWithValue("$mt", meta.MediaType ?? "");
                    cmd.Parameters.AddWithValue("$ct", meta.CachedTimeUtc.ToString("O"));
                    cmd.Parameters.AddWithValue("$fr", meta.FrameNormalized);
                    cmd.Parameters.AddWithValue("$q", meta.Quality);
                    cmd.Parameters.AddWithValue("$brm", meta.BitrateMultiplier);
                    cmd.Parameters.AddWithValue("$resm", meta.ResolutionMultiplier);
                    cmd.Parameters.AddWithValue("$brcap", meta.BitrateCapBps);
                    cmd.Parameters.AddWithValue("$resceil", meta.ResolutionCeilingPx);
                    cmd.Transaction = tx;
                    cmd.ExecuteNonQuery();
                }
                catch {}
                continue;
            }
            // Thumbnail: Hash.json
            try
            {
                string json = File.ReadAllText(jsonPath);
                var meta = JsonSerializer.Deserialize<CacheMetadata>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (meta == null) continue;
                string datPath = Path.Combine(legacyDir, Path.GetFileNameWithoutExtension(jsonPath) + ".dat");
                if (!File.Exists(datPath)) continue;
                byte[] data = File.ReadAllBytes(datPath);
                int frameKey = meta.FrameNormalized < 0 ? -1 : (int)Math.Round(meta.FrameNormalized * 100);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "INSERT OR REPLACE INTO thumbnails(relativePath, frame, quality, data, originalLength, processedLength, originalPreviewLength, mediaType, fileName, cachedTimeUtc) VALUES($p, $f, $q, $d, $ol, $pl, $opl, $mt, $fn, $ct)";
                cmd.Parameters.AddWithValue("$p", meta.RelativePath ?? "");
                cmd.Parameters.AddWithValue("$f", frameKey);
                cmd.Parameters.AddWithValue("$q", meta.Quality);
                cmd.Parameters.AddWithValue("$d", data);
                cmd.Parameters.AddWithValue("$ol", meta.OriginalLength);
                cmd.Parameters.AddWithValue("$pl", meta.ProcessedLength);
                cmd.Parameters.AddWithValue("$opl", meta.OriginalPreviewLength);
                cmd.Parameters.AddWithValue("$mt", meta.MediaType ?? "");
                cmd.Parameters.AddWithValue("$fn", meta.FileName ?? "");
                cmd.Parameters.AddWithValue("$ct", meta.CachedTimeUtc.ToString("O"));
                cmd.Transaction = tx;
                cmd.ExecuteNonQuery();
            }
            catch {}
        }
        // Folders
        foreach (var jsonPath in Directory.GetFiles(legacyDir, "folder_*.json"))
        {
            try
            {
                string json = File.ReadAllText(jsonPath);
                var entry = JsonSerializer.Deserialize<FolderCacheEntry>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (entry == null) continue;
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "INSERT OR REPLACE INTO folders(folderPath, json, cachedTimeUtc) VALUES($fp, $j, $ct)";
                cmd.Parameters.AddWithValue("$fp", entry.FolderPath ?? "");
                cmd.Parameters.AddWithValue("$j", json);
                cmd.Parameters.AddWithValue("$ct", entry.CachedTimeUtc.ToString("O"));
                cmd.Transaction = tx;
                cmd.ExecuteNonQuery();
            }
            catch {}
        }
        // deviceMeta
        string nameFile = Path.Combine(legacyDir, "device_name.txt");
        if (File.Exists(nameFile))
        {
            try
            {
                string name = File.ReadAllText(nameFile).Trim();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "INSERT OR REPLACE INTO deviceMeta(key, value) VALUES('deviceName', $v)";
                cmd.Parameters.AddWithValue("$v", name);
                cmd.Transaction = tx;
                cmd.ExecuteNonQuery();
            }
            catch {}
        }
        string lastFile = Path.Combine(legacyDir, "last_accessed.txt");
        if (File.Exists(lastFile))
        {
            try
            {
                string last = File.ReadAllText(lastFile).Trim();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "INSERT OR REPLACE INTO deviceMeta(key, value) VALUES('lastAccess', $v)";
                cmd.Parameters.AddWithValue("$v", last);
                cmd.Transaction = tx;
                cmd.ExecuteNonQuery();
            }
            catch {}
        }
        // Also store deviceId and cacheHashVersion
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "INSERT OR REPLACE INTO deviceMeta(key, value) VALUES('deviceId', $v)";
            cmd.Parameters.AddWithValue("$v", deviceId);
            cmd.Transaction = tx;
            cmd.ExecuteNonQuery();
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "INSERT OR REPLACE INTO deviceMeta(key, value) VALUES('cacheHashVersion', $v)";
            cmd.Parameters.AddWithValue("$v", CacheHashVersion);
            cmd.Transaction = tx;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        // Optionally keep legacy dir as backup: rename to .migrated
        try { Directory.Move(legacyDir, legacyDir + ".migrated"); } catch {}
        try { Directory.CreateDirectory(legacyDir); } catch {}
        // Re-create device_name and last_accessed for compat if needed
        try
        {
            var name = GetDeviceNameFromDb(conn);
            if (!string.IsNullOrEmpty(name)) File.WriteAllText(Path.Combine(legacyDir, "device_name.txt"), name);
        }
        catch {}
    }

    private static string GetDeviceNameFromDb(SqliteConnection conn)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM deviceMeta WHERE key='deviceName'";
            var res = cmd.ExecuteScalar();
            return res?.ToString() ?? "";
        }
        catch { return ""; }
    }

    private static readonly ConcurrentDictionary<string, DateTime> _lastAccessCache = new();
    public static void UpdateDeviceAccessTime(string deviceId)
    {
        // Debounce: only update if last update was >60s ago to avoid fan spin on 40 concurrent thumbs
        if (_lastAccessCache.TryGetValue(deviceId, out var last) && (DateTime.UtcNow - last).TotalSeconds < 60) return;
        _lastAccessCache[deviceId] = DateTime.UtcNow;
        try
        {
            using var conn = GetConnection(deviceId);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO deviceMeta(key, value) VALUES('lastAccess', $v)";
            cmd.Parameters.AddWithValue("$v", DateTime.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        catch {}
    }

    public static DateTime GetDeviceAccessTime(string deviceId)
    {
        // Try db first
        string dbPath = GetDeviceDbPath(deviceId);
        if (File.Exists(dbPath))
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={dbPath}");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT value FROM deviceMeta WHERE key='lastAccess'";
                var res = cmd.ExecuteScalar();
                if (res != null && DateTime.TryParse(res.ToString(), null, DateTimeStyles.RoundtripKind, out var dt)) return dt;
            }
            catch {}
        }
        // Fallback to legacy file
        string dir = Path.Combine(CacheBaseDir, deviceId);
        string file = Path.Combine(dir, "last_accessed.txt");
        if (!File.Exists(file)) return DateTime.MinValue;
        string content = File.ReadAllText(file).Trim();
        if (DateTime.TryParse(content, null, DateTimeStyles.RoundtripKind, out var dt2)) return dt2;
        return DateTime.MinValue;
    }

    private static readonly string CacheHashVersion = "v5-thumb-size-cap";

    private static string HashPath(string input)
    {
        using var sha256 = SHA256.Create();
        byte[] hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(CacheHashVersion + input));
        return Convert.ToHexString(hash).Substring(0, 16).ToUpperInvariant();
    }

    private static string NormalizeRelPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        return path.Trim().Trim('/');
    }

    private static IEnumerable<string> RelPathVariants(string path)
    {
        string norm = NormalizeRelPath(path);
        yield return norm;
        if (norm.Length > 0) yield return "/" + norm;
    }

    public static string FrameSuffix(float frame)
    {
        int key = Math.Clamp((int)Math.Round(frame * 100f), 0, 1000);
        return $"F{key:D4}";
    }

    public static string? GetFrameEntryName(string relativePath, float frame)
    {
        if (frame < 0f) return null;
        return $"{HashPath(relativePath)}_{FrameSuffix(frame)}";
    }

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
        string norm = NormalizeRelPath(relativePath);
        int frameKey = frame < 0 ? -1 : (int)Math.Round(frame * 100);
        // Try exact frame first
        try
        {
            using var conn = GetConnection(deviceId, false);
            // Don't create db if not exists for read
            string dbPath = GetDeviceDbPath(deviceId);
            if (!File.Exists(dbPath)) return false;
            using var cmd = conn.CreateCommand();
            if (frame >= 0)
            {
                cmd.CommandText = "SELECT data, originalLength, processedLength, originalPreviewLength, mediaType, fileName, cachedTimeUtc, frame, quality FROM thumbnails WHERE relativePath IN ($p,$ps) AND frame=$f";
                cmd.Parameters.AddWithValue("$p", norm);
                cmd.Parameters.AddWithValue("$ps", "/" + norm);
                cmd.Parameters.AddWithValue("$f", frameKey);
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    data = (byte[])reader["data"];
                    metadata = new CacheMetadata
                    {
                        RelativePath = norm,
                        FileName = reader["fileName"]?.ToString() ?? "",
                        OriginalLength = reader["originalLength"] is long ol ? ol : Convert.ToInt64(reader["originalLength"]),
                        ProcessedLength = reader["processedLength"] is long pl ? pl : Convert.ToInt64(reader["processedLength"]),
                        OriginalPreviewLength = reader["originalPreviewLength"] is long opl ? opl : Convert.ToInt64(reader["originalPreviewLength"]),
                        MediaType = reader["mediaType"]?.ToString() ?? "",
                        CachedTimeUtc = DateTime.TryParse(reader["cachedTimeUtc"]?.ToString(), out var ct) ? ct : DateTime.UtcNow,
                        FrameNormalized = frame,
                        Quality = Convert.ToSingle(reader["quality"])
                    };
                    // Check suitability: quality and frame
                    string ext = Path.GetExtension(metadata.FileName ?? "");
                    bool isVid = VideoExtensions.Contains(ext);
                    if (IsThumbnailSuitable(metadata, frame, quality, isVid))
                    {
                        UpdateDeviceAccessTime(deviceId);
                        return true;
                    }
                    // If not suitable, fall through to best frame search
                }
            }
            else
            {
                // Image single entry: frame -1
                cmd.CommandText = "SELECT data, originalLength, processedLength, originalPreviewLength, mediaType, fileName, cachedTimeUtc, quality FROM thumbnails WHERE relativePath IN ($p,$ps) AND frame=-1";
                cmd.Parameters.AddWithValue("$p", norm);
                cmd.Parameters.AddWithValue("$ps", "/" + norm);
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    data = (byte[])reader["data"];
                    metadata = new CacheMetadata
                    {
                        RelativePath = norm,
                        FileName = reader["fileName"]?.ToString() ?? "",
                        OriginalLength = reader["originalLength"] is long ol ? ol : Convert.ToInt64(reader["originalLength"]),
                        ProcessedLength = reader["processedLength"] is long pl ? pl : Convert.ToInt64(reader["processedLength"]),
                        OriginalPreviewLength = reader["originalPreviewLength"] is long opl ? opl : Convert.ToInt64(reader["originalPreviewLength"]),
                        MediaType = reader["mediaType"]?.ToString() ?? "",
                        CachedTimeUtc = DateTime.TryParse(reader["cachedTimeUtc"]?.ToString(), out var ct) ? ct : DateTime.UtcNow,
                        FrameNormalized = -1,
                        Quality = Convert.ToSingle(reader["quality"])
                    };
                    string ext2 = Path.GetExtension(metadata.FileName ?? "");
                    bool isVid2 = VideoExtensions.Contains(ext2);
                    if (IsThumbnailSuitable(metadata, frame, quality, isVid2))
                    {
                        UpdateDeviceAccessTime(deviceId);
                        return true;
                    }
                }
                // For image with frame -1, if not suitable, try best frame fallback for video? Actually for image, no per-frame, so return false
                // For video with frame -1 requested but we have per-frame entries, find best
                if (frame < 0)
                {
                    // Find best frame for this relativePath
                    using var cmd2 = conn.CreateCommand();
                    cmd2.CommandText = "SELECT data, originalLength, processedLength, originalPreviewLength, mediaType, fileName, cachedTimeUtc, frame, quality FROM thumbnails WHERE relativePath IN ($p,$ps) ORDER BY quality DESC, cachedTimeUtc DESC LIMIT 1";
                    cmd2.Parameters.AddWithValue("$p", norm);
                    cmd2.Parameters.AddWithValue("$ps", "/" + norm);
                    using var reader2 = cmd2.ExecuteReader();
                    if (reader2.Read())
                    {
                        data = (byte[])reader2["data"];
                        int fKey = Convert.ToInt32(reader2["frame"]);
                        float fr = fKey == -1 ? -1 : fKey / 100f;
                        metadata = new CacheMetadata
                        {
                            RelativePath = norm,
                            FileName = reader2["fileName"]?.ToString() ?? "",
                            OriginalLength = reader2["originalLength"] is long ol ? ol : Convert.ToInt64(reader2["originalLength"]),
                            ProcessedLength = reader2["processedLength"] is long pl ? pl : Convert.ToInt64(reader2["processedLength"]),
                            OriginalPreviewLength = reader2["originalPreviewLength"] is long opl ? opl : Convert.ToInt64(reader2["originalPreviewLength"]),
                            MediaType = reader2["mediaType"]?.ToString() ?? "",
                            CachedTimeUtc = DateTime.TryParse(reader2["cachedTimeUtc"]?.ToString(), out var ct) ? ct : DateTime.UtcNow,
                            FrameNormalized = fr,
                            Quality = Convert.ToSingle(reader2["quality"])
                        };
                        UpdateDeviceAccessTime(deviceId);
                        return true;
                    }
                }
            }
            // If exact not found or not suitable, try best frame fallback for video with specific frame requested but not found
            if (frame >= 0)
            {
                using var cmd3 = conn.CreateCommand();
                cmd3.CommandText = "SELECT data, originalLength, processedLength, originalPreviewLength, mediaType, fileName, cachedTimeUtc, frame, quality FROM thumbnails WHERE relativePath IN ($p,$ps) ORDER BY quality DESC, cachedTimeUtc DESC LIMIT 1";
                cmd3.Parameters.AddWithValue("$p", norm);
                cmd3.Parameters.AddWithValue("$ps", "/" + norm);
                using var reader3 = cmd3.ExecuteReader();
                if (reader3.Read())
                {
                    // Only return if quality is suitable and frame close?
                    // For now return best if exists
                    data = (byte[])reader3["data"];
                    int fKey = Convert.ToInt32(reader3["frame"]);
                    float fr = fKey == -1 ? -1 : fKey / 100f;
                    var tmpMeta = new CacheMetadata
                    {
                        RelativePath = norm,
                        FileName = reader3["fileName"]?.ToString() ?? "",
                        OriginalLength = reader3["originalLength"] is long ol ? ol : Convert.ToInt64(reader3["originalLength"]),
                        ProcessedLength = reader3["processedLength"] is long pl ? pl : Convert.ToInt64(reader3["processedLength"]),
                        OriginalPreviewLength = reader3["originalPreviewLength"] is long opl ? opl : Convert.ToInt64(reader3["originalPreviewLength"]),
                        MediaType = reader3["mediaType"]?.ToString() ?? "",
                        CachedTimeUtc = DateTime.TryParse(reader3["cachedTimeUtc"]?.ToString(), out var ct) ? ct : DateTime.UtcNow,
                        FrameNormalized = fr,
                        Quality = Convert.ToSingle(reader3["quality"])
                    };
                    string ext3 = Path.GetExtension(tmpMeta.FileName ?? "");
                    bool isVid3 = VideoExtensions.Contains(ext3);
                    if (IsThumbnailSuitable(tmpMeta, frame, quality, isVid3))
                    {
                        metadata = tmpMeta;
                        UpdateDeviceAccessTime(deviceId);
                        return true;
                    }
                }
            }
        }
        catch {}
        return false;
    }

    public float GetBestCachedThumbnailQuality(string deviceId, string relativePath)
    {
        string norm = NormalizeRelPath(relativePath);
        string dbPath = GetDeviceDbPath(deviceId);
        if (!File.Exists(dbPath)) return -1f;
        try
        {
            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT MAX(quality) FROM thumbnails WHERE relativePath=$p";
            cmd.Parameters.AddWithValue("$p", norm);
            var res = cmd.ExecuteScalar();
            if (res != null && res != DBNull.Value && float.TryParse(res.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var q)) return q;
        }
        catch {}
        return -1f;
    }

    private static bool ReadCacheEntry(string deviceId, string dir, string entryName, out byte[]? data, out CacheMetadata? metadata)
    {
        data = null; metadata = null; return false;
    }

    private static string? FindBestFrameEntry(string dir, string baseHash) { return null; }

    private static bool IsBetterFrame(CacheMetadata a, CacheMetadata b)
    {
        if (a.Quality > b.Quality + 0.0001f) return true;
        if (Math.Abs(a.Quality - b.Quality) <= 0.0001f && a.CachedTimeUtc > b.CachedTimeUtc) return true;
        return false;
    }

    public List<CachedVideoFrame> GetCachedVideoFrames(string deviceId, string relativePath)
    {
        var result = new SortedDictionary<int, CachedVideoFrame>();
        string norm = NormalizeRelPath(relativePath);
        string dbPath = GetDeviceDbPath(deviceId);
        if (!File.Exists(dbPath)) return new List<CachedVideoFrame>();
        try
        {
            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT frame, quality, cachedTimeUtc FROM thumbnails WHERE relativePath=$p AND frame != -1 ORDER BY frame";
            cmd.Parameters.AddWithValue("$p", norm);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                int fKey = Convert.ToInt32(reader["frame"]);
                float frame = fKey / 100f;
                float qual = Convert.ToSingle(reader["quality"]);
                DateTime ct = DateTime.TryParse(reader["cachedTimeUtc"]?.ToString(), out var dt) ? dt : DateTime.UtcNow;
                int key = (int)Math.Round(frame * 100f);
                if (result.TryGetValue(key, out var cur))
                {
                    if (qual < cur.Quality - 0.0001f) continue;
                    if (Math.Abs(qual - cur.Quality) <= 0.0001f && ct <= cur.CachedTimeUtc) continue;
                }
                result[key] = new CachedVideoFrame { FrameNormalized = frame, Quality = qual, CachedTimeUtc = ct };
            }
        }
        catch {}
        return result.Values.ToList();
    }

    public bool SaveCachedThumbnail(string deviceId, string relativePath, string fileName, byte[] data, long originalLength, string mediaType, float frame = -1f, float quality = -1f, long originalPreviewLength = 0)
    {
        // Saving an empty blob creates a corrupt row: stats would count phantom
        // bytes for it, the exporter skips it, and the viewer warns on it.
        // Refuse and let the caller count it as failed (honest re-fetch later).
        if (data == null || data.Length == 0) return false;
        string norm = NormalizeRelPath(relativePath);
        int frameKey = frame < 0 ? -1 : (int)Math.Round(frame * 100);
        try
        {
            using var conn = GetConnection(deviceId);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO thumbnails(relativePath, frame, quality, data, originalLength, processedLength, originalPreviewLength, mediaType, fileName, cachedTimeUtc) VALUES($p, $f, $q, $d, $ol, $pl, $opl, $mt, $fn, $ct)";
            cmd.Parameters.AddWithValue("$p", norm);
            cmd.Parameters.AddWithValue("$f", frameKey);
            cmd.Parameters.AddWithValue("$q", quality);
            cmd.Parameters.AddWithValue("$d", data);
            cmd.Parameters.AddWithValue("$ol", originalLength);
            cmd.Parameters.AddWithValue("$pl", data.Length);
            cmd.Parameters.AddWithValue("$opl", originalPreviewLength);
            cmd.Parameters.AddWithValue("$mt", mediaType ?? "");
            cmd.Parameters.AddWithValue("$fn", fileName ?? "");
            cmd.Parameters.AddWithValue("$ct", DateTime.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
            UpdateDeviceAccessTime(deviceId);
            return true;
        }
        catch (Exception ex)
        {
            // DIAGNOSTIC (temporary): silent write loss investigation.
            // Extended code distinguishes ghost-inode (DBMOVED=262) from WAL
            // recovery mode (264) from plain readonly (8).
            try
            {
                int ext = (ex as Microsoft.Data.Sqlite.SqliteException)?.SqliteExtendedErrorCode ?? -1;
                Console.WriteLine($"[CacheDIAG] SaveCachedThumbnail failed for '{relativePath}': {ex.GetType().Name}: {ex.Message} (ext={ext})");
            }
            catch { }
            return false;
        }
    }

    public static string FullFileEntryName(string relativePath) => $"FULL_{HashPath(NormalizeRelPath(relativePath))}";

    public static bool IsFullFileEntry(string entryName) =>
        entryName.StartsWith("FULL_", StringComparison.OrdinalIgnoreCase);

    private static string FullFileDatPath(string deviceId, string relativePath) =>
        Path.Combine(GetDeviceCacheDir(deviceId), $"{FullFileEntryName(relativePath)}.dat");

    private static string FullFileJsonPath(string deviceId, string relativePath) =>
        Path.Combine(GetDeviceCacheDir(deviceId), $"{FullFileEntryName(relativePath)}.json");

    public void SaveCachedFile(
        string deviceId, string relativePath, string fileName, byte[] data, long originalLength,
        string mediaType, float frame = -1f, float quality = -1f, float bitrateMultiplier = -1f,
        float resolutionMultiplier = -1f, int bitrateCapBps = -1, int resolutionCeilingPx = -1)
    {
        string norm = NormalizeRelPath(relativePath);
        try
        {
            using var conn = GetConnection(deviceId);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO fullFiles(relativePath, fileName, data, originalLength, processedLength, mediaType, cachedTimeUtc, frame, quality, bitrateMultiplier, resolutionMultiplier, bitrateCapBps, resolutionCeilingPx) VALUES($p, $fn, $d, $ol, $pl, $mt, $ct, $fr, $q, $brm, $resm, $brcap, $resceil)";
            cmd.Parameters.AddWithValue("$p", norm);
            cmd.Parameters.AddWithValue("$fn", fileName ?? "");
            cmd.Parameters.AddWithValue("$d", data);
            cmd.Parameters.AddWithValue("$ol", originalLength);
            cmd.Parameters.AddWithValue("$pl", data.Length);
            cmd.Parameters.AddWithValue("$mt", mediaType ?? "");
            cmd.Parameters.AddWithValue("$ct", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$fr", frame);
            cmd.Parameters.AddWithValue("$q", quality);
            cmd.Parameters.AddWithValue("$brm", bitrateMultiplier);
            cmd.Parameters.AddWithValue("$resm", resolutionMultiplier);
            cmd.Parameters.AddWithValue("$brcap", bitrateCapBps);
            cmd.Parameters.AddWithValue("$resceil", resolutionCeilingPx);
            cmd.ExecuteNonQuery();
            UpdateDeviceAccessTime(deviceId);
        }
        catch {}
    }

    public string? BeginStreamedFile(
        string deviceId, string relativePath, bool allowReplace)
    {
        // For SQLite, we use a temp .part file in the device dir, but track via filesystem
        string dir = GetDeviceCacheDir(deviceId);
        string part = Path.Combine(dir, $"{FullFileEntryName(relativePath)}.part");
        if (File.Exists(part)) File.Delete(part);
        return part;
    }

    public void CompleteFullFile(string partPath, string deviceId, string relativePath, string fileName,
        long transferredBytes, long originalLength, string mediaType, float frame = -1f, float quality = -1f,
        float bitrateMultiplier = -1f, float resolutionMultiplier = -1f, int bitrateCapBps = -1, int resolutionCeilingPx = -1)
    {
        try
        {
            byte[] data = File.ReadAllBytes(partPath);
            File.Delete(partPath);
            SaveCachedFile(deviceId, relativePath, fileName, data, originalLength, mediaType, frame, quality, bitrateMultiplier, resolutionMultiplier, bitrateCapBps, resolutionCeilingPx);
        }
        catch {}
    }

    public void DeleteFullFile(string deviceId, string relativePath)
    {
        string norm = NormalizeRelPath(relativePath);
        try
        {
            using var conn = GetConnection(deviceId);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM fullFiles WHERE relativePath=$p";
            cmd.Parameters.AddWithValue("$p", norm);
            cmd.ExecuteNonQuery();
        }
        catch {}
        // Also delete legacy file if exists
        foreach (var p in new[] { FullFileDatPath(deviceId, relativePath), FullFileJsonPath(deviceId, relativePath) })
            if (File.Exists(p)) try { File.Delete(p); } catch {}
    }

    public bool HasFullFile(string deviceId, string relativePath)
    {
        string norm = NormalizeRelPath(relativePath);
        string dbPath = GetDeviceDbPath(deviceId);
        if (File.Exists(dbPath))
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={dbPath}");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT 1 FROM fullFiles WHERE relativePath=$p LIMIT 1";
                cmd.Parameters.AddWithValue("$p", norm);
                var res = cmd.ExecuteScalar();
                if (res != null) return true;
            }
            catch {}
        }
        // Fallback to legacy
        string dir = Path.Combine(CacheBaseDir, deviceId);
        foreach (string variant in RelPathVariants(relativePath))
        {
            string name = FullFileEntryName(variant);
            string dat = Path.Combine(dir, $"{name}.dat");
            string json = Path.Combine(dir, $"{name}.json");
            if (File.Exists(dat) && File.Exists(json)) return true;
        }
        return false;
    }

    public string? GetFullFilePath(string deviceId, string relativePath)
    {
        // For SQLite, we return a temp file path with the data extracted
        string norm = NormalizeRelPath(relativePath);
        string dbPath = GetDeviceDbPath(deviceId);
        if (File.Exists(dbPath))
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={dbPath}");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT data FROM fullFiles WHERE relativePath=$p";
                cmd.Parameters.AddWithValue("$p", norm);
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    byte[] data = (byte[])reader["data"];
                    string tmp = Path.Combine(Path.GetTempPath(), $"full_{HashPath(norm)}.dat");
                    File.WriteAllBytes(tmp, data);
                    return tmp;
                }
            }
            catch {}
        }
        // Fallback to legacy file
        string dir = Path.Combine(CacheBaseDir, deviceId);
        foreach (string variant in RelPathVariants(relativePath))
        {
            string name = FullFileEntryName(variant);
            string dat = Path.Combine(dir, $"{name}.dat");
            string json = Path.Combine(dir, $"{name}.json");
            if (File.Exists(dat) && File.Exists(json)) return dat;
        }
        return null;
    }

    public CacheMetadata? GetFullFileMetadata(string deviceId, string relativePath)
    {
        string norm = NormalizeRelPath(relativePath);
        string dbPath = GetDeviceDbPath(deviceId);
        if (File.Exists(dbPath))
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={dbPath}");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT relativePath, fileName, originalLength, processedLength, mediaType, cachedTimeUtc, frame, quality, bitrateMultiplier, resolutionMultiplier, bitrateCapBps, resolutionCeilingPx FROM fullFiles WHERE relativePath=$p";
                cmd.Parameters.AddWithValue("$p", norm);
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    return new CacheMetadata
                    {
                        RelativePath = reader["relativePath"]?.ToString() ?? "",
                        FileName = reader["fileName"]?.ToString() ?? "",
                        OriginalLength = Convert.ToInt64(reader["originalLength"]),
                        ProcessedLength = Convert.ToInt64(reader["processedLength"]),
                        MediaType = reader["mediaType"]?.ToString() ?? "",
                        CachedTimeUtc = DateTime.TryParse(reader["cachedTimeUtc"]?.ToString(), out var ct) ? ct : DateTime.UtcNow,
                        FrameNormalized = reader["frame"] is double fr ? (float)fr : Convert.ToSingle(reader["frame"]),
                        Quality = reader["quality"] is double q ? (float)q : Convert.ToSingle(reader["quality"]),
                        IsFullFile = true,
                        BitrateMultiplier = reader["bitrateMultiplier"] is double brm ? (float)brm : Convert.ToSingle(reader["bitrateMultiplier"]),
                        ResolutionMultiplier = reader["resolutionMultiplier"] is double resm ? (float)resm : Convert.ToSingle(reader["resolutionMultiplier"]),
                        BitrateCapBps = Convert.ToInt32(reader["bitrateCapBps"]),
                        ResolutionCeilingPx = Convert.ToInt32(reader["resolutionCeilingPx"])
                    };
                }
            }
            catch {}
        }
        string dir2 = Path.Combine(CacheBaseDir, deviceId);
        foreach (string variant in RelPathVariants(relativePath))
        {
            string json = Path.Combine(dir2, $"{FullFileEntryName(variant)}.json");
            if (!File.Exists(json)) continue;
            try { return JsonSerializer.Deserialize<CacheMetadata>(File.ReadAllText(json), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); } catch { return null; }
        }
        return null;
    }

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
        var results = new List<CacheDeviceInfo>();
        if (!Directory.Exists(CacheBaseDir)) return results;
        // Enumerate both legacy dirs and .db files (now inside subfolders)
        var deviceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string dir in Directory.GetDirectories(CacheBaseDir))
            deviceIds.Add(Path.GetFileName(dir));
        foreach (string db in Directory.GetFiles(CacheBaseDir, "*.db", SearchOption.AllDirectories))
            deviceIds.Add(Path.GetFileNameWithoutExtension(db));
        foreach (string deviceId in deviceIds)
        {
            var info = GetCacheDeviceInfo(deviceId, cm);
            if (info != null) results.Add(info);
        }
        return results;
    }

    public CacheDeviceInfo? GetCacheDeviceInfo(string deviceId, ConnectionManager cm)
    {
        string dbPath = GetDeviceDbPath(deviceId);
        string legacyDir = Path.Combine(CacheBaseDir, deviceId);
        bool hasDb = File.Exists(dbPath);
        bool hasLegacy = Directory.Exists(legacyDir) && Directory.GetFiles(legacyDir, "*.json").Length > 0;
        if (!hasDb && !hasLegacy) return null;

        string deviceName = deviceId;
        // Try db deviceMeta
        if (hasDb)
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={dbPath}");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT value FROM deviceMeta WHERE key='deviceName'";
                var res = cmd.ExecuteScalar();
                if (res != null) deviceName = res.ToString() ?? deviceId;
            }
            catch {}
        }
        if (deviceName == deviceId)
        {
            string nameFile = Path.Combine(legacyDir, "device_name.txt");
            if (File.Exists(nameFile)) deviceName = File.ReadAllText(nameFile).Trim();
            else if (cm.TryGetDeviceIdentity(deviceId, out var identity) && identity != null) deviceName = identity.DeviceName;
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

        if (hasDb)
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={dbPath}");
                conn.Open();
                // Count thumbnails (group by relativePath). Rows with empty data
                // are corrupt leftovers (busy-era silent failures): unusable for
                // viewer and exporter alike, so they must not inflate stats.
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT relativePath, fileName, processedLength, originalLength, mediaType FROM thumbnails WHERE data IS NOT NULL AND LENGTH(data) > 0";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string rp = reader["relativePath"]?.ToString() ?? "";
                    if (string.IsNullOrEmpty(rp)) continue;
                    if (!seenPaths.Add(rp)) continue;
                    string fn = reader["fileName"]?.ToString() ?? "";
                    string ext = Path.GetExtension(fn ?? rp);
                    long storedBytes = reader["processedLength"] is long pl ? pl : Convert.ToInt64(reader["processedLength"]);
                    if (storedBytes <= 0) storedBytes = reader["originalLength"] is long ol ? ol : Convert.ToInt64(reader["originalLength"]);
                    totalSizeBytes += storedBytes;
                    totalFileCount++;
                    if (ImageExtensions.Contains(ext)) { imageSizeBytes += storedBytes; imageFileCount++; }
                    else if (VideoExtensions.Contains(ext)) { videoSizeBytes += storedBytes; videoFileCount++; }
                    else if (AudioExtensions.Contains(ext)) { audioSizeBytes += storedBytes; audioFileCount++; }
                    else { fileSizeBytes += storedBytes; fileFileCount++; }
                }
                // Also count fullFiles not already counted (different relativePaths).
                // Same empty-data exclusion as above.
                using var cmd2 = conn.CreateCommand();
                cmd2.CommandText = "SELECT relativePath, fileName, processedLength, originalLength, mediaType FROM fullFiles WHERE data IS NOT NULL AND LENGTH(data) > 0";
                using var reader2 = cmd2.ExecuteReader();
                while (reader2.Read())
                {
                    string rp = reader2["relativePath"]?.ToString() ?? "";
                    if (string.IsNullOrEmpty(rp) || !seenPaths.Add(rp)) continue;
                    string fn = reader2["fileName"]?.ToString() ?? "";
                    string ext = Path.GetExtension(fn ?? rp);
                    long storedBytes = reader2["processedLength"] is long pl ? pl : Convert.ToInt64(reader2["processedLength"]);
                    if (storedBytes <= 0) storedBytes = reader2["originalLength"] is long ol ? ol : Convert.ToInt64(reader2["originalLength"]);
                    totalSizeBytes += storedBytes;
                    totalFileCount++;
                    if (ImageExtensions.Contains(ext)) { imageSizeBytes += storedBytes; imageFileCount++; }
                    else if (VideoExtensions.Contains(ext)) { videoSizeBytes += storedBytes; videoFileCount++; }
                    else if (AudioExtensions.Contains(ext)) { audioSizeBytes += storedBytes; audioFileCount++; }
                    else { fileSizeBytes += storedBytes; fileFileCount++; }
                }
            }
            catch {}
            // Also include legacy files if any not yet migrated
            hasDb = false; // fall through to legacy count for remaining?
        }
        if (!hasDb || Directory.Exists(legacyDir))
        {
            // Legacy fallback for stats if db not yet fully migrated
            string dir = legacyDir;
            if (!Directory.Exists(dir)) return new CacheDeviceInfo
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
            foreach (string jsonPath in Directory.GetFiles(dir, "*.json"))
            {
                string fileName = Path.GetFileName(jsonPath);
                if (fileName.StartsWith("folder_", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    string json = File.ReadAllText(jsonPath);
                    var meta = JsonSerializer.Deserialize<CacheMetadata>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (meta == null) continue;
                    string key = string.IsNullOrEmpty(meta.RelativePath) ? fileName : meta.RelativePath;
                    if (!seenPaths.Add(key)) continue;
                    string ext = Path.GetExtension(meta.FileName ?? "");
                    long storedBytes = meta.ProcessedLength > 0 ? meta.ProcessedLength : meta.OriginalLength;
                    totalSizeBytes += storedBytes;
                    totalFileCount++;
                    if (ImageExtensions.Contains(ext)) { imageSizeBytes += storedBytes; imageFileCount++; }
                    else if (VideoExtensions.Contains(ext)) { videoSizeBytes += storedBytes; videoFileCount++; }
                    else if (AudioExtensions.Contains(ext)) { audioSizeBytes += storedBytes; audioFileCount++; }
                    else { fileSizeBytes += storedBytes; fileFileCount++; }
                }
                catch {}
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
        try
        {
            using var conn = GetConnection(deviceId);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO deviceMeta(key, value) VALUES('deviceName', $v)";
            cmd.Parameters.AddWithValue("$v", deviceName);
            cmd.ExecuteNonQuery();
        }
        catch {}
    }

    public string? TryResolveDeviceId(string deviceNameOrId)
    {
        // Check db files first
        foreach (string db in Directory.GetFiles(CacheBaseDir, "*.db", SearchOption.AllDirectories))
        {
            string id = Path.GetFileNameWithoutExtension(db);
            if (id.Equals(deviceNameOrId, StringComparison.OrdinalIgnoreCase)) return id;
            try
            {
                using var conn = new SqliteConnection($"Data Source={db}");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT value FROM deviceMeta WHERE key='deviceName' AND value=$v COLLATE NOCASE";
                cmd.Parameters.AddWithValue("$v", deviceNameOrId);
                var res = cmd.ExecuteScalar();
                if (res != null) return id;
            }
            catch {}
        }
        // Fallback to legacy dirs
        if (!Directory.Exists(CacheBaseDir)) return null;
        string directDir = Path.Combine(CacheBaseDir, deviceNameOrId);
        if (Directory.Exists(directDir)) return deviceNameOrId;
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
        {
            foreach (var db in Directory.GetFiles(CacheBaseDir, "*.db", SearchOption.AllDirectories)) try { File.Delete(db); } catch {}
            foreach (var wal in Directory.GetFiles(CacheBaseDir, "*.db-wal", SearchOption.AllDirectories)) try { File.Delete(wal); } catch {}
            foreach (var shm in Directory.GetFiles(CacheBaseDir, "*.db-shm", SearchOption.AllDirectories)) try { File.Delete(shm); } catch {}
            foreach (var dir in Directory.GetDirectories(CacheBaseDir))
                try { Directory.Delete(dir, true); } catch {}
        }
        Directory.CreateDirectory(CacheBaseDir);
        _schemaEnsured.Clear();
        try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); } catch {}
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
        string dbPath = GetDeviceDbPath(deviceId);
        if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch {}
        foreach (var ext in new[] { "-wal", "-shm" }) try { File.Delete(dbPath + ext); } catch {}
        string dir = Path.Combine(CacheBaseDir, deviceId);
        if (Directory.Exists(dir)) try { Directory.Delete(dir, true); } catch {}
        // Also delete .migrated backup
        string migrated = dir + ".migrated";
        if (Directory.Exists(migrated)) try { Directory.Delete(migrated, true); } catch {}
        DropEnsureFlags(dbPath);
        // Pooled connections may still reference the deleted inode (WAL mode):
        // reusing them turns every write into SQLITE_READONLY. Drop the pool.
        try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); } catch {}
    }

    public void ClearDeviceMediaType(string deviceId, string? mediaType)
    {
        if (string.IsNullOrEmpty(mediaType))
        {
            ClearDeviceCache(deviceId);
            return;
        }
        string dbPath = GetDeviceDbPath(deviceId);
        if (File.Exists(dbPath))
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={dbPath}");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM thumbnails WHERE mediaType=$mt COLLATE NOCASE";
                cmd.Parameters.AddWithValue("$mt", mediaType);
                cmd.ExecuteNonQuery();
                using var cmd2 = conn.CreateCommand();
                cmd2.CommandText = "DELETE FROM fullFiles WHERE mediaType=$mt COLLATE NOCASE";
                cmd2.Parameters.AddWithValue("$mt", mediaType);
                cmd2.ExecuteNonQuery();
                return;
            }
            catch {}
        }
        string dir = Path.Combine(CacheBaseDir, deviceId);
        if (!Directory.Exists(dir)) return;
        foreach (string jsonPath in Directory.GetFiles(dir, "*.json"))
        {
            string jsonFileName = Path.GetFileName(jsonPath);
            if (jsonFileName.StartsWith("folder_", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                string json = File.ReadAllText(jsonPath);
                var meta = JsonSerializer.Deserialize<CacheMetadata>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (meta != null && meta.MediaType.Equals(mediaType, StringComparison.OrdinalIgnoreCase))
                {
                    string hash = Path.GetFileNameWithoutExtension(jsonPath);
                    string datPath = Path.Combine(dir, $"{hash}.dat");
                    if (File.Exists(datPath)) File.Delete(datPath);
                    if (File.Exists(jsonPath)) File.Delete(jsonPath);
                }
            }
            catch {}
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
            if (MergeCache(sourceId, targetId)) total++;
        return total;
    }

    public bool MergeCache(string oldDeviceId, string newDeviceId)
    {
        string oldDb = GetDeviceDbPath(oldDeviceId);
        string newDb = GetDeviceDbPath(newDeviceId);
        if (File.Exists(oldDb))
        {
            if (Path.GetFullPath(oldDb).Equals(Path.GetFullPath(newDb), StringComparison.OrdinalIgnoreCase)) return true;
            try
            {
                // Attach and merge
                using var newConn = GetConnection(newDeviceId);
                newConn.Close(); // ensure db exists
                using var conn = new SqliteConnection($"Data Source={newDb}");
                conn.Open();
                using (var attachCmd = conn.CreateCommand())
                {
                    attachCmd.CommandText = "ATTACH DATABASE @old AS oldDb";
                    attachCmd.Parameters.AddWithValue("@old", oldDb);
                    attachCmd.ExecuteNonQuery();
                }
                // Merge thumbnails: keep larger data (or newer)
                // For simplicity, INSERT OR REPLACE where not exists, else keep larger processedLength
                // We do: INSERT OR IGNORE, then update where old larger
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT OR IGNORE INTO main.thumbnails SELECT * FROM oldDb.thumbnails;
                    UPDATE main.thumbnails SET data = oldDb.thumbnails.data, processedLength = oldDb.thumbnails.processedLength
                    WHERE EXISTS (SELECT 1 FROM oldDb.thumbnails WHERE oldDb.thumbnails.relativePath=main.thumbnails.relativePath AND oldDb.thumbnails.frame=main.thumbnails.frame AND length(oldDb.thumbnails.data) > length(main.thumbnails.data));
                    INSERT OR IGNORE INTO main.fullFiles SELECT * FROM oldDb.fullFiles;
                    INSERT OR IGNORE INTO main.folders SELECT * FROM oldDb.folders;
                    INSERT OR IGNORE INTO main.deviceMeta SELECT * FROM oldDb.deviceMeta WHERE key NOT IN (SELECT key FROM main.deviceMeta);
                ";
                cmd.ExecuteNonQuery();
                using (var detachCmd = conn.CreateCommand())
                {
                    detachCmd.CommandText = "DETACH DATABASE oldDb";
                    detachCmd.ExecuteNonQuery();
                }
                // Update lastAccess to most recent
                var oldTime = GetDeviceAccessTime(oldDeviceId);
                var newTime = GetDeviceAccessTime(newDeviceId);
                if (oldTime > newTime) UpdateDeviceAccessTime(newDeviceId);
                File.Delete(oldDb);
                foreach (var ext in new[] { "-wal", "-shm" }) try { File.Delete(oldDb + ext); } catch {}
                // Also delete legacy dir if exists
                string oldDir = Path.Combine(CacheBaseDir, oldDeviceId);
                if (Directory.Exists(oldDir)) Directory.Delete(oldDir, true);
                string oldMigrated = oldDir + ".migrated";
                if (Directory.Exists(oldMigrated)) Directory.Delete(oldMigrated, true);
                return true;
            }
            catch
            {
                // Fallback to file copy
            }
        }
        // Fallback to legacy file-based merge
        string oldDir2 = Path.Combine(CacheBaseDir, oldDeviceId);
        string newDir = GetDeviceCacheDir(newDeviceId);
        if (!Directory.Exists(oldDir2)) return false;
        if (Path.GetFullPath(oldDir2).Equals(Path.GetFullPath(newDir), StringComparison.OrdinalIgnoreCase)) return true;
        var oldFiles = Directory.GetFiles(oldDir2);
        foreach (string oldFilePath in oldFiles)
        {
            string fileName = Path.GetFileName(oldFilePath);
            if (fileName.Equals("device_name.txt", StringComparison.OrdinalIgnoreCase)) continue;
            string newFilePath = Path.Combine(newDir, fileName);
            if (!File.Exists(newFilePath)) File.Copy(oldFilePath, newFilePath);
            else
            {
                var oldInfo = new FileInfo(oldFilePath);
                var newInfo = new FileInfo(newFilePath);
                if (oldInfo.Length > newInfo.Length) File.Copy(oldFilePath, newFilePath, true);
            }
        }
        try { Directory.Delete(oldDir2, true); } catch {}
        return true;
    }

    public byte[]? ExportDeviceCache(string deviceId)
    {
        return ExportDeviceCacheLive(deviceId, null, null).GetAwaiter().GetResult();
    }

    public async Task<byte[]?> ExportDeviceCacheLive(string deviceId, ConnectionManager? cm, IHubContext<CommunicateSignalR>? hubContext)
    {
        string dbPath = GetDeviceDbPath(deviceId);
        bool hasDb = File.Exists(dbPath);
        string legacyDir = Path.Combine(CacheBaseDir, deviceId);
        bool hasLegacy = Directory.Exists(legacyDir) && Directory.GetFiles(legacyDir, "*.json").Length > 0;
        if (!hasDb && !hasLegacy) return null;

        string deviceName = deviceId;
        // Try db deviceMeta
        if (hasDb)
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={dbPath}");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT value FROM deviceMeta WHERE key='deviceName'";
                var res = cmd.ExecuteScalar();
                if (res != null) deviceName = res.ToString() ?? deviceId;
            }
            catch {}
        }
        if (deviceName == deviceId)
        {
            string nameFile = Path.Combine(legacyDir, "device_name.txt");
            if (File.Exists(nameFile)) deviceName = File.ReadAllText(nameFile).Trim();
        }

        // Collect folders for live walk (if connected)
        var allFolders = new HashSet<string>();
        var folderJsonMap = new Dictionary<string, string>();
        if (hasDb)
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={dbPath}");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT folderPath, json FROM folders";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string fp = reader["folderPath"]?.ToString() ?? "";
                    string json = reader["json"]?.ToString() ?? "";
                    folderJsonMap[fp] = json;
                    if (!string.IsNullOrEmpty(fp))
                    {
                        var parts = fp.Split('/');
                        for (int i = 1; i <= parts.Length; i++) allFolders.Add(string.Join("/", parts.Take(i)));
                    }
                }
            }
            catch {}
        }
        else
        {
            foreach (var jsonPath in Directory.GetFiles(legacyDir, "folder_*.json"))
            {
                try
                {
                    string raw = File.ReadAllText(jsonPath);
                    var fe = JsonSerializer.Deserialize<FolderCacheEntry>(raw, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (fe == null) continue;
                    string folderPath = (fe.FolderPath ?? "").Replace('\\', '/').Trim('/');
                    string hash = Path.GetFileNameWithoutExtension(jsonPath)["folder_".Length..];
                    folderJsonMap[folderPath] = hash;
                    if (!string.IsNullOrEmpty(folderPath))
                    {
                        var parts = folderPath.Split('/');
                        for (int i = 1; i <= parts.Length; i++) allFolders.Add(string.Join("/", parts.Take(i)));
                    }
                }
                catch {}
            }
        }

        if (cm != null && hubContext != null)
        {
            string? connId = null;
            foreach (var d in cm.GetAllDevices())
                if (d.DeviceId.Equals(deviceId, StringComparison.OrdinalIgnoreCase)) { connId = d.ConnectionId; break; }
            if (connId != null) await WalkPhoneTree(hubContext, connId, "", allFolders, 3);
        }

        // Build zip: new format is single .db file + details in deviceMeta
        // For backward compat, also include human tree and _raw mirror if legacy
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            // If we have db, export stripped db (metadata only) + human-readable folder structure
            if (hasDb)
            {
                // Ensure db is checkpointed
                try
                {
                    using var conn = new SqliteConnection($"Data Source={dbPath}");
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                    cmd.ExecuteNonQuery();
                }
                catch {}
                // Create stripped copy (no BLOBs) for metadata only
                string strippedTmp = Path.Combine(Path.GetTempPath(), $"stripped_{deviceId}_{Guid.NewGuid()}.db");
                try
                {
                    File.Copy(dbPath, strippedTmp, true);
                    using var sconn = new SqliteConnection($"Data Source={strippedTmp}");
                    sconn.Open();
                    using (var cmd = sconn.CreateCommand())
                    {
                        cmd.CommandText = "UPDATE thumbnails SET data = x''; UPDATE fullFiles SET data = x'';";
                        cmd.ExecuteNonQuery();
                    }
                    using (var cmd = sconn.CreateCommand())
                    {
                        cmd.CommandText = "VACUUM;";
                        cmd.ExecuteNonQuery();
                    }
                    sconn.Close();
                    var dbEntry = zip.CreateEntry($"{deviceId}.db", CompressionLevel.Fastest);
                    using (var src = File.OpenRead(strippedTmp))
                    using (var dst = dbEntry.Open()) src.CopyTo(dst);
                }
                finally { try { File.Delete(strippedTmp); } catch {} }
                // Add human-readable files from original db's BLOBs
                try
                {
                    using var conn = new SqliteConnection($"Data Source={dbPath}");
                    conn.Open();
                    var usedNames = new HashSet<string>();
                    // Thumbnails
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT relativePath, fileName, data, frame FROM thumbnails";
                        using var reader = cmd.ExecuteReader();
                        while (reader.Read())
                        {
                            string rp = reader["relativePath"]?.ToString() ?? "";
                            string fn = reader["fileName"]?.ToString() ?? Path.GetFileName(rp);
                            byte[] data = reader["data"] as byte[] ?? new byte[0];
                            if (data.Length == 0) continue;
                            float frame = Convert.ToSingle(reader["frame"]) / 100f;
                            if (reader["frame"] is long fl && fl == -1) frame = -1;
                            else if (reader["frame"] is int fi && fi == -1) frame = -1;
                            string folder = rp.Contains("/") ? rp[..rp.LastIndexOf('/')] : "";
                            string exportPath;
                            if (frame >= 0)
                            {
                                string frameLabel = frame.ToString("0.00", CultureInfo.InvariantCulture);
                                exportPath = string.IsNullOrEmpty(folder) ? $"{fn}_Frame{frameLabel}.jpg" : $"{folder}/{fn}_Frame{frameLabel}.jpg";
                            }
                            else exportPath = string.IsNullOrEmpty(folder) ? fn : $"{folder}/{fn}";
                            string orig = exportPath; int dup = 1;
                            while (!usedNames.Add(exportPath))
                            {
                                string bn = Path.GetFileNameWithoutExtension(orig);
                                string ext = Path.GetExtension(orig);
                                string parent = Path.GetDirectoryName(orig)?.Replace('\\', '/') ?? "";
                                exportPath = string.IsNullOrEmpty(parent) ? $"{bn}_{dup}{ext}" : $"{parent}/{bn}_{dup}{ext}";
                                dup++;
                            }
                            var entry = zip.CreateEntry(exportPath, CompressionLevel.Fastest);
                            using (var dst = entry.Open()) dst.Write(data, 0, data.Length);
                        }
                    }
                    // Full files
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT relativePath, fileName, data FROM fullFiles";
                        using var reader = cmd.ExecuteReader();
                        while (reader.Read())
                        {
                            string rp = reader["relativePath"]?.ToString() ?? "";
                            string fn = reader["fileName"]?.ToString() ?? Path.GetFileName(rp);
                            byte[] data = reader["data"] as byte[] ?? new byte[0];
                            if (data.Length == 0) continue;
                            string folder = rp.Contains("/") ? rp[..rp.LastIndexOf('/')] : "";
                            string exportPath = string.IsNullOrEmpty(folder) ? fn : $"{folder}/{fn}";
                            string orig = exportPath; int dup = 1;
                            while (!usedNames.Add(exportPath))
                            {
                                string bn = Path.GetFileNameWithoutExtension(orig);
                                string ext = Path.GetExtension(orig);
                                string parent = Path.GetDirectoryName(orig)?.Replace('\\', '/') ?? "";
                                exportPath = string.IsNullOrEmpty(parent) ? $"{bn}_{dup}{ext}" : $"{parent}/{bn}_{dup}{ext}";
                                dup++;
                            }
                            var entry = zip.CreateEntry(exportPath, CompressionLevel.Fastest);
                            using (var dst = entry.Open()) dst.Write(data, 0, data.Length);
                        }
                    }
                    // Folders as empty dirs
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT folderPath FROM folders";
                        using var reader = cmd.ExecuteReader();
                        var humanAllFolders = new HashSet<string>();
                        while (reader.Read())
                        {
                            string fp = reader["folderPath"]?.ToString() ?? "";
                            if (!string.IsNullOrEmpty(fp))
                            {
                                var parts = fp.Split('/');
                                for (int i = 1; i <= parts.Length; i++) humanAllFolders.Add(string.Join("/", parts.Take(i)));
                            }
                        }
                        foreach (var folder in humanAllFolders)
                            if (!string.IsNullOrEmpty(folder)) zip.CreateEntry(folder + "/", CompressionLevel.NoCompression);
                    }
                }
                catch {}
            }
            else
            {
                // Legacy fallback: export as before with human + _raw
                var hashToMeta = new Dictionary<string, CacheMetadata>();
                foreach (var jsonPath in Directory.GetFiles(legacyDir, "*.json"))
                {
                    if (Path.GetFileName(jsonPath).StartsWith("folder_")) continue;
                    try
                    {
                        string raw = File.ReadAllText(jsonPath);
                        var meta = JsonSerializer.Deserialize<CacheMetadata>(raw, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (meta != null) hashToMeta[Path.GetFileNameWithoutExtension(jsonPath)] = meta;
                    }
                    catch {}
                }
                var detailsEntry = zip.CreateEntry("details.txt", CompressionLevel.Fastest);
                using (var w = new StreamWriter(detailsEntry.Open()))
                {
                    w.WriteLine($"DeviceName: {deviceName}");
                    w.WriteLine($"DeviceId: {deviceId}");
                    w.WriteLine($"ExportedUtc: {DateTime.UtcNow:O}");
                }
                var usedNames = new HashSet<string>();
                var bestFrameByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kvp in hashToMeta)
                {
                    if (kvp.Value.FrameNormalized < 0f) continue;
                    string rp = (kvp.Value.RelativePath ?? "").Replace('\\', '/');
                    if (!bestFrameByPath.TryGetValue(rp, out var curHash) || IsBetterFrame(kvp.Value, hashToMeta[curHash]))
                        bestFrameByPath[rp] = kvp.Key;
                }
                foreach (var datPath in Directory.GetFiles(legacyDir, "*.dat"))
                {
                    string hash = Path.GetFileNameWithoutExtension(datPath);
                    if (!hashToMeta.TryGetValue(hash, out var meta)) continue;
                    if (string.IsNullOrEmpty(meta.RelativePath)) continue;
                    string rp = meta.RelativePath.Replace('\\', '/');
                    int lastSlash = rp.LastIndexOf('/');
                    string folder = lastSlash >= 0 ? rp[..lastSlash] : "";
                    bool isVideoFrame = meta.FrameNormalized >= 0f;
                    string exportPath;
                    if (isVideoFrame)
                    {
                        string frameLabel = meta.FrameNormalized.ToString("0.00", CultureInfo.InvariantCulture);
                        exportPath = string.IsNullOrEmpty(folder) ? $"{meta.FileName}_Frame{frameLabel}.jpg" : $"{folder}/{meta.FileName}_Frame{frameLabel}.jpg";
                    }
                    else exportPath = string.IsNullOrEmpty(folder) ? meta.FileName : $"{folder}/{meta.FileName}";
                    string orig = exportPath; int dup = 1;
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
                    if (isVideoFrame && bestFrameByPath.TryGetValue(rp, out var bestHash) && bestHash.Equals(hash, StringComparison.OrdinalIgnoreCase))
                    {
                        string plainPath = string.IsNullOrEmpty(folder) ? meta.FileName : $"{folder}/{meta.FileName}";
                        string plainOrig = plainPath; int plainDup = 1;
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
                foreach (var jsonPath in Directory.GetFiles(legacyDir, "*.json"))
                {
                    var rEntry = zip.CreateEntry($"_raw/{Path.GetFileName(jsonPath)}", CompressionLevel.Fastest);
                    var jb = File.ReadAllBytes(jsonPath);
                    using (var a = new MemoryStream(jb))
                    using (var b = rEntry.Open()) { a.CopyTo(b); }
                }
                foreach (var folder in allFolders)
                    if (!string.IsNullOrEmpty(folder)) zip.CreateEntry(folder + "/", CompressionLevel.NoCompression);
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
                    await WalkPhoneTree(hubContext, connId, childPath, folders, depth - 1);
            }
        }
        catch {}
    }

    public CacheImportResult ImportDeviceCache(byte[] zipData)
    {
        using var ms = new MemoryStream(zipData);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        // Check for .db file in zip (new format)
        var dbEntry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".db", StringComparison.OrdinalIgnoreCase) && !e.FullName.Contains("/") && !e.FullName.StartsWith("_raw/"));
        if (dbEntry == null) dbEntry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".db", StringComparison.OrdinalIgnoreCase));
        if (dbEntry != null)
        {
            // New format: stripped db (metadata only) + human-readable files
            string? deviceId = Path.GetFileNameWithoutExtension(dbEntry.FullName);
            string tmpDb = Path.Combine(Path.GetTempPath(), $"import_{Guid.NewGuid()}.db");
            try
            {
                using (var s = dbEntry.Open())
                using (var fs = File.Create(tmpDb)) s.CopyTo(fs);
                // Try to get deviceId from db's deviceMeta
                try
                {
                    using var conn = new SqliteConnection($"Data Source={tmpDb}");
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT value FROM deviceMeta WHERE key='deviceId'";
                    var res = cmd.ExecuteScalar();
                    if (res != null && !string.IsNullOrWhiteSpace(res.ToString())) deviceId = res.ToString().Trim();
                    else
                    {
                        using var cmd2 = conn.CreateCommand();
                        cmd2.CommandText = "SELECT value FROM deviceMeta WHERE key='deviceName'";
                        var res2 = cmd2.ExecuteScalar();
                        if (res2 != null) deviceId = res2.ToString().Trim();
                    }
                }
                catch {}
                string finalDb = GetDeviceDbPath(deviceId);
                Directory.CreateDirectory(Path.GetDirectoryName(finalDb) ?? CacheBaseDir);
                File.Copy(tmpDb, finalDb, true);
                // Now fill BLOBs from human files in zip (stripped db has empty data)
                try
                {
                    using var conn = new SqliteConnection($"Data Source={finalDb}");
                    conn.Open();
                    // Build lookup for human files in zip: FullName -> bytes
                    var humanFiles = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                    foreach (var e in zip.Entries)
                    {
                        if (e.FullName.EndsWith(".db", StringComparison.OrdinalIgnoreCase)) continue;
                        if (e.FullName.StartsWith("_raw/", StringComparison.OrdinalIgnoreCase)) continue;
                        if (e.FullName.EndsWith("/") || string.IsNullOrEmpty(e.Name)) continue;
                        // Human file like DCIM/Camera/IMG.jpg or DCIM/Camera/VID.mp4_Frame0.23.jpg
                        try
                        {
                            using var es = e.Open();
                            using var ms2 = new MemoryStream();
                            es.CopyTo(ms2);
                            humanFiles[e.FullName] = ms2.ToArray();
                        }
                        catch {}
                    }
                    // Update thumbnails with data from human files
                    // For each thumbnail row where data is empty, try to find matching human file
                    // Human file FullName is like "DCIM/Camera/IMG.jpg" which matches relativePath
                    // For video frames, human file is like "DCIM/Camera/VID.mp4_Frame0.23.jpg" -> relativePath is "DCIM/Camera/VID.mp4" + frame
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT relativePath, frame FROM thumbnails WHERE length(data) = 0 OR data IS NULL";
                    var toUpdate = new List<(string rp, int frame)>();
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            string rp = reader["relativePath"]?.ToString() ?? "";
                            int fr = Convert.ToInt32(reader["frame"]);
                            toUpdate.Add((rp, fr));
                        }
                    }
                    foreach (var (rp, frame) in toUpdate)
                    {
                        string humanPath = rp;
                        if (frame != -1)
                        {
                            float fr = frame / 100f;
                            string frameLabel = fr.ToString("0.00", CultureInfo.InvariantCulture);
                            // Human file for video frame is like "DCIM/Camera/VID.mp4_Frame0.23.jpg"
                            // Find which human file corresponds: try with _Frame suffix
                            string baseName = Path.GetFileName(rp);
                            string dir = rp.Contains("/") ? rp[..rp.LastIndexOf('/')] : "";
                            string candidate = string.IsNullOrEmpty(dir) ? $"{baseName}_Frame{frameLabel}.jpg" : $"{dir}/{baseName}_Frame{frameLabel}.jpg";
                            if (humanFiles.TryGetValue(candidate, out var data))
                            {
                                using var upd = conn.CreateCommand();
                                upd.CommandText = "UPDATE thumbnails SET data=$d WHERE relativePath=$rp AND frame=$fr";
                                upd.Parameters.AddWithValue("$d", data);
                                upd.Parameters.AddWithValue("$rp", rp);
                                upd.Parameters.AddWithValue("$fr", frame);
                                upd.ExecuteNonQuery();
                                continue;
                            }
                            // Fallback: try plain file name (for near match)
                            if (humanFiles.TryGetValue(rp, out var data2))
                            {
                                using var upd = conn.CreateCommand();
                                upd.CommandText = "UPDATE thumbnails SET data=$d WHERE relativePath=$rp AND frame=$fr";
                                upd.Parameters.AddWithValue("$d", data2);
                                upd.Parameters.AddWithValue("$rp", rp);
                                upd.Parameters.AddWithValue("$fr", frame);
                                upd.ExecuteNonQuery();
                            }
                        }
                        else
                        {
                            if (humanFiles.TryGetValue(rp, out var data))
                            {
                                using var upd = conn.CreateCommand();
                                upd.CommandText = "UPDATE thumbnails SET data=$d WHERE relativePath=$rp AND frame=$fr";
                                upd.Parameters.AddWithValue("$d", data);
                                upd.Parameters.AddWithValue("$rp", rp);
                                upd.Parameters.AddWithValue("$fr", frame);
                                upd.ExecuteNonQuery();
                            }
                        }
                    }
                    // Full files
                    using var cmd2 = conn.CreateCommand();
                    cmd2.CommandText = "SELECT relativePath FROM fullFiles WHERE length(data) = 0 OR data IS NULL";
                    var fullToUpdate = new List<string>();
                    using (var reader = cmd2.ExecuteReader())
                    {
                        while (reader.Read()) fullToUpdate.Add(reader["relativePath"]?.ToString() ?? "");
                    }
                    foreach (var rp in fullToUpdate)
                    {
                        if (humanFiles.TryGetValue(rp, out var data))
                        {
                            using var upd = conn.CreateCommand();
                            upd.CommandText = "UPDATE fullFiles SET data=$d WHERE relativePath=$rp";
                            upd.Parameters.AddWithValue("$d", data);
                            upd.Parameters.AddWithValue("$rp", rp);
                            upd.ExecuteNonQuery();
                        }
                    }
                }
                catch {}
                File.Delete(tmpDb);
                UpdateDeviceAccessTime(deviceId);
                string deviceName = deviceId;
                try
                {
                    using var conn = new SqliteConnection($"Data Source={finalDb}");
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT value FROM deviceMeta WHERE key='deviceName'";
                    var r = cmd.ExecuteScalar();
                    if (r != null) deviceName = r.ToString();
                }
                catch {}
                return new CacheImportResult { Success = true, DeviceId = deviceId, Message = $"Imported db for '{deviceName}'." };
            }
            catch (Exception ex)
            {
                try { File.Delete(tmpDb); } catch {}
                return new CacheImportResult { Success = false, Message = $"Db import failed: {ex.Message}" };
            }
        }

        string? legacyDeviceId = null;
        string? legacyDeviceName = null;
        var detailsEntry = zip.GetEntry("details.txt");
        if (detailsEntry != null)
        {
            using var reader = new StreamReader(detailsEntry.Open());
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.StartsWith("DeviceId: ")) legacyDeviceId = line["DeviceId: ".Length..].Trim();
                else if (line.StartsWith("DeviceName: ")) legacyDeviceName = line["DeviceName: ".Length..].Trim();
            }
        }
        if (string.IsNullOrEmpty(legacyDeviceId)) legacyDeviceId = Guid.NewGuid().ToString();
        if (string.IsNullOrEmpty(legacyDeviceName)) legacyDeviceName = "Imported_" + DateTime.UtcNow.ToString("yyyyMMdd");
        SaveDeviceName(legacyDeviceId, legacyDeviceName);
        string legacyDir = GetDeviceCacheDir(legacyDeviceId);
        var rawEntries = zip.Entries.Where(e => e.FullName.StartsWith("_raw/") && !e.FullName.EndsWith("/")).ToList();
        if (rawEntries.Count > 0)
        {
            int imported = 0;
            foreach (var entry in rawEntries)
            {
                try { entry.ExtractToFile(Path.Combine(legacyDir, Path.GetFileName(entry.FullName)), true); imported++; } catch {}
            }
            // Migrate to db after extracting raw
            try
            {
                string dbPath = GetDeviceDbPath(legacyDeviceId);
                if (File.Exists(dbPath)) File.Delete(dbPath);
                using var conn = GetConnection(legacyDeviceId);
                conn.Close();
            }
            catch {}
            return new CacheImportResult { Success = true, DeviceId = legacyDeviceId, Message = $"Imported {imported} files for '{legacyDeviceName}'." };
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
                    using (var metaStream = metaEntry.Open()) meta = JsonSerializer.Deserialize<CacheMetadata>(metaStream, jsonOptions);
                    if (meta == null) continue;
                    // Save via new db path (will create db)
                    string hash = HashPath(meta.RelativePath);
                    // Use SaveCachedThumbnail with data from dat
                    string datName = metaEntry.Name.Replace(".meta.json", ".dat", StringComparison.OrdinalIgnoreCase);
                    var datEntry = zip.GetEntry(datName) ?? zip.GetEntry(metaEntry.FullName.Replace(".meta.json", ".dat", StringComparison.OrdinalIgnoreCase));
                    if (datEntry != null)
                    {
                        using var datStream = datEntry.Open();
                        using var msDat = new MemoryStream();
                        datStream.CopyTo(msDat);
                        byte[] data = msDat.ToArray();
                        SaveCachedThumbnail(legacyDeviceId, meta.RelativePath, meta.FileName, data, meta.OriginalLength, meta.MediaType, meta.FrameNormalized, meta.Quality, meta.OriginalPreviewLength);
                    }
                }
                catch {}
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
                    string jsonPath = Path.Combine(legacyDir, $"{hash}.json");
                    string datName = $"{hash}.dat";
                    var datEntry = zip.GetEntry(datName) ?? zip.Entries.FirstOrDefault(e => e.Name.Equals(datName, StringComparison.OrdinalIgnoreCase));
                    if (datEntry != null)
                    {
                        string datPath = Path.Combine(legacyDir, $"{hash}.dat");
                        using var datStream = datEntry.Open();
                        using var fs = File.Create(datPath);
                        datStream.CopyTo(fs);
                        using var jsonStream = jsonEntry.Open();
                        using var reader = new StreamReader(jsonStream);
                        string jsonContent = reader.ReadToEnd();
                        File.WriteAllText(jsonPath, jsonContent);
                    }
                }
                catch {}
            }
        }
        // Migrate to db
        try
        {
            string dbPath2 = GetDeviceDbPath(legacyDeviceId);
            if (File.Exists(dbPath2)) File.Delete(dbPath2);
            using var conn2 = GetConnection(legacyDeviceId);
            conn2.Close();
        }
        catch {}
        UpdateDeviceAccessTime(legacyDeviceId);
        return new CacheImportResult { Success = true, Message = "Cache imported successfully.", DeviceId = legacyDeviceId };
    }

    public void EnforceCacheLimit(int maxLimitMb)
    {
        // For SQLite, sum BLOB lengths via SQL
        long maxLimitBytes = (long)maxLimitMb * 1024 * 1024;
        if (maxLimitBytes <= 0) return;
        var allDbs = Directory.GetFiles(CacheBaseDir, "*.db", SearchOption.AllDirectories);
        var entries = new List<(string deviceId, string relativePath, int frame, DateTime lastAccess, long size)>();
        foreach (var db in allDbs)
        {
            string deviceId = Path.GetFileNameWithoutExtension(db);
            try
            {
                using var conn = new SqliteConnection($"Data Source={db}");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT relativePath, frame, cachedTimeUtc, length(data) as sz FROM thumbnails UNION ALL SELECT relativePath, -2, cachedTimeUtc, length(data) FROM fullFiles";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string rp = reader["relativePath"]?.ToString() ?? "";
                    int fr = Convert.ToInt32(reader["frame"]);
                    DateTime ct = DateTime.TryParse(reader["cachedTimeUtc"]?.ToString(), out var dt) ? dt : DateTime.MinValue;
                    long sz = Convert.ToInt64(reader["sz"]);
                    entries.Add((deviceId, rp, fr, ct, sz));
                }
            }
            catch {}
        }
        // Also include legacy files
        foreach (string deviceDir in Directory.GetDirectories(CacheBaseDir))
        {
            if (File.Exists(Path.Combine(CacheBaseDir, Path.GetFileName(deviceDir) + ".db"))) continue; // already counted via db
            foreach (string datPath in Directory.GetFiles(deviceDir, "*.dat"))
            {
                string hash = Path.GetFileNameWithoutExtension(datPath);
                string jsonPath = Path.Combine(deviceDir, $"{hash}.json");
                DateTime lastAccess = File.GetLastAccessTimeUtc(datPath);
                long sz = 0; try { sz = new FileInfo(datPath).Length; } catch {}
                entries.Add((Path.GetFileName(deviceDir), hash, -1, lastAccess, sz));
            }
        }
        entries = entries.OrderBy(f => f.lastAccess).ToList();
        long currentTotalSize = entries.Sum(f => f.size);
        for (int i = 0; i < entries.Count && currentTotalSize > maxLimitBytes; i++)
        {
            var (deviceId, rp, frame, _, sz) = entries[i];
            string dbPath = GetDeviceDbPath(deviceId);
            if (File.Exists(dbPath))
            {
                try
                {
                    using var conn = new SqliteConnection($"Data Source={dbPath}");
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    if (frame == -2)
                        cmd.CommandText = "DELETE FROM fullFiles WHERE relativePath=$p";
                    else
                        cmd.CommandText = "DELETE FROM thumbnails WHERE relativePath=$p AND frame=$f";
                    cmd.Parameters.AddWithValue("$p", rp);
                    cmd.Parameters.AddWithValue("$f", frame);
                    cmd.ExecuteNonQuery();
                }
                catch {}
            }
            else
            {
                string dir = Path.Combine(CacheBaseDir, deviceId);
                string datPath = Path.Combine(dir, $"{HashPath(rp)}.dat");
                string jsonPath = Path.Combine(dir, $"{HashPath(rp)}.json");
                if (File.Exists(datPath)) try { File.Delete(datPath); } catch {}
                if (File.Exists(jsonPath)) try { File.Delete(jsonPath); } catch {}
            }
            currentTotalSize -= sz;
        }
    }

    public void SaveFolderCache(string deviceId, string folderPath, FileNodeResultDto nodeInfo)
    {
        string normalized = NormalizeRelPath(folderPath);
        try
        {
            using var conn = GetConnection(deviceId);
            var entry = new FolderCacheEntry
            {
                FolderPath = normalized,
                CachedTimeUtc = DateTime.UtcNow,
                Exists = nodeInfo.Exists,
                CurrentNode = nodeInfo.CurrentNode,
                Children = nodeInfo.Children?.GroupBy(c => NormalizeRelPath(c.RelativePath), StringComparer.OrdinalIgnoreCase).Select(g => g.Last()).ToList() ?? new List<FileNodeDto>()
            };
            string json = JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true });
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO folders(folderPath, json, cachedTimeUtc) VALUES($fp, $j, $ct)";
            cmd.Parameters.AddWithValue("$fp", normalized);
            cmd.Parameters.AddWithValue("$j", json);
            cmd.Parameters.AddWithValue("$ct", entry.CachedTimeUtc.ToString("O"));
            cmd.ExecuteNonQuery();
            UpdateDeviceAccessTime(deviceId);
            return;
        }
        catch {}
        // Fallback to file
        string hash = HashPath(normalized);
        string dir = GetDeviceCacheDir(deviceId);
        string jsonPath = Path.Combine(dir, $"folder_{hash}.json");
        var deduped = new Dictionary<string, FileNodeDto>(StringComparer.OrdinalIgnoreCase);
        if (nodeInfo.Children != null)
            foreach (var c in nodeInfo.Children) deduped[NormalizeRelPath(c.RelativePath)] = c;
        var entry2 = new FolderCacheEntry
        {
            FolderPath = normalized,
            CachedTimeUtc = DateTime.UtcNow,
            Exists = nodeInfo.Exists,
            CurrentNode = nodeInfo.CurrentNode,
            Children = deduped.Values.ToList()
        };
        string json2 = JsonSerializer.Serialize(entry2, new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true });
        File.WriteAllText(jsonPath, json2);
        UpdateDeviceAccessTime(deviceId);
    }

    public void MergeFolderCache(string deviceId, string folderPath, FileNodeDto? currentNode, List<FileNodeDto> children)
    {
        string normalized = NormalizeRelPath(folderPath);
        try
        {
            using var conn = GetConnection(deviceId);
            FolderCacheEntry? existing = null;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT json FROM folders WHERE folderPath=$fp";
                cmd.Parameters.AddWithValue("$fp", normalized);
                var res = cmd.ExecuteScalar();
                if (res != null) existing = JsonSerializer.Deserialize<FolderCacheEntry>(res.ToString() ?? "", new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            var merged = new Dictionary<string, FileNodeDto>(StringComparer.OrdinalIgnoreCase);
            if (existing?.Children != null)
                foreach (var c in existing.Children) merged[NormalizeRelPath(c.RelativePath)] = c;
            foreach (var c in children) merged[NormalizeRelPath(c.RelativePath)] = c;
            var entry = new FolderCacheEntry
            {
                FolderPath = normalized,
                CachedTimeUtc = DateTime.UtcNow,
                Exists = true,
                CurrentNode = currentNode ?? existing?.CurrentNode,
                Children = merged.Values.ToList()
            };
            string json = JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true });
            using var cmd2 = conn.CreateCommand();
            cmd2.CommandText = "INSERT OR REPLACE INTO folders(folderPath, json, cachedTimeUtc) VALUES($fp, $j, $ct)";
            cmd2.Parameters.AddWithValue("$fp", normalized);
            cmd2.Parameters.AddWithValue("$j", json);
            cmd2.Parameters.AddWithValue("$ct", entry.CachedTimeUtc.ToString("O"));
            cmd2.ExecuteNonQuery();
            UpdateDeviceAccessTime(deviceId);
            return;
        }
        catch {}
        // Fallback
        string hash2 = HashPath(normalized);
        string dir2 = GetDeviceCacheDir(deviceId);
        string jsonPath2 = Path.Combine(dir2, $"folder_{hash2}.json");
        FolderCacheEntry? existing2 = null;
        if (File.Exists(jsonPath2))
        {
            try { existing2 = JsonSerializer.Deserialize<FolderCacheEntry>(File.ReadAllText(jsonPath2), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); } catch {}
        }
        var merged2 = new Dictionary<string, FileNodeDto>(StringComparer.OrdinalIgnoreCase);
        if (existing2?.Children != null)
            foreach (var c in existing2.Children) merged2[NormalizeRelPath(c.RelativePath)] = c;
        foreach (var c in children) merged2[NormalizeRelPath(c.RelativePath)] = c;
        var entry2 = new FolderCacheEntry
        {
            FolderPath = normalized,
            CachedTimeUtc = DateTime.UtcNow,
            Exists = true,
            CurrentNode = currentNode ?? existing2?.CurrentNode,
            Children = merged2.Values.ToList()
        };
        string json3 = JsonSerializer.Serialize(entry2, new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true });
        File.WriteAllText(jsonPath2, json3);
        UpdateDeviceAccessTime(deviceId);
    }

    public FolderCacheEntry? GetFolderCache(string deviceId, string folderPath)
    {
        string normalized = NormalizeRelPath(folderPath);
        string dbPath = GetDeviceDbPath(deviceId);
        if (File.Exists(dbPath))
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={dbPath}");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT json FROM folders WHERE folderPath=$fp";
                cmd.Parameters.AddWithValue("$fp", normalized);
                var res = cmd.ExecuteScalar();
                if (res != null)
                {
                    var entry = JsonSerializer.Deserialize<FolderCacheEntry>(res.ToString() ?? "", new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (entry != null)
                    {
                        // Merge any thumbnails/fullFiles for this folder that are missing from Children (e.g. 194 files for DCIM/Camera)
                        try
                        {
                            var existing = new HashSet<string>(entry.Children.Where(c => !string.IsNullOrEmpty(c.RelativePath)).Select(c => NormalizeRelPath(c.RelativePath)), StringComparer.OrdinalIgnoreCase);
                            existing.UnionWith(entry.Children.Where(c => string.IsNullOrEmpty(c.RelativePath) && !string.IsNullOrEmpty(c.Name)).Select(c => NormalizeRelPath(c.Name)));
                            using var cmd2 = conn.CreateCommand();
                            cmd2.CommandText = "SELECT relativePath, fileName, originalLength, cachedTimeUtc FROM thumbnails WHERE relativePath LIKE $pat UNION ALL SELECT relativePath, fileName, originalLength, cachedTimeUtc FROM fullFiles WHERE relativePath LIKE $pat";
                            cmd2.Parameters.AddWithValue("$pat", normalized == "" ? "%" : normalized + "/%");
                            using var reader = cmd2.ExecuteReader();
                            while (reader.Read())
                            {
                                string rp = reader["relativePath"]?.ToString() ?? "";
                                string normRp = NormalizeRelPath(rp);
                                if (string.IsNullOrEmpty(normRp) || existing.Contains(normRp)) continue;
                                // Only add if direct child of this folder (not deeper)
                                string parent = normRp.Contains("/") ? normRp[..normRp.LastIndexOf('/')] : "";
                                if (parent != normalized) continue;
                                string fn = reader["fileName"]?.ToString() ?? Path.GetFileName(rp);
                                long ol = 0; try { ol = Convert.ToInt64(reader["originalLength"]); } catch {}
                                string ctStr = reader["cachedTimeUtc"]?.ToString() ?? "";
                                DateTime ct = DateTime.TryParse(ctStr, out var d) ? d : DateTime.UtcNow;
                                var dto = new FileNodeDto(fn, normRp, false, ol, ol, ct, 0, null, false, null, 0, 0);
                                entry.Children.Add(dto);
                                existing.Add(normRp);
                            }
                        }
                        catch {}
                        return entry;
                    }
                }
            }
            catch {}
        }
        // Fallback to file
        string hash = HashPath(normalized);
        string dir = Path.Combine(CacheBaseDir, deviceId);
        string jsonPath = Path.Combine(dir, $"folder_{hash}.json");
        if (!File.Exists(jsonPath))
        {
            // Try variant
            foreach (var variant in RelPathVariants(folderPath))
            {
                string h2 = HashPath(NormalizeRelPath(variant));
                string p2 = Path.Combine(dir, $"folder_{h2}.json");
                if (File.Exists(p2)) { jsonPath = p2; break; }
            }
            if (!File.Exists(jsonPath)) return null;
        }
        try { return JsonSerializer.Deserialize<FolderCacheEntry>(File.ReadAllText(jsonPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); } catch { return null; }
    }

    public bool HasFolderCache(string deviceId, string folderPath)
    {
        string normalized = NormalizeRelPath(folderPath);
        string dbPath = GetDeviceDbPath(deviceId);
        if (File.Exists(dbPath))
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={dbPath}");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT 1 FROM folders WHERE folderPath=$fp LIMIT 1";
                cmd.Parameters.AddWithValue("$fp", normalized);
                var res = cmd.ExecuteScalar();
                if (res != null) return true;
            }
            catch {}
        }
        string hash = HashPath(normalized);
        string dir = Path.Combine(CacheBaseDir, deviceId);
        return File.Exists(Path.Combine(dir, $"folder_{hash}.json"));
    }

    /// <summary>
    /// Normalized relative paths having actual downloaded bytes (thumbnail or
    /// full file). Offline browsing filters to this set so counters and cards
    /// reflect downloaded content only — never structure-only listings.
    /// Includes both slash variants (legacy rows carry a leading slash).
    /// </summary>
    public HashSet<string> GetCachedContentPaths(string deviceId)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string dbPath = GetDeviceDbPath(deviceId);
        if (!File.Exists(dbPath)) return set;
        try
        {
            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT relativePath FROM thumbnails WHERE data IS NOT NULL AND LENGTH(data) > 0"
                + " UNION SELECT relativePath FROM fullFiles WHERE data IS NOT NULL AND LENGTH(data) > 0";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string rp = reader[0]?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(rp)) continue;
                string norm = NormalizeRelPath(rp);
                set.Add(norm);
                set.Add("/" + norm);
            }
        }
        catch { }
        return set;
    }

    public (int Images, int Videos, int Audio, int Other) GetFolderChildCounts(string deviceId, string folderPath)
    {
        var entry = GetFolderCache(deviceId, folderPath);
        if (entry?.Children == null) return (0,0,0,0);
        int img=0, vid=0, aud=0, oth=0;
        foreach (var c in entry.Children)
        {
            if (c.IsDirectory) continue;
            string ext = Path.GetExtension(c.Name ?? c.RelativePath ?? "").ToLowerInvariant();
            if (ImageExtensions.Contains(ext)) img++;
            else if (VideoExtensions.Contains(ext)) vid++;
            else if (AudioExtensions.Contains(ext)) aud++;
            else oth++;
        }
        return (img, vid, aud, oth);
    }

    public List<FileNodeDto> SearchCache(string deviceId, string query, string startPath, bool recursive, int maxResults)    {
        string normStart = NormalizeRelPath(startPath);
        var results = new List<FileNodeDto>();
        string dbPath = GetDeviceDbPath(deviceId);
        bool useDb = File.Exists(dbPath);
        if (useDb)
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={dbPath}");
                conn.Open();
                // Search thumbnails and folders
                // For simplicity, scan folders table and filter
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT json FROM folders";
                using var reader = cmd.ExecuteReader();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (reader.Read() && results.Count < maxResults)
                {
                    string json = reader["json"]?.ToString() ?? "";
                    var entry = JsonSerializer.Deserialize<FolderCacheEntry>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (entry?.Children == null) continue;
                    foreach (var child in entry.Children)
                    {
                        if (results.Count >= maxResults) break;
                        string rel = NormalizeRelPath(child.RelativePath ?? "");
                        if (!seen.Add(rel)) continue;
                        if (!string.IsNullOrEmpty(normStart) && !rel.StartsWith(normStart, StringComparison.OrdinalIgnoreCase) && !child.RelativePath.Contains(normStart, StringComparison.OrdinalIgnoreCase)) 
                        {
                            if (recursive)
                            {
                                // Check if child is under startPath
                                if (!rel.StartsWith(normStart + "/", StringComparison.OrdinalIgnoreCase) && rel != normStart) continue;
                            }
                            else
                            {
                                // Non-recursive: only direct children of startPath
                                string parent = rel.Contains("/") ? rel[..rel.LastIndexOf('/')] : "";
                                if (parent != normStart) continue;
                            }
                        }
                        if (child.Name != null && child.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                            results.Add(child);
                    }
                }
                if (results.Count > 0) return results.Take(maxResults).ToList();
            }
            catch {}
        }
        // Fallback to legacy file scan
        string dir = Path.Combine(CacheBaseDir, deviceId);
        if (!Directory.Exists(dir)) return results;
        foreach (var jsonPath in Directory.GetFiles(dir, "folder_*.json"))
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
                    if (child.Name != null && child.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        string rel = NormalizeRelPath(child.RelativePath ?? "");
                        if (!string.IsNullOrEmpty(normStart))
                        {
                            if (recursive)
                            {
                                if (!rel.StartsWith(normStart, StringComparison.OrdinalIgnoreCase) && !rel.Equals(normStart, StringComparison.OrdinalIgnoreCase) && !rel.StartsWith(normStart + "/", StringComparison.OrdinalIgnoreCase)) continue;
                            }
                            else
                            {
                                string parent = rel.Contains("/") ? rel[..rel.LastIndexOf('/')] : "";
                                if (parent != normStart && rel != normStart) continue;
                            }
                        }
                        results.Add(child);
                    }
                }
            }
            catch {}
        }
        return results.Take(maxResults).ToList();
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
