// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyout.Classes.Utils;
using FluentFlyoutWPF.Classes.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Media;
using Windows.Media.Control;
using Wpf.Ui.Controls;

namespace FluentFlyoutWPF.Controls;

public partial class MediaSessionControl : UserControl
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    private static readonly TimeSpan SkipOffset = TimeSpan.FromSeconds(5);

    public IMediaPlaybackSession? Session { get; private set; }
    public string SessionId { get; private set; } = string.Empty;

    public event EventHandler<UIElement>? SessionSwitcherRequested;
    public event EventHandler? CloseFlyoutRequested;

    private string? _lastTitle;
    private string? _lastArtist;
    private int _lastThumbnailHash = -1;

    public MediaSessionControl()
    {
        InitializeComponent();
    }

    public static int ComputeNormalExtraWidth()
    {
        int extraWidth = SettingsManager.Current.RepeatEnabled ? 36 : 0;
        extraWidth += SettingsManager.Current.ShuffleEnabled ? 36 : 0;
        extraWidth += 72; // space for -5s and +5s skip buttons
        return Math.Max(extraWidth, 72);
    }

    public static double GetCardHeight(bool compact) => compact ? 56 : 102;

    public static double GetFlyoutWidth(bool compact) => compact ? 400 : 310 - 72 + ComputeNormalExtraWidth();

    public void ApplyLayout(bool isPrimaryCard)
    {
        bool compact = SettingsManager.Current.CompactLayout;
        bool repeat = SettingsManager.Current.RepeatEnabled;
        bool shuffle = SettingsManager.Current.ShuffleEnabled;
        bool centerText = SettingsManager.Current.CenterTitleArtist;
        bool alwaysDisplay = SettingsManager.Current.MediaFlyoutAlwaysDisplay;

        Height = GetCardHeight(compact);

        if (compact)
        {
            BodyStackPanel.Orientation = Orientation.Horizontal;
            BodyStackPanel.Width = 300;
            ControlsStackPanelContainer.Margin = new Thickness(2, 0, 0, 0);
            ControlsStackPanelContainer.Width = 104;
            ControlsStackPanelContainer.HorizontalAlignment = HorizontalAlignment.Left;
            ControlsStackPanel.HorizontalAlignment = HorizontalAlignment.Left;
            MediaIdButton.Visibility = Visibility.Collapsed;
            ControlSkipBack.Visibility = Visibility.Collapsed;
            ControlSkipForward.Visibility = Visibility.Collapsed;
            ControlRepeat.Visibility = Visibility.Collapsed;
            ControlShuffle.Visibility = Visibility.Collapsed;
            ControlClose.Visibility = alwaysDisplay && isPrimaryCard ? Visibility.Visible : Visibility.Collapsed;

            SongImageBorder.Margin = new Thickness(0);
            SongImageBorder.Width = 36;
            SongImageBorder.Height = 36;
            SongImagePlaceholder.FontSize = 20;

            SongInfoStackPanel.Margin = new Thickness(8, 0, 0, 0);
            SongInfoStackPanel.Width = alwaysDisplay && isPrimaryCard ? 192 : 228;
            if (alwaysDisplay && isPrimaryCard)
                ControlsStackPanelContainer.Width += 36;
        }
        else
        {
            int extraWidth = ComputeNormalExtraWidth();

            BodyStackPanel.Orientation = Orientation.Vertical;
            BodyStackPanel.Width = 194 - 72 + extraWidth;
            ControlsStackPanelContainer.Margin = new Thickness(8, 8, 0, 0);
            ControlsStackPanelContainer.Width = double.NaN;
            ControlsStackPanelContainer.HorizontalAlignment = HorizontalAlignment.Stretch;
            ControlsStackPanel.HorizontalAlignment = centerText ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            ControlClose.Visibility = Visibility.Collapsed;

            ControlRepeat.Visibility = repeat ? Visibility.Visible : Visibility.Collapsed;
            ControlShuffle.Visibility = shuffle ? Visibility.Visible : Visibility.Collapsed;
            MediaIdButton.Visibility = Visibility.Collapsed;

            SongImageBorder.Margin = new Thickness(0);
            SongImageBorder.Width = 82;
            SongImageBorder.Height = 82;
            SongImagePlaceholder.FontSize = 40;

            SongInfoStackPanel.Margin = new Thickness(8, 0, 0, 0);
            SongInfoStackPanel.Width = 182 - 72 + extraWidth;
        }

        var textAlignment = centerText ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        SongTitle.HorizontalAlignment = textAlignment;
        SongArtist.HorizontalAlignment = textAlignment;
    }

    public void ApplyPlaybackState(IMediaPlaybackSession session)
    {
        Session = session;
        SessionId = session.Id;

        SymbolPlayPause.Symbol = session.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
            ? SymbolRegular.Pause16
            : SymbolRegular.Play16;

        ControlPlayPause.IsEnabled = session.CanPlayPause;
        ControlPlayPause.Opacity = ControlPlayPause.IsEnabled ? 1 : 0.35;

        ControlBack.IsEnabled = session.CanPrevious;
        ControlBack.Opacity = ControlBack.IsEnabled ? 1 : 0.35;

        ControlForward.IsEnabled = session.CanNext;
        ControlForward.Opacity = ControlForward.IsEnabled ? 1 : 0.35;

        bool compact = SettingsManager.Current.CompactLayout;
        bool isSeekEnabled = !compact && session.CanSeek;
        ControlSkipBack.Visibility = isSeekEnabled ? Visibility.Visible : Visibility.Collapsed;
        ControlSkipForward.Visibility = isSeekEnabled ? Visibility.Visible : Visibility.Collapsed;

        if (SettingsManager.Current.RepeatEnabled && !compact)
        {
            ControlRepeat.Visibility = Visibility.Visible;
            ControlRepeat.IsEnabled = session.CanRepeat;
            ControlRepeat.Opacity = ControlRepeat.IsEnabled ? 1 : 0.35;
            switch (session.AutoRepeatMode)
            {
                case MediaPlaybackAutoRepeatMode.List:
                    SymbolRepeat.Symbol = SymbolRegular.ArrowRepeatAll24;
                    SymbolRepeat.Opacity = 1;
                    break;
                case MediaPlaybackAutoRepeatMode.Track:
                    SymbolRepeat.Symbol = SymbolRegular.ArrowRepeat124;
                    SymbolRepeat.Opacity = 1;
                    break;
                default:
                    SymbolRepeat.Symbol = SymbolRegular.ArrowRepeatAllOff24;
                    SymbolRepeat.Opacity = 0.5;
                    break;
            }
        }
        else
        {
            ControlRepeat.Visibility = Visibility.Collapsed;
        }

        if (SettingsManager.Current.ShuffleEnabled && !compact)
        {
            ControlShuffle.Visibility = Visibility.Visible;
            ControlShuffle.IsEnabled = session.CanShuffle;
            ControlShuffle.Opacity = ControlShuffle.IsEnabled ? 1 : 0.35;
            bool isShuffling = session.IsShuffleActive == true;
            SymbolShuffle.Symbol = isShuffling ? SymbolRegular.ArrowShuffle24 : SymbolRegular.ArrowShuffleOff24;
            SymbolShuffle.Opacity = isShuffling ? 1 : 0.5;
        }
        else
        {
            ControlShuffle.Visibility = Visibility.Collapsed;
        }

        MediaIdButton.Visibility = Visibility.Collapsed;
    }

    public void ApplyMediaMetadata(string title, string artist, BitmapImage? thumbnail, int thumbnailHash, SolidColorBrush? accentBrush)
    {
        if (!string.Equals(_lastTitle, title, StringComparison.Ordinal) ||
            !string.Equals(_lastArtist, artist, StringComparison.Ordinal))
        {
            _lastTitle = title;
            _lastArtist = artist;
            SongTitle.Text = title;
            SongArtist.Text = artist;

            SongInfoStackPanel.ToolTip = !string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(artist)
                ? $"{title}\n\n{artist}"
                : (!string.IsNullOrEmpty(title) ? title : artist);
        }

        if (_lastThumbnailHash != thumbnailHash)
        {
            _lastThumbnailHash = thumbnailHash;
            SongImage.ImageSource = thumbnail;
            SongImagePlaceholder.Visibility = thumbnail == null ? Visibility.Visible : Visibility.Collapsed;
        }

        if (SettingsManager.Current.UseAlbumArtAsAccentColor && accentBrush != null)
        {
            ControlPlayPause.Background = accentBrush;
        }
        else
        {
            ControlPlayPause.ClearValue(BackgroundProperty);
        }
    }

    public void ShowNoMediaState()
    {
        Session = null;
        SessionId = string.Empty;
        _lastTitle = "No media playing";
        _lastArtist = string.Empty;
        _lastThumbnailHash = 0;

        SongTitle.Text = _lastTitle;
        SongArtist.Text = string.Empty;
        SongImage.ImageSource = null;
        SongImagePlaceholder.Visibility = Visibility.Visible;
        SymbolPlayPause.Symbol = SymbolRegular.Stop16;
        ControlPlayPause.IsEnabled = false;
        ControlPlayPause.Opacity = 0.35;
        ControlBack.IsEnabled = ControlForward.IsEnabled = false;
        ControlBack.Opacity = ControlForward.Opacity = 0.35;
        ControlSkipBack.Visibility = ControlSkipForward.Visibility = Visibility.Collapsed;
        ControlRepeat.Visibility = ControlShuffle.Visibility = Visibility.Collapsed;
        MediaIdButton.Visibility = Visibility.Collapsed;
        SongInfoStackPanel.ToolTip = string.Empty;
    }

    private async void MediaIdButton_Click(object sender, RoutedEventArgs e)
    {
        if (!SettingsManager.Current.PlayerInfoEnabled || SettingsManager.Current.CompactLayout) return;
        e.Handled = true;

        if (e.OriginalSource is ToggleButton)
        {
            SessionSwitcherRequested?.Invoke(this, MediaIdButton);
            return;
        }

        if (Session is not { } session) return;
        string? title = _lastTitle;
        try { await session.TryActivateAsync(title); }
        catch (Exception ex) { Logger.Warn(ex, "Failed to activate media session"); }
    }

    private async void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Session is not { } session) return;
        try { await session.TrySkipPreviousAsync(); }
        catch (Exception ex) { Logger.Warn(ex, "Failed to skip previous"); }
    }

    private async void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (Session is not { } session) return;
        try { await session.TryTogglePlayPauseAsync(); }
        catch (Exception ex) { Logger.Warn(ex, "Failed to toggle play/pause"); }
    }

    private async void Forward_Click(object sender, RoutedEventArgs e)
    {
        if (Session is not { } session) return;
        try { await session.TrySkipNextAsync(); }
        catch (Exception ex) { Logger.Warn(ex, "Failed to skip next"); }
    }

    private void SkipBack_Click(object sender, RoutedEventArgs e) => _ = SeekByAsync(-SkipOffset);

    private void SkipForward_Click(object sender, RoutedEventArgs e) => _ = SeekByAsync(SkipOffset);

    private async Task SeekByAsync(TimeSpan offset)
    {
        if (Session is not { } session) return;
        try { await session.TrySeekByAsync(offset); }
        catch (Exception ex) { Logger.Warn(ex, "Failed to seek playback position"); }
    }

    private async void Repeat_Click(object sender, RoutedEventArgs e)
    {
        if (Session is not { } session) return;
        try
        {
            switch (session.AutoRepeatMode)
            {
                case MediaPlaybackAutoRepeatMode.None:
                    SymbolRepeat.Symbol = SymbolRegular.ArrowRepeatAll24;
                    await session.TryChangeAutoRepeatModeAsync(MediaPlaybackAutoRepeatMode.List);
                    break;
                case MediaPlaybackAutoRepeatMode.List:
                    SymbolRepeat.Symbol = SymbolRegular.ArrowRepeat124;
                    await session.TryChangeAutoRepeatModeAsync(MediaPlaybackAutoRepeatMode.Track);
                    break;
                case MediaPlaybackAutoRepeatMode.Track:
                    SymbolRepeat.Symbol = SymbolRegular.ArrowRepeatAllOff24;
                    await session.TryChangeAutoRepeatModeAsync(MediaPlaybackAutoRepeatMode.None);
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to change repeat mode");
        }
    }

    private async void Shuffle_Click(object sender, RoutedEventArgs e)
    {
        if (Session is not { } session) return;
        try
        {
            bool nextShuffle = session.IsShuffleActive != true;
            SymbolShuffle.Symbol = nextShuffle ? SymbolRegular.ArrowShuffle24 : SymbolRegular.ArrowShuffleOff24;
            await session.TryChangeShuffleActiveAsync(nextShuffle);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to toggle shuffle mode");
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseFlyoutRequested?.Invoke(this, EventArgs.Empty);
}
