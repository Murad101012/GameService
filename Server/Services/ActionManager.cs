using System.Collections.Concurrent;
using System.Text.Json;
using PhonesBridge.Services.Definitions;

namespace PhonesBridge.Services;

/// <summary>Persists action definitions and tracks background action runs.</summary>
public class ActionManager
{
    private static readonly string ActionsDir = Path.Combine("Services", "DataContainers", "Actions");
    private static readonly string ActionsPath = Path.Combine(ActionsDir, "actions.json");
    private static readonly string RunsPath = Path.Combine(ActionsDir, "runs.json");

    /// <summary>Upper bound of run records kept in the persisted history file.</summary>
    private const int MaxPersistedRuns = 100;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    private readonly ConcurrentDictionary<string, ActionEntry> _actions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ActionRunState> _runs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ActionRunner _runner;
    private readonly object _runsSaveLock = new();

    /// <summary>Last serialized snapshot written to disk — skipped when unchanged.</summary>
    private string _lastSavedRuns = "";

    public ActionManager(ActionRunner runner)
    {
        _runner = runner;
        Directory.CreateDirectory(ActionsDir);
        Load();
        LoadRuns();
        // Persist run progress (counters + log) that mutates outside ActionManager
        // while a run is active. The file is only rewritten when the snapshot changes.
        _ = Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(3));
                PersistRunsIfChanged();
            }
        });
    }

    /// <summary>Restores previously persisted run history after a restart.</summary>
    private void LoadRuns()
    {
        if (!File.Exists(RunsPath)) return;
        try
        {
            var list = JsonSerializer.Deserialize<List<ActionRunState>>(File.ReadAllText(RunsPath), JsonOptions);
            if (list == null) return;
            foreach (var run in list)
            {
                if (string.IsNullOrWhiteSpace(run.RunId)) continue;
                // A run left in "Running" state was cut short by the restart.
                if (string.Equals(run.Status, "Running", StringComparison.OrdinalIgnoreCase))
                {
                    run.Status = "Cancelled";
                    run.EndedUtc = DateTime.UtcNow;
                    run.AppendLog("Server restarted — run interrupted.");
                }
                _runs[run.RunId] = run;
            }
            _lastSavedRuns = SerializeRuns();
        }
        catch (Exception ex)
        {
            /* Console.WriteLine($"[Action] Failed to load runs.json: {ex.Message}"); */
        }
    }

    private string SerializeRuns() =>
        JsonSerializer.Serialize(_runs.Values.OrderByDescending(r => r.StartedUtc).Take(MaxPersistedRuns).ToList(), JsonOptions);

    /// <summary>Immediately writes the run history snapshot to disk.</summary>
    public void SaveRuns()
    {
        try
        {
            lock (_runsSaveLock)
            {
                _lastSavedRuns = SerializeRuns();
                File.WriteAllText(RunsPath, _lastSavedRuns);
            }
        }
        catch (Exception ex)
        {
            /* Console.WriteLine($"[Action] Failed to save runs.json: {ex.Message}"); */
        }
    }

    private void PersistRunsIfChanged()
    {
        if (_runs.IsEmpty) return;
        try
        {
            lock (_runsSaveLock)
            {
                string json = SerializeRuns();
                if (json == _lastSavedRuns) return;
                _lastSavedRuns = json;
                File.WriteAllText(RunsPath, json);
            }
        }
        catch (Exception ex)
        {
            /* Console.WriteLine($"[Action] Failed to save runs.json: {ex.Message}"); */
        }
    }

    private void Load()
    {
        if (!File.Exists(ActionsPath)) return;
        try
        {
            var list = JsonSerializer.Deserialize<List<ActionEntry>>(File.ReadAllText(ActionsPath), JsonOptions);
            if (list == null) return;
            _actions.Clear();
            foreach (var a in list)
            {
                if (!string.IsNullOrWhiteSpace(a.Id))
                {
                    if (!string.IsNullOrEmpty(a.DeviceId)
                        && string.Equals(a.DeviceTarget, "none", StringComparison.OrdinalIgnoreCase))
                    {
                        a.DeviceTarget = "device";
                    }
                    _actions[a.Id] = a;
                }
            }
        }
        catch (Exception ex)
        {
            /* Console.WriteLine($"[Action] Failed to load actions.json: {ex.Message}"); */
        }
    }

    private void Save()
    {
        try
        {
            string json = JsonSerializer.Serialize(_actions.Values.ToList(), JsonOptions);
            File.WriteAllText(ActionsPath, json);
        }
        catch (Exception ex)
        {
            /* Console.WriteLine($"[Action] Failed to save actions.json: {ex.Message}"); */
        }
    }

    public List<ActionEntry> GetAll() => _actions.Values.OrderBy(a => a.Name).ToList();

    public ActionEntry? Get(string id) => _actions.TryGetValue(id, out var a) ? a : null;

    public ActionEntry Upsert(ActionEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Id)) entry.Id = Guid.NewGuid().ToString();
        if (string.IsNullOrWhiteSpace(entry.Name))
        {
            entry.Name = entry.Type == "fetch" ? "Fetch the folder"
                : entry.Type == "fetchFiles" ? "Fetch the Files"
                : entry.Type;
        }
        // Legacy actions (created before DeviceTarget) had a bound DeviceId but no
        // target declared — normalise them to a single-device target so the wizard
        // and cards render the correct control.
        if (!string.IsNullOrEmpty(entry.DeviceId)
            && string.Equals(entry.DeviceTarget, "none", StringComparison.OrdinalIgnoreCase))
        {
            entry.DeviceTarget = "device";
        }
        _actions[entry.Id] = entry;
        Save();
        return entry;
    }

    public bool Delete(string id) { bool ok = _actions.TryRemove(id, out _); if (ok) Save(); return ok; }

    /// <summary>
    /// Appends storage-relative file paths to an existing action, skipping duplicates
    /// (same path, case-insensitive) and non-"fetchFiles" actions. Returns the updated
    /// action plus the number of files added/skipped.
    /// </summary>
    public (ActionEntry? Action, int Added, int Skipped) AddFiles(string id, IEnumerable<string> files)
    {
        var entry = Get(id);
        if (entry == null) return (null, 0, 0);
        if (!string.Equals(entry.Type, "fetchFiles", StringComparison.OrdinalIgnoreCase))
            return (entry, 0, files.Count());

        var existing = new HashSet<string>(entry.Files, StringComparer.OrdinalIgnoreCase);
        int added = 0, skipped = 0;
        foreach (var f in files)
        {
            string relPath = (f ?? "").Trim().TrimStart('/');
            if (string.IsNullOrEmpty(relPath)) continue;
            if (existing.Add(relPath)) { entry.Files.Add(relPath); added++; }
            else skipped++;
        }
        if (added > 0) Save();
        return (entry, added, skipped);
    }

    public ActionEntry DisableEnable(string id, bool enabled)
    {
        var entry = Get(id);
        if (entry == null) return null!;
        entry.Enabled = enabled;
        Save();
        return entry;
    }

    public List<ActionRunState> GetRecentRuns(int max = 20) =>
        _runs.Values.OrderByDescending(r => r.StartedUtc).Take(max).ToList();

    public void ClearRuns()
    {
        foreach (var key in _runs.Keys)
        {
            _runs.TryRemove(key, out _);
        }
        SaveRuns();
    }

    public ActionRunState? GetRun(string runId) => _runs.TryGetValue(runId, out var r) ? r : null;

    /// <summary>Starts a manual run against a connected device.</summary>
    public ActionRunState? Run(ActionEntry action, DeviceIdentity device)
    {
        if (!action.Enabled) return null;
        return BeginRun(action, device, manual: true);
    }

    /// <summary>Starts all enabled "onconnect" actions for a newly-connected device.</summary>
    public void RunOnConnect(DeviceIdentity device)
    {
        foreach (var action in _actions.Values.Where(a => a.Enabled && a.Trigger == "onconnect"))
        {
            // Actions bound to a specific device only ever run against that device.
            if (!string.IsNullOrEmpty(action.DeviceId)
                && !string.Equals(action.DeviceId, device.DeviceId, StringComparison.OrdinalIgnoreCase))
                continue;
            BeginRun(action, device, manual: false);
        }
    }

    private ActionRunState? BeginRun(ActionEntry action, DeviceIdentity device, bool manual)
    {
        if (!string.IsNullOrEmpty(action.DeviceId)
            && !string.Equals(action.DeviceId, device.DeviceId, StringComparison.OrdinalIgnoreCase))
            return null;

        // A single "onconnect" trigger firing repeatedly while a device reconnects
        // must not pile up identical runs — reuse an unfinished run for the same pair.
        var existing = _runs.Values.FirstOrDefault(r =>
            r.ActionId == action.Id &&
            r.DeviceName.Equals(device.DeviceName, StringComparison.OrdinalIgnoreCase) &&
            !r.IsFinished);
        if (existing != null) return existing;

        var run = new ActionRunState
        {
            RunId = Guid.NewGuid().ToString("N")[..12],
            ActionId = action.Id,
            ActionName = action.Name,
            DeviceName = device.DeviceName,
            Status = "Running",
            StartedUtc = DateTime.UtcNow
        };
        _runs[run.RunId] = run;
        run.AppendLog(manual
            ? $"Run {run.RunId} started for '{action.Name}' on {device.DeviceName}"
            : $"Auto-run {run.RunId} of '{action.Name}' for newly-connected {device.DeviceName}");
        SaveRuns();

        _ = _runner.RunAsync(action, device, run);
        return run;
    }
}

public class ActionRunState
{
    public string RunId { get; set; } = "";
    public string ActionId { get; set; } = "";
    public string ActionName { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string Status { get; set; } = "Running"; // Running | Done | Failed | Cancelled
    public DateTime StartedUtc { get; set; }
    public DateTime? EndedUtc { get; set; }
    public int DiscoveredFiles { get; set; }
    public int SkippedFiles { get; set; }
    public int FetchedFiles { get; set; }
    public int FailedFiles { get; set; }
    private long _totalBytes;
    /// <summary>Bytes downloaded so far (chunks written). Used for transfer speed.</summary>
    public long TotalBytes => Interlocked.Read(ref _totalBytes);
    public void AddBytes(long n)
    {
        if (n > 0) Interlocked.Add(ref _totalBytes, n);
    }
    public string CurrentPath { get; set; } = "";
    public string Message { get; set; } = "";
    public bool IsFinished => Status != "Running";

    // ── Dynamic core split (displayed in KDE notification) ───────────────
    public int AllowedCores { get; set; }
    public int DiscoverCores { get; set; }
    public int ProceedCores { get; set; }
    public int QueueSize; // field for Interlocked

    private readonly List<ActionLogLine> _log = new();
    private readonly object _logLock = new();

    public void AppendLog(string message)
    {
        lock (_logLock)
        {
            _log.Add(new ActionLogLine { TimeUtc = DateTime.UtcNow, Message = message });
            if (_log.Count > 500) _log.RemoveRange(0, _log.Count - 500);
        }
        /* Console.WriteLine($"[Action] {message}"); */
    }

    public List<ActionLogLine> GetLog()
    {
        lock (_logLock) return _log.ToList();
    }

    /// <summary>Exposed for JSON serialization of run history.</summary>
    public List<ActionLogLine> Log => GetLog();
}

public class ActionLogLine
{
    public DateTime TimeUtc { get; set; }
    public string Message { get; set; } = "";
}