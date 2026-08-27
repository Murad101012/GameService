using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PhonesBridge.Services;
using PhonesBridge.Services.Definitions;

var builder = WebApplication.CreateBuilder(args);

// Keep "Stop the server" responsive: without this, Kestrel drains the phone's
// long-lived SignalR connection for the 30 s default before force-aborting it.
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(10));

// Sync the Kestrel listening port (where 8052 was written) to the saved Settings
// port BEFORE the host builds so the configuration files Kestrel reads are current.
int savedPort = SettingsManager.LoadPortFromDisk();
bool savedAllowLan = SettingsManager.LoadAllowLanAccessFromDisk();
SettingsManager.SyncKestrelConfig(savedPort, savedAllowLan);
// Also apply the port deterministically in memory so the Kestrel endpoint binding
// is correct immediately (avoids any config-reload race at server start).
string bindHost = savedAllowLan ? "0.0.0.0" : "127.0.0.1";
builder.Configuration["Kestrel:Endpoints:Http:Url"] = $"http://{bindHost}:{savedPort}";

// ── Services ────────────────────────────────────────────────────────────────
builder.Services.AddSignalR(options =>
{
    // Increase incoming message limit from default 32 KB to 10 MB
    options.MaximumReceiveMessageSize = 10 * 1024 * 1024;
});
builder.Services.AddSingleton<ConnectionManager>();
builder.Services.AddSingleton<CacheManager>();
builder.Services.AddSingleton<DeviceConfigManager>();
builder.Services.AddSingleton<MoondreamService>();
builder.Services.AddSingleton<NsfwService>();
builder.Services.AddSingleton<ActionRunner>();
builder.Services.AddSingleton<ActionManager>();
builder.Services.AddSingleton<SettingsManager>();
builder.Services.AddSingleton<TunnelManager>();
builder.Services.AddSingleton<Socks5ProxyServer>();
builder.Services.AddSingleton<TunCaptureController>();
builder.Services.AddSingleton<P2pManager>();

// Razor Pages: enables .cshtml PageModel classes under Pages/
builder.Services.AddRazorPages();

var app = builder.Build();

// Let SettingsManager restart the host when the port is changed.
app.Services.GetRequiredService<SettingsManager>().AttachHost(app);

// Log how the WebUI is reachable (LAN vs localhost-only)
if (savedAllowLan)
{
    var lanUrls = SettingsManager.GetLocalLanUrls(savedPort);
    if (lanUrls.Count > 0)
    {
        Console.WriteLine("[WebUI] LAN access ENABLED — reachable from same network at:");
        foreach (var u in lanUrls) Console.WriteLine($"  {u}  (open on phone)");
    }
    else
        Console.WriteLine($"[WebUI] LAN access ENABLED — listening on 0.0.0.0:{savedPort} (no private IP detected yet)");
}
else
    Console.WriteLine($"[WebUI] LAN access disabled — only http://127.0.0.1:{savedPort} works. Enable in Settings → Allow LAN Access to open from phone on same Wi-Fi.");

// On shutdown, tell connected phones the server is stopping so their clients can
// drop the connection immediately instead of Kestrel waiting for the drain.
var hubContextForStop = app.Services.GetRequiredService<IHubContext<CommunicateSignalR>>();
app.Lifetime.ApplicationStopping.Register(() =>
{
    try
    {
        var connManager = app.Services.GetRequiredService<ConnectionManager>();
        foreach (var dev in connManager.GetAllDevices())
        {
            try
            {
                _ = hubContextForStop.Clients.Client(dev.ConnectionId)
                    .SendAsync("ServerStopping", cancellationToken: CancellationToken.None);
            }
            catch { }
        }
    }
    catch { }
});

// Start the tunnel: SOCKS5 proxy that relays through a connected phone. Always
// tries to bind; failures are logged, never fatal.
try
{
    app.Services.GetRequiredService<Socks5ProxyServer>().Start();
}
catch (Exception ex)
{
    Console.WriteLine($"[Tunnel] Failed to start SOCKS5 proxy: {ex.Message}");
}

// Crash recovery for TUN full-capture: clean up any routes/processes left behind
// by a previous run so the host's own internet can never be held hostage.
_ = Task.Run(async () =>
{
    try { await app.Services.GetRequiredService<TunCaptureController>().SweepStartupAsync(); }
    catch (Exception ex) { Console.WriteLine($"[Tunnel] Startup sweep error: {ex.Message}"); }
});

// Start the P2P direct-datapath listener. Bind failures are logged, never fatal —
// the Domain relay keeps working regardless.
try
{
    app.Services.GetRequiredService<P2pManager>().Start();
}
catch (Exception ex)
{
    Console.WriteLine($"[P2P] Failed to start direct link listener: {ex.Message}");
}

// ── Middleware ───────────────────────────────────────────────────────────────
app.Use(async (ctx, next) =>
{
    ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = 256 * 1024 * 1024; // 256MB for cache ZIP imports
    await next();
});

// ── Browse debug logging ──
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api/browse"))
        Console.WriteLine($"[TEST] Incoming {ctx.Request.Method} {ctx.Request.Path}{ctx.Request.QueryString}");
    await next();
    if (ctx.Request.Path.StartsWithSegments("/api/browse"))
        Console.WriteLine($"[TEST] Outgoing {ctx.Response.StatusCode} for {ctx.Request.Path}");
});

// ── LAN access guard: when disabled only localhost may reach the WebUI; when enabled only localhost + same-network private IPs ──
app.Use(async (ctx, next) =>
{
    var sm = ctx.RequestServices.GetRequiredService<SettingsManager>();
    bool allowLan = sm.IsLanAccessEnabled;
    var remoteIp = ctx.Connection.RemoteIpAddress;
    if (!SettingsManager.IsAllowedForLan(remoteIp, allowLan))
    {
        ctx.Response.StatusCode = 403;
        await ctx.Response.WriteAsync("LAN access disabled — enable it in Settings to allow other devices on the same network.");
        return;
    }
    await next();
});

app.UseStaticFiles();

// ── Debug log storage ───────────────────────────────────────────────────────
var debugLogQueue = new ConcurrentQueue<(DateTime Time, string Message)>();
const int MaxDebugLogs = 300;

// Make queue accessible to SignalR hub
CommunicateSignalR.DebugLogQueue = debugLogQueue;

// ── Server's own debug capture (🐛 Device Debug → "Server Device") ──────────
// Tee Console.WriteLine so every server-side log line is visible in the
// Device Debug console under the "Server Device" entry.
Console.SetOut(new ConsoleTeeWriter(Console.Out));
CommunicateSignalR.ServerDeviceName = Environment.MachineName;

// ── Quiet health-check ───────────────────────────────────────────────────────
app.MapGet("/status", () => Results.Ok());

// ── Debug log endpoint (phone calls this) ────────────────────────────────────
app.MapPost("/Debug", async (HttpContext context, IHubContext<CommunicateSignalR> hubContext) =>
{
    using var reader = new StreamReader(context.Request.Body);
    var message = await reader.ReadToEndAsync();
    message = message.Trim();
    Console.WriteLine($"[Debug] {message}");

    // Store for polling
    debugLogQueue.Enqueue((DateTime.UtcNow, message));
    while (debugLogQueue.Count > MaxDebugLogs) debugLogQueue.TryDequeue(out _);

    // Broadcast to browser via SignalR
    await hubContext.Clients.All.SendAsync("ReceiveDebugLog", message);
    context.Response.StatusCode = 200;
});

// ── Debug log polling endpoint (browser calls this) ──────────────────────────
app.MapGet("/api/debug/logs", () =>
{
    return Results.Ok(debugLogQueue.Select(x => new { time = x.Time, text = x.Message }).ToList());
});

// ── Device Debug console endpoints (used by wwwroot/js/app.js dbgFetchTargets etc.) ──
app.MapGet("/api/debug/targets", (ConnectionManager cm) =>
{
    var devices = cm.GetAllDevices().Select(d => new {
        name = d.DeviceName,
        platform = d.Platform,
        services = CommunicateSignalR.GetDeviceServices(d.DeviceName)
    }).ToList();
    var serverName = CommunicateSignalR.ServerDeviceName;
    return Results.Ok(new { success = true, server = new { name = serverName }, devices });
});
app.MapGet("/api/debug/service-logs", (string? device, string? service, int limit, ConnectionManager cm) =>
{
    device ??= ""; service ??= "";
    var logs = CommunicateSignalR.GetCachedServiceLogsV2(device, service, limit > 0 ? limit : 300)
        .Select(x => new { time = x.Time, text = x.Text }).ToList();
    return Results.Ok(logs);
});
app.MapGet("/api/debug/all", (string? device, int limit) =>
{
    device ??= "";
    var merged = CommunicateSignalR.GetAllServiceLogsMerged(device, limit > 0 ? limit : 500)
        .Select(x => new { time = x.Time, text = x.Text, service = x.Service }).ToList();
    return Results.Ok(merged);
});
app.MapGet("/api/debug/server-logs", (int limit) =>
{
    limit = limit > 0 ? limit : 400;
    var logs = ServerLogs.Queue.ToList().TakeLast(limit).Select(x => new { time = x.Time, text = x.Text }).ToList();
    return Results.Ok(logs);
});
app.MapGet("/api/device-events", async (HttpContext ctx, ConnectionManager cm) =>
{
    ctx.Response.Headers["Content-Type"] = "text/event-stream";
    ctx.Response.Headers["Cache-Control"] = "no-cache";
    ctx.Response.Headers["Connection"] = "keep-alive";
    var tcs = new TaskCompletionSource();
    void OnChange(string? _) => tcs.TrySetResult();
    cm.DevicesChanged += OnChange;
    cm.LinkStatusChanged += OnChange;
    try
    {
        // Send initial devices event so UI can render immediately
        var payload = System.Text.Json.JsonSerializer.Serialize(cm.GetAllDevices().Select(d => new { deviceName = d.DeviceName, platform = d.Platform }));
        await ctx.Response.WriteAsync($"event: devices\ndata: {payload}\n\n", ctx.RequestAborted);
        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
        while (!ctx.RequestAborted.IsCancellationRequested)
        {
            var delay = Task.Delay(30000, ctx.RequestAborted);
            var completed = await Task.WhenAny(tcs.Task, delay);
            if (completed == tcs.Task)
            {
                tcs = new TaskCompletionSource();
                cm.DevicesChanged += OnChange;
                cm.LinkStatusChanged += OnChange;
                var p2 = System.Text.Json.JsonSerializer.Serialize(cm.GetAllDevices().Select(d => new { deviceName = d.DeviceName, platform = d.Platform }));
                await ctx.Response.WriteAsync($"event: devices\ndata: {p2}\n\n", ctx.RequestAborted);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
            }
        }
    }
    catch { }
    finally
    {
        cm.DevicesChanged -= OnChange;
        cm.LinkStatusChanged -= OnChange;
    }
});

// ── Cache offline endpoints (used by Offline.cshtml / app.js) ──
app.MapGet("/api/cache/devices", (CacheManager cm, ConnectionManager conn) => Results.Ok(cm.GetAllCachedDevices(conn)));
app.MapGet("/api/cache/browse/{device}", async (string device, string? path, int startIndex, int fileLimit, string? sort, CacheManager cacheMgr, ConnectionManager connMgr) =>
{
    device = Uri.UnescapeDataString(device); path ??= "";
    var devId = connMgr.TryGetDeviceIdentity(device, out var devIdIdent) ? devIdIdent.DeviceId : device;
    // Try to resolve via cache if device is offline but cached
    if (!connMgr.TryGetDeviceIdentity(device, out _))
    {
        var cached = cacheMgr.GetAllCachedDevices(connMgr).FirstOrDefault(d => d.DeviceName == device);
        if (cached != null) devId = cached.DeviceId;
    }
    var folderEntry = cacheMgr.GetFolderCache(devId, path);
    if (folderEntry != null)
    {
        var dirs = folderEntry.Children?.Where(c => c.IsDirectory).ToList() ?? new();
        var files = folderEntry.Children?.Where(c => !c.IsDirectory).ToList() ?? new();
        return Results.Ok(new { currentNode = folderEntry.CurrentNode, directories = dirs, files, totalFileCount = files.Count, startIndex, fileLimit, exceededAt = (object?)null });
    }
    return Results.NotFound(new { message = "Folder not cached. Connect the phone and browse this folder first." });
});
app.MapDelete("/api/cache/", (CacheManager cm) =>
{
    cm.ClearCache();
    return Results.Ok(new { message = "Cache wiped" });
});
app.MapDelete("/api/cache/{deviceId}", (string deviceId, string? mediaType, CacheManager cm) =>
{
    deviceId = Uri.UnescapeDataString(deviceId);
    // Safety: never allow empty to wipe all via this route - use /api/cache/ for that
    if (string.IsNullOrWhiteSpace(deviceId)) return Results.BadRequest(new { message = "Invalid device" });
    cm.ClearCache(deviceId, mediaType);
    return Results.Ok(new { message = $"Cache cleared for {deviceId} mediaType={mediaType ?? "all"}" });
});

// ── Actions endpoints ──
app.MapGet("/api/actions", (ActionManager am) => Results.Ok(am.GetAll()));
app.MapPost("/api/actions", async (HttpContext ctx, ActionManager am) =>
{
    try
    {
        var body = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
        var entry = System.Text.Json.JsonSerializer.Deserialize<PhonesBridge.Services.Definitions.ActionEntry>(body, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (entry == null) return Results.BadRequest(new { message = "Invalid action" });
        var saved = am.Upsert(entry);
        return Results.Ok(saved);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
});
app.MapPut("/api/actions/{id}", async (string id, HttpContext ctx, ActionManager am) =>
{
    try
    {
        var body = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
        var entry = System.Text.Json.JsonSerializer.Deserialize<PhonesBridge.Services.Definitions.ActionEntry>(body, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (entry == null) return Results.BadRequest(new { message = "Invalid action" });
        entry.Id = id;
        var saved = am.Upsert(entry);
        return Results.Ok(saved);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
});
app.MapDelete("/api/actions/{id}", (string id, ActionManager am) =>
{
    bool ok = am.Delete(id);
    return ok ? Results.Ok(new { message = "Deleted" }) : Results.NotFound(new { message = "Not found" });
});
app.MapPost("/api/actions/{id}/enabled", async (string id, HttpContext ctx, ActionManager am) =>
{
    try
    {
        var body = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
        var doc = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(body, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        bool enabled = true;
        if (doc.TryGetProperty("enabled", out var el) && (el.ValueKind == System.Text.Json.JsonValueKind.True || el.ValueKind == System.Text.Json.JsonValueKind.False)) enabled = el.GetBoolean();
        var updated = am.DisableEnable(id, enabled);
        if (updated == null) return Results.NotFound(new { message = "Action not found" });
        return Results.Ok(updated);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
});
app.MapGet("/api/actions/runs", (ActionManager am) => Results.Ok(am.GetRecentRuns(20)));
app.MapDelete("/api/actions/runs", (ActionManager am) => { am.ClearRuns(); return Results.Ok(new { message = "Cleared" }); });
app.MapPost("/api/actions/{id}/add-files", async (string id, HttpContext ctx, ActionManager am) =>
{
    try
    {
        var body = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
        var doc = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(body, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        var files = new List<string>();
        if (doc.TryGetProperty("files", out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var el in arr.EnumerateArray()) if (el.GetString() is string s) files.Add(s);
        }
        var (act, added, skipped) = am.AddFiles(id, files);
        if (act == null) return Results.NotFound(new { message = "Action not found" });
        return Results.Ok(new { action = act, added, skipped });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
});
app.MapPost("/api/actions/{id}/run", async (string id, HttpContext ctx, ActionManager am, ConnectionManager cm) =>
{
    try
    {
        var body = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
        var doc = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(body, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        string? deviceName = null; bool allDevices = false;
        if (doc.TryGetProperty("device", out var devEl) && devEl.ValueKind == System.Text.Json.JsonValueKind.String) deviceName = devEl.GetString();
        if (doc.TryGetProperty("allDevices", out var allEl) && allEl.ValueKind == System.Text.Json.JsonValueKind.True) allDevices = true;
        var action = am.Get(id);
        if (action == null) return Results.NotFound(new { message = "Action not found" });
        if (allDevices)
        {
            var runs = new List<ActionRunState>();
            foreach (var dev in cm.GetAllDevices())
            {
                var run = am.Run(action, dev);
                if (run != null) runs.Add(run);
            }
            if (runs.Count == 0) return Results.NotFound(new { message = "No devices connected for All Devices run" });
            return Results.Ok(new { runs });
        }
        else
        {
            if (string.IsNullOrWhiteSpace(deviceName)) return Results.BadRequest(new { message = "Device not specified" });
            if (!cm.TryGetDeviceIdentity(deviceName, out var ident) || ident == null) return Results.NotFound(new { message = $"Device '{deviceName}' not connected" });
            var run = am.Run(action, ident);
            if (run == null) return Results.BadRequest(new { message = "Failed to start run (maybe disabled or device mismatch)" });
            return Results.Ok(run);
        }
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
});
app.MapPost("/api/actions/cache-status", async (HttpContext ctx, ActionManager am, CacheManager cm, ConnectionManager conn, DeviceConfigManager dcm) =>
{
    var body = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
    var doc = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(body);
    // Simplified: return empty status
    return Results.Ok(new object[] {});
});
app.MapGet("/api/device-config/{device}", (string device, ConnectionManager cm, DeviceConfigManager dcm) =>
{
    device = Uri.UnescapeDataString(device);
    if (cm.TryGetDeviceIdentity(device, out var ident) && ident != null)
    {
        var cfg = dcm.LoadDeviceConfig(ident.DeviceId);
        if (cfg != null) return Results.Ok(cfg);
    }
    return Results.NotFound(new { message = "Could not fetch device config" });
});
app.MapGet("/api/device-config-saved/{device}", (string device, DeviceConfigManager dcm, ConnectionManager cm) =>
{
    device = Uri.UnescapeDataString(device);
    var devId = cm.TryGetDeviceIdentity(device, out var ident) ? ident.DeviceId : device;
    var cfg = dcm.LoadDeviceConfig(devId) ?? dcm.LoadGlobalConfig();
    return Results.Ok(cfg);
});

// Background service that polls all connected phones for debug logs
_ = Task.Run(async () =>
{
    var hubContext = app.Services.GetRequiredService<IHubContext<CommunicateSignalR>>();
    var connManager = app.Services.GetRequiredService<ConnectionManager>();
    while (true)
    {
        try
        {
            foreach (var dev in connManager.GetAllDevices())
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    var logs = await hubContext.Clients.Client(dev.ConnectionId)
                        .InvokeCoreAsync<JsonElement>("GetDebugBuffer", Array.Empty<object>(), cts.Token);
                    if (logs.ValueKind == JsonValueKind.Array)
                    {
                        var lines = new List<string>();
                        foreach (var entry in logs.EnumerateArray())
                        {
                            string text = entry.GetString() ?? "";
                            if (text.Length > 0) lines.Add(text);
                        }
                        if (lines.Count > 0)
                        {
                            // Route into the per-device service store. Phones announce
                            // themselves as the single "Android" source; Windows devices
                            // polled through their Bridge report CoreAgent's buffer.
                            string svc = dev.Platform.Equals("Windows", StringComparison.OrdinalIgnoreCase)
                                ? "GameServiceCoreAgent.exe"
                                : "Android";
                            CommunicateSignalR.IngestPolledDebug(dev.DeviceName, svc, lines);
                            foreach (var text in lines)
                            {
                                debugLogQueue.Enqueue((DateTime.UtcNow, text));
                                while (debugLogQueue.Count > MaxDebugLogs) debugLogQueue.TryDequeue(out _);
                            }
                        }
                    }
                }
                catch { } // phone may not support GetDebugBuffer yet
            }
        }
        catch { }
        await Task.Delay(3000);
    }
});

// ── WebDAV middleware (fallback) ─────────────────────────────────────────────
app.UseMiddleware<WebDavMiddleware>();

// ── SignalR Hub ──────────────────────────────────────────────────────────────
app.MapHub<CommunicateSignalR>("/communicate");

// ── Test endpoint ────────────────────────────────────────────────────────────
app.MapGet("/api/test", () => Results.Ok("test ok"));

// ── Web UI API endpoints (/api/*) ────────────────────────────────────────────
app.MapWebUiEndpoints();

// ── Razor Pages ──────────────────────────────────────────────────────────────
app.MapRazorPages();

// ── Terminal listener ────────────────────────────────────────────────────────
_ = Task.Run(async () =>
{
    await Task.Delay(2000);
    Console.WriteLine("Terminal ready. Type 'refreshconfiguration' to push config.json to all phones.");

    while (true)
    {
        string? input = Console.ReadLine();
        if (input == null) break; // stdin closed (detached/background launch) — no interactive commands

        if (input.Equals("refreshconfiguration", StringComparison.OrdinalIgnoreCase))
        {
            string configPath = Path.Combine("Services", "DataContainers", "config.json");
            if (!File.Exists(configPath))
            {
                Console.WriteLine($"[Error] config.json not found at {configPath}!");
                continue;
            }

            try
            {
                string jsonContent = await File.ReadAllTextAsync(configPath);
                var config = JsonSerializer.Deserialize<Configuration>(jsonContent);
                if (config != null)
                {
                    var hubContext = app.Services.GetRequiredService<IHubContext<CommunicateSignalR>>();
                    var deviceConfigManager = app.Services.GetRequiredService<DeviceConfigManager>();
                    var cm = app.Services.GetRequiredService<ConnectionManager>();

                    // Save per-device config for all connected devices
                    foreach (var device in cm.GetAllDevices())
                    {
                        deviceConfigManager.SaveDeviceConfig(device.DeviceId, config);
                    }

                    await hubContext.Clients.All.SendAsync("RefreshConfiguration", jsonContent);
                    Console.WriteLine($"[Success] Configuration pushed! Frame: {config.VideoFrameNormalized}, SendDebug: {config.SendDebug}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] Failed to read or parse config.json: {ex.Message}");
            }
        }
    }
});

// Show the server in the system tray (Linux GTK). Best-effort: skipped silently
// when no tray stack is available. The tray's "Stop the server" SIGTERMs us,
// which runs the finally block below and cleans the tray back up. A background
// monitor respawns the tray if it ever dies while the server keeps running.
int trayPid = TrayHelper.Spawn(app);
using var trayMonitor = TrayHelper.StartMonitor(app);
try
{
    app.Run();
}
finally
{
    trayMonitor.Cancel();
    TrayHelper.Stop(TrayHelper.CurrentPid);
}
// ── Server's own debug store + console tee (types must follow top-level) ────
public static class ServerLogs
{
    public static readonly System.Collections.Concurrent.ConcurrentQueue<(DateTime Time, string Text)> Queue = new();
    private const int Cap = 500;

    public static void Capture(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        Queue.Enqueue((DateTime.UtcNow, line));
        while (Queue.Count > Cap) Queue.TryDequeue(out _);
    }
}

class ConsoleTeeWriter : TextWriter
{
    private readonly TextWriter _inner;
    public override System.Text.Encoding Encoding => _inner.Encoding;
    public ConsoleTeeWriter(TextWriter inner) => _inner = inner;
    public override void WriteLine(string? value)
    {
        _inner.WriteLine(value);
        ServerLogs.Capture(value ?? "");
    }
    public override void Write(string? value) => _inner.Write(value);
    public override void Flush() => _inner.Flush();
}
