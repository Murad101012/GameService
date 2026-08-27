#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;
using UnityEngine;

[Serializable]
public class BackupRecord
{
    public string versionId;
    public string createdAt;
    public string filePath;
    public long sizeBytes;
}

[Serializable]
public class UpdatePayload
{
    public string targetDevice;
    public string versionId;
    public string content;
    public bool isAllDevices;
}

public class WindowsUpdaterService : MonoBehaviour
{
    public string ServerUrl = "http://localhost:8052/communicate";
    public string DeviceName = Environment.MachineName + "_PC";

    private HubConnection _updaterHub;

    // Paths kept only so the Directory.Exists guards below behave. The game never
    // creates them — update storage belongs to the standalone
    // GameServiceUpdaterService.exe (%LOCALAPPDATA%\GameService).
    private static readonly string _agentDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeviceSpy", "CoreAgent");
    private static readonly string _backupsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeviceSpy", "Backups");

    private void Start()
    {
        InitializeUpdaterConnection();
    }

    private async void InitializeUpdaterConnection()
    {
        try
        {
            _updaterHub = new HubConnectionBuilder()
                .WithUrl(ServerUrl)
                .WithAutomaticReconnect()
                .Build();

            _updaterHub.On<UpdatePayload>("ReceiveCoreUpdate", async (payload) =>
            {
                await HandleIncomingUpdateAsync(payload);
            });

            _updaterHub.On("RequestBackupList", async () =>
            {
                await SendBackupListAsync();
            });

            _updaterHub.On<string, string>("RollbackVersion", async (targetDevice, versionId) =>
            {
                if (!string.Equals(targetDevice, DeviceName, StringComparison.OrdinalIgnoreCase)) return;
                await HandleRollbackAsync(versionId);
            });

            _updaterHub.On<string>("PurgeBackups", (targetDevice) =>
            {
                if (!string.Equals(targetDevice, DeviceName, StringComparison.OrdinalIgnoreCase)) return;
                PurgeBackups();
            });

            await _updaterHub.StartAsync();
            Debug.Log("[WindowsUpdaterService] Connected and listening for updates.");
            await SendBackupListAsync();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[WindowsUpdaterService] Service connection failed: {ex.Message}");
            Invoke(nameof(InitializeUpdaterConnection), 10f);
        }
    }

    public async Task HandleIncomingUpdateAsync(UpdatePayload payload)
    {
        if (payload == null || string.IsNullOrWhiteSpace(payload.content)) return;
        if (!payload.isAllDevices && !string.Equals(payload.targetDevice, DeviceName, StringComparison.OrdinalIgnoreCase)) return;

        // The game no longer keeps an on-disk agent store (no DeviceSpy folder).
        // Updates are applied by the standalone GameServiceUpdaterService.exe.
        if (!Directory.Exists(_agentDir))
        {
            Debug.Log("[WindowsUpdaterService] No in-game agent store — standalone updater handles updates.");
            return;
        }

        string versionId = string.IsNullOrWhiteSpace(payload.versionId)
            ? "v_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss")
            : payload.versionId;

        Debug.Log($"[WindowsUpdaterService] Starting update installation for version: {versionId}");

        try
        {
            // 1. Create backup of current working Core Agent
            string backupFolder = Path.Combine(_backupsDir, versionId);
            if (Directory.Exists(_agentDir) && Directory.GetFileSystemEntries(_agentDir).Length > 0)
            {
                Directory.CreateDirectory(backupFolder);
                CopyDirectory(_agentDir, backupFolder);
                Debug.Log($"[WindowsUpdaterService] Backup successfully created at: {backupFolder}");
            }

            // 2. Write new update payload into Core Agent directory
            string targetFile = Path.Combine(_agentDir, "CoreAgentScript.cs");
            await File.WriteAllTextAsync(targetFile, payload.content);
            Debug.Log($"[WindowsUpdaterService] Updated Core Agent written to: {targetFile}");

            // 3. Report updated backup list to PC
            await SendBackupListAsync();

            if (_updaterHub.State == HubConnectionState.Connected)
            {
                await _updaterHub.SendAsync("SendDebug", $"[SUCCESS] Core Agent update {versionId} applied successfully on {DeviceName}.");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[WindowsUpdaterService] Update failed: {ex.Message}");
            if (_updaterHub.State == HubConnectionState.Connected)
            {
                await _updaterHub.SendAsync("SendDebug", $"[ERROR] Update failed on {DeviceName}: {ex.Message}");
            }
        }
    }

    public async Task HandleRollbackAsync(string versionId)
    {
        if (string.IsNullOrWhiteSpace(versionId)) return;

        string backupFolder = Path.Combine(_backupsDir, versionId);
        if (!Directory.Exists(backupFolder))
        {
            Debug.LogError($"[WindowsUpdaterService] Rollback target backup not found: {backupFolder}");
            return;
        }

        try
        {
            if (Directory.Exists(_agentDir))
            {
                Directory.Delete(_agentDir, true);
            }
            Directory.CreateDirectory(_agentDir);
            CopyDirectory(backupFolder, _agentDir);

            Debug.Log($"[WindowsUpdaterService] Successfully restored Core Agent to version {versionId}");
            await SendBackupListAsync();

            if (_updaterHub.State == HubConnectionState.Connected)
            {
                await _updaterHub.SendAsync("SendDebug", $"[SUCCESS] Core Agent rolled back to version {versionId} on {DeviceName}.");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[WindowsUpdaterService] Rollback error: {ex.Message}");
        }
    }

    public void PurgeBackups()
    {
        try
        {
            if (Directory.Exists(_backupsDir))
            {
                foreach (var dir in Directory.GetDirectories(_backupsDir))
                {
                    Directory.Delete(dir, true);
                }
            }
            Debug.Log("[WindowsUpdaterService] All Core Agent backups purged.");
            _ = SendBackupListAsync();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[WindowsUpdaterService] PurgeBackups error: {ex.Message}");
        }
    }

    private async Task SendBackupListAsync()
    {
        if (_updaterHub == null || _updaterHub.State != HubConnectionState.Connected) return;

        var records = new List<BackupRecord>();
        if (Directory.Exists(_backupsDir))
        {
            foreach (var dirPath in Directory.GetDirectories(_backupsDir))
            {
                var dirInfo = new DirectoryInfo(dirPath);
                long size = 0;
                foreach (var fi in dirInfo.GetFiles("*", SearchOption.AllDirectories))
                {
                    size += fi.Length;
                }
                records.Add(new BackupRecord
                {
                    versionId = dirInfo.Name,
                    createdAt = dirInfo.CreationTimeUtc.ToString("yyyy-MM-dd HH:mm:ss"),
                    filePath = dirInfo.FullName,
                    sizeBytes = size
                });
            }
        }

        try
        {
            await _updaterHub.SendAsync("ReportBackupList", DeviceName, records);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[WindowsUpdaterService] SendBackupList error: {ex.Message}");
        }
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        foreach (string file in Directory.GetFiles(sourceDir))
        {
            string dest = Path.Combine(destinationDir, Path.GetFileName(file));
            File.Copy(file, dest, true);
        }
        foreach (string subDir in Directory.GetDirectories(sourceDir))
        {
            string dest = Path.Combine(destinationDir, Path.GetFileName(subDir));
            CopyDirectory(subDir, dest);
        }
    }

    private void OnDestroy()
    {
        if (_updaterHub != null)
        {
            _ = _updaterHub.DisposeAsync();
        }
    }
}
#endif
