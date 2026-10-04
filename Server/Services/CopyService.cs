using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using PhonesBridge.Services;

namespace PhonesBridge.Services;

/// <summary>
/// Copies files/folders on the device itself: shared storage -> the isolated
/// App Data playground (GameService/…). Nothing travels over the network —
/// the phone copies locally and only progress is reported back.
/// Progress is shown through the same KDE job notification as fetch actions
/// (RunNotifier): total files to copy vs. already copied.
/// </summary>
public sealed class CopyService
{
    private readonly IHubContext<CommunicateSignalR> _hub;
    private readonly ConnectionManager _cm;

    public CopyService(IHubContext<CommunicateSignalR> hub, ConnectionManager cm)
    {
        _hub = hub;
        _cm = cm;
    }

    public sealed record CopyStartResult(bool Ok, string Message, string SessionId = "");

    /// <summary>Starts a copy session on the device and drives its progress job.</summary>
    public async Task<CopyStartResult> StartCopyAsync(string deviceName, List<string> paths, CancellationToken ct = default)
    {
        if (paths == null || paths.Count == 0)
            return new CopyStartResult(false, "Nothing selected to copy.");
        if (!_cm.TryGetConnectionId(deviceName, out string? connId))
            return new CopyStartResult(false, $"Device '{deviceName}' not connected.");

        string sessionId;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token);
            var resp = await _hub.Clients.Client(connId)
                .InvokeCoreAsync<JsonElement>("CopyToAppData", paths.ToArray(), linked.Token);
            sessionId = resp.TryGetProperty("sessionId", out var s) ? s.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(sessionId))
                return new CopyStartResult(false, "Phone refused to start the copy.");
        }
        catch (Exception ex)
        {
            return new CopyStartResult(false, $"Could not start copy: {ex.Message}");
        }

        var run = new ActionRunState
        {
            RunId = Guid.NewGuid().ToString("N")[..12],
            ActionId = "copy-to-appdata",
            ActionName = "Copy to App Data",
            DeviceName = deviceName,
            Status = "Running",
            StartedUtc = DateTime.UtcNow
        };
        run.AppendLog($"Copy {sessionId} started for {paths.Count} selected item(s) on {deviceName}");

        bool cancelled = false;
        var notifier = new RunNotifier(run.ActionName,
            onCancel: () =>
            {
                cancelled = true;
                try
                {
                    _ = _hub.Clients.Client(connId).SendAsync("CancelCopy", sessionId);
                }
                catch { }
            });
        _ = Task.Run(() => CopyProgressLoopAsync(connId, sessionId, notifier, run, () => cancelled));
        return new CopyStartResult(true, "Copy started.", sessionId);
    }

    private async Task CopyProgressLoopAsync(string connId, string sessionId, RunNotifier notifier, ActionRunState run, Func<bool> wasCancelled)
    {
        int errors = 0;
        int unknownStreak = 0;
        bool sessionLost = false;
        try
        {
            notifier.Start();
            while (true)
            {
                await Task.Delay(3000);
                JsonElement state;
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    state = await _hub.Clients.Client(connId)
                        .InvokeCoreAsync<JsonElement>("GetCopyProgress", new object[] { sessionId }, cts.Token);
                    errors = 0;
                }
                catch
                {
                    // Transient blips happen; only give up after repeated failures
                    // (e.g. device disconnected mid-copy).
                    if (++errors >= 3) break;
                    continue;
                }

                int total = GetInt(state, "total");
                int copied = GetInt(state, "copied");
                int skipped = GetInt(state, "skipped");
                int failed = GetInt(state, "failed");
                bool done = GetBool(state, "done");
                // Unknown session on the phone (old-or-missing session id):
                // wait briefly in case it appears, then fail explicitly instead
                // of declaring a false success. Absent flag = known session.
                bool known = !state.TryGetProperty("known", out var kn) || kn.ValueKind != JsonValueKind.False;
                string current = state.TryGetProperty("currentPath", out var cp) ? cp.GetString() ?? "" : "";

                if (!known)
                {
                    if (++unknownStreak > 10)
                    {
                        sessionLost = true;
                        run.Status = "Failed";
                        run.EndedUtc = DateTime.UtcNow;
                        run.Message = "Copy failed: session lost on device";
                        run.AppendLog(run.Message);
                        break; // finally[] finishes the notification exactly once
                    }
                    continue;
                }
                unknownStreak = 0;

                run.DiscoveredFiles = total;
                run.FetchedFiles = copied;
                run.SkippedFiles = skipped;
                run.FailedFiles = failed;
                run.CurrentPath = current;
                string currentFile = string.IsNullOrEmpty(current) ? "starting…" : current;
                notifier.Update(run, $"Copying {currentFile} · {copied} copied, {skipped} already there", indeterminate: true);

                if (done || wasCancelled()) break;
            }
        }
        catch { }
        finally
        {
            bool userCancelled = wasCancelled();
            int doneCount = run.FetchedFiles + run.SkippedFiles;
            if (userCancelled)
            {
                run.Status = "Cancelled";
                run.EndedUtc = DateTime.UtcNow;
                run.Message = $"Stopped: {doneCount} done, {run.FailedFiles} failed of {run.DiscoveredFiles} discovered";
            }
            else if (sessionLost)
            {
                // Status/Message already set above; just finish the notification.
            }
            else if (errors >= 3 && doneCount == 0)
            {
                run.Status = "Failed";
                run.EndedUtc = DateTime.UtcNow;
                run.Message = "Copy failed: device unreachable";
            }
            else if (run.DiscoveredFiles == 0)
            {
                // Legit empty: restricted/unreadable folder, or nothing selected.
                run.Status = "Done";
                run.EndedUtc = DateTime.UtcNow;
                run.Message = "Nothing to copy: folder is empty or unreadable.";
            }
            else if (run.FailedFiles > 0 && doneCount == 0)
            {
                run.Status = "Failed";
                run.EndedUtc = DateTime.UtcNow;
                run.Message = $"Copy failed: {run.FailedFiles} failed of {run.DiscoveredFiles} discovered";
            }
            else
            {
                run.Status = "Done";
                run.EndedUtc = DateTime.UtcNow;
                run.Message = $"Copied {run.FetchedFiles} of {run.DiscoveredFiles} ({run.SkippedFiles} already there)";
            }
            run.AppendLog(run.Message);
            notifier.Finish(run);
        }
    }

    private static int GetInt(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static bool GetBool(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
