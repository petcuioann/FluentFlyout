// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Utils;
using FluentFlyoutWPF.Classes.Media;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;
using Windows.Media;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace FluentFlyoutWPF.Classes.Services;

public sealed class ZenMediaPlaybackSession : IMediaPlaybackSession
{
    private readonly ZenBridgeService _bridge;

    public ZenMediaPlaybackSession(ZenBridgeService bridge, string connectionId, string windowKey, ZenBridgeSessionDto dto)
    {
        _bridge = bridge;
        ConnectionId = connectionId;
        WindowKey = windowKey;
        ControllerId = dto.Id;
        Id = $"zen:{windowKey}:{dto.Id}";
        Update(connectionId, dto);
    }

    public string ConnectionId { get; private set; }
    public string WindowKey { get; }
    public string ControllerId { get; }
    public string Id { get; }
    public string SourceAppName { get; private set; } = "Zen Browser";
    public string IconUrl { get; private set; } = string.Empty;
    public ImageSource? SourceAppIcon => _bridge.ResolveSourceIcon(IconUrl);
    public GlobalSystemMediaTransportControlsSessionPlaybackStatus PlaybackStatus { get; private set; }
    public bool IsPlaying => PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
    public bool CanPlayPause { get; private set; }
    public bool CanPrevious { get; private set; }
    public bool CanNext { get; private set; }
    public bool CanSeek { get; private set; }
    public bool CanRepeat => false;
    public bool CanShuffle => false;
    public MediaPlaybackAutoRepeatMode AutoRepeatMode => MediaPlaybackAutoRepeatMode.None;
    public bool? IsShuffleActive => null;
    public string Title { get; private set; } = string.Empty;
    public string Artist { get; private set; } = string.Empty;
    public string ArtworkUrl { get; private set; } = string.Empty;

    public void Update(string connectionId, ZenBridgeSessionDto dto)
    {
        if (!string.Equals(connectionId, "http", StringComparison.Ordinal) || string.IsNullOrEmpty(ConnectionId))
            ConnectionId = connectionId;
        SourceAppName = string.IsNullOrWhiteSpace(dto.SourceName) ? "Zen Browser" : dto.SourceName;
        IconUrl = dto.IconUrl ?? string.Empty;
        Title = dto.Title ?? string.Empty;
        Artist = dto.Artist ?? string.Empty;
        ArtworkUrl = dto.ArtworkUrl ?? string.Empty;
        PlaybackStatus = dto.IsPlaying
            ? GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
            : GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused;
        CanPlayPause = dto.CanPlayPause;
        CanPrevious = dto.CanPrevious;
        CanNext = dto.CanNext;
        CanSeek = dto.CanSeek;
    }

    public Task<(string Title, string Artist, IRandomAccessStreamReference? SmtcThumbnail, string? ArtworkUrl)> GetMetadataAsync(CancellationToken token = default)
        => Task.FromResult<(string, string, IRandomAccessStreamReference?, string?)>((Title, Artist, null, ArtworkUrl));

    public Task<bool> TryTogglePlayPauseAsync() => _bridge.SendCommandAsync(ConnectionId, ControllerId, "toggle");
    public Task<bool> TrySkipPreviousAsync() => _bridge.SendCommandAsync(ConnectionId, ControllerId, "prev");
    public Task<bool> TrySkipNextAsync() => _bridge.SendCommandAsync(ConnectionId, ControllerId, "next");
    public Task<bool> TrySeekByAsync(TimeSpan offset) => _bridge.SendCommandAsync(ConnectionId, ControllerId, "seekBy", offset.TotalSeconds);
    public Task<bool> TryChangeAutoRepeatModeAsync(MediaPlaybackAutoRepeatMode mode) => Task.FromResult(false);
    public Task<bool> TryChangeShuffleActiveAsync(bool active) => Task.FromResult(false);
    public Task<bool> TryActivateAsync(string? currentTitle = null) => _bridge.SendCommandAsync(ConnectionId, ControllerId, "activate");
}

public sealed class ZenBridgeSessionDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("sourceName")] public string SourceName { get; set; } = string.Empty;
    [JsonPropertyName("iconUrl")] public string IconUrl { get; set; } = string.Empty;
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
    [JsonPropertyName("artist")] public string Artist { get; set; } = string.Empty;
    [JsonPropertyName("artworkUrl")] public string ArtworkUrl { get; set; } = string.Empty;
    [JsonPropertyName("isPlaying")] public bool IsPlaying { get; set; }
    [JsonPropertyName("canPlayPause")] public bool CanPlayPause { get; set; }
    [JsonPropertyName("canPrevious")] public bool CanPrevious { get; set; }
    [JsonPropertyName("canNext")] public bool CanNext { get; set; }
    [JsonPropertyName("canSeek")] public bool CanSeek { get; set; }
}

internal sealed class ZenBridgeSyncPayload
{
    [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
    [JsonPropertyName("windowId")] public string WindowId { get; set; } = string.Empty;
    [JsonPropertyName("sessions")] public List<ZenBridgeSessionDto> Sessions { get; set; } = [];
}

public sealed class ZenBridgeService : IDisposable
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    private const string Prefix = "http://127.0.0.1:27415/";
    private const int MaxFrameBytes = 65536;

    private readonly CancellationTokenSource _cts = new();
    private readonly HttpListener _listener = new();
    private readonly ConcurrentDictionary<string, WebSocket> _connections = new();
    private readonly ConcurrentDictionary<string, ZenMediaPlaybackSession> _sessions = new();
    private readonly ConcurrentDictionary<string, ImageSource?> _iconCache = new();
    private readonly ConcurrentQueue<object> _httpPendingCommands = new();
    private readonly Lock _syncLock = new();

    public event Action<IReadOnlyList<string>>? SessionsChanged;

    public void Start()
    {
        if (!HttpListener.IsSupported) return;
        try
        {
            _listener.Prefixes.Add(Prefix);
            _listener.Start();
            _ = AcceptLoopAsync(_cts.Token);
            Logger.Info("ZenBridgeService listening on " + Prefix);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "ZenBridgeService failed to start");
        }
    }

    public IReadOnlyList<ZenMediaPlaybackSession> GetSessions()
        => [.. _sessions.Values];

    internal ImageSource? ResolveSourceIcon(string iconUrl)
    {
        if (!string.IsNullOrEmpty(iconUrl))
        {
            return _iconCache.GetOrAdd(iconUrl, static url =>
            {
                if (url.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
                    return BitmapHelper.DecodeDataUrl(url, 32);
                return null;
            });
        }

        return MediaPlayerData.GetAndCacheMediaPlayerData("zen.exe").Item2;
    }

    public async Task<bool> SendCommandAsync(string connectionId, string controllerId, string action, double? offsetSeconds = null)
    {
        object cmdObj = offsetSeconds.HasValue
            ? new { action, id = controllerId, offsetSeconds = offsetSeconds.Value }
            : new { action, id = controllerId };

        if (_connections.TryGetValue(connectionId, out var socket) && socket.State == WebSocketState.Open)
        {
            try
            {
                string payload = JsonSerializer.Serialize(cmdObj);
                byte[] bytes = Encoding.UTF8.GetBytes(payload);
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, _cts.Token);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Failed sending command to Zen bridge");
            }
        }

        _httpPendingCommands.Enqueue(cmdObj);
        return true;
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _listener.IsListening)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                if (!context.Request.IsLocal)
                {
                    context.Response.StatusCode = 403;
                    context.Response.Close();
                    continue;
                }

                if (!context.Request.IsWebSocketRequest)
                {
                    string rawUrl = context.Request.RawUrl ?? string.Empty;
                    if (rawUrl.StartsWith("/diag", StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Info("ZenBridge diag: " + rawUrl);
                    }
                    else if (string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase) &&
                             rawUrl.StartsWith("/sync", StringComparison.OrdinalIgnoreCase))
                    {
                        using StreamReader reader = new(context.Request.InputStream, Encoding.UTF8);
                        string body = await reader.ReadToEndAsync(token);
                        ProcessSyncMessage("http", body);

                        List<object> commands = [];
                        while (_httpPendingCommands.TryDequeue(out var cmd))
                            commands.Add(cmd);

                        byte[] respBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { commands }));
                        context.Response.StatusCode = 200;
                        context.Response.ContentType = "application/json";
                        await context.Response.OutputStream.WriteAsync(respBytes, token);
                        context.Response.Close();
                        continue;
                    }
                    context.Response.StatusCode = 200;
                    context.Response.Close();
                    continue;
                }

                var wsContext = await context.AcceptWebSocketAsync(null);
                string connectionId = Guid.NewGuid().ToString("N")[..8];
                Logger.Info("ZenBridgeService WebSocket connected: " + connectionId);
                _ = HandleClientAsync(connectionId, wsContext.WebSocket, token);
            }
            catch (HttpListenerException) when (token.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                Logger.Warn(ex, "ZenBridgeService accept error");
            }
        }
    }

    private async Task HandleClientAsync(string connectionId, WebSocket socket, CancellationToken token)
    {
        _connections[connectionId] = socket;
        byte[] buffer = new byte[16384];
        using MemoryStream ms = new();

        try
        {
            while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                ms.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, token);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return;
                    if (ms.Length + result.Count > MaxFrameBytes)
                        return;
                    ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var json = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                    ProcessSyncMessage(connectionId, json);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (Exception ex)
        {
            Logger.Warn(ex, "ZenBridgeService client error");
        }
        finally
        {
            _connections.TryRemove(connectionId, out _);
            RemoveConnectionSessions(connectionId);
            socket.Dispose();
        }
    }

    private void ProcessSyncMessage(string connectionId, string json)
    {
        var payload = JsonSerializer.Deserialize<ZenBridgeSyncPayload>(json);
        if (payload == null || !string.Equals(payload.Type, "sync", StringComparison.Ordinal))
            return;

        string windowKey = !string.IsNullOrEmpty(payload.WindowId) ? payload.WindowId : connectionId;
        List<string> removedIds = [];
        List<string> newlyPlayingIds = [];

        lock (_syncLock)
        {
            var incomingKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var dto in payload.Sessions)
            {
                if (string.IsNullOrEmpty(dto.Id)) continue;
                string key = $"zen:{windowKey}:{dto.Id}";
                incomingKeys.Add(key);

                if (_sessions.TryGetValue(key, out var existing))
                {
                    bool wasPlaying = existing.IsPlaying;
                    existing.Update(connectionId, dto);
                    if (!wasPlaying && existing.IsPlaying)
                        newlyPlayingIds.Add(key);
                }
                else
                {
                    var created = new ZenMediaPlaybackSession(this, connectionId, windowKey, dto);
                    _sessions[key] = created;
                    if (created.IsPlaying)
                        newlyPlayingIds.Add(key);
                }
            }

            foreach (var existingKey in _sessions.Keys.Where(k => k.StartsWith($"zen:{windowKey}:", StringComparison.Ordinal)).ToList())
            {
                if (!incomingKeys.Contains(existingKey) && _sessions.TryRemove(existingKey, out _))
                    removedIds.Add(existingKey);
            }
        }

        if (newlyPlayingIds.Count > 0 || removedIds.Count > 0)
            SessionsChanged?.Invoke(newlyPlayingIds);
        else
            SessionsChanged?.Invoke([]);
    }

    private void RemoveConnectionSessions(string connectionId)
    {
        bool changed = false;
        lock (_syncLock)
        {
            foreach (var pair in _sessions.Where(kv => string.Equals(kv.Value.ConnectionId, connectionId, StringComparison.Ordinal)).ToList())
            {
                if (_sessions.TryRemove(pair.Key, out _))
                    changed = true;
            }
        }
        if (changed) SessionsChanged?.Invoke([]);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Close(); } catch { }
        foreach (var socket in _connections.Values)
        {
            try { socket.Dispose(); } catch { }
        }
        _connections.Clear();
        _sessions.Clear();
        _cts.Dispose();
    }
}
