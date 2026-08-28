using System.IO.Compression;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.StaticFiles;
using PhonesBridge.Services.Definitions;

namespace PhonesBridge.Services;

/// <summary>
/// All /api/* minimal-API endpoints for the Web UI.
/// Call app.MapWebUiEndpoints() in Program.cs to register them.
/// </summary>
public static class WebUiEndpoints
{
    private static readonly FileExtensionContentTypeProvider ContentTypeProvider = new();
    private static readonly string ConfigPath =
        Path.Combine("Services", "DataContainers", "config.json");

    public static void MapWebUiEndpoints(this WebApplication app)
    {
        // ── GET /api/devices ──────────────────────────────────────────────────
        // Returns a JSON array of DeviceIdentity objects with p2p/domain availability (used by renderDeviceItems + renderLinkTag).
        app.MapGet("/api/devices", (ConnectionManager cm) =>
        {
            var devices = cm.GetAllDevices().Select(d => {
                var link = cm.GetLinkStatus(d.DeviceName);
                return new {
                    deviceName = d.DeviceName,
                    deviceId = d.DeviceId,
                    platform = d.Platform,
                    p2p = link.P2PAvailable,
                    domain = link.DomainAvailable
                };
            }).ToList();
            return Results.Ok(devices);
        });

        // ── GET /api/config ───────────────────────────────────────────────────
        // Returns the current config.json as JSON.
        app.MapGet("/api/config", async () =>
        {
            if (!File.Exists(ConfigPath))
                return Results.NotFound("config.json not found");

            string json = await File.ReadAllTextAsync(ConfigPath);
            return Results.Content(json, "application/json");
        });

        // ── POST /api/config ──────────────────────────────────────────────────
        // Writes config.json and broadcasts RefreshConfiguration to all phones.
        app.MapPost("/api/config", async (
            Configuration config,
            ConnectionManager cm,
            DeviceConfigManager dcm,
            IHubContext<CommunicateSignalR> hubContext) =>
        {
            config.VideoFrameNormalized = Math.Clamp(config.VideoFrameNormalized, 0f, 1f);

            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(config, options);
            await File.WriteAllTextAsync(ConfigPath, json);

            foreach (var dev in cm.GetAllDevices())
                dcm.SaveDeviceConfig(dev.DeviceId, config);

            await hubContext.Clients.All.SendAsync("RefreshConfiguration", json);
            /* Console.WriteLine($"[WebUI] Config pushed. Frame={config.VideoFrameNormalized:F2} Debug={config.SendDebug}"); */

            return Results.Ok(new { message = "Config pushed", config });
        });

        // ── POST /api/config/push/{device} ────────────────────────────────────
        // Pushes the form config to a single device (used by Device Manager / Config per-device buttons).
        // The JS sends FormData→JSON where numbers are strings, so allow reading from string.
        app.MapPost("/api/config/push/{device}", async (
            string device,
            HttpContext ctx,
            ConnectionManager cm,
            DeviceConfigManager dcm,
            IHubContext<CommunicateSignalR> hubContext) =>
        {
            device = Uri.UnescapeDataString(device);
            if (!cm.TryGetConnectionId(device, out string? connId))
                return Results.NotFound($"Device '{device}' not connected.");
            if (!cm.TryGetDeviceIdentity(device, out var ident) || ident == null)
                return Results.NotFound($"Device '{device}' not connected.");

            string body = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(body))
                return Results.BadRequest("Empty body");

            var opts = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
            };
            Configuration? incoming;
            try
            {
                incoming = JsonSerializer.Deserialize<Configuration>(body, opts);
            }
            catch (Exception ex)
            {
                return Results.BadRequest($"Invalid config JSON: {ex.Message}");
            }
            if (incoming == null)
                return Results.BadRequest("Invalid config");

            incoming.VideoFrameNormalized = Math.Clamp(incoming.VideoFrameNormalized, 0f, 1f);
            incoming.ThumbnailQuality = Math.Clamp(incoming.ThumbnailQuality, 0f, 1f);
            incoming.VideoQuality = Math.Clamp(incoming.VideoQuality, 0f, 1f);

            dcm.SaveDeviceConfig(ident.DeviceId, incoming);

            string json = JsonSerializer.Serialize(incoming, new JsonSerializerOptions { WriteIndented = true });
            await hubContext.Clients.Client(connId).SendAsync("RefreshConfiguration", json);
            /* Console.WriteLine($"[WebUI] Config pushed to single device {device} ({ident.DeviceId}): Frame={incoming.VideoFrameNormalized:F2} Debug={incoming.SendDebug}"); */

            return Results.Ok(new { message = $"Config pushed to {device}", config = incoming });
        });

        // ── GET /api/browse/{device} ──────────────────────────────────────────
        // Lists a directory on the connected phone.
        // Query: path (default ""), startIndex (default 0), fileLimit (default 0 = all)
        // Now forwards pagination & filters to phone so metadata is limited phone→Server as well.
        app.MapGet("/api/browse/{device}", async (
            string device,
            string? path,
            ConnectionManager cm,
            IHubContext<CommunicateSignalR> hubContext,
            CancellationToken ct,
            int startIndex = 0,
            int fileLimit = 0,
            bool imageOnly = false,
            bool videoOnly = false,
            int sinceDays = 0) =>
        {
            device = Uri.UnescapeDataString(device);
            path ??= "";
            /* Console.WriteLine($"[WebUi] Browse {device} path='{path}'"); */
            if (!cm.TryGetConnectionId(device, out string? connId))
            {
                /* Console.WriteLine($"[WebUi] Browse {device} not connected"); */
                return Results.NotFound($"Device '{device}' not connected.");
            }
            /* Console.WriteLine($"[WebUi] Browse {device} connId={connId} invoking GetPathInfo..."); */

            FileNodeResultDto? nodeInfo;
            try
            {
                /* Console.WriteLine($"[WebUi] Browse {device} path='{path}' start={startIndex} limit={fileLimit} img={imageOnly} vid={videoOnly} since={sinceDays}"); */
                // Try new paginated/filtered call first; fallback to old for legacy clients
                Task<FileNodeResultDto> invokeTask;
                try
                {
                    invokeTask = hubContext.Clients.Client(connId).InvokeAsync<FileNodeResultDto>("GetPathInfoWithOverride", path, false, -1f, -1f, imageOnly, videoOnly, sinceDays, startIndex, fileLimit, ct);
                }
                catch
                {
                    invokeTask = hubContext.Clients.Client(connId).InvokeAsync<FileNodeResultDto>("GetPathInfo", path, false, ct);
                }
                var delayTask = Task.Delay(TimeSpan.FromSeconds(15));
                var completed = await Task.WhenAny(invokeTask, delayTask);
                if (completed == delayTask)
                {
                    /* Console.WriteLine($"[WebUi] Browse {device} GetPathInfo timeout after 15s"); */
                    return Results.StatusCode(504);
                }
                try { nodeInfo = await invokeTask; }
                catch (Exception ex) when (ex.Message.Contains("GetPathInfoWithOverride") || ex.Message.Contains("No such method"))
                {
                    /* Console.WriteLine($"[WebUi] Browse {device} WithOverride not supported, fallback to GetPathInfo: {ex.Message}"); */
                    nodeInfo = await hubContext.Clients.Client(connId).InvokeAsync<FileNodeResultDto>("GetPathInfo", path, false, ct);
                }
                /* Console.WriteLine($"[WebUi] Browse {device} GetPathInfo returned exists={nodeInfo?.Exists} children={nodeInfo?.Children?.Count} total={nodeInfo?.TotalFileCount}"); */
            }
            catch (OperationCanceledException)
            {
                /* Console.WriteLine($"[WebUi] Browse {device} GetPathInfo canceled"); */
                return Results.StatusCode(499); // Client closed request
            }
            catch (Exception ex)
            {
                /* Console.WriteLine($"[WebUi] Browse {device} GetPathInfo error: {ex.GetType().Name}: {ex.Message}"); */
                return Results.StatusCode(500);
            }

            if (nodeInfo == null || !nodeInfo.Exists)
                return Results.NotFound($"Path '{path}' not found on device.");

            // Separate dirs and files; only files are paginated (dirs always returned)
            var dirs  = nodeInfo.Children?.Where(c => c.IsDirectory).ToList()  ?? new();
            var files = nodeInfo.Children?.Where(c => !c.IsDirectory).ToList() ?? new();

            int totalFileCount = nodeInfo.TotalFileCount != 0 ? nodeInfo.TotalFileCount : files.Count;
            int? exceededAt    = null;

            // If phone already paginated (files.Count <= fileLimit or TotalFileCount > files.Count), don't re-paginate.
            // Otherwise fallback for old clients that returned all.
            if (fileLimit > 0 && files.Count > fileLimit)
            {
                // Old client returned all → paginate server-side
                startIndex = Math.Max(0, Math.Min(startIndex, totalFileCount - 1));
                int available = totalFileCount - startIndex;
                if (available < fileLimit) exceededAt = totalFileCount;
                files = files.Skip(startIndex).Take(fileLimit).ToList();
            }
            else if (fileLimit > 0)
            {
                // Phone did pagination → just compute exceededAt
                int available = totalFileCount - startIndex;
                if (available < fileLimit) exceededAt = totalFileCount;
            }

            return Results.Ok(new
            {
                currentNode    = nodeInfo.CurrentNode,
                directories    = dirs,
                files,
                totalFileCount,
                startIndex,
                fileLimit,
                exceededAt     // null = no overflow; int = last available index
            });
        });

        // ── GET /api/thumb/{device} ───────────────────────────────────────────
        // Returns the low-resolution thumbnail for a single file.
        // Called only for files currently visible in the browser viewport.
        // Query: path, frameOverride, qualityOverride, bestCache
        app.MapGet("/api/thumb/{device}", async (
            string device,
            string path,
            ConnectionManager cm,
            IHubContext<CommunicateSignalR> hubContext,
            HttpContext ctx,
            CacheManager cacheManager,
            DeviceConfigManager deviceConfigManager,
            CancellationToken ct) =>
        {
            device = Uri.UnescapeDataString(device);
            var query = ctx.Request.Query;
            float frameOverride = -1f; float qualityOverride = -1f; bool bestCache = false;
            if (query.TryGetValue("frameOverride", out var fov) && float.TryParse(fov, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fo)) frameOverride = fo;
            if (query.TryGetValue("qualityOverride", out var qov) && float.TryParse(qov, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var qo)) qualityOverride = qo;
            if (query.TryGetValue("bestCache", out var bcv) && (bcv == "1" || bcv == "true")) bestCache = true;

            // Try to serve from cache first (never downgrade)
            string deviceIdForCache = device;
            if (cm.TryGetDeviceIdentity(device, out var identForCache) && identForCache != null) deviceIdForCache = identForCache.DeviceId;
            else
            {
                var cached = cacheManager.GetAllCachedDevices(cm).FirstOrDefault(d => d.DeviceName == device);
                if (cached != null) deviceIdForCache = cached.DeviceId;
            }
            // Resolve effective frame/quality from config or overrides
            float effectiveFrame = -1f; float effectiveQuality = -1f; bool isVideo = false;
            try
            {
                string ext = Path.GetExtension(path ?? "").ToLowerInvariant();
                isVideo = ext == ".mp4" || ext == ".mkv" || ext == ".mov" || ext == ".avi" || ext == ".webm" || ext == ".3gp" || ext == ".m4v";
                var cfg = deviceConfigManager.LoadDeviceConfig(deviceIdForCache) ?? deviceConfigManager.LoadGlobalConfig();
                if (frameOverride >= 0) effectiveFrame = frameOverride;
                else if (isVideo && cfg != null) effectiveFrame = cfg.VideoFrameNormalized;
                if (qualityOverride >= 0) effectiveQuality = qualityOverride;
                else if (cfg != null) effectiveQuality = cfg.ThumbnailQuality;
                // bestCache means serve best cached thumbnail regardless of current quality setting
                if (bestCache)
                {
                    effectiveQuality = -1f; // -1 means accept any quality, IsThumbnailSuitable will return true for any cached
                    // For bestCache, we want the highest quality cached, so we will check cache with effectiveQuality -1 and then pick best
                }
            } catch { }

            if (cacheManager.TryGetCachedThumbnail(deviceIdForCache, path, effectiveFrame, effectiveQuality, out var cachedData, out var cachedMeta)
                && cachedData != null && CacheManager.IsThumbnailSuitable(cachedMeta, effectiveFrame, effectiveQuality, isVideo))
            {
                string ct2 = GetContentType(path);
                ctx.Response.Headers["X-Cache"] = "HIT";
                ctx.Response.Headers["X-Original-Size"] = (cachedMeta?.OriginalLength ?? cachedData.Length).ToString();
                ctx.Response.Headers["X-Processed-Size"] = cachedData.Length.ToString();
                ctx.Response.Headers["X-Native"] = (cachedMeta?.Quality >= 0.99f ? "1" : "0");
                return Results.Bytes(cachedData, ct2);
            }
            // Also try best cached thumbnail for offline case (when bestCache=1, try any frame)
            if (bestCache && cacheManager.TryGetCachedThumbnail(deviceIdForCache, path, -1f, -1f, out var bestData, out var bestMeta) && bestData != null)
            {
                string ct2 = GetContentType(path);
                ctx.Response.Headers["X-Cache"] = "HIT-BEST";
                return Results.Bytes(bestData, ct2);
            }

            if (!cm.TryGetConnectionId(device, out string? connId))
                return Results.NotFound();

            try
            {
                // Use override-aware calls when frame/quality is specified (video frame slider)
                FileNodeResultDto? nodeInfo;
                if (effectiveFrame >= 0f || effectiveQuality >= 0f)
                {
                    nodeInfo = await hubContext.Clients.Client(connId)
                        .InvokeAsync<FileNodeResultDto>("GetPathInfoWithOverride", path, true, effectiveFrame, effectiveQuality, ct);
                }
                else
                {
                    nodeInfo = await hubContext.Clients.Client(connId)
                        .InvokeAsync<FileNodeResultDto>("GetPathInfo", path, true, ct);
                }

                if (nodeInfo == null || !nodeInfo.Exists || nodeInfo.CurrentNode.IsDirectory)
                    return Results.NotFound();

                // Stream all chunks into a MemoryStream
                using var ms = new MemoryStream();
                long offset = 0;
                long totalLength = nodeInfo.CurrentNode.Length;
                const int chunkSize = 512 * 1024; // 512 KB chunks

                while (offset < totalLength)
                {
                    ct.ThrowIfCancellationRequested();

                    if (!cm.TryGetConnectionId(device, out string? activeConn))
                        return Results.StatusCode(503);

                    int bytesToRead = (int)Math.Min(chunkSize, totalLength - offset);
                    byte[]? chunk;
                    if (effectiveFrame >= 0f || effectiveQuality >= 0f)
                    {
                        chunk = await hubContext.Clients.Client(activeConn)
                            .InvokeAsync<byte[]>("GetFileChunkWithOverride", path, offset, bytesToRead, true, effectiveFrame, effectiveQuality, ct);
                    }
                    else
                    {
                        chunk = await hubContext.Clients.Client(activeConn)
                            .InvokeAsync<byte[]>("GetFileChunk", path, offset, bytesToRead, true, ct);
                    }

                    if (chunk == null || chunk.Length == 0) break;
                    await ms.WriteAsync(chunk, ct);
                    offset += chunk.Length;
                }

                var data = ms.ToArray();
                // Save to cache for offline and future bestCache hits (never downgrade)
                try
                {
                    string ext = Path.GetExtension(path ?? "").ToLowerInvariant();
                    bool isVid = ext == ".mp4" || ext == ".mkv" || ext == ".mov" || ext == ".avi" || ext == ".webm" || ext == ".3gp" || ext == ".m4v";
                    float cacheFrame = isVid ? effectiveFrame : -1f;
                    float cacheQuality = effectiveQuality;
                    // If we fetched with overrides, use those for cache key, otherwise use config
                    if (cacheQuality < 0) cacheQuality = 0f;
                    cacheManager.SaveCachedThumbnail(deviceIdForCache, path, Path.GetFileName(path), data, nodeInfo.CurrentNode.OriginalLength, isVid ? "video" : "image", cacheFrame, cacheQuality, nodeInfo.CurrentNode.OriginalLength);
                } catch { }

                string contentType = GetContentType(path);
                ctx.Response.Headers["X-Cache"] = "MISS";
                return Results.Bytes(data, contentType);
            }
            catch (OperationCanceledException)
            {
                // Browser scrolled away and aborted the request — no action needed
                return Results.StatusCode(499);
            }
        });

        // ── GET /api/file/{device} ────────────────────────────────────────────
        // Streams the full-resolution file to the browser for download or preview.
        // When preview=true for images, also saves the full file as a cached thumbnail with quality 1.0 so the cover shows highest quality.
        app.MapGet("/api/file/{device}", async (
            string device,
            string path,
            bool preview, // ?preview=true → inline; false/absent → attachment (download)
            ConnectionManager cm,
            IHubContext<CommunicateSignalR> hubContext,
            HttpContext ctx,
            CacheManager cacheManager,
            DeviceConfigManager deviceConfigManager,
            CancellationToken ct) =>
        {
            device = Uri.UnescapeDataString(device);

            if (!cm.TryGetConnectionId(device, out string? connId))
            {
                ctx.Response.StatusCode = 404;
                return;
            }

            try
            {
                var nodeInfo = await hubContext.Clients.Client(connId)
                    .InvokeAsync<FileNodeResultDto>("GetPathInfo", path, false, ct);

                if (nodeInfo == null || !nodeInfo.Exists || nodeInfo.CurrentNode.IsDirectory)
                {
                    ctx.Response.StatusCode = 404;
                    return;
                }

                string fileName    = nodeInfo.CurrentNode.Name;
                string contentType = GetContentType(fileName);
                long   totalLength = nodeInfo.CurrentNode.Length;

                string disposition = preview
                    ? $"inline; filename=\"{fileName}\""
                    : $"attachment; filename=\"{fileName}\"";

                // ── Range support for video seeking ──
                long rangeStart = 0, rangeEnd = totalLength - 1;
                bool isRange = false;
                var rangeHeader = ctx.Request.Headers["Range"].ToString();
                if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
                {
                    var range = rangeHeader.Substring(6).Split('-');
                    if (long.TryParse(range[0], out var s)) rangeStart = s;
                    if (range.Length > 1 && long.TryParse(range[1], out var e)) rangeEnd = e;
                    rangeEnd = Math.Min(rangeEnd, totalLength - 1);
                    if (rangeStart < 0) rangeStart = 0;
                    if (rangeStart <= rangeEnd) isRange = true;
                }

                long contentLength = isRange ? (rangeEnd - rangeStart + 1) : totalLength;

                ctx.Response.ContentType   = contentType;
                ctx.Response.ContentLength = contentLength;
                ctx.Response.Headers["Content-Disposition"] = disposition;
                ctx.Response.Headers["Accept-Ranges"] = "bytes";
                if (isRange)
                {
                    ctx.Response.StatusCode = 206;
                    ctx.Response.Headers["Content-Range"] = $"bytes {rangeStart}-{rangeEnd}/{totalLength}";
                }
                else
                {
                    ctx.Response.StatusCode = 200;
                }

                long offset = isRange ? rangeStart : 0;
                long endOffset = isRange ? rangeEnd : totalLength - 1;
                const int chunkSize = 512 * 1024; // 512 KB chunks
                using var msCache = new MemoryStream();
                bool isImageForCache = !isRange && preview && (Path.GetExtension(fileName ?? "").ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".webp" or ".bmp" or ".gif");

                while (offset <= endOffset)
                {
                    ct.ThrowIfCancellationRequested();

                    if (!cm.TryGetConnectionId(device, out string? activeConn))
                        throw new IOException($"Device '{device}' disconnected.");

                    int bytesToRead = (int)Math.Min(chunkSize, endOffset - offset + 1);
                    byte[]? chunk = await hubContext.Clients.Client(activeConn)
                        .InvokeAsync<byte[]>("GetFileChunk", path, offset, bytesToRead, false, ct);

                    if (chunk == null || chunk.Length == 0) break;
                    await ctx.Response.Body.WriteAsync(chunk, ct);
                    if (isImageForCache) await msCache.WriteAsync(chunk, ct);
                    offset += chunk.Length;
                    if (chunk.Length < bytesToRead) break;
                }
                // Save full-quality image to thumbnail cache with quality 1.0 so its cover shows highest quality next time (never downgrade)
                if (isImageForCache && msCache.Length > 0)
                {
                    try
                    {
                        string deviceIdForCache = device;
                        if (cm.TryGetDeviceIdentity(device, out var identForCache) && identForCache != null) deviceIdForCache = identForCache.DeviceId;
                        var data = msCache.ToArray();
                        cacheManager.SaveCachedThumbnail(deviceIdForCache, path, fileName, data, totalLength, "image", -1f, 1.0f, totalLength);
                    } catch { }
                }
            }
            catch (OperationCanceledException) { /* client disconnected */ }
            catch (Exception ex)
            {
                /* Console.WriteLine($"[WebUI /api/file] Error: {ex.Message}"); */
                if (!ctx.Response.HasStarted)
                    ctx.Response.StatusCode = 500;
                else
                    ctx.Abort();
            }
        });

        // ── GET /api/download-zip/{device} ────────────────────────────────────
        // Accepts multiple ?paths= query params, streams a ZIP built on-the-fly.
        // Nothing is written to disk on the PC.
        app.MapGet("/api/download-zip/{device}", async (
            string device,
            HttpContext ctx,
            ConnectionManager cm,
            IHubContext<CommunicateSignalR> hubContext,
            CancellationToken ct) =>
        {
            device = Uri.UnescapeDataString(device);
            var paths = ctx.Request.Query["paths"].Where(p => !string.IsNullOrEmpty(p)).ToList();

            if (paths.Count == 0)
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsync("No paths specified.");
                return;
            }

            if (!cm.TryGetConnectionId(device, out string? connId))
            {
                ctx.Response.StatusCode = 404;
                return;
            }

            ctx.Response.ContentType = "application/zip";
            ctx.Response.Headers["Content-Disposition"] = "attachment; filename=\"download.zip\"";
            ctx.Response.StatusCode = 200;

            try
            {
                using var zipArchive = new ZipArchive(ctx.Response.Body, ZipArchiveMode.Create, leaveOpen: true);

                foreach (string? filePath in paths)
                {
                    ct.ThrowIfCancellationRequested();
                    if (filePath == null) continue;

                    if (!cm.TryGetConnectionId(device, out string? activeConn))
                        break;

                    FileNodeResultDto? nodeInfo;
                    try
                    {
                        nodeInfo = await hubContext.Clients.Client(activeConn)
                            .InvokeAsync<FileNodeResultDto>("GetPathInfo", filePath, false, ct);
                    }
                    catch { continue; }

                    if (nodeInfo == null || !nodeInfo.Exists || nodeInfo.CurrentNode.IsDirectory)
                        continue;

                    string entryName = nodeInfo.CurrentNode.Name;
                    var entry = zipArchive.CreateEntry(entryName, CompressionLevel.NoCompression);

                    using var entryStream = entry.Open();
                    long offset = 0;
                    long totalLength = nodeInfo.CurrentNode.Length;
                    const int chunkSize = 512 * 1024; // 512 KB chunks

                    while (offset < totalLength)
                    {
                        ct.ThrowIfCancellationRequested();

                        if (!cm.TryGetConnectionId(device, out string? chunkConn)) break;

                        int bytesToRead = (int)Math.Min(chunkSize, totalLength - offset);
                        byte[]? chunk = await hubContext.Clients.Client(chunkConn)
                            .InvokeAsync<byte[]>("GetFileChunk", filePath, offset, bytesToRead, false, ct);

                        if (chunk == null || chunk.Length == 0) break;
                        await entryStream.WriteAsync(chunk, ct);
                        offset += chunk.Length;
                    }
                }
            }
            catch (OperationCanceledException) { /* browser cancelled */ }
            catch (Exception ex)
            {
                /* Console.WriteLine($"[WebUI /api/download-zip] Error: {ex.Message}"); */
                if (!ctx.Response.HasStarted)
                    ctx.Response.StatusCode = 500;
            }
        });
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static string GetContentType(string fileName)
    {
        return ContentTypeProvider.TryGetContentType(fileName, out string? ct)
            ? ct
            : "application/octet-stream";
    }
}