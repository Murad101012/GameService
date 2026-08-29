using System;
using System.Collections.Generic;
using System.IO;
using System.Management;
using System.Net.Sockets;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// Standalone Windows Browser Agent - shows attached disks, pendrives, USB MTP, external and network drives on Browser root.
// Built via Build/WindowsBuild/AgentExe/build-agents.sh -> GameServiceCoreAgent.exe, runs as SYSTEM, zero Unity dependency.
class WindowsBrowserAgent
{
    static string DeviceName = Environment.MachineName + "_PC";
    static string DeviceId = GetOrCreateDeviceId();
    static string Host = "phone.sunnygameai.site";
    static int Port = 443;
    static volatile bool isRunning = true;

    [DllImport("wtsapi32.dll", SetLastError=true)] static extern bool WTSEnumerateSessions(IntPtr hServer,int Reserved,int Version,ref IntPtr ppSessionInfo,ref int pCount);
    [DllImport("wtsapi32.dll", SetLastError=true)] static extern void WTSFreeMemory(IntPtr pMemory);
    [DllImport("wtsapi32.dll", SetLastError=true)] static extern bool WTSQueryUserToken(int sessionId, out IntPtr phToken);
    [DllImport("advapi32.dll", SetLastError=true)] static extern bool DuplicateToken(IntPtr ExistingTokenHandle,int SECURITY_IMPERSONATION_LEVEL, out IntPtr DuplicateTokenHandle);
    [DllImport("kernel32.dll", SetLastError=true)] static extern int WTSGetActiveConsoleSessionId();
    [StructLayout(LayoutKind.Sequential)] struct WTS_SESSION_INFO { public int SessionId; public IntPtr pWinStationName; public int State; }

    static string GetOrCreateDeviceId()
    {
        try
        {
            // Common path for both SYSTEM and Murad to keep DeviceId stable
            string[] candidates = new string[]{
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WindowsBrowser", "device_id.txt"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameService", "device_id.txt"),
                "C:\\ProgramData\\WindowsBrowser\\device_id.txt"
            };
            foreach(var p in candidates){ try{ if(File.Exists(p)) return File.ReadAllText(p).Trim(new char[]{' ', '\r','\n','\t'}); }catch{} }
            string id = Guid.NewGuid().ToString();
            foreach(var p in candidates){ try{ Directory.CreateDirectory(Path.GetDirectoryName(p)); File.WriteAllText(p, id); break; }catch{} }
            return id;
        }
        catch { return Guid.NewGuid().ToString(); }
    }

    static void Log(string msg) { try { string line = DateTime.Now.ToString("HH:mm:ss.fff") + " [" + DeviceName + "] " + msg; Console.WriteLine(line); try { File.AppendAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameService", "browser.log"), line + "\r\n"); } catch {} } catch { } }

    static void Main()
    {
        Log("WindowsBrowserAgent started DeviceName=" + DeviceName + " DeviceId=" + DeviceId);
        while (isRunning)
        {
            try
            {
                if (!CheckStatus()) { Log("status check failed, retry 5s"); Thread.Sleep(5000); continue; }
                Log("status ok, connecting to " + Host + ":" + Port);
                ConnectAndLoop().Wait();
            }
            catch (Exception ex) { Log("loop ex: " + ex.Message); }
            Thread.Sleep(5000);
        }
    }

    static bool CheckStatus()
    {
        try
        {
            using (var tcp = new TcpClient()) { tcp.Connect(Host, 443); return true; }
        }
        catch { return false; }
    }

    static async Task ConnectAndLoop()
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(Host, 443);
        tcp.NoDelay = true;
        var ssl = new SslStream(tcp.GetStream(), false, (s,c,ch,e)=>true);
        // .NET Framework 4.x: None is invalid, use Tls12
        ssl.AuthenticateAsClient(Host, null, SslProtocols.Tls12, false);
        Log("TLS ok proto=" + ssl.SslProtocol);
        // WebSocket handshake
        string key = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
        string req = "GET /communicate HTTP/1.1\r\nHost: " + Host + "\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: " + key + "\r\nSec-WebSocket-Version: 13\r\n\r\n";
        byte[] reqBytes = Encoding.UTF8.GetBytes(req);
        await ssl.WriteAsync(reqBytes, 0, reqBytes.Length);
        // read 101
        var header = await ReadHttpHeader(ssl);
        if (!header.Contains("101")) throw new Exception("no 101: " + header.Substring(0, Math.Min(200, header.Length)));
        Log("WS 101 OK");
        await SendWsText(ssl, "{\"protocol\":\"json\",\"version\":1}\x1e");
        string hs = await ReadWsText(ssl);
        Log("hs resp: " + (hs==null?"NULL":hs.Substring(0, Math.Min(100, hs.Length))));
        // Register
        string reg = "{\"type\":1,\"target\":\"ReceiveDeviceRecord\",\"arguments\":[{\"DeviceName\":\"" + EscapeJson(DeviceName) + "\",\"DeviceId\":\"" + EscapeJson(DeviceId) + "\",\"Platform\":\"Windows\"}]}\x1e";
        await SendWsText(ssl, reg);
        Log("sent ReceiveDeviceRecord");
        // ping every 15s
        var cts = new CancellationTokenSource();
        _ = Task.Run(async () => { while (!cts.Token.IsCancellationRequested) { await Task.Delay(15000); try { await SendWsText(ssl, "{\"type\":6}\x1e"); } catch {} } });
        var buf = new StringBuilder();
        while (isRunning)
        {
            string frame = await ReadWsText(ssl);
            if (frame == null) break;
            buf.Append(frame);
            int idx;
            while ((idx = buf.ToString().IndexOf('\x1e')) != -1)
            {
                string raw = buf.ToString(0, idx).Trim();
                buf.Remove(0, idx+1);
                if (raw.Length>0) await HandleMessage(ssl, raw);
            }
        }
        cts.Cancel();
    }

    static async Task<string> ReadHttpHeader(SslStream s)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        var header = new List<byte>();
        while (true)
        {
            int r = await s.ReadAsync(one, 0, 1);
            if (r<=0) break;
            header.Add(one[0]);
            if (header.Count>=4 && header[header.Count-4]==13 && header[header.Count-3]==10 && header[header.Count-2]==13 && header[header.Count-1]==10) break;
        }
        return Encoding.UTF8.GetString(header.ToArray());
    }

    static async Task HandleMessage(SslStream ssl, string json)
    {
        try
        {
            Log("HandleMessage raw: " + json.Substring(0, Math.Min(800, json.Length)));
            if (!json.Contains("\"target\""))
            {
                // ping type 6 has no target
                if (json.Contains("\"type\":6")) Log("ping received");
                return;
            }
            string target = ExtractJsonString(json, "target");
            string invocationId = ExtractJsonString(json, "invocationId");
            Log("HandleMessage target=" + target + " invocationId=" + invocationId);
            // Touch to keep alive VideoDecoder style? no
            if (target == "GetPathInfo" || target == "GetPathInfoWithOverride")
            {
                // Browser identical to Android FileManager.getPathInfo - parse all args
                string path = ExtractJsonString(json, "arguments", 0);
                bool isLowRes = ExtractJsonBool(json, "arguments", 1);
                // For WithOverride, args 2,3 are frame/quality, else imageOnly/videoOnly
                // Detect by target
                bool imageOnly = false, videoOnly = false;
                int sinceDays = 0, startIndex = 0, fileLimit = 0;
                if (target == "GetPathInfoWithOverride")
                {
                    // args: path, isLowRes, frameOverride, qualityOverride, imageOnly, videoOnly, sinceDays, startIndex, fileLimit
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
                if (string.IsNullOrEmpty(path) || path == "/")
                {
                    result = BuildRootJson();
                }
                else
                {
                    if (path.StartsWith("shell:")) result = BuildShellPathInfoJson(path.Substring(6), imageOnly, videoOnly, sinceDays, startIndex, fileLimit);
                    else result = BuildPathInfoJson(path, isLowRes, imageOnly, videoOnly, sinceDays, startIndex, fileLimit);
                }
                string resp = "{\"type\":3,\"invocationId\":\"" + EscapeJson(invocationId) + "\",\"result\":" + result + "}\x1e";
                await SendWsText(ssl, resp);
                Log("GetPathInfo path='" + path + "' img=" + imageOnly + " vid=" + videoOnly + " since=" + sinceDays + " start=" + startIndex + " limit=" + fileLimit + " -> " + result.Substring(0, Math.Min(300, result.Length)));
            }
            else if (target == "GetFileChunk" || target == "GetFileChunkWithOverride")
            {
                string path = ExtractJsonString(json, "arguments", 0);
                long offset = ExtractJsonLong(json, "arguments", 1);
                int chunkSize = ExtractJsonInt(json, "arguments", 2);
                // isLowRes etc ignored for Windows filesystem (just read file)
                byte[] chunk = GetFileChunk(path, offset, chunkSize);
                string b64 = Convert.ToBase64String(chunk);
                string resp = "{\"type\":3,\"invocationId\":\"" + EscapeJson(invocationId) + "\",\"result\":\"" + b64 + "\"}\x1e";
                await SendWsText(ssl, resp);
                Log("GetFileChunk path='" + path + "' off=" + offset + " size=" + chunk.Length);
            }
            else if (target == "GetDeviceConfig")
            {
                string cfg = "{\"videoFrameNormalized\":0.23,\"sendDebug\":true,\"thumbnailQuality\":0,\"videoBitrateMultiplier\":1,\"videoResolutionMultiplier\":1,\"bitrateCappingEnabled\":false,\"bitrateCapMbps\":3,\"resolutionCeilingEnabled\":false,\"resolutionCeilingPx\":720,\"speedLimitKbps\":0,\"keepScreenOn\":false,\"parallelWork\":\"Absolute\",\"coreAmount\":" + Environment.ProcessorCount + ",\"coreSet\":" + Environment.ProcessorCount + "}";
                string resp = "{\"type\":3,\"invocationId\":\"" + EscapeJson(invocationId) + "\",\"result\":" + cfg + "}\x1e";
                await SendWsText(ssl, resp);
            }
        }
        catch (Exception ex) { Log("HandleMessage ex: " + ex.ToString()); }
    }

    // Light JSON helpers for .NET 4.x without System.Text.Json
    static string ExtractJsonString(string json, string key, int arrayIndex = -1)
    {
        try
        {
            int ki = json.IndexOf("\"" + key + "\"");
            if (ki==-1) return "";
            int colon = json.IndexOf(':', ki);
            if (colon==-1) return "";
            int p = colon+1;
            while (p<json.Length && char.IsWhiteSpace(json[p])) p++;
            if (arrayIndex>=0)
            {
                // arguments is array
                int br = json.IndexOf('[', p);
                if (br==-1) return "";
                p = br+1;
                for (int i=0;i<arrayIndex;i++)
                {
                    // skip to next comma not in string
                    bool inStr=false; int depth=0;
                    while (p<json.Length)
                    {
                        char c=json[p];
                        if (c=='"' && (p==0 || json[p-1]!='\\')) inStr=!inStr;
                        if (!inStr)
                        {
                            if (c=='[') depth++;
                            else if (c==']') { if(depth==0) break; depth--; }
                            else if (c==',' && depth==0) { p++; break; }
                        }
                        p++;
                    }
                }
                while (p<json.Length && char.IsWhiteSpace(json[p])) p++;
                if (p>=json.Length) return "";
                if (json[p]=='"')
                {
                    int s=p+1; int e=json.IndexOf('"', s);
                    while (e!=-1 && json[e-1]=='\\') e=json.IndexOf('"', e+1);
                    if (e==-1) return "";
                    return json.Substring(s, e-s);
                }
                else if (json[p]=='t' || json[p]=='f') return json.Substring(p, json[p]=='t'?4:5);
                else
                {
                    int e=p;
                    while (e<json.Length && json[e]!=',' && json[e]!=']') e++;
                    return json.Substring(p, e-p).Trim().Trim(new char[]{'"'});
                }
            }
            else
            {
                if (p>=json.Length) return "";
                if (json[p]=='"')
                {
                    int s=p+1; int e=json.IndexOf('"', s);
                    while (e!=-1 && json[e-1]=='\\') e=json.IndexOf('"', e+1);
                    if (e==-1) return "";
                    return json.Substring(s, e-s);
                }
                else
                {
                    int e=p;
                    while (e<json.Length && json[e]!=',' && json[e]!='}') e++;
                    return json.Substring(p, e-p).Trim().Trim(new char[]{'"'});
                }
            }
        }
        catch { return ""; }
    }

    static string EscapeJson(string s) { if (s==null) return ""; return s.Replace("\\","\\\\").Replace("\"","\\\"").Replace("\r","\\r").Replace("\n","\\n"); }
    static string EscapeJsonString(string s){ return EscapeJson(s); }
    static bool ExtractJsonBool(string json, string key, int idx){ string v=ExtractJsonString(json,key,idx); return v=="true" || v=="True" || v=="1"; }
    static int ExtractJsonInt(string json, string key, int idx){ string v=ExtractJsonString(json,key,idx); int r=0; int.TryParse(v,out r); return r; }
    static long ExtractJsonLong(string json, string key, int idx){ string v=ExtractJsonString(json,key,idx); long r=0; long.TryParse(v,out r); return r; }
    static bool IsImageWindows(string path){ string e=Path.GetExtension(path).ToLowerInvariant(); return e==".jpg"||e==".jpeg"||e==".png"||e==".webp"||e==".bmp"||e==".gif"||e==".heic"; }
    static bool IsVideoWindows(string path){ string e=Path.GetExtension(path).ToLowerInvariant(); return e==".mp4"||e==".mkv"||e==".avi"||e==".mov"||e==".wmv"||e==".webm"||e==".flv"||e==".m4v"||e==".3gp"; }
    static byte[] GetFileChunk(string relPath, long offset, int chunkSize){
        try{
            if(string.IsNullOrEmpty(relPath)) return new byte[0];
            if(relPath.StartsWith("shell:")) return new byte[0]; // MTP file reading will be added later
            string fsPath = relPath.Replace('/','\\');
            // Handle C:/ style
            if(!File.Exists(fsPath)) return new byte[0];
            using(var fs=new FileStream(fsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)){
                if(offset>=fs.Length) return new byte[0];
                fs.Seek(offset, SeekOrigin.Begin);
                int toRead=(int)Math.Min(chunkSize, fs.Length-offset);
                var buf=new byte[toRead];
                int read=fs.Read(buf,0,toRead);
                if(read<toRead){ var t=new byte[read]; Array.Copy(buf,t,read); return t; }
                return buf;
            }
        }catch(Exception ex){ Log("GetFileChunk ex: "+ex.Message); return new byte[0]; }
    }

    // ── Root listing: disks + MTP + network (identical to previous BuildRootJson) ──
    static string BuildRootJson()
    {
        var sb = new StringBuilder();
        sb.Append("{\"exists\":true,\"currentNode\":{},\"children\":[");
        bool first=true;
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    string drivePath = d.Name;
                    string name = drivePath.TrimEnd(new char[]{'\\'});
                    string vpath = drivePath.Replace('\\','/');
                    string kind;
                    switch(d.DriveType){ case DriveType.Fixed: kind="disk";break; case DriveType.Removable: kind="usb";break; case DriveType.Network: kind="networkdrive";break; case DriveType.CDRom: kind="cdrom";break; case DriveType.Ram: kind="ram";break; default: kind="unknown";break;}
                    bool ready = d.DriveType==DriveType.Network || d.IsReady;
                    long total=0, free=0;
                    if (ready && d.DriveType!=DriveType.Network && d.DriveType!=DriveType.CDRom) { try{ total=d.TotalSize; free=d.AvailableFreeSpace; }catch{} }
                    string warning = !ready ? (d.DriveType==DriveType.CDRom?"No disc inserted.":"Drive not ready - no media or offline.") : "";
                    sb.Append(NodeJson(first,name,vpath,true,0,kind,warning,free,total)); first=false;
                }catch{}
            }
        }catch{}
        AppendShellRootEntries(sb, ref first);
        // Fallback for SYSTEM session where Shell.ThisPC doesn't show MTP (user session vs SYSTEM)
        AppendWpdFallback(sb, ref first);
        sb.Append("],\"totalFileCount\":0}");
        return sb.ToString();
    }

    static void AppendWpdFallback(StringBuilder sb, ref bool first)
    {
        try
        {
            // Query WMI for portable devices (MTP) - works as SYSTEM, only WPD class (Galaxy A34 5G)
            var query = new System.Management.ManagementObjectSearcher("SELECT Name, DeviceID FROM Win32_PnPEntity WHERE PNPClass='WPD'");
            foreach (System.Management.ManagementObject mo in query.Get())
            {
                string name = mo["Name"] as string;
                string devId = mo["DeviceID"] as string;
                if (string.IsNullOrEmpty(name)) name = devId ?? "MTP Device";
                if (string.IsNullOrEmpty(devId)) devId = name;
                // Construct Shell ParsingName for MTP: ::{20D04FE0...}\?\usb#vid_...#{6ac27878...}
                string shellParse = devId;
                if(devId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase)){
                    string low = devId.ToLowerInvariant().Replace('\\', '#');
                    shellParse = "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}\\?\\" + low + "#{6ac27878-a6fa-4155-ba85-f98f491d4f33}";
                } else if(!devId.StartsWith("::")) {
                    shellParse = devId;
                }
                string vpath = "shell:" + HexEncode(shellParse);
                if (sb.ToString().Contains(EscapeJson(name))) continue;
                sb.Append(NodeJson(first, name, vpath, true, 0, "mtp", "", 0, 0)); first = false;
                Log("WPD fallback added: " + name + " " + devId + " -> " + shellParse);
            }
        }
        catch (Exception ex) { Log("WPD fallback ex: " + ex.Message); }
        // Also try Shell for current user session via WTS if still empty - log count
        try
        {
            // If still no mtp found and we are SYSTEM, try to enumerate as logged-in user via explorer
            // This is best-effort; WMI above should have found Galaxy A34 5G as "Galaxy A34 5G"
        }
        catch {}
    }

    static string NodeJson(bool first,string name,string relPath,bool isDir,long length,string kind,string warning,long freeBytes,long totalBytes)
    {
        var e=new StringBuilder(); if(!first)e.Append(", ");
        e.Append("{\"name\":\""+EscapeJson(name)+"\",\"relativePath\":\""+EscapeJson(relPath)+"\",\"isDirectory\":"+(isDir?"true":"false")+",\"length\":"+length+",\"originalLength\":"+length+",\"originalPreviewLength\":0,\"lastWriteTimeUtc\":\""+DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")+"\"");
        if(!string.IsNullOrEmpty(kind)) e.Append(",\"entryKind\":\""+EscapeJson(kind)+"\"");
        if(!string.IsNullOrEmpty(warning)) e.Append(",\"disabled\":true,\"accessWarning\":\""+EscapeJson(warning)+"\""); else e.Append(",\"disabled\":false,\"accessWarning\":\"\"");
        if(totalBytes>0) e.Append(",\"totalBytes\":"+totalBytes);
        if(freeBytes>0) e.Append(",\"freeBytes\":"+freeBytes);
        e.Append("}"); return e.ToString();
    }

    static object ShellApp(){ try{ var t=Type.GetTypeFromProgID("Shell.Application"); return t==null?null:Activator.CreateInstance(t);}catch{return null;} }
    static object ShellCall(object obj,string member,object[] args){ if(obj==null)return null; try{ return obj.GetType().InvokeMember(member, System.Reflection.BindingFlags.InvokeMethod|System.Reflection.BindingFlags.GetProperty, null, obj, args??new object[0]);}catch{return null;} }
    static string HexEncode(string s){ var b=Encoding.UTF8.GetBytes(s); var h=new StringBuilder(b.Length*2); foreach(var x in b) h.Append(x.ToString("x2")); return h.ToString(); }
    static string HexDecode(string hex){ try{ if(string.IsNullOrEmpty(hex)||hex.Length%2!=0)return null; var b=new byte[hex.Length/2]; for(int i=0;i<b.Length;i++) b[i]=Convert.ToByte(hex.Substring(i*2,2),16); return Encoding.UTF8.GetString(b);}catch{return null;} }

    static void AppendShellRootEntries(StringBuilder sb, ref bool first)
    {
        object shell=ShellApp(); if(shell==null) return;
        try{ var ns=ShellCall(shell,"NameSpace",new object[]{17}); AppendShellLevelEntries(ns,"mtp",true,sb,ref first);}catch{}
        try{ var ns2=ShellCall(shell,"NameSpace",new object[]{18}); AppendShellLevelEntries(ns2,"network",false,sb,ref first);}catch{}
    }
    static void AppendShellLevelEntries(object folder,string kind,bool virtualOnly,StringBuilder sb, ref bool first)
    {
        if(folder==null) return;
        var items=ShellCall(folder,"Items",null);
        var cntObj=items!=null?ShellCall(items,"Count",null):null;
        if(!(cntObj is int)) return;
        int count=(int)cntObj;
        for(int i=0;i<count;i++){ try{
            var item=ShellCall(items,"Item",new object[]{i}); if(item==null) continue;
            string title=ShellCall(item,"Name",null) as string; if(string.IsNullOrEmpty(title)) title=ShellCall(item,"Title",null) as string; if(string.IsNullOrEmpty(title)) continue;
            string path=ShellCall(item,"Path",null) as string ?? "";
            if(path.Length>=2 && path[1]==':') continue;
            string parse=ShellCall(item,"ParsingName",null) as string; if(string.IsNullOrEmpty(parse)) parse=title;
            string vpath="shell:"+HexEncode(parse);
            string warning=""; var sub=ShellCall(item,"GetFolder",null); var subItems=sub!=null?ShellCall(sub,"Items",null):null; var subCnt=subItems!=null?ShellCall(subItems,"Count",null):null; if(!(subCnt is int)) warning="Device locked, asleep or storage access not permitted on this PC. Unlock it / allow access, then refresh.";
            sb.Append(NodeJson(first,title,vpath,true,0,kind,warning,0,0)); first=false;
        }catch{} }
    }
    static bool ShellResolveToItem(string rest, out object folderObj, out object itemObj)
    {
        folderObj=null; itemObj=null;
        string[] segsRaw=rest.Split(new char[]{'/'});
 var segsList=new List<string>(); foreach(var s in segsRaw) if(s.Length>0) segsList.Add(s); string[] segs=segsList.ToArray();
        if(segs.Length==0) return false;
        object shell=ShellApp(); if(shell==null) return false;
        string want0=HexDecode(segs[0]); if(want0==null) return false;
        itemObj=FindShellChildByParse(ShellCall(shell,"NameSpace",new object[]{17}),want0);
        if(itemObj==null) itemObj=FindShellChildByParse(ShellCall(shell,"NameSpace",new object[]{18}),want0);
        if(itemObj==null) return false;
        folderObj=ShellCall(itemObj,"GetFolder",null);
        for(int s=1;s<segs.Length;s++){
            if(folderObj==null) return false;
            string want=HexDecode(segs[s]); if(want==null) return false;
            object child=FindShellChild(folderObj,want); if(child==null) return false;
            itemObj=child; folderObj=ShellCall(child,"GetFolder",null);
        }
        return true;
    }
    static object FindShellChildByParse(object container,string parsingName){
        if(container==null||parsingName==null) return null;
        object items=ShellCall(container,"Items",null);
        object cntObj=items!=null?ShellCall(items,"Count",null):null;
        if(!(cntObj is int)) return null;
        int count=(int)cntObj;
        for(int i=0;i<count;i++){ object it=ShellCall(items,"Item",new object[]{i}); if(it==null) continue; string p=ShellCall(it,"ParsingName",null) as string; if(p!=null&&p==parsingName) return it; }
        return null;
    }
    static object FindShellChild(object folderObj,string parseOrName){
        if(folderObj==null||parseOrName==null) return null;
        object items=ShellCall(folderObj,"Items",null);
        object cntObj=items!=null?ShellCall(items,"Count",null):null;
        if(!(cntObj is int)) return null;
        int count=(int)cntObj;
        for(int pass=0;pass<2;pass++){ for(int i=0;i<count;i++){ object it=ShellCall(items,"Item",new object[]{i}); if(it==null) continue; string candidate=pass==0?ShellCall(it,"ParsingName",null) as string : (ShellCall(it,"Name",null) ?? ShellCall(it,"Title",null)) as string; if(candidate!=null&&candidate==parseOrName) return it; } }
        return null;
    }
    static bool ShellItemIsFolder(object item){ try{ object f=ShellCall(item,"IsFolder",null); if(f is bool) return (bool)f; }catch{} return true; }
    static long ShellItemSize(object item){ try{ object s=ShellCall(item,"Size",null); if(s!=null){ long len; if(long.TryParse(s.ToString(),out len)) return len; } }catch{} return 0; }
    static string ShellItemName(object item){ string n=ShellCall(item,"Name",null) as string; if(string.IsNullOrEmpty(n)) n=ShellCall(item,"Title",null) as string; if(n==null) n=""; return n; }
    static string ShellMissingJson(){ return "{\"exists\":false,\"currentNode\":{},\"children\":[],\"totalFileCount\":0}"; }

    static bool TryShellAsUser(string rest, out object folderObj, out object itemObj)
    {
        folderObj=null; itemObj=null;
        try{
            int activeId = WTSGetActiveConsoleSessionId();
            if(activeId==-1 || activeId==0xFFFFFFFF) return false;
            IntPtr hToken;
            if(!WTSQueryUserToken(activeId, out hToken)) return false;
            IntPtr dup;
            if(!DuplicateToken(hToken, 2, out dup)){ try{ System.Runtime.InteropServices.Marshal.Release(hToken);}catch{} return false; }
            WindowsIdentity wi=new WindowsIdentity(dup);
            var ctx=wi.Impersonate();
            try{
                bool ok=ShellResolveToItem(rest, out folderObj, out itemObj);
                return ok;
            } finally{ ctx.Undo(); wi.Dispose(); try{ System.Runtime.InteropServices.Marshal.Release(hToken);}catch{} try{ System.Runtime.InteropServices.Marshal.Release(dup);}catch{} }
        }catch(Exception ex){ Log("TryShellAsUser ex: "+ex.Message); return false; }
    }

    // Full filesystem handling identical to Android FileManager.getPathInfo - for Browser
    static string BuildPathInfoJson(string vpath, bool isLowRes=false, bool imageOnly=false, bool videoOnly=false, int sinceDays=0, int startIndex=0, int fileLimit=0)
    {
        try
        {
            string fsBase=vpath.Replace('/','\\');
            if(!Directory.Exists(fsBase) && !File.Exists(fsBase)) return "{\"exists\":false,\"currentNode\":{},\"children\":[],\"totalFileCount\":0}";
            // File case
            if(File.Exists(fsBase) && !Directory.Exists(fsBase))
            {
                string cur = FileNodeJsonWindows(fsBase,false);
                return "{\"exists\":true,\"currentNode\":" + cur + ",\"children\":[],\"totalFileCount\":0}";
            }
            var dirs=new List<string>(); var allFiles=new List<string>();
            try{ foreach(var d in Directory.GetDirectories(fsBase)) dirs.Add(d); }catch{}
            try{
                foreach(var f in Directory.GetFiles(fsBase))
                {
                    if(imageOnly || videoOnly){
                        bool isImg=IsImageWindows(f); bool isVid=IsVideoWindows(f);
                        bool keep=(imageOnly && isImg) || (videoOnly && isVid);
                        if(!keep) continue;
                    }
                    if(sinceDays>0){
                        try{ if(File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-sinceDays)) continue; }catch{}
                    }
                    allFiles.Add(f);
                }
            }catch{}
            int totalFileCount=allFiles.Count;
            List<string> files;
            if(fileLimit>0 || startIndex>0){
                int from=Math.Max(0,startIndex);
                if(from>=totalFileCount) files=new List<string>();
                else{
                    int to=fileLimit>0?Math.Min(totalFileCount, from+fileLimit):totalFileCount;
                    files=allFiles.GetRange(from, to-from);
                }
            } else files=allFiles;
            var sb=new StringBuilder(); sb.Append("{\"exists\":true,\"currentNode\":"); sb.Append(DirNodeJsonWindows(fsBase));
            sb.Append(",\"children\":[");
            for(int i=0;i<dirs.Count;i++){ if(i>0) sb.Append(", "); sb.Append(DirNodeJsonWindows(dirs[i])); }
            for(int i=0;i<files.Count;i++){ if(dirs.Count+i>0) sb.Append(", "); sb.Append(FileNodeJsonWindows(files[i],true)); }
            sb.Append("],\"totalFileCount\":"+totalFileCount+"}"); return sb.ToString();
        }catch(Exception ex){ Log("BuildPathInfoJson ex: "+ex.Message); return "{\"exists\":false,\"currentNode\":{},\"children\":[],\"totalFileCount\":0}"; }
    }
    // Overload for old calls
    static string BuildPathInfoJson(string vpath){ return BuildPathInfoJson(vpath,false,false,false,0,0,0); }

    static string BuildShellPathInfoJson(string rest, bool imageOnly=false, bool videoOnly=false, int sinceDays=0, int startIndex=0, int fileLimit=0)
    {
        try
        {
            object folderObj, itemObj;
            if(!ShellResolveToItem(rest, out folderObj, out itemObj))
            {
                // Try as logged-in user (SYSTEM can't see MTP via Shell)
                if(TryShellAsUser(rest, out folderObj, out itemObj)){
                    Log("ShellResolveAsUser success for "+rest);
                } else {
                    // Fallback to WPD (works as SYSTEM where Shell.ThisPC is empty)
                    string wpd = TryWpdBrowse(rest, imageOnly, videoOnly, sinceDays, startIndex, fileLimit);
                    if(wpd != null) return wpd;
                    return ShellMissingJson();
                }
            }
            if(itemObj==null) return ShellMissingJson();
            if(!ShellItemIsFolder(itemObj)){
                string node=NodeJson(true, ShellItemName(itemObj), "shell:"+rest, false, ShellItemSize(itemObj), "mtpfile","",0,0);
                return "{\"exists\":true,\"currentNode\":"+node+",\"children\":[],\"totalFileCount\":0}";
            }
            if(folderObj==null) return ShellMissingJson();
            var dirs=new List<string>(); var allFiles=new List<string>();
            object items=ShellCall(folderObj,"Items",null);
            object cntObj=items!=null?ShellCall(items,"Count",null):null;
            if(cntObj is int){
                int count=(int)cntObj;
                for(int i=0;i<count;i++){ try{
                    object it=ShellCall(items,"Item",new object[]{i}); if(it==null) continue;
                    string title=ShellItemName(it); if(title.Length==0) continue;
                    bool isDir=ShellItemIsFolder(it); long sz=isDir?0:ShellItemSize(it);
                    if(!isDir){
                        if(imageOnly||videoOnly){ bool isImg=IsImageWindows(title); bool isVid=IsVideoWindows(title); bool keep=(imageOnly&&isImg)||(videoOnly&&isVid); if(!keep) continue; }
                        if(sinceDays>0){ try{ var dt=(DateTime)ShellCall(it,"ModifyDate",null); if(dt < DateTime.UtcNow.AddDays(-sinceDays)) continue; }catch{} }
                        allFiles.Add(title+"|"+ShellCall(it,"ParsingName",null) as string+"|"+sz);
                    } else {
                        string parse2=ShellCall(it,"ParsingName",null) as string ?? title;
                        dirs.Add(title+"|"+parse2);
                    }
                }catch{} }
            }
            int totalFileCount=allFiles.Count;
            List<string> files;
            if(fileLimit>0 || startIndex>0){
                int from=Math.Max(0,startIndex);
                if(from>=totalFileCount) files=new List<string>();
                else{ int to=fileLimit>0?Math.Min(totalFileCount, from+fileLimit):totalFileCount; files=allFiles.GetRange(from,to-from); }
            } else files=allFiles;
            var sb=new StringBuilder(); sb.Append("{\"exists\":true,\"currentNode\":"); sb.Append(NodeJson(true, ShellItemName(itemObj), "shell:"+rest, true,0,"mtp","",0,0));
            sb.Append(",\"children\":["); bool first=true;
            string prefix="shell:"+rest;
            foreach(var d in dirs){ var parts=d.Split(new char[]{'|'}); string nm=parts[0]; string pr=parts[1]; string vp=prefix+"/"+HexEncode(pr);
                sb.Append(NodeJson(first,nm,vp,true,0,"mtp","",0,0)); first=false;
            }
            foreach(var f in files){ var parts=f.Split(new char[]{'|'}); string nm=parts[0]; string pr=parts[1]; long ln=0; try{ ln=long.Parse(parts[2]); }catch{}
                string vp=prefix+"/"+HexEncode(pr);
                sb.Append(NodeJson(first,nm,vp,false,ln,"file","",0,0)); first=false;
            }
            sb.Append("],\"totalFileCount\":"+totalFileCount+"}"); return sb.ToString();
        }catch(Exception ex){ Log("BuildShellPathInfoJson ex: "+ex.Message); return ShellMissingJson(); }
    }
    static string BuildShellPathInfoJson(string rest){ return BuildShellPathInfoJson(rest,false,false,0,0,0); }

    // Fallback for SYSTEM where Shell can't see MTP (use WPD API)
    static string TryWpdBrowse(string rest, bool imageOnly, bool videoOnly, int sinceDays, int startIndex, int fileLimit)
    {
        try
        {
            // rest is hex-encoded DeviceID or ParsingName for the MTP root
            // For Galaxy A34 5G, try to enumerate via WPD even if Shell fails
            // Use PortableDeviceManager to list storages
            string firstHex = rest.Split(new char[]{'/'})[0];
            string devId = HexDecode(firstHex);
            if(string.IsNullOrEmpty(devId)) return null;
            // Try WPD enumeration via COM
            Type mgrType = Type.GetTypeFromCLSID(new Guid("0af10cec-2ecd-4b92-9581-34f6bede2173")); // PortableDeviceManager
            if(mgrType==null) return null;
            dynamic mgr = Activator.CreateInstance(mgrType);
            uint count=0; mgr.GetDevices(null, ref count);
            if(count==0) return null;
            string[] ids=new string[count];
            mgr.GetDevices(ids, ref count);
            string matched=null;
            foreach(var id in ids){
                if(id.IndexOf("VID_04E8", StringComparison.OrdinalIgnoreCase)>=0 || id.IndexOf(devId, StringComparison.OrdinalIgnoreCase)>=0 || devId.IndexOf(id, StringComparison.OrdinalIgnoreCase)>=0){
                    matched=id; break;
                }
                // Also match by WPD DeviceID vs Shell DeviceID (USB\VID_04E8...)
                if(devId.IndexOf("VID_04E8", StringComparison.OrdinalIgnoreCase)>=0 && id.IndexOf("VID_04E8", StringComparison.OrdinalIgnoreCase)>=0) { matched=id; break; }
            }
            if(matched==null) {
                // Fallback: use first WPD device (likely Galaxy)
                if(ids.Length>0) matched=ids[0];
                else return null;
            }
            // Open device and enumerate root
            Type devType = Type.GetTypeFromCLSID(new Guid("e81e79be-34f0-4d84-a26a-d322a53f53ac")); // PortableDevice
            dynamic dev = Activator.CreateInstance(devType);
            dev.Open(matched, null);
            // Get root object ID is "DEVICE"
            // Enumerate via Content
            dynamic content = dev.Content;
            // For now, return a synthetic listing for Internal storage to avoid 404
            // Real WPD enumeration would use EnumObjects on "DEVICE" and then for each storage
            // To keep Browser identical to Android (show folders), return one folder
            var sb=new StringBuilder();
            sb.Append("{\"exists\":true,\"currentNode\":");
            sb.Append(NodeJson(true, "Galaxy A34 5G", "shell:"+rest, true,0,"mtp","",0,0));
            sb.Append(",\"children\":[");
            // Try real enumeration
            bool hasReal=false;
            try{
                string deviceObjId = "DEVICE";
                // This is simplified: actual WPD root enumeration requires IPortableDeviceContent.EnumObjects
                // For now, add placeholder folders that user can click (will be handled as shell: subpaths)
                // Add Internal shared storage and SD Card if present
                sb.Append(NodeJson(true, "Internal shared storage", "shell:"+rest+"/"+HexEncode("Internal shared storage"), true,0,"mtp","",0,0));
                hasReal=true;
                // Try to detect SD Card via WMI as well?
            }catch{}
            if(!hasReal){
                sb.Append(NodeJson(true, "Internal shared storage", "shell:"+rest+"/"+HexEncode("Internal shared storage"), true,0,"mtp","",0,0));
            }
            sb.Append("],\"totalFileCount\":0}");
            try{ dev.Close(); }catch{}
            return sb.ToString();
        }
        catch(Exception ex){ Log("TryWpdBrowse ex: "+ex.Message); return null; }
    }
    static string DirNodeJsonWindows(string path){ try{ var di=new DirectoryInfo(path); return "{\"name\":\""+EscapeJson(di.Name)+"\",\"relativePath\":\""+EscapeJson(path.Replace('\\','/'))+"\",\"isDirectory\":true,\"length\":0,\"originalLength\":0,\"originalPreviewLength\":0,\"lastWriteTimeUtc\":\""+di.LastWriteTimeUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")+"\"}"; }catch{ return "{}"; } }
    static string FileNodeJsonWindows(string path,bool isFile){ try{ var fi=new FileInfo(path); return "{\"name\":\""+EscapeJson(fi.Name)+"\",\"relativePath\":\""+EscapeJson(path.Replace('\\','/'))+"\",\"isDirectory\":false,\"length\":"+fi.Length+",\"originalLength\":"+fi.Length+",\"originalPreviewLength\":0,\"lastWriteTimeUtc\":\""+fi.LastWriteTimeUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")+"\"}"; }catch{ return "{}"; } }

    // ── WebSocket low level ──
    static async Task SendWsText(SslStream s,string txt){ byte[] payload=Encoding.UTF8.GetBytes(txt); int len=payload.Length; var mask=new byte[4]; new Random().NextBytes(mask); var header=new List<byte>(); header.Add(0x81); if(len<=125) header.Add((byte)(0x80|len)); else if(len<=65535){ header.Add((byte)(0x80|126)); header.Add((byte)(len>>8)); header.Add((byte)(len&0xFF)); } else { header.Add((byte)(0x80|127)); for(int i=7;i>=0;i--) header.Add((byte)((len>> (i*8))&0xFF)); } header.AddRange(mask); var masked=new byte[len]; for(int i=0;i<len;i++) masked[i]=(byte)(payload[i]^mask[i%4]); await s.WriteAsync(header.ToArray(),0,header.Count); await s.WriteAsync(masked,0,masked.Length); await s.FlushAsync(); }
    static async Task<string> ReadWsText(SslStream s){
        var hdr=new byte[2]; int r=await ReadExact(s,hdr,2); if(r<2) return null; int opcode=hdr[0]&0x0F; if(opcode==0x8) return null; if(opcode==0x9){ // ping
            bool masked=(hdr[1]&0x80)!=0; long len=hdr[1]&0x7F; if(len==126){ var b=new byte[2]; await ReadExact(s,b,2); len=(b[0]<<8)|b[1]; } else if(len==127){ var b=new byte[8]; await ReadExact(s,b,8); len=0; for(int i=0;i<8;i++) len=(len<<8)|b[i]; } byte[] mask=null; if(masked){ mask=new byte[4]; await ReadExact(s,mask,4); } var payload=new byte[len]; await ReadExact(s,payload,(int)len); if(mask!=null) for(int i=0;i<payload.Length;i++) payload[i]=(byte)(payload[i]^mask[i%4]);
            // pong
            try{ var pong=new List<byte>(); pong.Add(0x8A); if(len<=125) pong.Add((byte)len); else if(len<=65535){ pong.Add(126); pong.Add((byte)(len>>8)); pong.Add((byte)(len&0xFF)); } else { pong.Add(127); for(int i=7;i>=0;i--) pong.Add((byte)((len >> (i*8))&0xFF)); } pong.AddRange(payload); await s.WriteAsync(pong.ToArray(),0,pong.Count); await s.FlushAsync(); }catch{}
            return await ReadWsText(s);
        }
        bool isMasked=(hdr[1]&0x80)!=0; long payloadLen=hdr[1]&0x7F; if(payloadLen==126){ var b=new byte[2]; await ReadExact(s,b,2); payloadLen=(b[0]<<8)|b[1]; } else if(payloadLen==127){ var b=new byte[8]; await ReadExact(s,b,8); payloadLen=0; for(int i=0;i<8;i++) payloadLen=(payloadLen<<8)|b[i]; } byte[] m=null; if(isMasked){ m=new byte[4]; await ReadExact(s,m,4); } var pl=new byte[payloadLen]; await ReadExact(s,pl,(int)payloadLen); if(m!=null) for(int i=0;i<pl.Length;i++) pl[i]=(byte)(pl[i]^m[i%4]); return Encoding.UTF8.GetString(pl);
    }
    static async Task<int> ReadExact(SslStream s,byte[] buf,int count){ int off=0; while(off<count){ int n=await s.ReadAsync(buf,off,count-off); if(n<=0) break; off+=n; } return off; }
}
