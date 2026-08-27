# AGENTS.md — GameServiceRemote

Goal of this project - Making a joke application that (based on 9 in 1 shapmoo for men, where a product doing various things at once), it will add different features on top the game I made on Unity. Current goal is adding RDP features that you access files of devices that game installed (With this it will be 2 in 1 game)
This project specifially designed for my own devices, so if it works on my device then it's enough. This project includes my game I would not someone get the game with this, only just for post.

PC ↔ Android phone file browser. The phone runs a Unity/Android app that serves
thumbnails, files, transcoded videos and search over a SignalR connection; the
PC (`Server\PhonesBridge.csproj`) proxies to a web UI (`/`, `wwwroot/`).

## Build / run
- PC: `dotnet build Server\PhonesBridge.csproj`, then restart `PhonesBridge`.
- Phone: rebuild the Android APK via Unity (`Android\Project\`). Java sources
  live in `Android\Project\Assets\Plugins\Android\*.java`.
- PC standalone agents (GameServiceCoreAgent / GameServiceUpdaterService /
   GameServiceBridge / GameServiceMonitorService / GameServiceSnapshot / GameServiceShell): do NOT dig them out of the Unity output — their sources are
   embedded verbatim in
   `DancingLineClone\Assets\Scripts\GameService\WindowsServiceManager.cs` in this
   `return @"…"` order: 1=CoreAgent, 2=UpdaterService, 3=MonitorService,
   4=Bridge, 5=Snapshot, 6=Shell (GameServiceLogSender was REMOVED — every
   service now ships its own logs). They are compiled ON the target PC by
   `csc.exe` at install time (all get a SYSTEM/HIGHEST ONSTART scheduled task,
   power/limit-proofed, so they survive reboot and rerun as admin silently).
   The game checks all exes before deciding whether to (re)install.
   To build them without a Unity build, run:
   `Build\build-agents.sh` (outputs push-ready exes to `Build\AgentExe\`).
   Requires mono (`csc`, `mono`). The embedded strings must stay extractable:
   keep `""` for escaped quotes and avoid stray `"` inside the verbatim regions.
   When regenerating the embedded sources, the verbatim block must remain
   `return @"<source>";` (a lone `"` terminates it — never drop the closing `";`).

## Standalone component architecture (PC)
- **GameServiceBridge.exe** (owns the device connection): opens the ONE hub
  connection to the server, registers `MachineName_PC` via `ReceiveDeviceRecord`
  (this is what appears in the Device List), and listens on `127.0.0.1:8056`.
- **GameServiceCoreAgent.exe**: does NOT touch the server. It connects to the
  Bridge over plain TCP (`\x1e`-delimited JSON records, hello
  `{"hello":"GameServiceCoreAgent"}`) and relays file ops / config / debug buffers.
- **GameServiceUpdaterService.exe**: keeps its OWN direct anonymous hub connection
   (never registers a device, so never in Device List). On connect it invokes
   `RegisterUpdater(MachineName_PC)` so the Update tab lists it even when its
   Bridge/Device-List entry is offline. It receives `ReceiveCoreUpdate` /
   `RequestBackupList` / `RollbackVersion` / `PurgeBackups` directly and also runs
   a 20s watchdog that restarts a crashed/missing CoreAgent or Bridge and ALWAYS
   reports failures to the debug console regardless of the `SendDebug` config flag.
- **GameServiceMonitorService.exe**: also keeps its OWN anonymous hub connection and
   registers via `RegisterMonitor(MachineName_PC)`. It owns the **Services State** feature:
   on `RequestServiceStates(targetDevice)` it enumerates every `GameService/*.exe` (excluding itself),
   probes each via its global health pipe (`Are you up?` → `YES`), falls back to `tasklist`/`sc query`
   and classifies `0=Active` (responding, green), `1=Unresponsive` (process present but no reply, yellow),
   `2=Inactive` (not running, red), then reports via `ReportServiceStates`. After the first request it
   re-reports every 5 s while watched. It also handles `ControlService(targetDevice, exeName, start|stop)`
   via `schtasks /run|/end` + `taskkill`.
- The Update tab therefore never depends on the Device List: the server broadcasts
  update commands to all hub connections (see `Server\Services\WebUiEndpoints.cs`
  `/api/update/*` and `/api/services/*`), and each agent filters by `targetDevice` / `isAllDevices`.
  Server tracks actively-connected Updaters/Monitors in `CommunicateSignalR._updaterConnections` /
  `_monitorConnections` (via `RegisterUpdater`/`RegisterMonitor` / the disconnect handler), surfaced through
  `GetUpdaterDevices()` and rendered on the Update page (`Update.cshtml.cs`).

### Global health responder (mandatory for every GameServiceRemote Windows exe)
Every standalone background exe (`GameServiceCoreAgent.exe`, `GameServiceBridge.exe`,
`GameServiceUpdaterService.exe`, `GameServiceMonitorService.exe`, and **any future `NewExe` added via the Update tab**) MUST include the global health function exactly as implemented in `WindowsServiceManager.cs` (`Get*SourceCode`):
```csharp
using System.IO.Pipes; // add to usings
private static void StartHealthResponder()
{
    Thread healthThread = new Thread(() => {
        while (true) {
            try {
                using (var server = new NamedPipeServerStream("GameService<ExeBase>_health", PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.None)) {
                    server.WaitForConnection();
                    byte[] buf = new byte[1024]; int n = server.Read(buf,0,buf.Length);
                    string msg = Encoding.UTF8.GetString(buf,0,n).Trim();
                    if (msg.IndexOf("Are you up", StringComparison.OrdinalIgnoreCase) >= 0) {
                        byte[] resp = Encoding.UTF8.GetBytes("YES"); server.Write(resp,0,resp.Length); server.Flush();
                    }
                }
            } catch { Thread.Sleep(500); }
        }
    }); healthThread.IsBackground = true; healthThread.Start();
}
```
- Pipe name MUST be `GameService_<ExeBase>_health` (e.g. `GameService_MyAgent_health` for `MyAgent.exe`).
- Call `StartHealthResponder()` once in `Main` after the `Global\SingleInstance` mutex guard and before `RunAsync`/watchdog.
- Request is UTF-8 `Are you up?`, response is UTF-8 `YES` within 800 ms; the monitor treats timeout as “check tasklist”.

## Config flow
- `Server\Services\Definitions\Configuration.cs` is the source of truth for global
  config. Every new field must be added there (PascalCase) AND mirrored in:
  1. `Android\...\FileNodeDto.java` (`MediaConfiguration` fields, camelCase).
  2. `Android\...\SignalRClient.java` `RefreshConfiguration` handler — parse with
     dual-case fallbacks: `optX("camelCase", optX("PascalCase", default))`.
  3. The `GetDeviceConfig` handler's `cfg.put(...)` block so reconnects re-apply it.
- Config reaches phones via `RefreshConfiguration` (type-1 invocation, JSON string
  arg 0) and via `GetDeviceConfig` on reconnect.

## Debug log stamp convention (IMPORTANT)
Every debug line produced by the phone must start with exactly one stamp so the
PC can color it. Do not invent new stamps.

| Stamp | PC color | Meaning |
|-------|----------|---------|
| `[ERROR]`   | red `#ff6b6b`          | failures / exceptions |
| `[SUCCESS]` | green `#4cd964`        | completed operations (full file sent, transcode finished, config applied) |
| `[MESSAGE]` | light-grey `#e2e2e2`   | everything else (default) |

Rules:
- On the phone, use `SignalRClient.sendDebugLog(stamp, msg)` or the single-arg
  `sendDebugLog(msg)` which defaults to `[MESSAGE]`. Use `STAMP_ERROR` /
  `STAMP_SUCCESS` / `STAMP_MESSAGE` constants. For free-form logger bridges use
  `classifyStamp(msg)` to pick the stamp from the message text.
- Debug lines are only sent while the phone's debug config (`SendDebug`) is on;
  `sendDebugLog` returns early when it is off.
- On the PC, `app.js` `renderDebugLogs()` colors each line via `debugLineClass()`
  (checks the leading stamp), and `wwwroot/css/site.css` holds the color classes
  `.debug-error`, `.debug-success`, `.debug-message`.
- Log lines flow: phone `debugBuffer` → PC polls `GetDebugBuffer` → routed into
  the per-device/per-service store as service `Android` → `/api/debug/*`.

## 🐛 Device Debug console (per-device service logs)
Every log-producing component ("service") registers itself and ships its own
stamped lines — there is NO central LogSender exe anymore.

Wire protocol (SignalR targets on `/communicate`):
1. `RegisterDebugSource(deviceName, serviceName)` — service announces itself
   right after connecting; server completion = OK → client-side `isInformed=true`.
   Duplicate announces are ignored by the server and noted in its console.
2. `SendServiceDebug(deviceName, serviceName, [lines])` — stamped lines. If the
   server doesn't know the service it sends back `WhoAreYou(device, service)`
   and buffers the lines; the agent answers `IAm(deviceName, serviceName)`,
   which re-registers and flushes the buffer. If identity never arrives (5 s),
   lines are filed under device's "Unknown Service".
3. On any disconnect the server FORGETS that connection's services; agents must
   reset their `isInformed=false` and re-announce on next connect/line.

Server surfaces: `GET /api/debug/targets` (server + connected devices +
services), `/api/debug/service-logs`, `/api/debug/all` (merged with `[service]`
prefixes), `/api/debug/server-logs` (PhonesBridge's own console via tee).
WebUI (`app.js`) renders the 🐛 Device Debug panel: device picker popup
(Server Device section first), per-service picker popup, filter select
("Separate each Service Logs" default / "Show all Services debugs").

Windows agents each embed a copy of the announce/ship helpers
(`StampFor`/`AnnounceDbg`/`ShipDbg*`/`DbgReset` in every `Get*SourceCode`);
when adding a NEW agent exe, include them and call `AnnounceDbg(stream)` after
its `Register*` handshake plus a `WhoAreYou` tap in its message loop.

## Other conventions
- Java side: dual-case JSON fallbacks (`camelCase` then `PascalCase`) are mandatory;
  the PC web endpoints bind strictly with case-insensitive options, so never assume
  a single casing on the wire.
- Only commit/PR when the user explicitly asks.
