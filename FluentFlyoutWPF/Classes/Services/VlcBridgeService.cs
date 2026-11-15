// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Utils;
using FluentFlyoutWPF.Classes.Media;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using Windows.Media;
using Windows.Media.Control;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace FluentFlyoutWPF.Classes.Services;

public sealed class VlcMediaPlaybackSession : IMediaPlaybackSession
{
    private readonly VlcBridgeService _service;

    public VlcMediaPlaybackSession(VlcBridgeService service)
    {
        _service = service;
    }

    public string Id => "vlc:local";
    public string SourceAppName => "VLC media player";
    public ImageSource? SourceAppIcon => MediaPlayerData.GetAndCacheMediaPlayerData("vlc.exe").Item2;
    public GlobalSystemMediaTransportControlsSessionPlaybackStatus PlaybackStatus { get; internal set; } =
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused;
    public bool IsPlaying => PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
    public bool CanPlayPause => true;
    public bool CanPrevious => true;
    public bool CanNext => true;
    public bool CanSeek { get; internal set; } = true;
    public bool CanRepeat => true;
    public bool CanShuffle => true;
    public MediaPlaybackAutoRepeatMode AutoRepeatMode { get; internal set; } = MediaPlaybackAutoRepeatMode.None;
    public bool? IsShuffleActive { get; internal set; } = false;

    public string Title { get; internal set; } = string.Empty;
    public string Artist { get; internal set; } = string.Empty;
    public string ArtworkUrl { get; internal set; } = string.Empty;

    public Task<(string Title, string Artist, IRandomAccessStreamReference? SmtcThumbnail, string? ArtworkUrl)> GetMetadataAsync(CancellationToken token = default)
        => Task.FromResult<(string, string, IRandomAccessStreamReference?, string?)>((Title, Artist, null, ArtworkUrl));

    public Task<bool> TryTogglePlayPauseAsync() => _service.SendCommandAsync("pl_pause");
    public Task<bool> TrySkipPreviousAsync() => _service.SendCommandAsync("pl_previous");
    public Task<bool> TrySkipNextAsync() => _service.SendCommandAsync("pl_next");

    public Task<bool> TrySeekByAsync(TimeSpan offset)
    {
        int sec = (int)Math.Round(offset.TotalSeconds);
        string val = sec >= 0 ? $"%2B{sec}S" : $"{sec}S";
        return _service.SendCommandAsync($"seek&val={val}");
    }

    public Task<bool> TryChangeAutoRepeatModeAsync(MediaPlaybackAutoRepeatMode mode)
    {
        return mode switch
        {
            MediaPlaybackAutoRepeatMode.List => _service.SendCommandAsync("pl_loop"),
            MediaPlaybackAutoRepeatMode.Track => _service.SendCommandAsync("pl_repeat"),
            _ => _service.SendCommandAsync("pl_repeat")
        };
    }

    public Task<bool> TryChangeShuffleActiveAsync(bool active) => _service.SendCommandAsync("pl_random");

    public Task<bool> TryActivateAsync(string? currentTitle = null)
        => Task.Run(() => MediaPlayerData.TryActivateMediaPlayer("vlc.exe", currentTitle));
}

public sealed class VlcBridgeService : IDisposable
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    private const string BaseUrl = "http://127.0.0.1:42129/requests/";

    private readonly CancellationTokenSource _cts = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMilliseconds(700) };
    private readonly ConcurrentDictionary<string, string> _fileThumbnailDataUrlCache = new(StringComparer.OrdinalIgnoreCase);
    private VlcMediaPlaybackSession? _session;

    public event Action<string>? SessionChanged;

    public VlcBridgeService()
    {
        string token = Convert.ToBase64String(Encoding.ASCII.GetBytes(":fluentflyout"));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
    }

    public void Start()
    {
        _ = PollLoopAsync(_cts.Token);
    }

    public VlcMediaPlaybackSession? GetSession() => _session;

    public async Task<bool> SendCommandAsync(string commandQuery)
    {
        try
        {
            using var resp = await _http.GetAsync($"{BaseUrl}status.json?command={commandQuery}", _cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return false;
            var json = await resp.Content.ReadAsStringAsync(_cts.Token).ConfigureAwait(false);
            await UpdateFromStatusJsonAsync(json, _cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "VLC command failed");
            return false;
        }
    }

    private async Task PollLoopAsync(CancellationToken token)
    {
        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(1000));
        while (!token.IsCancellationRequested && await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
        {
            try
            {
                var procs = Process.GetProcessesByName("vlc");
                bool vlcRunning = procs.Length > 0;
                foreach (var p in procs) p.Dispose();

                if (!vlcRunning)
                {
                    if (_session != null)
                    {
                        _session = null;
                        SessionChanged?.Invoke(string.Empty);
                    }
                    continue;
                }

                using var resp = await _http.GetAsync($"{BaseUrl}status.json", token).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    continue;

                string json = await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                await UpdateFromStatusJsonAsync(json, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // VLC starting up or closing
            }
        }
    }

    private async Task UpdateFromStatusJsonAsync(string json, CancellationToken token)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string state = root.TryGetProperty("state", out var stateProp) ? stateProp.GetString() ?? "stopped" : "stopped";
        if (string.Equals(state, "stopped", StringComparison.OrdinalIgnoreCase))
        {
            if (_session != null)
            {
                _session = null;
                SessionChanged?.Invoke(string.Empty);
            }
            return;
        }

        string title = string.Empty;
        string filename = string.Empty;
        string artist = string.Empty;
        string artworkUrl = string.Empty;

        if (root.TryGetProperty("information", out var info) &&
            info.TryGetProperty("category", out var cat) &&
            cat.TryGetProperty("meta", out var meta))
        {
            if (meta.TryGetProperty("title", out var t)) title = t.GetString() ?? string.Empty;
            if (meta.TryGetProperty("filename", out var f)) filename = f.GetString() ?? string.Empty;
            if (meta.TryGetProperty("artist", out var a)) artist = a.GetString() ?? string.Empty;
            if (meta.TryGetProperty("artwork_url", out var art)) artworkUrl = art.GetString() ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(title))
            title = Path.GetFileNameWithoutExtension(filename);
        if (string.IsNullOrWhiteSpace(title))
            title = "VLC media player";

        double length = root.TryGetProperty("length", out var lenProp) && lenProp.TryGetDouble(out var l) ? l : 0;
        bool loop = root.TryGetProperty("loop", out var loopProp) && loopProp.ValueKind == JsonValueKind.True;
        bool repeat = root.TryGetProperty("repeat", out var repProp) && repProp.ValueKind == JsonValueKind.True;
        bool random = root.TryGetProperty("random", out var rndProp) && rndProp.ValueKind == JsonValueKind.True;

        string resolvedArt = await ResolveArtworkDataUrlAsync(artworkUrl, filename, token).ConfigureAwait(false);
        var newStatus = string.Equals(state, "playing", StringComparison.OrdinalIgnoreCase)
            ? GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
            : GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused;
        var repeatMode = repeat
            ? MediaPlaybackAutoRepeatMode.Track
            : (loop ? MediaPlaybackAutoRepeatMode.List : MediaPlaybackAutoRepeatMode.None);

        var session = _session ?? new VlcMediaPlaybackSession(this);
        bool wasPlaying = _session?.IsPlaying == true;
        bool changed = _session == null ||
                       session.PlaybackStatus != newStatus ||
                       !string.Equals(session.Title, title, StringComparison.Ordinal) ||
                       !string.Equals(session.Artist, artist, StringComparison.Ordinal) ||
                       !string.Equals(session.ArtworkUrl, resolvedArt, StringComparison.Ordinal) ||
                       session.AutoRepeatMode != repeatMode ||
                       session.IsShuffleActive != random;

        session.Title = title;
        session.Artist = artist;
        session.ArtworkUrl = resolvedArt;
        session.PlaybackStatus = newStatus;
        session.CanSeek = length > 0;
        session.AutoRepeatMode = repeatMode;
        session.IsShuffleActive = random;
        _session = session;

        if (changed)
        {
            SessionChanged?.Invoke(!wasPlaying && session.IsPlaying ? session.Id : string.Empty);
        }
    }

    private async Task<string> ResolveArtworkDataUrlAsync(string artworkUrl, string filename, CancellationToken token)
    {
        string cacheKey = !string.IsNullOrEmpty(artworkUrl) ? artworkUrl : filename;
        if (string.IsNullOrEmpty(cacheKey)) return string.Empty;

        if (_fileThumbnailDataUrlCache.TryGetValue(cacheKey, out var cached))
            return cached;

        try
        {
            string? localPath = null;
            if (!string.IsNullOrEmpty(artworkUrl) && Uri.TryCreate(artworkUrl, UriKind.Absolute, out var artUri) && artUri.IsFile)
            {
                localPath = artUri.LocalPath;
            }

            if (string.IsNullOrEmpty(localPath) || !File.Exists(localPath))
            {
                localPath = await TryGetCurrentPlaylistFilePathAsync(token).ConfigureAwait(false);
            }

            if (string.IsNullOrEmpty(localPath) || !File.Exists(localPath))
                return string.Empty;

            var file = await StorageFile.GetFileFromPathAsync(localPath);
            using var thumb = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 256, ThumbnailOptions.UseCurrentScale);
            if (thumb == null || thumb.Size == 0)
                return string.Empty;

            using var stream = thumb.AsStreamForRead();
            using MemoryStream ms = new();
            await stream.CopyToAsync(ms, token).ConfigureAwait(false);
            string dataUrl = "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
            _fileThumbnailDataUrlCache[cacheKey] = dataUrl;
            return dataUrl;
        }
        catch
        {
            return string.Empty;
        }
    }

    private async Task<string?> TryGetCurrentPlaylistFilePathAsync(CancellationToken token)
    {
        try
        {
            using var resp = await _http.GetAsync($"{BaseUrl}playlist.json", token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            string json = await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            string? uriStr = FindCurrentUri(doc.RootElement);
            if (!string.IsNullOrEmpty(uriStr) && Uri.TryCreate(uriStr, UriKind.Absolute, out var uri) && uri.IsFile)
                return uri.LocalPath;
        }
        catch { }
        return null;
    }

    private static string? FindCurrentUri(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            if (el.TryGetProperty("current", out _) && el.TryGetProperty("uri", out var uriProp))
                return uriProp.GetString();

            if (el.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in children.EnumerateArray())
                {
                    var found = FindCurrentUri(child);
                    if (found != null) return found;
                }
            }
        }
        return null;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _http.Dispose();
        _cts.Dispose();
    }
}
