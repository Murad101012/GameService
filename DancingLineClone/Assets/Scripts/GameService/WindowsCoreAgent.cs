#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;
using UnityEngine;

public class WindowsCoreAgent : MonoBehaviour
{
    public string ServerUrl = "http://localhost:8052/communicate";
    public string DeviceName = Environment.MachineName + "_PC";
    public string DeviceId = SystemInfo.deviceUniqueIdentifier;

    private HubConnection _hubConnection;
    private bool _isConnecting = false;

    private void Start()
    {
        InitializeSignalR();
    }

    private async void InitializeSignalR()
    {
        if (_isConnecting) return;
        _isConnecting = true;

        try
        {
            _hubConnection = new HubConnectionBuilder()
                .WithUrl(ServerUrl)
                .WithAutomaticReconnect()
                .Build();

            RegisterHandlers();

            await _hubConnection.StartAsync();
            Debug.Log($"[WindowsCoreAgent] Connected to PC server: {ServerUrl}");

            // Register device as Windows platform
            var record = new { DeviceName = DeviceName, DeviceId = DeviceId, Platform = "Windows" };
            await _hubConnection.SendAsync("ReceiveDeviceRecord", record);
            Debug.Log($"[WindowsCoreAgent] Registered identity: {DeviceName} (Windows)");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[WindowsCoreAgent] Connection error: {ex.Message}");
            Invoke(nameof(InitializeSignalR), 5f);
        }
        finally
        {
            _isConnecting = false;
        }
    }

    private void RegisterHandlers()
    {
        _hubConnection.On<string>("RefreshConfiguration", (configJson) =>
        {
            Debug.Log($"[WindowsCoreAgent] Received configuration update: {configJson}");
        });

        _hubConnection.On<string, long, int, bool, float, float>("GetFileChunkWithOverride", async (path, offset, count, lowRes, frame, quality) =>
        {
            byte[] chunk = ReadFileChunk(path, offset, count);
            await _hubConnection.SendAsync("SendDebug", $"[SUCCESS] Read {chunk?.Length ?? 0} bytes from {path}");
        });

        _hubConnection.On<int, byte[]>("TunnelData", (streamId, data) =>
        {
            // Echo / relay tunnel data for SOCKS5 proxy
        });

        _hubConnection.On<int, bool, string>("TunnelStatus", (streamId, ok, msg) => { });
        _hubConnection.On<int>("TunnelClosed", (streamId) => { });
    }

    private byte[] ReadFileChunk(string path, long offset, int count)
    {
        try
        {
            if (!File.Exists(path)) return new byte[0];
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                fs.Seek(offset, SeekOrigin.Begin);
                byte[] buffer = new byte[count];
                int read = fs.Read(buffer, 0, count);
                if (read < count)
                {
                    Array.Resize(ref buffer, read);
                }
                return buffer;
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[WindowsCoreAgent] ReadFileChunk error: {ex.Message}");
            return new byte[0];
        }
    }

    private void OnDestroy()
    {
        if (_hubConnection != null)
        {
            _ = _hubConnection.DisposeAsync();
        }
    }
}
#endif
