using System.Diagnostics;
using System.Text.Json;

namespace PhonesBridge.Services;

/// <summary>
/// Drives a Dolphin-style persistent KDE progress notification for a running
/// fetch action: a real KDE progress bar (via the org.kde.JobViewV3 DBus
/// protocol, what KIO/Dolphin actually uses) that shows the live transfer speed
/// (per second) plus the Discovered/Fetched/Skipped/Failed counters, a 0→100%
/// progress bar, refreshes per second, and stays resident in the KDE tray.
/// On finish it is terminated and a short-lived result popup is shown.
///
/// Linux only (spawns jobview.py against the session bus). Other platforms are
/// no-ops (failures are logged, never thrown).
/// </summary>
public sealed class RunNotifier
{
    private readonly string _actionName;
    private readonly object _lock = new();
    private Process? _proc;
    private StreamWriter? _writer;
    private bool _started;
    private bool _finished;
    private long _lastBytes;
    private long _lastTick;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public RunNotifier(string actionName)
    {
        _actionName = actionName;
    }

    public void Start()
    {
        try
        {
            if (!OperatingSystem.IsLinux())
            {
                _ready.TrySetResult();
                return;
            }

            string script = Path.Combine(Directory.GetCurrentDirectory(), "jobview.py");
            if (!File.Exists(script))
            {
                script = Path.Combine(AppContext.BaseDirectory, "jobview.py");
            }

            var psi = new ProcessStartInfo("python3")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add(Path.GetFullPath(script));
            psi.ArgumentList.Add("PhonesBridge");
            psi.ArgumentList.Add(_actionName);
            TrayHelper.ApplyLinuxSessionEnv(psi);

            _proc = Process.Start(psi);
            if (_proc == null)
            {
                _ready.TrySetResult();
                return;
            }
            _proc.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data)) Console.WriteLine($"[jobview] {e.Data}");
            };
            _proc.BeginErrorReadLine();
            _writer = _proc.StandardInput;
            _writer.AutoFlush = true;

            _lastBytes = 0;
            _lastTick = Environment.TickCount64;
            _started = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RunNotifier] Start failed: {ex.Message}");
        }
        finally
        {
            _ready.TrySetResult();
        }
    }

    /// <summary>Refresh the persistent KDE progress job with current counters and speed.</summary>
    public void Update(ActionRunState run)
    {
        lock (_lock)
        {
            try
            {
                if (!OperatingSystem.IsLinux() || !_started || _finished || _writer == null) return;

                double progress = run.DiscoveredFiles > 0
                    ? Math.Clamp((double)(run.FetchedFiles + run.FailedFiles + run.SkippedFiles) / run.DiscoveredFiles, 0, 1)
                    : 0;
                long bytes = run.TotalBytes;
                long now = Environment.TickCount64;
                double seconds = (now - _lastTick) / 1000.0;
                double mbps = 0;
                if (seconds > 0)
                {
                    mbps = Math.Max(0, (bytes - _lastBytes) / (double)(1024 * 1024) / seconds);
                }
                _lastBytes = bytes;
                _lastTick = now;

                string info = run.DiscoveredFiles > 0
                    ? $"Discovered: {run.DiscoveredFiles} · Fetched: {run.FetchedFiles} · Skipped: {run.SkippedFiles} · Failed: {run.FailedFiles}"
                    : "Fetching…";

                var msg = new
                {
                    title = $"{_actionName} — {FormatSpeed(mbps)}",
                    info,
                    percent = (int)(progress * 100),
                    speed = (long)Math.Max(0, bytes),
                    l1 = "Progress",
                    v1 = $"{progress * 100:F0}%",
                    l2 = "Speed",
                    v2 = FormatSpeed(mbps)
                };
                _writer.WriteLine(JsonSerializer.Serialize(msg, _jsonOptions));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RunNotifier] Update failed: {ex.Message}");
            }
        }
    }

    /// <summary>Terminate the progress job and show a short-lived result popup.</summary>
    public void Finish(ActionRunState run)
    {
        _ready.Task.Wait(TimeSpan.FromSeconds(4));
        lock (_lock)
        {
            try
            {
                if (OperatingSystem.IsLinux() && _started && !_finished && _writer != null)
                {
                    string status = string.Equals(run.Status, "Done", StringComparison.OrdinalIgnoreCase) ? "Finished" : run.Status ?? "Finished";
                    var msg = new { terminate = true, error = 0, message = "" };
                    if (string.Equals(run.Status, "Failed", StringComparison.OrdinalIgnoreCase))
                        msg = new { terminate = true, error = 1, message = run.Message ?? status };
                    _writer.WriteLine(JsonSerializer.Serialize(msg, _jsonOptions));
                    _writer.Close();
                    _finished = true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RunNotifier] Finish failed: {ex.Message}");
            }
        }

        // Short-lived result popup via notify-send.
        DesktopNotifier.Notify(StatusTitle(run), run.Message ?? StatusTitle(run));
    }

    private string StatusTitle(ActionRunState run)
    {
        if (string.Equals(run.Status, "Done", StringComparison.OrdinalIgnoreCase))
            return _actionName;
        return run.Status ?? _actionName;
    }

    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private static string FormatSpeed(double mbps)
    {
        if (mbps <= 0) return "0 B/s";
        if (mbps >= 1) return $"{mbps:F1} MB/s";
        return $"{(int)(mbps * 1024)} KB/s";
    }
}
