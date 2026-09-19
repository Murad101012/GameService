using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using PhonesBridge.Services.Definitions;

namespace PhonesBridge.Services;

/// <summary>
/// Executes "fetch" actions: recursively walks the selected folders on a phone,
/// downloads the requested media into the PC offline cache, honouring the same
/// cache-suitability rules as manual browsing (never downgrade). Concurrency is
/// per action: images use 5 workers by default, heavy/video downloads use 1 by
/// default, but each action can choose custom counts or unlimited concurrency.
/// Folder structure is cached for the target folders AND their parent chain so
/// the offline browser can still navigate back up.
/// </summary>
public class ActionRunner
{
    private readonly ConnectionManager _cm;
    private readonly CacheManager _cache;
    private readonly DeviceConfigManager _deviceConfig;
    private readonly IHubContext<CommunicateSignalR> _hub;

    private const int ThumbChunkSize = 64 * 1024;
    private const int FileChunkSize = 512 * 1024;
    private const int DefaultImageWorkers = 5;
    private const int DefaultHeavyWorkers = 1;

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _runTokens = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ManualResetEventSlim> _runPauseGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ActionRunState> _runStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _runUserCancelled = new(StringComparer.OrdinalIgnoreCase);

    public ActionRunner(ConnectionManager cm, CacheManager cache, DeviceConfigManager deviceConfig, IHubContext<CommunicateSignalR> hub)
    {
        _cm = cm;
        _cache = cache;
        _deviceConfig = deviceConfig;
        _hub = hub;
    }

    public bool TryCancelRun(string runId)
    {
        if (_runTokens.TryGetValue(runId, out var cts))
        {
            try { cts.Cancel(); return true; } catch { return false; }
        }
        return false;
    }

    /// <summary>Pause a run: workers finish in-flight items, then wait before
    /// starting new ones. Polled via the pause gate in the worker lanes.</summary>
    public bool TryPauseRun(string runId)
    {
        if (_runPauseGates.TryGetValue(runId, out var gate))
        {
            gate.Reset();
            if (_runStates.TryGetValue(runId, out var run))
                run.IsPaused = true;
            return true;
        }
        return false;
    }

    public bool TryResumeRun(string runId)
    {
        if (_runPauseGates.TryGetValue(runId, out var gate))
        {
            gate.Set();
            if (_runStates.TryGetValue(runId, out var run))
                run.IsPaused = false;
            return true;
        }
        return false;
    }

    public bool IsRunPaused(string runId)
        => _runPauseGates.TryGetValue(runId, out var gate) && !gate.IsSet;

    private ManualResetEventSlim? PauseGateFor(string runId)
        => _runPauseGates.TryGetValue(runId, out var gate) ? gate : null;

    private enum FileKind { Image, Video, Audio, Other }

    private sealed class WorkItem
    {
        public required string RelPath { get; init; }
        public required string Name { get; init; }
        public required FileKind Kind { get; init; }
        public bool IsThumb { get; init; }
    }

    // ── Dynamic core split (rules from user) ─────────────────────────────
    // allowed = coreSet from phone (based on ParallelWork). pending = image+heavy queue size.
    // 1) queue==0 -> all to discover (proceed=0)
    // 2) queue>0  -> 1 proceed
    // 3) queue>100 -> 2 proceed, >500 -> 3 proceed, >1000 -> allowed-1 proceed
    // * always at least 1 discover
    // * increase immediately, decrease only when queue<5 (hysteresis) -> reset to 1 (or 0 if empty)
    private static int ComputeProceedCores(int pending, int allowed, int currentProceed)
    {
        if (allowed <= 1) return pending > 0 ? 1 : 0;
        if (pending <= 0) return 0;
        int desired;
        if (pending > 1000) desired = allowed - 1;
        else if (pending > 500) desired = Math.Min(3, allowed - 1);
        else if (pending > 100) desired = Math.Min(2, allowed - 1);
        else desired = 1;
        desired = Math.Clamp(desired, 1, allowed - 1);
        // Hysteresis: hold high allocation until queue <5
        if (desired < currentProceed && pending >= 5) return currentProceed;
        return desired;
    }

    public async Task RunAsync(ActionEntry action, DeviceIdentity device, ActionRunState run)
    {
        using var runCts = new CancellationTokenSource(TimeSpan.FromHours(4));
        _runTokens[run.RunId] = runCts;
        var pauseGate = new ManualResetEventSlim(true);
        _runPauseGates[run.RunId] = pauseGate;
        _runStates[run.RunId] = run;
        bool wasCancelled = false;
        bool deviceDisconnected = false;
        bool subscribed = false;
        RunNotifier? notifier = null;
        CancellationTokenSource? notifierCts = null;

        // Abort the whole run the instant the phone drops, and remember why so the
        // run can be tagged "Failed" with a clear reason instead of grinding through
        // every queued item and logging dozens of per-file connection errors.
        void OnDevicesChanged(string deviceName)
        {
            if (!string.Equals(deviceName, device.DeviceName, StringComparison.OrdinalIgnoreCase)) return;
            if (!_cm.TryGetConnectionId(deviceName, out _))
            {
                deviceDisconnected = true;
                runCts.Cancel();
            }
        }

        try
        {
            if (!_cm.TryGetConnectionId(device.DeviceName, out string? connId))
            {
                run.Status = "Failed"; run.EndedUtc = DateTime.UtcNow; run.Message = "Device not connected";
                return;
            }

            _cm.DevicesChanged += OnDevicesChanged;
            subscribed = true;

            Configuration cfg = ResolveConfig(action, device.DeviceId);
            string deviceId = device.DeviceId;

            // ── Dynamic core split: fetch allowed cores from phone (coreSet) ──
            int allowed = await GetAllowedCoresAsync(connId, cfg.ParallelWork, runCts.Token);
            if (allowed <= 0) allowed = Math.Max(1, Environment.ProcessorCount);
            int currentProceed = 0;
            // initially all to discover
            run.AllowedCores = allowed;
            run.DiscoverCores = allowed;
            run.ProceedCores = 0;
            Volatile.Write(ref run.QueueSize, 0);
            void UpdateCoreSplit()
            {
                int p = Volatile.Read(ref run.QueueSize);
                int desiredProceed = ComputeProceedCores(p, allowed, Volatile.Read(ref currentProceed));
                int prev = Volatile.Read(ref currentProceed);
                if (desiredProceed == prev) return;
                Volatile.Write(ref currentProceed, desiredProceed);
                run.ProceedCores = desiredProceed;
                run.DiscoverCores = p <= 0 ? allowed : Math.Max(1, allowed - desiredProceed);
                if (allowed <= 1 && p > 0) { run.DiscoverCores = 1; run.ProceedCores = 1; }
                run.AppendLog($"Cores: discover={run.DiscoverCores} proceed={run.ProceedCores} queue={p} allowed={allowed}");
            }

            // "Fetch the Files" actions may be bound to one device only.
            if (!string.IsNullOrEmpty(action.DeviceId)
                && !string.Equals(action.DeviceId, device.DeviceId, StringComparison.OrdinalIgnoreCase))
            {
                run.Status = "Failed"; run.EndedUtc = DateTime.UtcNow;
                run.Message = "Action is bound to another device";
                return;
            }

            bool isFileAction = string.Equals(action.Type, "fetchFiles", StringComparison.OrdinalIgnoreCase);

            bool wantImages, wantVideoThumb, wantVideoActual, wantAudio, wantFiles;
            if (isFileAction)
            {
                wantImages = true;
                wantVideoThumb = string.Equals(action.VideoFetchType, "thumbnail", StringComparison.OrdinalIgnoreCase);
                wantVideoActual = string.Equals(action.VideoFetchType, "actual", StringComparison.OrdinalIgnoreCase);
                wantAudio = true;
                wantFiles = true;
            }
            else
            {
                wantImages = action.ContentTypes.Contains("images", StringComparer.OrdinalIgnoreCase);
                wantVideoThumb = action.ContentTypes.Contains("videoThumb", StringComparer.OrdinalIgnoreCase);
                wantVideoActual = action.ContentTypes.Contains("videoActual", StringComparer.OrdinalIgnoreCase);
                wantAudio = action.ContentTypes.Contains("audio", StringComparer.OrdinalIgnoreCase);
                wantFiles = action.ContentTypes.Contains("files", StringComparer.OrdinalIgnoreCase);
            }

            if (!wantImages && !wantVideoThumb && !wantVideoActual && !wantAudio && !wantFiles)
            {
                run.Status = "Done"; run.EndedUtc = DateTime.UtcNow; run.Message = "No content types selected";
                return;
            }

            // "Fetch the Files": if every listed file is already cached at an equal-or-higher
            // quality, report "already finished" without re-downloading anything. This check
            // runs on every fire so a cache that was lost or cleared retriggers a real fetch.
            if (isFileAction && AllFilesAlreadyFetched(action, cfg, deviceId))
            {
                run.Status = "Done"; run.EndedUtc = DateTime.UtcNow;
                run.Message = $"Action ({action.Name}) already finished";
                run.AppendLog(run.Message);
                return;
            }

            // ── Persistent KDE progress notification (acts like Dolphin's file-copy
            //    toast: speed per second, LIVE counters, 0→100% bar, shrinks to tray).
            //    Its own pause/cancel buttons drive this run (no separate popup).
            RunNotifier? jobNotifier = null;
            notifier = new RunNotifier(action.Name,
                onCancel: () =>
                {
                    _runUserCancelled[run.RunId] = true;
                    TryCancelRun(run.RunId);
                },
                onSuspend: () =>
                {
                    if (TryPauseRun(run.RunId))
                        jobNotifier?.SetSuspended(true);
                },
                onResume: () =>
                {
                    if (TryResumeRun(run.RunId))
                        jobNotifier?.SetSuspended(false);
                });
            jobNotifier = notifier;
            notifierCts = new CancellationTokenSource();
            _ = Task.Run(() => RunNotifierLoopAsync(notifier, run, notifierCts.Token));

            // ── Cache folder structure for the ancestor chain (incl. root "") ──
            var ancestors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IEnumerable<string> sourcePaths = isFileAction
                ? action.Files.Select(ParentDir)
                : action.Folders;
            foreach (var folder in sourcePaths)
            {
                if (string.IsNullOrEmpty(folder)) continue;
                foreach (var anc in Ancestors(folder))
                {
                    if (!string.Equals(anc, folder, StringComparison.OrdinalIgnoreCase))
                        ancestors.Add(anc);
                }
                ancestors.Add(folder);
            }
            foreach (var anc in ancestors)
            {
                var nodeInfo = await GetPathInfoAsync(connId, anc, ct: runCts.Token);
                if (nodeInfo == null || !nodeInfo.Exists) continue;
                _cache.SaveFolderCache(deviceId, anc, nodeInfo);
                run.AppendLog($"Folder structure cached: {(string.IsNullOrEmpty(anc) ? "/" : anc)}");
            }

            // ── Channels + worker lanes sized by the action's concurrency settings ──
            var imageChannel = Channel.CreateUnbounded<WorkItem>();
            var heavyChannel = Channel.CreateUnbounded<WorkItem>();

            string imgMode = (action.ImageConcurrencyMode ?? "default").ToLowerInvariant();
            string hvyMode = (action.HeavyConcurrencyMode ?? "default").ToLowerInvariant();
            int imgWorkers = ResolveWorkerCount(imgMode, action.ImageConcurrency, DefaultImageWorkers);
            int hvyWorkers = ResolveWorkerCount(hvyMode, action.HeavyConcurrency, DefaultHeavyWorkers);

            // Dynamic proceed limiter (enforces proceedCores concurrency) - only for heavy lane
            int concurrentProceed = 0;
            async Task WaitProceedSlot(CancellationToken c)
            {
                while (!c.IsCancellationRequested)
                {
                    int limit = Volatile.Read(ref currentProceed);
                    if (limit <= 0) { await Task.Delay(50, c); continue; }
                    int cur = Volatile.Read(ref concurrentProceed);
                    if (cur < limit) break;
                    await Task.Delay(20, c);
                }
            }

            // Wrappers: image thumbs bypass proceed limiter (they are discover-bound, not proceed),
            // heavy lane still respects it. Both decrement pending queue.
            async Task ProcessThumbLimited(WorkItem item)
            {
                // No WaitProceedSlot for thumbs - allow full unlimited concurrency
                try { await ProcessThumbnailAsync(run, connId, deviceId, item, cfg.VideoFrameNormalized, cfg.ThumbnailQuality, runCts.Token); }
                finally { Interlocked.Decrement(ref run.QueueSize); UpdateCoreSplit(); }
            }
            async Task ProcessHeavyLimited(WorkItem item)
            {
                await WaitProceedSlot(runCts.Token);
                Interlocked.Increment(ref concurrentProceed);
                try { await ProcessHeavyAsync(run, connId, deviceId, item, cfg, runCts.Token); }
                finally { Interlocked.Decrement(ref concurrentProceed); Interlocked.Decrement(ref run.QueueSize); UpdateCoreSplit(); }
            }

            var workers = new List<Task>();
            // pauseGate was created at run start (set = running).
            if (imgMode == "unlimited")
            {
                workers.Add(DrainUnlimitedAsync(imageChannel.Reader, ProcessThumbLimited, runCts.Token, pauseGate));
            }
            else
            {
                // Use RunBoundedLane directly with dynamic proceed limiter; keep original image worker count
                workers.Add(RunBoundedLaneAsync(imageChannel.Reader, ProcessThumbLimited, imgWorkers * DefaultImageWorkers, runCts.Token, pauseGate));
            }

            if (hvyMode == "unlimited")
            {
                workers.Add(DrainUnlimitedAsync(heavyChannel.Reader, ProcessHeavyLimited, runCts.Token, pauseGate));
            }
            else
            {
                // Ensure enough heavy workers to utilize dynamic proceed cores (up to allowed-1)
                int heavyPool = Math.Max(hvyWorkers, Math.Max(1, allowed - 1));
                workers.Add(RunBoundedLaneAsync(heavyChannel.Reader, ProcessHeavyLimited, heavyPool, runCts.Token, pauseGate));
            }

            // ── Producer: deep-walk each target folder (fetch) or queue the explicit
            //    file list (fetchFiles), sending matching files to the worker lanes ──
            var seenItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (isFileAction)
                {
                    foreach (var file in action.Files)
                    {
                        runCts.Token.ThrowIfCancellationRequested();
                        await ProduceFromFileAsync(connId, deviceId, file, run, wantVideoThumb, wantVideoActual,
                            imageChannel.Writer, heavyChannel.Writer, seenItems, runCts.Token, pauseGate);
                    }
                }
                else
                {
                    foreach (var folder in action.Folders)
                    {
                        runCts.Token.ThrowIfCancellationRequested();
                        await EnumerateAsync(connId, deviceId, folder, run, wantImages, wantVideoThumb, wantVideoActual, wantAudio, wantFiles,
                            imageChannel.Writer, heavyChannel.Writer, seenItems, runCts.Token, pauseGate);
                    }
                }
            }
            catch (OperationCanceledException) when (runCts.IsCancellationRequested) { }

            imageChannel.Writer.TryComplete();
            heavyChannel.Writer.TryComplete();
            try { await Task.WhenAll(workers); }
            catch (OperationCanceledException) { }

            if (deviceDisconnected)
            {
                run.Status = "Failed";
                run.EndedUtc = DateTime.UtcNow;
                run.Message = "Failed: the phone disconnected mid-fetch";
                run.AppendLog("Run failed: the phone disconnected while fetching");
            }
            else if (_runUserCancelled.TryGetValue(run.RunId, out bool cancelled) && cancelled)
            {
                run.Status = "Cancelled";
                run.EndedUtc = DateTime.UtcNow;
                run.Message = $"Stopped: {run.FetchedFiles} fetched, {run.SkippedFiles} skipped, {run.FailedFiles} failed of {run.DiscoveredFiles} discovered";
                run.AppendLog(run.Message);
            }
            else
            {
                run.Status = "Done";
                run.EndedUtc = DateTime.UtcNow;
                run.Message = $"Done: {run.FetchedFiles} fetched, {run.SkippedFiles} skipped, {run.FailedFiles} failed of {run.DiscoveredFiles} discovered";
                run.AppendLog(run.Message);
            }
        }
        catch (Exception ex)
        {
            run.Status = "Failed";
            run.EndedUtc = DateTime.UtcNow;
            run.Message = deviceDisconnected ? "Failed: the phone disconnected mid-fetch" : ex.Message;
            run.AppendLog($"Run failed: {run.Message}");
        }
        finally
        {
            _runTokens.TryRemove(run.RunId, out _);
            _runStates.TryRemove(run.RunId, out _);
            _runUserCancelled.TryRemove(run.RunId, out _);
            if (_runPauseGates.TryRemove(run.RunId, out var gate))
            {
                try { gate.Set(); gate.Dispose(); } catch { }
            }
            if (subscribed) _cm.DevicesChanged -= OnDevicesChanged;
            if (notifier != null)
            {
                notifierCts!.Cancel();
                notifier.Finish(run);
                notifierCts.Dispose();
            }
        }
    }

    private static async Task RunNotifierLoopAsync(RunNotifier notifier, ActionRunState run, CancellationToken ct)
    {
        notifier.Start();
        try
        {
            while (!ct.IsCancellationRequested && !run.IsFinished)
            {
                notifier.Update(run);
                await Task.Delay(1000, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            /* Console.WriteLine($"[RunNotifier] Loop error: {ex.Message}"); */
        }
    }

    private Configuration ResolveConfig(ActionEntry action, string deviceId)
    {
        if (action.UseGlobalConfig)
        {
            return _deviceConfig.LoadDeviceConfig(deviceId)
                ?? _deviceConfig.LoadGlobalConfig()
                ?? new Configuration();
        }

        return new Configuration
        {
            VideoFrameNormalized = Math.Clamp(action.Overrides.VideoFrameNormalized, 0f, 1f),
            ThumbnailQuality = Math.Clamp(action.Overrides.ThumbnailQuality, 0f, 1f),
            VideoQuality = 0f,
            VideoBitrateMultiplier = Math.Clamp(action.Overrides.VideoBitrateMultiplier, 0.05f, 1f),
            VideoResolutionMultiplier = Math.Clamp(action.Overrides.VideoResolutionMultiplier, 0.05f, 1f),
            BitrateCappingEnabled = action.Overrides.BitrateCappingEnabled,
            BitrateCapMbps = action.Overrides.BitrateCapMbps,
            ResolutionCeilingEnabled = action.Overrides.ResolutionCeilingEnabled,
            ResolutionCeilingPx = action.Overrides.ResolutionCeilingPx
        };
    }

    private async Task<int> GetAllowedCoresAsync(string connId, string parallelWorkHint, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var cfg = await _hub.Clients.Client(connId).InvokeAsync<JsonElement>("GetDeviceConfig", cts.Token);
            if (cfg.ValueKind == JsonValueKind.Object)
            {
                if (cfg.TryGetProperty("coreSet", out var cs) && cs.TryGetInt32(out int v) && v > 0) return Math.Max(1, v);
                if (cfg.TryGetProperty("coreAmount", out var ca) && ca.TryGetInt32(out int tot) && tot > 0)
                {
                    // fallback compute locally using hint and tot
                    string m = (parallelWorkHint ?? "Medium").ToLowerInvariant();
                    if (m == "low") return Math.Min(2, tot);
                    if (m == "high") return Math.Max(1, (int)Math.Ceiling(tot * 0.75));
                    if (m == "absolute") return tot;
                    return Math.Max(1, (int)Math.Ceiling(tot / 2.0));
                }
            }
        }
        catch { }
        // fallback from pushed cfg
        string mode = (parallelWorkHint ?? "Medium").ToLowerInvariant();
        int localTot = Environment.ProcessorCount;
        if (mode == "low") return Math.Min(2, localTot);
        if (mode == "high") return Math.Max(1, (int)Math.Ceiling(localTot * 0.75));
        if (mode == "absolute") return localTot;
        return Math.Max(1, (int)Math.Ceiling(localTot / 2.0));
    }

    /// <summary>Ancestor chain of a relative folder path: immediate parent up to the root ("").</summary>
    private static IEnumerable<string> Ancestors(string folder)
    {
        var parts = folder.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = new List<string>();
        for (int i = 0; i < parts.Length - 1; i++)
        {
            current.Add(parts[i]);
            yield return string.Join("/", current);
        }
        yield return "";
    }

    private async Task<FileNodeResultDto?> GetPathInfoAsync(string connId, string path,
        bool imageOnly = false, bool videoOnly = false, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        return await _hub.Clients.Client(connId).InvokeAsync<FileNodeResultDto>(
            "GetPathInfo", path, false, imageOnly, videoOnly, 0, 0, 0, cts.Token);
    }

    private async Task EnumerateAsync(string connId, string deviceId, string folder, ActionRunState run,
        bool wantImages, bool wantVideoThumb, bool wantVideoActual, bool wantAudio, bool wantFiles,
        ChannelWriter<WorkItem> imageWriter, ChannelWriter<WorkItem> heavyWriter,
        HashSet<string> seenItems, CancellationToken ct, ManualResetEventSlim? pauseGate = null)
    {
        if (ct.IsCancellationRequested) return;
        // Pause gate: no phone traffic while paused (cancel still interrupts).
        if (pauseGate != null)
        {
            try { await Task.Run(() => pauseGate.Wait(ct), ct); }
            catch (OperationCanceledException) { return; }
        }
        // Skip Android .Trash and other trash folders that cause Invalid path loops
        if (folder.Contains(".Trash", StringComparison.OrdinalIgnoreCase) || folder.Contains(".thumbnails", StringComparison.OrdinalIgnoreCase) || folder.Contains("/.") ) return;

        var nodeInfo = await GetPathInfoAsync(connId, folder, ct: ct);
        if (nodeInfo == null)
        {
            run.AppendLog($"Path validation failed for '{folder}' (unreachable)");
            return;
        }
        if (!nodeInfo.Exists)
        {
            run.AppendLog($"Invalid folder path: '{folder}' does not exist on the device");
            return;
        }
        if (nodeInfo.CurrentNode is { IsDirectory: false })
        {
            run.AppendLog($"'{folder}' is not a folder on the device");
            return;
        }

        _cache.SaveFolderCache(deviceId, folder, nodeInfo);

        foreach (var child in nodeInfo.Children ?? new())
        {
            if (child.IsDirectory)
            {
                await EnumerateAsync(connId, deviceId, child.RelativePath, run,
                    wantImages, wantVideoThumb, wantVideoActual, wantAudio, wantFiles,
                    imageWriter, heavyWriter, seenItems, ct, pauseGate);
                continue;
            }

            var kind = Classify(child.Name);
            bool added = false;
            switch (kind)
            {
                case FileKind.Image when wantImages:
                    added = Queue(imageWriter, child, kind, isThumb: true, seenItems, run);
                    break;
                case FileKind.Video when wantVideoThumb:
                    added = Queue(heavyWriter, child, kind, isThumb: true, seenItems, run);
                    break;
                case FileKind.Video when wantVideoActual:
                    added = Queue(heavyWriter, child, kind, isThumb: false, seenItems, run);
                    break;
                case FileKind.Audio when wantAudio:
                    added = Queue(heavyWriter, child, kind, isThumb: false, seenItems, run);
                    break;
                case FileKind.Other when wantFiles:
                    added = Queue(heavyWriter, child, kind, isThumb: false, seenItems, run);
                    break;
            }

            if (added)
            {
                run.CurrentPath = child.RelativePath;
                run.DiscoveredFiles++;
            }
        }
    }

    /// <summary>Parent directory ("" for a root-level file) of a storage-relative path.</summary>
    private static string ParentDir(string path)
    {
        string p = path.Trim().TrimStart('/').TrimEnd('/');
        int idx = p.LastIndexOf('/');
        return idx < 0 ? "" : p[..idx];
    }

    /// <summary>
    /// Queues a single explicit file (fetchFiles action): validates it exists on the
    /// phone and routes it to the correct worker lane based on its kind and the
    /// action's Video Fetch Type.
    /// </summary>
    private async Task ProduceFromFileAsync(string connId, string deviceId, string file, ActionRunState run,
        bool wantVideoThumb, bool wantVideoActual,
        ChannelWriter<WorkItem> imageWriter, ChannelWriter<WorkItem> heavyWriter,
        HashSet<string> seenItems, CancellationToken ct, ManualResetEventSlim? pauseGate = null)
    {
        if (ct.IsCancellationRequested) return;
        if (pauseGate != null)
        {
            try { await Task.Run(() => pauseGate.Wait(ct), ct); }
            catch (OperationCanceledException) { return; }
        }

        string relPath = file.Trim().TrimStart('/');
        if (string.IsNullOrEmpty(relPath)) return;

        var nodeInfo = await GetPathInfoAsync(connId, relPath, ct: ct);
        if (nodeInfo == null)
        {
            run.FailedFiles++;
            run.AppendLog($"Path validation failed for '{relPath}' (unreachable)");
            return;
        }
        if (!nodeInfo.Exists)
        {
            run.FailedFiles++;
            run.AppendLog($"Invalid file path: '{relPath}' does not exist on the device");
            return;
        }
        if (nodeInfo.CurrentNode is { IsDirectory: true })
        {
            run.FailedFiles++;
            run.AppendLog($"'{relPath}' is a folder, not a file");
            return;
        }

        var kind = Classify(nodeInfo.CurrentNode.Name);
        bool added;
        switch (kind)
        {
            case FileKind.Image:
                added = Queue(imageWriter, nodeInfo.CurrentNode, kind, isThumb: true, seenItems, run);
                break;
            case FileKind.Video when wantVideoThumb:
                added = Queue(heavyWriter, nodeInfo.CurrentNode, kind, isThumb: true, seenItems, run);
                break;
            case FileKind.Video when wantVideoActual:
                added = Queue(heavyWriter, nodeInfo.CurrentNode, kind, isThumb: false, seenItems, run);
                break;
            default:
                added = Queue(heavyWriter, nodeInfo.CurrentNode, kind, isThumb: false, seenItems, run);
                break;
        }

        if (added)
        {
            run.CurrentPath = relPath;
            run.DiscoveredFiles++;
        }
    }

    private static bool Queue(ChannelWriter<WorkItem> w, FileNodeDto node, FileKind kind, bool isThumb, HashSet<string> seenItems, ActionRunState? run = null)
    {
        string key = node.RelativePath + (isThumb ? "#thumb" : "#full");
        if (!seenItems.Add(key)) return false;
        bool ok = w.TryWrite(new WorkItem { RelPath = node.RelativePath, Name = node.Name, Kind = kind, IsThumb = isThumb });
        if (ok && run != null)
        {
            int pending = Interlocked.Increment(ref run.QueueSize);
            int allowed = run.AllowedCores > 0 ? run.AllowedCores : 1;
            int cur = run.ProceedCores;
            int desired = ComputeProceedCores(pending, allowed, cur);
            if (desired != cur)
            {
                run.ProceedCores = desired;
                run.DiscoverCores = pending <= 0 ? allowed : Math.Max(1, allowed - desired);
                if (allowed <= 1 && pending > 0) { run.DiscoverCores = 1; run.ProceedCores = 1; }
                run.AppendLog($"Cores: discover={run.DiscoverCores} proceed={run.ProceedCores} queue={pending} allowed={allowed}");
            }
        }
        return ok;
    }

    private static FileKind Classify(string name)
    {
        string ext = Path.GetExtension(name).ToLowerInvariant();
        if (ext is ".jpg" or ".jpeg" or ".png" or ".webp" or ".bmp" or ".gif" or ".heic") return FileKind.Image;
        if (ext is ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".webm" or ".flv" or ".m4v" or ".3gp") return FileKind.Video;
        if (ext is ".mp3" or ".wav" or ".flac" or ".aac" or ".ogg" or ".m4a" or ".opus") return FileKind.Audio;
        return FileKind.Other;
    }

    // ── Workers ────────────────────────────────────────────────────────────────

    private static int ResolveWorkerCount(string mode, int custom, int fallback)
    {
        if (mode == "custom")
        {
            return custom > 0 ? Math.Clamp(custom, 1, 512) : fallback;
        }
        return fallback;
    }

    /// <summary>
    /// Bounded lane: N workers pull items from the channel and process them one
    /// at a time, giving at most N concurrent fetches.
    /// </summary>
    private static async Task RunBoundedLaneAsync(ChannelReader<WorkItem> reader, Func<WorkItem, Task> handler, int workers, CancellationToken ct, ManualResetEventSlim? pauseGate = null)
    {
        var tasks = new List<Task>(Math.Max(1, workers));
        for (int i = 0; i < Math.Max(1, workers); i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await foreach (var item in reader.ReadAllAsync(ct))
                    {
                        // Pause gate: block before starting new items (cancel still works).
                        if (pauseGate != null)
                        {
                            try { await Task.Run(() => pauseGate.Wait(ct), ct); }
                            catch (OperationCanceledException) { throw; }
                        }
                        await handler(item);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Run cancelled (e.g. phone disconnected) — stop quietly.
                }
            }, ct));
        }
        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    /// <summary>
    /// Unlimited lane: every item that arrives starts its own fetch immediately and
    /// runs to completion side-by-side with all others — no concurrency cap.
    /// Fixed: was enqueuing without starting until channel closed, causing 799 discovered but 0 fetched.
    /// </summary>
    private static async Task DrainUnlimitedAsync(ChannelReader<WorkItem> reader, Func<WorkItem, Task> handler, CancellationToken ct, ManualResetEventSlim? pauseGate = null)
    {
        var pending = new List<Task>();
        try
        {
            await foreach (var item in reader.ReadAllAsync(ct))
            {
                // Pause gate: block before starting new items (cancel still works).
                if (pauseGate != null)
                {
                    try { await Task.Run(() => pauseGate.Wait(ct), ct); }
                    catch (OperationCanceledException) { throw; }
                }
                // Start immediately and keep reference to observe completion
                var t = handler(item);
                pending.Add(t);
                // Opportunistically clean up completed tasks to avoid unbounded list
                if (pending.Count > 100)
                {
                    var done = await Task.WhenAny(pending);
                    pending.RemoveAll(x => x.IsCompleted);
                }
            }
            await Task.WhenAll(pending);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Run cancelled — in-flight fetches stop via their linked tokens.
        }
    }

    private async Task ThumbnailWorkerAsync(ChannelReader<WorkItem> reader, ActionRunState run, string connId, string deviceId,
        float videoFrame, float thumbQuality, CancellationToken ct)
    {
        await RunBoundedLaneAsync(reader,
            item => ProcessThumbnailAsync(run, connId, deviceId, item, videoFrame, thumbQuality, ct),
            DefaultImageWorkers, ct, PauseGateFor(run.RunId));
    }

    private async Task HeavyWorkerAsync(ChannelReader<WorkItem> reader, ActionRunState run, string connId, string deviceId,
        Configuration cfg, CancellationToken ct)
    {
        await RunBoundedLaneAsync(reader,
            item => ProcessHeavyAsync(run, connId, deviceId, item, cfg, ct),
            DefaultHeavyWorkers, ct, PauseGateFor(run.RunId));
    }

    private async Task ProcessThumbnailAsync(ActionRunState run, string connId, string deviceId, WorkItem item,
        float videoFrame, float thumbQuality, CancellationToken ct)
    {
        try
        {
            await FetchThumbnailAsync(run, connId, deviceId, item, videoFrame, thumbQuality, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            run.FailedFiles++;
            run.AppendLog($"Thumb fail {item.RelPath}: {ex.Message}");
        }
    }

    private async Task ProcessHeavyAsync(ActionRunState run, string connId, string deviceId, WorkItem item, Configuration cfg, CancellationToken ct)
    {
        try
        {
            if (item.IsThumb)
            {
                await FetchThumbnailAsync(run, connId, deviceId, item, cfg.VideoFrameNormalized, cfg.ThumbnailQuality, ct);
            }
            else
            {
                await FetchFullFileAsync(run, connId, deviceId, item, cfg, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            run.FailedFiles++;
            run.AppendLog($"Failed {item.RelPath}: {ex.Message}");
        }
    }

    private async Task FetchThumbnailAsync(ActionRunState run, string connId, string deviceId, WorkItem item,
        float videoFrame, float thumbQuality, CancellationToken ct)
    {
        bool isVideo = item.Kind == FileKind.Video;
        float wantFrame = isVideo ? videoFrame : -1f;
        run.AppendLog($"FetchThumb start {item.RelPath} wantFrame={wantFrame} wantQuality={thumbQuality} isVideo={isVideo}");

        // 1) Never downgrade: skip if the cache already meets the requested settings.
        _cache.TryGetCachedThumbnail(deviceId, item.RelPath, wantFrame, thumbQuality, out _, out var meta);
        if (meta != null && CacheManager.IsThumbnailSuitable(meta, wantFrame, thumbQuality, isVideo))
        {
            run.SkippedFiles++;
            run.AppendLog($"FetchThumb skip (cached suitable) {item.RelPath}");
            return;
        }

        float fetchQuality = meta != null && meta.Quality > 0f ? Math.Max(thumbQuality, meta.Quality) : thumbQuality;
        float fetchFrame = isVideo ? wantFrame : -1f;

        // FIX: For Home ("/") with quality 0, force Domain route and bypass P2P which was stalling samsung
        // Also ensure .Trash is already skipped in EnumerateAsync, but double-check here
        if (item.RelPath.Contains(".Trash", StringComparison.OrdinalIgnoreCase)) { run.SkippedFiles++; return; }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token);

        FileNodeResultDto? nodeInfo = null;
        try
        {
            nodeInfo = await _hub.Clients.Client(connId).InvokeAsync<FileNodeResultDto>(
                "GetPathInfoWithOverride", item.RelPath, true, fetchFrame, fetchQuality, linked.Token);
        }
        catch (Exception ex)
        {
            run.AppendLog($"FetchThumb GetPathInfo fail {item.RelPath}: {ex.GetType().Name}: {ex.Message}");
            // Retry once on any failure (covers P2P hang, timeout, disconnect)
            try
            {
                using var retryCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                using var retryLinked = CancellationTokenSource.CreateLinkedTokenSource(ct, retryCts.Token);
                nodeInfo = await _hub.Clients.Client(connId).InvokeAsync<FileNodeResultDto>(
                    "GetPathInfoWithOverride", item.RelPath, true, fetchFrame, fetchQuality, retryLinked.Token);
            }
            catch (Exception ex2)
            {
                run.AppendLog($"FetchThumb retry fail {item.RelPath}: {ex2.Message}");
                throw;
            }
        }

        if (nodeInfo == null || !nodeInfo.Exists || nodeInfo.CurrentNode == null)
            throw new IOException("Could not read source for thumbnail");
        long total = nodeInfo.CurrentNode.Length;

        using var ms = new MemoryStream();
        long offset = 0;
        while (offset < total)
        {
            byte[]? chunk = null;
            try
            {
                chunk = await _hub.Clients.Client(connId).InvokeAsync<byte[]>(
                    "GetFileChunkWithOverride", item.RelPath, offset, ThumbChunkSize, true, fetchFrame, fetchQuality, linked.Token);
            }
            catch (Exception ex)
            {
                run.AppendLog($"FetchThumb chunk fail {item.RelPath}@{offset}: {ex.Message}");
                throw new IOException($"Chunk fail @{offset}: {ex.Message}", ex);
            }
            if (chunk == null || chunk.Length == 0) break;
            await ms.WriteAsync(chunk, ct);
            run.AddBytes(chunk.Length);
            offset += chunk.Length;
        }

        if (ms.Length == 0 || offset < total) throw new IOException("Incomplete thumbnail transfer");

        _cache.SaveCachedThumbnail(deviceId, item.RelPath, item.Name, ms.ToArray(),
            nodeInfo.CurrentNode.OriginalLength > 0 ? nodeInfo.CurrentNode.OriginalLength : total,
            isVideo ? "video" : "image", fetchFrame, fetchQuality,
            nodeInfo.CurrentNode.OriginalPreviewLength > 0 ? nodeInfo.CurrentNode.OriginalPreviewLength : 0);

        run.FetchedFiles++;
        run.AppendLog($"Cached thumb {item.RelPath} (Q={fetchQuality:F2})");
    }

    private async Task FetchFullFileAsync(ActionRunState run, string connId, string deviceId, WorkItem item, Configuration cfg, CancellationToken ct)
    {
        if (item.Kind == FileKind.Video)
        {
            float brm = Math.Clamp(cfg.VideoBitrateMultiplier, 0.05f, 1f);
            float resm = Math.Clamp(cfg.VideoResolutionMultiplier, 0.05f, 1f);
            if (brm >= 0.999f && resm >= 0.999f)
            {
                // Raw mode: the user configured native quality → cache the original file.
                var meta = _cache.GetFullFileMetadata(deviceId, item.RelPath);
                if (CacheManager.IsFullFileSuitable(meta, 1f, 1f, 0, 0))
                { run.SkippedFiles++; return; }
                await StreamNativeFileAsync(run, connId, deviceId, item, cfg, storeNative: true, ct);
            }
            else
            {
                int reqBrCap = cfg.BitrateCappingEnabled ? (int)Math.Round(cfg.BitrateCapMbps * 1_000_000) : 0;
                int reqCeilPx = cfg.ResolutionCeilingEnabled ? cfg.ResolutionCeilingPx : 0;
                var meta = _cache.GetFullFileMetadata(deviceId, item.RelPath);
                if (CacheManager.IsFullFileSuitable(meta, brm, resm, reqBrCap, reqCeilPx))
                { run.SkippedFiles++; return; }
                await TranscodeAndCacheAsync(run, connId, deviceId, item, cfg, brm, resm, ct);
            }
        }
        else
        {
            // Audio / other files: no quality settings — cache once.
            if (_cache.HasFullFile(deviceId, item.RelPath))
            { run.SkippedFiles++; return; }
            await StreamNativeFileAsync(run, connId, deviceId, item, cfg, storeNative: true, ct);
        }
    }

    private async Task StreamNativeFileAsync(ActionRunState run, string connId, string deviceId, WorkItem item, Configuration cfg, bool storeNative, CancellationToken ct)
    {
        var nodeInfo = await _hub.Clients.Client(connId).InvokeAsync<FileNodeResultDto>("GetPathInfo", item.RelPath, false, ct);
        if (nodeInfo == null || !nodeInfo.Exists || nodeInfo.CurrentNode == null)
            throw new IOException("File not found");

        long total = nodeInfo.CurrentNode.Length;
        string? part = _cache.BeginStreamedFile(deviceId, item.RelPath, allowReplace: true);
        if (part == null) { run.SkippedFiles++; return; }

        try
        {
            long offset = 0;
            using var fs = File.Create(part);
            while (offset < total)
            {
                var chunk = await _hub.Clients.Client(connId).InvokeAsync<byte[]>("GetFileChunk", item.RelPath, offset, FileChunkSize, false, ct);
                if (chunk == null || chunk.Length == 0) break;
                await fs.WriteAsync(chunk, ct);
                run.AddBytes(chunk.Length);
                offset += chunk.Length;
            }
            await fs.FlushAsync(ct);
            if (offset < total) throw new IOException($"Incomplete transfer ({offset}/{total} bytes)");

            string mediaType = item.Kind == FileKind.Video ? "video" : item.Kind == FileKind.Audio ? "audio" : "file";

            if (storeNative)
            {
                _cache.CompleteFullFile(part, deviceId, item.RelPath, item.Name, offset, total, mediaType,
                    frame: -1f, quality: 1.0f);
            }
            else
            {
                _cache.CompleteFullFile(part, deviceId, item.RelPath, item.Name, offset, total, mediaType,
                    frame: -1f, quality: Math.Clamp(cfg.ThumbnailQuality, 0f, 1f),
                    bitrateMultiplier: cfg.VideoBitrateMultiplier,
                    resolutionMultiplier: cfg.VideoResolutionMultiplier,
                    bitrateCapBps: cfg.BitrateCappingEnabled ? (int)Math.Round(cfg.BitrateCapMbps * 1_000_000) : -1,
                    resolutionCeilingPx: cfg.ResolutionCeilingEnabled ? cfg.ResolutionCeilingPx : -1);
            }

            run.FetchedFiles++;
            run.AppendLog($"Cached file {item.RelPath} ({offset / 1024d / 1024d:F1} MB)");
        }
        catch
        {
            TryDelete(part);
            throw;
        }
    }

    private async Task TranscodeAndCacheAsync(ActionRunState run, string connId, string deviceId, WorkItem item, Configuration cfg, float brm, float resm, CancellationToken ct)
    {
        int capBps = (int)Math.Round(cfg.BitrateCapMbps * 1_000_000);
        int resCeilPx = cfg.ResolutionCeilingPx;

        var startResp = await _hub.Clients.Client(connId).InvokeCoreAsync<JsonElement>("StartVideoTranscode",
            new object?[] { item.RelPath, brm, resm, cfg.BitrateCappingEnabled, capBps, cfg.ResolutionCeilingEnabled, resCeilPx }, ct);

        string sessionId = "";
        string outputPath = "";
        if (startResp.ValueKind == JsonValueKind.Object)
        {
            sessionId = startResp.TryGetProperty("sessionId", out var s) ? s.GetString() ?? "" : "";
            outputPath = startResp.TryGetProperty("outputPath", out var o) ? o.GetString() ?? "" : "";
        }
        if (string.IsNullOrEmpty(sessionId)) throw new IOException("Phone refused to start a transcode");

        int progress = -1;
        using var pollCts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        using var linkedPoll = CancellationTokenSource.CreateLinkedTokenSource(ct, pollCts.Token);
        while (!linkedPoll.IsCancellationRequested && progress < 100)
        {
            var state = await _hub.Clients.Client(connId).InvokeCoreAsync<JsonElement>("GetTranscodeProgress", new object[] { sessionId }, linkedPoll.Token);
            if (state.ValueKind == JsonValueKind.Object)
            {
                progress = state.TryGetProperty("progress", out var p) ? p.GetInt32() : 0;
                if (state.TryGetProperty("outputPath", out var op) && outputPath.Length == 0) outputPath = op.GetString() ?? "";
                if (progress < 0) throw new IOException("Phone transcoder reported an error");
            }
            if (progress < 100) await Task.Delay(2000, linkedPoll.Token);
        }

        if (string.IsNullOrEmpty(outputPath))
            throw new IOException("Transcoder produced no output file");

        var nodeInfo = await _hub.Clients.Client(connId).InvokeAsync<FileNodeResultDto>("GetPathInfo", outputPath, false, ct);
        if (nodeInfo == null || !nodeInfo.Exists) throw new IOException($"Transcoded file missing: {outputPath}");
        long total = nodeInfo.CurrentNode?.Length ?? 0;

        string? part = _cache.BeginStreamedFile(deviceId, item.RelPath, allowReplace: true);
        if (part == null) { run.SkippedFiles++; return; }
        try
        {
            long offset = 0;
            using var fs = File.Create(part);
            while (offset < total)
            {
                var chunk = await _hub.Clients.Client(connId).InvokeAsync<byte[]>("GetFileChunk", outputPath, offset, FileChunkSize, false, ct);
                if (chunk == null || chunk.Length == 0) break;
                await fs.WriteAsync(chunk, ct);
                run.AddBytes(chunk.Length);
                offset += chunk.Length;
            }
            await fs.FlushAsync(ct);
            if (offset < total) throw new IOException("Incomplete transcode transfer");

            _cache.CompleteFullFile(part, deviceId, item.RelPath, item.Name, offset, total, "video",
                frame: -1f, quality: Math.Clamp(cfg.ThumbnailQuality, 0f, 1f),
                bitrateMultiplier: brm, resolutionMultiplier: resm,
                bitrateCapBps: cfg.BitrateCappingEnabled ? capBps : -1,
                resolutionCeilingPx: cfg.ResolutionCeilingEnabled ? resCeilPx : -1);

            run.FetchedFiles++;
            run.AppendLog($"Cached transcoded video {item.RelPath} ({offset / 1024d / 1024d:F1} MB)");
        }
        catch
        {
            TryDelete(part);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    // ── "Fetch the Files" cache-status / dedup helpers ───────────────────────

    /// <summary>
    /// true when every file in the action's list is already cached at an equal-or-higher
    /// quality than the requested settings (re-checked on every run, never memoized).
    /// </summary>
    private bool AllFilesAlreadyFetched(ActionEntry action, Configuration cfg, string deviceId)
    {
        if (action.Files.Count == 0) return false;
        foreach (var file in action.Files)
        {
            string relPath = file.Trim().TrimStart('/');
            if (string.IsNullOrEmpty(relPath)) continue;
            var kind = Classify(Path.GetFileName(relPath));
            switch (kind)
            {
                case FileKind.Image:
                    _cache.TryGetCachedThumbnail(deviceId, relPath, -1f, cfg.ThumbnailQuality, out _, out var imgMeta);
                    if (!CacheManager.IsThumbnailSuitable(imgMeta, -1f, cfg.ThumbnailQuality, false)) return false;
                    break;
                case FileKind.Video when string.Equals(action.VideoFetchType, "actual", StringComparison.OrdinalIgnoreCase):
                    if (!IsFullFileSuitable(cfg, _cache.GetFullFileMetadata(deviceId, relPath))) return false;
                    break;
                case FileKind.Video:
                    _cache.TryGetCachedThumbnail(deviceId, relPath, cfg.VideoFrameNormalized, cfg.ThumbnailQuality, out _, out var vidMeta);
                    if (!CacheManager.IsThumbnailSuitable(vidMeta, cfg.VideoFrameNormalized, cfg.ThumbnailQuality, true)) return false;
                    break;
                default:
                    if (!_cache.HasFullFile(deviceId, relPath)) return false;
                    break;
            }
        }
        return true;
    }

    /// <summary>
    /// Per-file fetch status against the current cache (used by the UI to grey out
    /// files already fetched at an equal-or-higher quality, and to show why).
    /// </summary>
    public List<FileFetchStatusDto> CheckFileStatus(ActionEntry action, string deviceId, IReadOnlyList<string> files)
    {
        Configuration cfg = ResolveConfig(action, deviceId);
        var results = new List<FileFetchStatusDto>();
        foreach (var file in files)
        {
            string relPath = file.Trim().TrimStart('/');
            if (string.IsNullOrEmpty(relPath)) continue;
            var kind = Classify(Path.GetFileName(relPath));
            results.Add(ComputeStatus(relPath, kind, action, cfg, deviceId));
        }
        return results;
    }

    private FileFetchStatusDto ComputeStatus(string relPath, FileKind kind, ActionEntry action, Configuration cfg, string deviceId)
    {
        string kindName = kind switch
        {
            FileKind.Image => "image",
            FileKind.Video => "video",
            FileKind.Audio => "audio",
            _ => "file"
        };

        switch (kind)
        {
            case FileKind.Image:
                _cache.TryGetCachedThumbnail(deviceId, relPath, -1f, cfg.ThumbnailQuality, out _, out var imgMeta);
                return ThumbStatus(relPath, kindName, imgMeta, isVideo: false, reqFrame: -1f, cfg.ThumbnailQuality);

            case FileKind.Video:
                if (string.Equals(action.VideoFetchType, "actual", StringComparison.OrdinalIgnoreCase))
                {
                    var meta = _cache.GetFullFileMetadata(deviceId, relPath);
                    if (!IsFullFileSuitable(cfg, meta))
                        return new FileFetchStatusDto(relPath, kindName, "fetch", "");
                    bool higher = IsCachedStrictlyHigher(meta!, cfg);
                    return new FileFetchStatusDto(relPath, kindName, higher ? "higher" : "same",
                        higher ? "Higher quality version already Fetched"
                               : "This file already fetched of equal quality settings");
                }
                _cache.TryGetCachedThumbnail(deviceId, relPath, cfg.VideoFrameNormalized, cfg.ThumbnailQuality, out _, out var vidMeta);
                return ThumbStatus(relPath, kindName, vidMeta, isVideo: true, cfg.VideoFrameNormalized, cfg.ThumbnailQuality);

            default:
                if (_cache.HasFullFile(deviceId, relPath))
                    return new FileFetchStatusDto(relPath, kindName, "same", "This file already fetched");
                return new FileFetchStatusDto(relPath, kindName, "fetch", "");
        }
    }

    private static FileFetchStatusDto ThumbStatus(string relPath, string kindName, CacheMetadata? meta, bool isVideo, float reqFrame, float reqQuality)
    {
        if (!CacheManager.IsThumbnailSuitable(meta, reqFrame, reqQuality, isVideo))
            return new FileFetchStatusDto(relPath, kindName, "fetch", "");
        if (meta!.Quality >= 0.999f)
            return new FileFetchStatusDto(relPath, kindName, "higher", "High Quality Version already Fetched");
        return new FileFetchStatusDto(relPath, kindName, "same",
            $"This file already fetched of same quality {reqQuality:0.00} settings");
    }

    private static bool IsFullFileSuitable(Configuration cfg, CacheMetadata? meta)
    {
        float brm = Math.Clamp(cfg.VideoBitrateMultiplier, 0.05f, 1f);
        float resm = Math.Clamp(cfg.VideoResolutionMultiplier, 0.05f, 1f);
        if (brm >= 0.999f && resm >= 0.999f)
            return CacheManager.IsFullFileSuitable(meta, 1f, 1f, 0, 0);
        int reqBrCap = cfg.BitrateCappingEnabled ? (int)Math.Round(cfg.BitrateCapMbps * 1_000_000) : 0;
        int reqCeilPx = cfg.ResolutionCeilingEnabled ? cfg.ResolutionCeilingPx : 0;
        return CacheManager.IsFullFileSuitable(meta, brm, resm, reqBrCap, reqCeilPx);
    }

    private static bool IsCachedStrictlyHigher(CacheMetadata meta, Configuration cfg)
    {
        float brm = Math.Clamp(cfg.VideoBitrateMultiplier, 0.05f, 1f);
        float resm = Math.Clamp(cfg.VideoResolutionMultiplier, 0.05f, 1f);
        int capBps = cfg.BitrateCappingEnabled ? (int)Math.Round(cfg.BitrateCapMbps * 1_000_000) : -1;
        int ceilPx = cfg.ResolutionCeilingEnabled ? cfg.ResolutionCeilingPx : -1;
        if (meta.BitrateMultiplier > brm + 0.001f) return true;
        if (meta.ResolutionMultiplier > resm + 0.001f) return true;
        if (capBps > 0 && meta.BitrateCapBps > capBps) return true;
        if (ceilPx > 0 && meta.ResolutionCeilingPx > ceilPx) return true;
        return false;
    }
}

/// <summary>Per-file cache status for "Fetch the Files" grey-out rendering.</summary>
public record FileFetchStatusDto(string Path, string Kind, string Status, string Note);