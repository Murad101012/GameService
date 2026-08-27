using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PhonesBridge.Services;

/// <summary>
/// Manages the Moondream 2 Python AI service as a subprocess.
/// Communicates via stdin/stdout JSON-line protocol.
/// </summary>
public class NsfwService : IDisposable
{
    private Process? _process;
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<NsfwResult>> _pendingQueries = new();
    private int _nextId;
    private bool _isLoaded;
    private NsfwVramInfo _lastVram = new();

    private readonly string _pythonPath;
    private readonly string _scriptPath;

    public bool IsLoaded => _isLoaded;
    public NsfwVramInfo LastVramInfo => _lastVram;

    public NsfwService()
    {
        // Determine Python path — prefer the project AI venv on Linux
        _pythonPath = ResolvePythonPath();

        // Script is in the same directory as this source file
        var serviceDir = Path.Combine(AppContext.BaseDirectory);
        // During development, the script is in the project's Services folder
        var devScriptPath = Path.Combine(Directory.GetCurrentDirectory(), "Services", "nsfw_service.py");
        if (File.Exists(devScriptPath))
        {
            _scriptPath = devScriptPath;
        }
        else
        {
            _scriptPath = Path.Combine(serviceDir, "nsfw_service.py");
        }

        Console.WriteLine($"[NSFW] Python: {_pythonPath}, Script: {_scriptPath}");
    }

    /// <summary>
    /// Resolve the Python interpreter to run this service. Prefers the project
    /// AI venv (Linux) so it picks up torch/transformers; falls back to the
    /// system python3 (Linux) or python (Windows).
    /// </summary>
    private static string ResolvePythonPath()
    {
        foreach (var dir in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var venvPy = Path.Combine(dir, "ai-venv", "bin", "python3");
            if (File.Exists(venvPy))
                return venvPy;
        }
        return File.Exists("/usr/bin/python3") ? "python3" : "python";
    }

    /// <summary>
    /// Start the Python subprocess and load the model.
    /// </summary>
    public async Task<NsfwStatus> StartAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_process != null && !_process.HasExited)
            {
                return new NsfwStatus { Success = true, Message = "Already running", Vram = _lastVram };
            }
        }

        if (!File.Exists(_scriptPath))
        {
            return new NsfwStatus { Success = false, Message = $"Script not found: {_scriptPath}" };
        }

        var psi = new ProcessStartInfo
        {
            FileName = _pythonPath,
            Arguments = $"\"{_scriptPath}\"",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(_scriptPath) ?? ""
        };

        // Set environment for Python
        psi.Environment["PYTHONUNBUFFERED"] = "1";
        psi.Environment["PYTHONIOENCODING"] = "utf-8";

        _process = new Process { StartInfo = psi };

        lock (_lock)
        {
            _process.Start();
        }

        // Start reading stdout in background
        _ = Task.Run(() => ReadStdoutLoop(_process, ct), ct);
        _ = Task.Run(() => ReadStderrLoop(_process, ct), ct);

        // Send load command
        await SendCommandAsync(new { action = "load" }, ct);

        // Wait for load confirmation (with timeout)
        var tcs = new TaskCompletionSource<NsfwStatus>();
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        // We'll poll _isLoaded
        var startTime = DateTime.UtcNow;
        while (!_isLoaded && (DateTime.UtcNow - startTime).TotalMinutes < 5)
        {
            if (ct.IsCancellationRequested || timeoutCts.IsCancellationRequested)
                break;
            await Task.Delay(500, ct);
        }

        if (_isLoaded)
        {
            return new NsfwStatus { Success = true, Message = "Model loaded", Vram = _lastVram };
        }

        return new NsfwStatus { Success = false, Message = "Model load timed out" };
    }

    /// <summary>
    /// Stop the Python subprocess (unloads model from VRAM).
    /// </summary>
    public async Task<NsfwStatus> StopAsync(CancellationToken ct = default)
    {
        try
        {
            await SendCommandAsync(new { action = "unload" }, ct);
            await Task.Delay(500, ct);
        }
        catch { }

        lock (_lock)
        {
            if (_process != null && !_process.HasExited)
            {
                try
                {
                    SendCommandAsync(new { action = "shutdown" }, CancellationToken.None).Wait(2000);
                }
                catch { }

                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
                _process.Dispose();
                _process = null;
            }
            _isLoaded = false;
        }

        // Cancel all pending queries
        foreach (var kvp in _pendingQueries)
        {
            kvp.Value.TrySetCanceled();
        }
        _pendingQueries.Clear();

        return new NsfwStatus { Success = true, Message = "Stopped", Vram = new NsfwVramInfo() };
    }

    /// <summary>
    /// Query an image file. Returns YES/NO answer.
    /// </summary>
    public async Task<NsfwResult> QueryImageAsync(string imagePath, string prompt, CancellationToken ct = default)
    {
        if (!_isLoaded)
            return new NsfwResult { Success = false, Error = "Model not loaded" };

        int id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<NsfwResult>();
        _pendingQueries[id] = tcs;

        try
        {
            await SendCommandAsync(new
            {
                action = "query",
                id,
                path = imagePath,
                prompt
            }, ct);

            // Wait for result with timeout
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            linkedCts.Token.Register(() => tcs.TrySetCanceled());

            return await tcs.Task;
        }
        finally
        {
            _pendingQueries.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Query a base64-encoded image.
    /// </summary>
    public async Task<NsfwResult> QueryImageBase64Async(string imageBase64, string prompt, string mode = "yesno", CancellationToken ct = default)
    {
        if (!_isLoaded)
            return new NsfwResult { Success = false, Error = "Model not loaded" };

        int id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<NsfwResult>();
        _pendingQueries[id] = tcs;

        try
        {
            await SendCommandAsync(new
            {
                action = "query",
                id,
                image_base64 = imageBase64,
                prompt,
                mode
            }, ct);

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            linkedCts.Token.Register(() => tcs.TrySetCanceled());

            return await tcs.Task;
        }
        finally
        {
            _pendingQueries.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Get current VRAM usage.
    /// </summary>
    public async Task<NsfwVramInfo> GetVramInfoAsync(CancellationToken ct = default)
    {
        if (_process == null || _process.HasExited)
            return new NsfwVramInfo();

        try
        {
            await SendCommandAsync(new { action = "vram" }, ct);
            await Task.Delay(200, ct);
            return _lastVram;
        }
        catch
        {
            return _lastVram;
        }
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private async Task SendCommandAsync(object command, CancellationToken ct)
    {
        Process? proc;
        lock (_lock) { proc = _process; }

        if (proc == null || proc.HasExited)
            throw new InvalidOperationException("Python process not running");

        string json = JsonSerializer.Serialize(command);
        await proc.StandardInput.WriteLineAsync(json.AsMemory(), ct);
        await proc.StandardInput.FlushAsync(ct);
    }

    private async Task ReadStdoutLoop(Process proc, CancellationToken ct)
    {
        try
        {
            while (!proc.HasExited && !ct.IsCancellationRequested)
            {
                string? line = await proc.StandardOutput.ReadLineAsync(ct);
                if (line == null) break;

                line = line.Trim();
                if (string.IsNullOrEmpty(line)) continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;

                    string? type = root.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;

                    switch (type)
                    {
                        case "status":
                            string? msg = root.TryGetProperty("message", out var msgProp) ? msgProp.GetString() : "";
                            Console.WriteLine($"[NSFW] Status: {msg}");

                            if (msg?.Contains("Model loaded") == true || msg?.Contains("already loaded") == true)
                            {
                                _isLoaded = true;
                                if (root.TryGetProperty("vram", out var vramProp))
                                {
                                    _lastVram = JsonSerializer.Deserialize<NsfwVramInfo>(vramProp.GetRawText()) ?? new();
                                }
                            }
                            else if (msg?.Contains("Model unloaded") == true)
                            {
                                _isLoaded = false;
                                _lastVram = new NsfwVramInfo();
                            }
                            break;

                        case "result":
                            int id = root.TryGetProperty("id", out var idProp) ? idProp.GetInt32() : -1;
                            string answer = root.TryGetProperty("answer", out var ansProp) ? ansProp.GetString() ?? "NO" : "NO";
                            string raw = root.TryGetProperty("raw", out var rawProp) ? rawProp.GetString() ?? "" : "";

                            var result = new NsfwResult
                            {
                                Success = true,
                                Id = id,
                                Answer = answer,
                                Raw = raw
                            };

                            if (_pendingQueries.TryRemove(id, out var tcs))
                            {
                                tcs.TrySetResult(result);
                            }
                            break;

                        case "vram":
                            _lastVram = JsonSerializer.Deserialize<NsfwVramInfo>(root.GetRawText()) ?? new();
                            break;

                        case "error":
                            int errId = root.TryGetProperty("id", out var eidProp) ? eidProp.GetInt32() : -1;
                            string errMsg = root.TryGetProperty("message", out var emProp) ? emProp.GetString() ?? "Unknown error" : "Unknown error";
                            Console.WriteLine($"[NSFW] Error (id={errId}): {errMsg}");

                            if (errId >= 0 && _pendingQueries.TryRemove(errId, out var errTcs))
                            {
                                errTcs.TrySetResult(new NsfwResult { Success = false, Id = errId, Error = errMsg });
                            }
                            break;
                    }
                }
                catch (JsonException ex)
                {
                    Console.WriteLine($"[NSFW] JSON parse error: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"[NSFW] Stdout read error: {ex.Message}");
        }
    }

    private async Task ReadStderrLoop(Process proc, CancellationToken ct)
    {
        try
        {
            while (!proc.HasExited && !ct.IsCancellationRequested)
            {
                string? line = await proc.StandardError.ReadLineAsync(ct);
                if (line == null) break;
                Console.WriteLine($"[moondream-py] {line}");
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"[NSFW] Stderr read error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        try { StopAsync().Wait(3000); } catch { }
        _process?.Dispose();
    }
}

// ── Data types ───────────────────────────────────────────────────────────────

public class NsfwStatus
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public NsfwVramInfo Vram { get; set; } = new();
}

public class NsfwResult
{
    public bool Success { get; set; }
    public int Id { get; set; }
    public string Answer { get; set; } = "NO";
    public string Raw { get; set; } = "";
    public string Error { get; set; } = "";
}

public class NsfwVramInfo
{
    [JsonPropertyName("used_mb")]
    public double UsedMb { get; set; }
    [JsonPropertyName("total_mb")]
    public double TotalMb { get; set; }
    [JsonPropertyName("percent")]
    public double Percent { get; set; }
}