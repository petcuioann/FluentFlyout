// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Classes.Media;
using FluentFlyoutWPF.Classes.Services;
using System.Windows;
using System.Windows.Media;

namespace FluentFlyoutWPF;

public partial class MainWindow
{
    public IReadOnlyList<IMediaPlaybackSession> GetSwitchableSessions() => GetAllowedPlaybackSessions();

    public void PinSession(string? sessionId)
    {
        SettingsManager.Current.PinnedSessionId = sessionId ?? string.Empty;
        if (!string.IsNullOrEmpty(sessionId))
        {
            PromoteSessionHistory(sessionId);
        }
        RefreshFilteredMedia();
    }

    internal void ShowSessionSwitcherMenu(UIElement placementTarget)
    {
        var menu = new System.Windows.Controls.ContextMenu
        {
            PlacementTarget = placementTarget,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Top
        };

        PopulateSessionSwitcherMenu(menu);
        menu.IsOpen = true;
    }

    internal void PopulateSessionSwitcherMenu(System.Windows.Controls.ItemsControl menu)
    {
        var sessions = GetSwitchableSessions();
        string pinnedId = SettingsManager.Current.PinnedSessionId;
        string autoText = TryFindResource("SessionSwitcherAuto") as string ?? string.Empty;

        // Clear existing items and events
        foreach (var item in menu.Items.OfType<System.Windows.Controls.MenuItem>())
        {
            item.Click -= SessionMenuItem_Click;
        }
        menu.Items.Clear();

        var autoItem = new System.Windows.Controls.MenuItem
        {
            Header = autoText,
            IsCheckable = true,
            IsChecked = string.IsNullOrEmpty(pinnedId),
            Tag = string.Empty
        };
        autoItem.Click += SessionMenuItem_Click;
        menu.Items.Add(autoItem);
        menu.Items.Add(new System.Windows.Controls.Separator());

        foreach (var session in sessions)
        {
            string sessionId = session.Id;
            string appName = session.SourceAppName;
            ImageSource? appIcon = session.SourceAppIcon;
            bool isPlaying = session.IsPlaying;

            string trackLabel = string.Empty;
            try
            {
                if (session is ZenMediaPlaybackSession zenSession)
                {
                    if (!string.IsNullOrWhiteSpace(zenSession.Title) || !string.IsNullOrWhiteSpace(zenSession.Artist))
                        trackLabel = string.IsNullOrWhiteSpace(zenSession.Artist) ? zenSession.Title : $"{zenSession.Title} - {zenSession.Artist}";
                }
                else if (session is VlcMediaPlaybackSession vlcSession)
                {
                    if (!string.IsNullOrWhiteSpace(vlcSession.Title) || !string.IsNullOrWhiteSpace(vlcSession.Artist))
                        trackLabel = string.IsNullOrWhiteSpace(vlcSession.Artist) ? vlcSession.Title : $"{vlcSession.Title} - {vlcSession.Artist}";
                }
                else if (session is SmtcMediaPlaybackSession smtcSession && smtcSession.RawSession.ControlSession != null)
                {
                    var props = TryGetMediaProperties(smtcSession.RawSession.ControlSession);
                    if (props != null && (!string.IsNullOrWhiteSpace(props.Title) || !string.IsNullOrWhiteSpace(props.Artist)))
                        trackLabel = string.IsNullOrWhiteSpace(props.Artist) ? props.Title : $"{props.Title} - {props.Artist}";
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Failed to read properties for session {SessionId}", sessionId);
            }

            var item = new System.Windows.Controls.MenuItem
            {
                Header = appName,
                ToolTip = string.IsNullOrEmpty(trackLabel) ? sessionId : $"{trackLabel}\n{sessionId}",
                IsCheckable = true,
                IsChecked = sessionId == pinnedId,
                Tag = sessionId,
                FontWeight = isPlaying ? FontWeights.Bold : FontWeights.Normal
            };

            if (appIcon != null)
            {
                item.Icon = new System.Windows.Controls.Image
                {
                    Source = appIcon,
                    Width = 16,
                    Height = 16
                };
            }

            item.Click += SessionMenuItem_Click;
            menu.Items.Add(item);
        }
    }

    private void SessionMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { Tag: string sessionId }) return;
        PinSession(string.IsNullOrEmpty(sessionId) ? null : sessionId);
    }
}