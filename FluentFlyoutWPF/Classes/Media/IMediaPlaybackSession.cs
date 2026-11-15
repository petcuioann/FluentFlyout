// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Utils;
using System.Runtime.InteropServices;
using System.Windows.Media;
using Windows.Media;
using Windows.Media.Control;
using Windows.Storage.Streams;
using static WindowsMediaController.MediaManager;

namespace FluentFlyoutWPF.Classes.Media;

public interface IMediaPlaybackSession
{
    string Id { get; }
    string SourceAppName { get; }
    ImageSource? SourceAppIcon { get; }
    GlobalSystemMediaTransportControlsSessionPlaybackStatus PlaybackStatus { get; }
    bool IsPlaying => PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
    bool CanPlayPause { get; }
    bool CanPrevious { get; }
    bool CanNext { get; }
    bool CanSeek { get; }
    bool CanRepeat { get; }
    bool CanShuffle { get; }
    MediaPlaybackAutoRepeatMode AutoRepeatMode { get; }
    bool? IsShuffleActive { get; }

    Task<(string Title, string Artist, IRandomAccessStreamReference? SmtcThumbnail, string? ArtworkUrl)> GetMetadataAsync(CancellationToken token = default);
    Task<bool> TryTogglePlayPauseAsync();
    Task<bool> TrySkipPreviousAsync();
    Task<bool> TrySkipNextAsync();
    Task<bool> TrySeekByAsync(TimeSpan offset);
    Task<bool> TryChangeAutoRepeatModeAsync(MediaPlaybackAutoRepeatMode mode);
    Task<bool> TryChangeShuffleActiveAsync(bool active);
    Task<bool> TryActivateAsync(string? currentTitle = null);
}

public sealed class SmtcMediaPlaybackSession(MediaSession session) : IMediaPlaybackSession
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    public MediaSession RawSession { get; } = session;

    public string Id => RawSession.Id ?? string.Empty;

    public string SourceAppName => MediaPlayerData.GetAndCacheMediaPlayerData(Id).Item1;

    public ImageSource? SourceAppIcon => MediaPlayerData.GetAndCacheMediaPlayerData(Id).Item2;

    private GlobalSystemMediaTransportControlsSessionPlaybackInfo? PlaybackInfo
        => RawSession.ControlSession?.GetPlaybackInfo();

    public GlobalSystemMediaTransportControlsSessionPlaybackStatus PlaybackStatus
        => PlaybackInfo?.PlaybackStatus ?? GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;

    public bool CanPlayPause
        => PlaybackInfo?.Controls is { } c && (c.IsPlayEnabled || c.IsPauseEnabled);

    public bool CanPrevious => PlaybackInfo?.Controls.IsPreviousEnabled ?? false;

    public bool CanNext => PlaybackInfo?.Controls.IsNextEnabled ?? false;

    public bool CanSeek => PlaybackInfo?.Controls.IsPlaybackPositionEnabled ?? false;

    public bool CanRepeat => PlaybackInfo?.Controls.IsRepeatEnabled ?? false;

    public bool CanShuffle => PlaybackInfo?.Controls.IsShuffleEnabled ?? false;

    public MediaPlaybackAutoRepeatMode AutoRepeatMode
        => PlaybackInfo?.AutoRepeatMode ?? MediaPlaybackAutoRepeatMode.None;

    public bool? IsShuffleActive => PlaybackInfo?.IsShuffleActive;

    public async Task<(string Title, string Artist, IRandomAccessStreamReference? SmtcThumbnail, string? ArtworkUrl)> GetMetadataAsync(CancellationToken token = default)
    {
        if (RawSession.ControlSession is not { } controlSession)
            return (string.Empty, string.Empty, null, null);

        try
        {
            var props = await controlSession.TryGetMediaPropertiesAsync();
            if (props == null || token.IsCancellationRequested)
                return (string.Empty, string.Empty, null, null);

            return (props.Title ?? string.Empty, props.Artist ?? string.Empty, props.Thumbnail, null);
        }
        catch (COMException)
        {
            return (string.Empty, string.Empty, null, null);
        }
    }

    public async Task<bool> TryTogglePlayPauseAsync()
    {
        if (RawSession.ControlSession is not { } controlSession) return false;
        try { return await controlSession.TryTogglePlayPauseAsync(); }
        catch (Exception ex) { Logger.Warn(ex, "SMTC toggle play/pause failed"); return false; }
    }

    public async Task<bool> TrySkipPreviousAsync()
    {
        if (RawSession.ControlSession is not { } controlSession) return false;
        try { return await controlSession.TrySkipPreviousAsync(); }
        catch (Exception ex) { Logger.Warn(ex, "SMTC skip previous failed"); return false; }
    }

    public async Task<bool> TrySkipNextAsync()
    {
        if (RawSession.ControlSession is not { } controlSession) return false;
        try { return await controlSession.TrySkipNextAsync(); }
        catch (Exception ex) { Logger.Warn(ex, "SMTC skip next failed"); return false; }
    }

    public async Task<bool> TrySeekByAsync(TimeSpan offset)
    {
        if (RawSession.ControlSession is not { } controlSession) return false;
        try
        {
            var timeline = controlSession.GetTimelineProperties();
            if (timeline == null) return false;

            TimeSpan currentPos = timeline.Position;
            if (PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing &&
                timeline.LastUpdatedTime != DateTimeOffset.MinValue)
            {
                currentPos += DateTimeOffset.UtcNow - timeline.LastUpdatedTime;
            }

            long minTicks = timeline.StartTime.Ticks;
            long maxTicks = timeline.EndTime.Ticks > minTicks ? timeline.EndTime.Ticks : long.MaxValue;
            long targetTicks = Math.Clamp(currentPos.Ticks + offset.Ticks, minTicks, maxTicks);
            return await controlSession.TryChangePlaybackPositionAsync(targetTicks);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "SMTC seek failed");
            return false;
        }
    }

    public async Task<bool> TryChangeAutoRepeatModeAsync(MediaPlaybackAutoRepeatMode mode)
    {
        if (RawSession.ControlSession is not { } controlSession) return false;
        try { return await controlSession.TryChangeAutoRepeatModeAsync(mode); }
        catch (Exception ex) { Logger.Warn(ex, "SMTC repeat change failed"); return false; }
    }

    public async Task<bool> TryChangeShuffleActiveAsync(bool active)
    {
        if (RawSession.ControlSession is not { } controlSession) return false;
        try { return await controlSession.TryChangeShuffleActiveAsync(active); }
        catch (Exception ex) { Logger.Warn(ex, "SMTC shuffle change failed"); return false; }
    }

    public Task<bool> TryActivateAsync(string? currentTitle = null)
        => Task.Run(() => MediaPlayerData.TryActivateMediaPlayer(Id, currentTitle));
}
