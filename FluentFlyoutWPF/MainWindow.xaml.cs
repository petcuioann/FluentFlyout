// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes;
using FluentFlyout.Classes.Settings;
using FluentFlyout.Classes.Utils;
using FluentFlyout.Controls;
using FluentFlyout.Windows;
using FluentFlyoutWPF.Classes;
using FluentFlyoutWPF.Classes.Media;
using FluentFlyoutWPF.Classes.Services;
using FluentFlyoutWPF.Classes.Utils;
using FluentFlyoutWPF.Controls;
using FluentFlyoutWPF.ViewModels;
using FluentFlyoutWPF.Windows;
using MicaWPF.Controls;
using MicaWPF.Core.Extensions;
using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Windows.ApplicationModel;
using Windows.Media.Control;
using static FluentFlyout.Classes.NativeMethods;
using static FluentFlyoutWPF.Classes.Utils.MonitorUtil;
using static WindowsMediaController.MediaManager;


namespace FluentFlyoutWPF;

public partial class MainWindow : MicaWindow
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private int WM_TASKBARCREATED, WM_SHELLHOOK;

    private IntPtr _hookId = IntPtr.Zero;
    private LowLevelKeyboardProc _hookProc;

    private CancellationTokenSource cts; // to close the flyout after a certain time
    private long _lastFlyoutTime = 0;

    public readonly WindowsMediaController.MediaManager mediaManager = new();
    private readonly ZenBridgeService _zenBridgeService = new();
    private readonly VlcBridgeService _vlcBridgeService = new();

    // for detecting changes in settings (lazy way)
    private int _position = SettingsManager.Current.Position;
    private bool _layout = SettingsManager.Current.CompactLayout;
    private bool _repeatEnabled = SettingsManager.Current.RepeatEnabled;
    private bool _shuffleEnabled = SettingsManager.Current.ShuffleEnabled;
    private bool _playerInfoEnabled = SettingsManager.Current.PlayerInfoEnabled;
    private bool _centerTitleArtist = SettingsManager.Current.CenterTitleArtist;
    private bool _alwaysDisplay = SettingsManager.Current.MediaFlyoutAlwaysDisplay;
    private bool _acrylicEnabled = false; // default off to handle initialization
    private int _themeOption = SettingsManager.Current.AppTheme;

    static Mutex singleton = new Mutex(true, "FluentFlyout"); // to prevent multiple instances of the app
    private bool _volumeShortcutKeyDown;
    private NextUpWindow? nextUpWindow = null; // to prevent multiple instances of NextUpWindow
    private string currentTitle = ""; // to prevent NextUpWindow from showing the same song

    private bool _isHiding = true;

    private LockWindow? lockWindow;

    private readonly Lock _sessionHistoryLock = new();
    private readonly List<string> _sessionHistory = [];
    private readonly HashSet<string> _knownRawSessionIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _knownPlayingIds = new(StringComparer.Ordinal);
    private string? _lastPausedPlaceholderId;
    private readonly Dictionary<string, MediaSessionControl> _sessionControls = [];
    private MediaSessionControl? _noMediaControl;
    private CancellationTokenSource? _flyoutMetadataCts;
    private string? _lastBlurSessionId;
    private int _lastBlurThumbnailHash = -1;
    private int _lastBackgroundBlurStyle = -1;

    internal TaskbarWindow? taskbarWindow;

    private VolumeMixerWindow? volumeMixerWindow;

    private readonly DispatcherTimer _displayRefreshTimer;
    private string _pendingDisplayRefreshReason = "Unknown";
    private bool _displayRefreshInProgress;
    private bool _isCleaningUp;

    internal static volatile bool ExplorerRestarting = false;

    public MainWindow()
    {
        DataContext = SettingsManager.Current;
        WindowHelper.SetNoActivate(this); // prevents some fullscreen apps from minimizing
        InitializeComponent();
        WindowHelper.SetTopmost(this); // more prevention of fullscreen apps minimizing

        _displayRefreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(1000)
        };
        _displayRefreshTimer.Tick += DisplayRefreshTimer_Tick;

        if (!singleton.WaitOne(TimeSpan.Zero, true)) // if another instance is already running, close this one
        {
            // Signal the existing instance to open settings
            Task.Run(() =>
            {
                try
                {
                    using (EventWaitHandle settingsEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "FluentFlyout_OpenSettings"))
                    {
                        settingsEvent.Set();
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Failed to signal existing instance");
                }
            });

            Environment.Exit(0);
        }

        Logger.Info("Starting FluentFlyout MainWindow");

        // in the existing instance, listen for the signal to open settings
        Task.Run(() =>
        {
            try
            {
                using (EventWaitHandle settingsEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "FluentFlyout_OpenSettings"))
                {
                    while (true)
                    {
                        settingsEvent.WaitOne();
                        Application.Current.Dispatcher.Invoke(() => { SettingsWindow.ShowInstance(); });
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Settings event listener error");
            }
        });

        try
        {
            SettingsManager.RestoreSettings();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to restore settings: {ex.Message}");
            Logger.Error(ex, "Failed to restore settings");
        }

        // RestoreSettings may replace SettingsManager.Current instance, so rebind DataContext.
        DataContext = SettingsManager.Current;

        if (SettingsManager.Current.Startup == true) // add to startup programs if enabled, needs improvement
        {
            RegistryKey? key = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true);
            string? executablePath = Environment.ProcessPath;
            if (key != null && executablePath != null)
                key.SetValue("FluentFlyout", $"\"{executablePath}\"");
        }

        // display tray icon if enabled
        if (!SettingsManager.Current.NIconHide)
        {
            nIcon.Visibility = Visibility.Visible;
        }

        cts = new CancellationTokenSource();

        mediaManager.Start();
        _zenBridgeService.SessionsChanged += ZenBridge_SessionsChanged;
        _zenBridgeService.Start();
        _vlcBridgeService.SessionChanged += VlcBridge_SessionChanged;
        _vlcBridgeService.Start();

        _hookProc = HookCallback;
        _hookId = SetHook(_hookProc);

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -Width - 20; // workaround for window appearing on the screen before the animation starts
        CustomWindowChrome.CaptionHeight = 0; // hide the title bar

        mediaManager.OnAnyMediaPropertyChanged += MediaManager_OnAnyMediaPropertyChanged;
        mediaManager.OnAnyPlaybackStateChanged += CurrentSession_OnPlaybackStateChanged;
        mediaManager.OnAnySessionClosed += MediaManager_OnAnySessionClosed;

        InitializeHeadsetControls();

        WM_TASKBARCREATED = RegisterWindowMessage("TaskbarCreated");
        WM_SHELLHOOK = RegisterWindowMessage("SHELLHOOK");
        RegisterShellHookWindow(new WindowInteropHelper(this).Handle);

        string previousVersion = SettingsManager.Current.LastKnownVersion;
        _ = CheckForExperimentsOnStartupAsync(previousVersion);

        // apply other things on new thread
        Dispatcher.Invoke(() =>
        {
            LocalizationManager.ApplyLocalization();

            try // update last known version. gets the version of the app, works only in release mode
            {
                var version = Package.Current.Id.Version;
                SettingsManager.Current.LastKnownVersion = $"v{version.Major}.{version.Minor}.{version.Build}";
            }
            catch
            {
                SettingsManager.Current.LastKnownVersion = "debug";
            }

            Logger.Info($"Current version: {SettingsManager.Current.LastKnownVersion}");

            Notifications.ShowFirstOrUpdateNotification(previousVersion, SettingsManager.Current.LastKnownVersion);
            FlowDirection = SettingsManager.Current.FlowDirection;

            // check for updates on startup
            _ = CheckForUpdatesOnStartupAsync();
        });
    }

    private async Task CheckForExperimentsOnStartupAsync(string previousVersion)
    {
        OnboardingExperiment(previousVersion);
    }

    private void OnboardingExperiment(string previousVersion)
    {
        // show onboarding to new users (no previous version stored = user has never run the app before)
        if (string.IsNullOrEmpty(previousVersion))
        {
            if (ExperimentsService.HasExperiments)
            {
                if (ExperimentsService.CheckUuidInExperiment("onboarding") == "A")
                    OnboardingWindow.ShowInstance();
                else
                {
                    SettingsWindow.ShowInstance();
                    _ = TelemetryService.SendTelemetryEventAsync("onboarding_completed", "onboarding");
                }
            }
            else
                OnboardingWindow.ShowInstance();
        }
    }

    private async Task CheckForUpdatesOnStartupAsync()
    {
        try
        {
            var result = await UpdateCheckerService.CheckForUpdatesAsync(SettingsManager.Current.LastKnownVersion);

            if (result.Success)
            {
                UpdateState.Current.IsUpdateAvailable = result.IsUpdateAvailable;
                UpdateState.Current.NewestVersion = result.NewestVersion;
                UpdateState.Current.UpdateUrl = result.UpdateUrl;
                UpdateState.Current.LastUpdateCheck = result.CheckedAt;

                if (result.IsUpdateAvailable)
                {
                    Notifications.ShowUpdateAvailableNotification(result.NewestVersion, result.UpdateUrl);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to check for updates on startup");
        }
    }

    public bool IsSessionAllowed(MediaSession? session)
    {
        if (session == null) return false;
        string appId = session.Id ?? string.Empty;
        string appName = MediaPlayerData.GetAndCacheMediaPlayerData(appId).Item1 ?? appId;
        return IsSessionAllowed(appName, appId);
    }

    public bool IsSessionAllowed(string appName, string appId)
    {
        if (!SettingsManager.Current.AppFilteringEnabled) return true;

        if (SettingsManager.Current.AppFilteringMode == 0) // Blacklist mode
        {
            if (SettingsManager.Current.BlockedApps != null &&
                SettingsManager.Current.BlockedApps.Any(b => MatchesFilterEntry(b, appName, appId)))
                return false;

            return true;
        }
        else // Whitelist mode
        {
            if (SettingsManager.Current.AllowedApps != null &&
                SettingsManager.Current.AllowedApps.Any(a => MatchesFilterEntry(a, appName, appId)))
                return true;

            return false;
        }
    }

    private List<IMediaPlaybackSession> GetRawAllowedPlaybackSessions()
    {
        var rawZenSessions = _zenBridgeService.GetSessions();
        bool hasZenSessions = rawZenSessions.Count > 0;

        List<IMediaPlaybackSession> sessions = [];
        foreach (var zenSession in rawZenSessions)
        {
            if (IsSessionAllowed(zenSession.SourceAppName, zenSession.Id))
                sessions.Add(zenSession);
        }

        if (_vlcBridgeService.GetSession() is { } vlcSession &&
            IsSessionAllowed(vlcSession.SourceAppName, vlcSession.Id))
        {
            sessions.Add(vlcSession);
        }

        foreach (var smtcSession in mediaManager.CurrentMediaSessions.Values)
        {
            // Suppress generic single-process zen.exe SMTC (including Mozilla AUMID hashes resolved to "Zen Browser") whenever live Zen tab sessions are available
            if (hasZenSessions)
            {
                string smtcAppName = MediaPlayerData.GetAndCacheMediaPlayerData(smtcSession.Id).Item1 ?? string.Empty;
                if (smtcSession.Id.Contains("zen", StringComparison.OrdinalIgnoreCase) ||
                    smtcAppName.Contains("zen", StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            if (IsSessionAllowed(smtcSession))
                sessions.Add(new SmtcMediaPlaybackSession(smtcSession));
        }

        return sessions;
    }

    public IReadOnlyList<IMediaPlaybackSession> GetAllowedPlaybackSessions()
    {
        var rawSessions = GetRawAllowedPlaybackSessions();
        var playingSessions = rawSessions.Where(s => s.IsPlaying).ToList();

        lock (_sessionHistoryLock)
        {
            var newRawSessions = rawSessions.Where(s => !_knownRawSessionIds.Contains(s.Id)).ToList();

            _knownRawSessionIds.Clear();
            foreach (var s in rawSessions)
                _knownRawSessionIds.Add(s.Id);

            _sessionHistory.RemoveAll(id => !_knownRawSessionIds.Contains(id));

            if (playingSessions.Count > 0)
            {
                _knownPlayingIds.Clear();
                foreach (var s in playingSessions)
                    _knownPlayingIds.Add(s.Id);

                _lastPausedPlaceholderId = null;
                return playingSessions;
            }

            if (rawSessions.Count == 0)
            {
                _knownPlayingIds.Clear();
                _lastPausedPlaceholderId = null;
                return [];
            }

            if (_knownPlayingIds.Count > 0)
            {
                var justPaused = rawSessions.Where(s => _knownPlayingIds.Contains(s.Id)).ToList();
                _knownPlayingIds.Clear();

                if (justPaused.Count > 0)
                {
                    var chosen = _sessionHistory
                        .Select(id => justPaused.FirstOrDefault(s => s.Id == id))
                        .FirstOrDefault(s => s != null) ?? justPaused[0];
                    _lastPausedPlaceholderId = chosen.Id;
                    _sessionHistory.Remove(chosen.Id);
                    _sessionHistory.Insert(0, chosen.Id);
                }
            }
            else if (newRawSessions.Count > 0)
            {
                var chosen = _sessionHistory
                    .Select(id => newRawSessions.FirstOrDefault(s => s.Id == id))
                    .FirstOrDefault(s => s != null) ?? newRawSessions[0];
                _lastPausedPlaceholderId = chosen.Id;
                _sessionHistory.Remove(chosen.Id);
                _sessionHistory.Insert(0, chosen.Id);
            }

            if (!string.IsNullOrEmpty(_lastPausedPlaceholderId))
            {
                var placeholder = rawSessions.FirstOrDefault(s => s.Id == _lastPausedPlaceholderId);
                if (placeholder != null)
                    return [placeholder];

                _lastPausedPlaceholderId = null;
            }

            return [];
        }
    }

    // display names are matched in full: entries always hold a whole app name, and a substring match let one entry
    // swallow others (blocking "Amazon Music" also blocked "Amazon Music SMTC Bridge"). the session id keeps substring
    // matching so an entry can still target a whole package or publisher
    private static bool MatchesFilterEntry(string entry, string appName, string appId)
    {
        if (string.IsNullOrWhiteSpace(entry)) return false; // an empty entry would match every session

        return appName.Equals(entry, StringComparison.OrdinalIgnoreCase) || appId.Contains(entry, StringComparison.OrdinalIgnoreCase);
    }

    private void PromoteSessionHistory(string sessionId, bool isPlaying = false)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        lock (_sessionHistoryLock)
        {
            _sessionHistory.Remove(sessionId);
            _sessionHistory.Insert(0, sessionId);
            if (isPlaying)
                _knownPlayingIds.Add(sessionId);
        }
    }

    private void RemoveSessionHistory(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        lock (_sessionHistoryLock)
        {
            _sessionHistory.Remove(sessionId);
            _knownRawSessionIds.Remove(sessionId);
            _knownPlayingIds.Remove(sessionId);
            if (string.Equals(_lastPausedPlaceholderId, sessionId, StringComparison.Ordinal))
                _lastPausedPlaceholderId = null;
        }
    }

    public void CycleActiveMediaSession(int direction)
    {
        var playingSessions = GetAllowedPlaybackSessions().Where(s => s.IsPlaying).ToList();
        if (playingSessions.Count < 2) return;

        string? currentActiveId = GetActiveMediaSession()?.Id;
        int currentIndex = playingSessions.FindIndex(s => s.Id == currentActiveId);
        if (currentIndex < 0) currentIndex = 0;

        int newIndex = ((currentIndex + direction) % playingSessions.Count + playingSessions.Count) % playingSessions.Count;
        var nextSession = playingSessions[newIndex];

        SettingsManager.Current.PinnedSessionId = string.Empty;
        PromoteSessionHistory(nextSession.Id, isPlaying: true);
        UpdateTaskbar();

        if (IsVisible)
            UpdateUI(nextSession);
    }

    public IMediaPlaybackSession? GetActiveMediaSession()
    {
        var validSessions = GetAllowedPlaybackSessions();

        if (validSessions.Count == 0) return null;

        var pinnedId = SettingsManager.Current.PinnedSessionId;
        if (!string.IsNullOrEmpty(pinnedId))
        {
            var pinned = validSessions.FirstOrDefault(s => s.Id == pinnedId);
            if (pinned != null)
                return pinned;

            SettingsManager.Current.PinnedSessionId = string.Empty;
        }

        List<string> historySnapshot;
        lock (_sessionHistoryLock)
        {
            historySnapshot = [.. _sessionHistory];
        }

        foreach (var id in historySnapshot)
        {
            var manuallySelected = validSessions.FirstOrDefault(s => s.Id == id);
            if (manuallySelected != null)
                return manuallySelected;
        }

        var focused = mediaManager.GetFocusedSession();
        if (focused != null)
        {
            var focusedSession = validSessions.FirstOrDefault(s => s.Id == focused.Id);
            if (focusedSession != null)
                return focusedSession;
        }

        return validSessions.FirstOrDefault();
    }

    private static int? GetVolumeProcessId(IMediaPlaybackSession session)
        => MediaPlayerData.GetAndCacheProcessId(session switch
        {
            ZenMediaPlaybackSession => "zen.exe",
            VlcMediaPlaybackSession => "vlc.exe",
            _ => session.Id
        });

    public float? GetActiveMediaAppVolume()
    {
        if (volumeMixerWindow?.ViewModel is not { } volumeMixerViewModel ||
            GetActiveMediaSession() is not { } activeSession)
            return null;

        int? processId = GetVolumeProcessId(activeSession);
        return processId.HasValue
            ? volumeMixerViewModel.GetSessionVolume(processId.Value)
            : null;
    }

    public void AdjustTaskbarVolume(float delta)
    {
        if (volumeMixerWindow?.ViewModel is not { } volumeMixerViewModel) return;

        switch (SettingsManager.Current.TaskbarWidgetScrollVolumeMode)
        {
            // 0 = disabled, 1 = master volume, 2 = active media session
            case 1:
                bool success = volumeMixerViewModel.TryAdjustMasterVolume(delta);

                if (success && SettingsManager.Current.VolumeControlEnabled)
                    volumeMixerWindow?.ShowFlyout();
                break;

            case 2:
                if (GetActiveMediaSession() is not { } activeSession) return;

                int? processId = GetVolumeProcessId(activeSession);
                if (processId.HasValue)
                {
                    volumeMixerViewModel.TryAdjustSessionVolume(processId.Value, delta);
                }
                break;
        }
    }

    public void RefreshFilteredMedia()
    {
        UpdateTaskbar();

        if (IsVisible)
        {
            UpdateUI(GetActiveMediaSession());
        }
    }

    public async Task<bool> TrySkipPreviousAsync()
        => GetActiveMediaSession() is { } session && await session.TrySkipPreviousAsync();

    public async Task<bool> TryTogglePlayPauseAsync()
        => GetActiveMediaSession() is { } session && await session.TryTogglePlayPauseAsync();

    public async Task<bool> TrySkipNextAsync()
        => GetActiveMediaSession() is { } session && await session.TrySkipNextAsync();

    public async Task<bool> TryOpenMediaPlayerAsync()
    {
        try
        {
            if (GetActiveMediaSession() is { } activeSession)
            {
                string? title = activeSession is SmtcMediaPlaybackSession smtc
                    ? TryGetMediaProperties(smtc.RawSession.ControlSession)?.Title
                    : activeSession.CachedTitle;
                return await activeSession.TryActivateAsync(title);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to open media player");
        }
        return false;
    }

    private static GlobalSystemMediaTransportControlsSessionMediaProperties? TryGetMediaProperties(GlobalSystemMediaTransportControlsSession controlSession)
    {
        try
        {
            return controlSession.TryGetMediaPropertiesAsync().GetAwaiter().GetResult();
        }
        catch (COMException ex)
        {
            Logger.Error(ex, "Failed to retrieve data from the player");
            return null;
        }
    }

    private void openSettings(object? sender, EventArgs e)
    {
        SettingsWindow.ShowInstance();
    }

    public static int getDuration() // get the duration of the animation based on the speed setting
    {
        int msDuration = SettingsManager.Current.FlyoutAnimationSpeed switch
        {
            0 => 0, // off
            1 => 150, // 0.5x
            2 => 300, // 1x
            3 => 450, // 1.5x
            4 => 600, // 2x
            _ => 900 // 3x
        };
        return msDuration;
    }

    public EasingFunctionBase getEasingStyle(bool easeOut)
    {
        EasingMode easingMode = easeOut ? EasingMode.EaseOut : EasingMode.EaseIn;
        EasingFunctionBase easingStyle = SettingsManager.Current.FlyoutAnimationEasingStyle switch
        {
            // 0 is linear, null
            1 => new SineEase { EasingMode = easingMode }, // sine
            2 => new QuadraticEase { EasingMode = easingMode }, // quadratic
            _ => new CubicEase { EasingMode = easingMode }, // cubic
        };
        return easingStyle;
    }

    private MonitorUtil.MonitorInfo getSelectedMonitor()
    {
        return MonitorUtil.GetSelectedMonitor(SettingsManager.Current.FlyoutSelectedMonitor);
    }

    /// <summary>
    /// Computes the final resting position (left, top) for a window based on the current
    /// position setting and the selected monitor's work area.
    /// </summary>
    private static double GetBottomCenterFlyoutBottomMargin(bool reserveNativeVolumeOsdSpace)
    {
        if (!reserveNativeVolumeOsdSpace)
            return 16;

        return SettingsManager.Current.VolumeControlEnabled && SettingsManager.Current.VolumeControlAboveMediaFlyout ? 16 : 80;
    }

    private (double left, double top) GetFinalPosition(Rect windowRect, Rect workArea, bool reserveNativeVolumeOsdSpace = false)
    {
        int position = SettingsManager.Current.Position;
        double left = position switch
        {
            0 or 3 => workArea.Left + 16,
            2 or 5 => workArea.Left + workArea.Width - windowRect.Width - 16,
            _ => workArea.Left + workArea.Width / 2 - windowRect.Width / 2
        };
        double top = position switch
        {
            0 or 2 => workArea.Top + workArea.Height - windowRect.Height - 16,
            1 => workArea.Top + workArea.Height - windowRect.Height - GetBottomCenterFlyoutBottomMargin(reserveNativeVolumeOsdSpace),
            _ => workArea.Top + 16
        };
        return (left, top);
    }

    public void OpenAnimation(MicaWindow window, bool alwaysBottom = false, MonitorInfo? selectedMonitor = null, MicaWindow? aboveReference = null, bool reserveNativeVolumeOsdSpace = false)
    {
        if (window.Triggers[0] is not EventTrigger eventTriggers)
        {
            Logger.Error("OpenAnimation: EventTriggers is null.");
            return;
        }
        if (eventTriggers.Actions[0] is not BeginStoryboard beginStoryboard)
        {
            Logger.Error("OpenAnimation: BeginStoryboard is null.");
            return;
        }
        var storyboard = beginStoryboard.Storyboard;

        DoubleAnimation moveAnimation = (DoubleAnimation)storyboard.Children[0];
        var monitor = selectedMonitor != null ? selectedMonitor.Value : getSelectedMonitor();
        var workArea = monitor.workArea;

        // prevent flickering
        WindowHelper.SetVisibility(window, false); // window.Visibility = Visibility.Hidden works with some delay

        // Update the DPI by moving the window to the target workArea, ignoring WPF scaling
        WindowHelper.SetPosition(window, workArea.Left, workArea.Top);
        var windowRect = WindowHelper.GetPlacement(window); // here we take the updated window size in raw coordinates.

        double window_left = 0;

        // If a reference window is provided and visible, position the window next to it
        if (aboveReference != null && aboveReference.IsVisible)
        {
            // Here we work with raw monitor coordinates, without taking DPI into account.
            double refWidth = aboveReference.Width * monitor.dpiX / 96.0;
            double refHeight = aboveReference.Height * monitor.dpiY / 96.0;
            var refRect = new Rect(0, 0, refWidth, refHeight);
            var (refLeft, refTop) = GetFinalPosition(refRect, workArea, reserveNativeVolumeOsdSpace);

            window_left = refLeft + refWidth / 2 - windowRect.Width / 2;
            double aboveTop = refTop - windowRect.Height - 8;
            bool isTop = SettingsManager.Current.Position switch
            {
                3 or 4 or 5 => true,
                _ => false
            };

            // If the reference window is too close to the top edge, we place the flyout below it instead of above to prevent it from going off-screen.
            if (isTop)
                aboveTop = refTop + refHeight + 8;

            moveAnimation.To = aboveTop;
            if (SettingsManager.Current.FlyoutAnimationSpeed == 0)
                moveAnimation.From = moveAnimation.To;
            else
                moveAnimation.From = isTop ? aboveTop - 20 : aboveTop + 20;
        }
        // default behavior: position the flyout based on the user's settings
        else if (alwaysBottom == false)
        {
            _position = SettingsManager.Current.Position;
            if (_position == 0)
            {
                window_left = workArea.Left + 16;
                moveAnimation.To = workArea.Top + workArea.Height - windowRect.Height - 16;
                if (SettingsManager.Current.FlyoutAnimationSpeed == 0) // if off, don't animate (just appear at the bottom)
                    moveAnimation.From = moveAnimation.To;
                else
                    moveAnimation.From = workArea.Top + workArea.Height - windowRect.Height + 4; // appear from the bottom of the screen
            }
            else if (_position == 1)
            {
                window_left = workArea.Left + workArea.Width / 2 - windowRect.Width / 2;
                double bottomMargin = GetBottomCenterFlyoutBottomMargin(reserveNativeVolumeOsdSpace);
                double moveTo = workArea.Top + workArea.Height - windowRect.Height - bottomMargin;
                moveAnimation.To = moveTo;
                if (SettingsManager.Current.FlyoutAnimationSpeed == 0)
                    moveAnimation.From = moveTo;
                else
                    moveAnimation.From = moveTo + 20;
            }
            else if (_position == 2)
            {
                window_left = workArea.Left + workArea.Width - windowRect.Width - 16;
                moveAnimation.To = workArea.Top + workArea.Height - windowRect.Height - 16;
                if (SettingsManager.Current.FlyoutAnimationSpeed == 0)
                    moveAnimation.From = moveAnimation.To;
                else
                    moveAnimation.From = workArea.Top + workArea.Height - windowRect.Height + 4;
            }
            else if (_position == 3)
            {
                window_left = workArea.Left + 16;
                moveAnimation.To = workArea.Top + 16;
                if (SettingsManager.Current.FlyoutAnimationSpeed == 0)
                    moveAnimation.From = moveAnimation.To;
                else
                    moveAnimation.From = workArea.Top + -4;
            }
            else if (_position == 4)
            {
                window_left = workArea.Left + workArea.Width / 2 - windowRect.Width / 2;
                moveAnimation.To = workArea.Top + 16;
                if (SettingsManager.Current.FlyoutAnimationSpeed == 0)
                    moveAnimation.From = moveAnimation.To;
                else
                    moveAnimation.From = workArea.Top + -4;
            }
            else if (_position == 5)
            {
                window_left = workArea.Left + workArea.Width - windowRect.Width - 16;
                moveAnimation.To = workArea.Top + 16;
                if (SettingsManager.Current.FlyoutAnimationSpeed == 0)
                    moveAnimation.From = moveAnimation.To;
                else
                    moveAnimation.From = workArea.Top + -4;
            }
        }
        // other cases (e.g. if alwaysBottom is true): position the flyout at the bottom center of the screen
        else
        {
            window_left = workArea.Left + workArea.Width / 2 - windowRect.Width / 2;
            moveAnimation.To = workArea.Top + workArea.Height - windowRect.Height - 16;
            if (SettingsManager.Current.FlyoutAnimationSpeed == 0)
                moveAnimation.From = moveAnimation.To;
            else
                moveAnimation.From = workArea.Top + workArea.Height - windowRect.Height + 4;
        }

        // Set the initial position in raw coordinates.
        WindowHelper.SetPosition(window, window_left, moveAnimation.From!.Value);

        // Next coordinates will be used to set Window.Top, which takes DPI into account,
        // so we need to convert the coordinates to DPI scale.
        moveAnimation.From *= 96.0 / monitor.dpiY;
        moveAnimation.To *= 96.0 / monitor.dpiY;

        int msDuration = getDuration();

        DoubleAnimation opacityAnimation = (DoubleAnimation)storyboard.Children[1];
        if (SettingsManager.Current.FlyoutAnimationSpeed != 0) opacityAnimation.From = 0;
        opacityAnimation.To = 1;
        opacityAnimation.Duration = new Duration(TimeSpan.FromMilliseconds(msDuration));

        if (SettingsManager.Current.FlyoutAnimationEasingStyle == 0) moveAnimation.EasingFunction = opacityAnimation.EasingFunction = null;
        else moveAnimation.EasingFunction = opacityAnimation.EasingFunction = getEasingStyle(true);
        moveAnimation.Duration = new Duration(TimeSpan.FromMilliseconds(msDuration));

        storyboard.Begin(window);
        WindowHelper.SetVisibility(window, true);
        WindowHelper.SetTopmost(window);
    }

    public void CloseAnimation(MicaWindow window, MonitorInfo? selectedMonitor = null)
    {
        if (window.Triggers[0] is not EventTrigger eventTriggers)
        {
            Logger.Error("CloseAnimation: EventTriggers is null.");
            return;
        }
        if (eventTriggers.Actions[0] is not BeginStoryboard beginStoryboard)
        {
            Logger.Error("CloseAnimation: BeginStoryboard is null.");
            return;
        }
        var storyboard = beginStoryboard.Storyboard;

        DoubleAnimation moveAnimation = (DoubleAnimation)storyboard.Children[0];
        var monitor = selectedMonitor != null ? selectedMonitor.Value : getSelectedMonitor();
        var workArea = monitor.workArea;
        Rect windowRect = WindowHelper.GetPlacement(window);

        // Use the window's actual current position as the animation start
        moveAnimation.From = windowRect.Top;

        if (SettingsManager.Current.FlyoutAnimationSpeed != 0)
        {
            // Determine slide direction
            bool isTopHalf = windowRect.Top + windowRect.Height / 2 < workArea.Top + workArea.Height / 2;
            moveAnimation.To = windowRect.Top + (isTopHalf ? -20 : 20);
        }

        moveAnimation.From *= 96.0 / monitor.dpiY;
        if (moveAnimation.To != null)
            moveAnimation.To *= 96.0 / monitor.dpiY;

        int msDuration = getDuration();

        DoubleAnimation opacityAnimation = (DoubleAnimation)storyboard.Children[1];
        opacityAnimation.From = 1;
        if (SettingsManager.Current.FlyoutAnimationSpeed != 0) opacityAnimation.To = 0;
        opacityAnimation.Duration = new Duration(TimeSpan.FromMilliseconds(msDuration));

        if (SettingsManager.Current.FlyoutAnimationEasingStyle == 0) moveAnimation.EasingFunction = opacityAnimation.EasingFunction = null;
        else moveAnimation.EasingFunction = opacityAnimation.EasingFunction = getEasingStyle(false);
        moveAnimation.Duration = new Duration(TimeSpan.FromMilliseconds(msDuration));

        storyboard.Begin(window);
    }

    public void UpdateTaskbar()
    {
        var activeSession = GetActiveMediaSession();
        if (activeSession == null)
        {
            taskbarWindow?.UpdateUi("-", "-", null, GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed);
            return;
        }

        if (activeSession is SmtcMediaPlaybackSession smtcSession)
        {
            if (!mediaManager.IsStarted || smtcSession.RawSession.ControlSession == null)
            {
                taskbarWindow?.UpdateUi("-", "-", null, GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed);
                return;
            }

            var songInfo = TryGetMediaProperties(smtcSession.RawSession.ControlSession);
            if (songInfo == null)
                return;

            var playbackInfo = smtcSession.RawSession.ControlSession.GetPlaybackInfo();
            var thumbnail = BitmapHelper.GetThumbnail(songInfo.Thumbnail);
            BitmapHelper.GetDominantColors(1);
            taskbarWindow?.UpdateUi(songInfo.Title, songInfo.Artist, thumbnail, playbackInfo.PlaybackStatus, playbackInfo.Controls);
        }
        else
        {
            string title = activeSession.CachedTitle;
            string artist = activeSession.CachedArtist;
            string artUrl = activeSession.CachedArtworkUrl;
            var status = activeSession.PlaybackStatus;
            string sessionId = activeSession.Id;
            bool canPrev = activeSession.CanPrevious;
            bool canPlayPause = activeSession.CanPlayPause;
            bool canNext = activeSession.CanNext;

            taskbarWindow?.UpdateUi(title, artist, null, status, null, canPrev, canPlayPause, canNext);

            if (!string.IsNullOrEmpty(artUrl))
            {
                _ = Task.Run(async () =>
                {
                    var (img, _, _) = await BitmapHelper.GetThumbnailAndAccentFromUrlAsync(artUrl, CancellationToken.None).ConfigureAwait(false);
                    if (img == null) return;

                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        if (GetActiveMediaSession() is { } current && current.Id == sessionId)
                        {
                            taskbarWindow?.UpdateUi(title, artist, img, current.PlaybackStatus, null, current.CanPrevious, current.CanPlayPause, current.CanNext);
                        }
                    });
                });
            }
        }
    }

    private void VlcBridge_SessionChanged(string newlyPlayingId)
    {
        if (!string.IsNullOrEmpty(newlyPlayingId))
            ZenBridge_SessionsChanged([newlyPlayingId]);
        else
            ZenBridge_SessionsChanged([]);
    }

    private void ZenBridge_SessionsChanged(IReadOnlyList<string> updatedIds)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_isCleaningUp) return;

            var rawSessions = GetRawAllowedPlaybackSessions();
            foreach (string id in updatedIds)
            {
                var updated = rawSessions.FirstOrDefault(s => s.Id == id);
                if (updated != null && updated.IsPlaying)
                {
                    PromoteSessionHistory(updated.Id, isPlaying: true);
                    pauseOtherMediaSessionsIfNeeded(updated);
                }
            }

            UpdateTaskbar();
            if (IsVisible)
            {
                UpdateUI(GetActiveMediaSession());
            }
        });
    }

    public void reportBug(object? sender, EventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://github.com/unchihugo/FluentFlyout/issues/new/choose",
            UseShellExecute = true
        });
    }

    private void openRepository(object? sender, EventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://github.com/unchihugo/FluentFlyout",
            UseShellExecute = true
        });
    }

    public void openLogsFolder(object? sender, EventArgs e)
    {
        try
        {
            Process.Start("explorer.exe", FileSystemHelper.GetLogsPath());
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to open logs folder");
        }
    }

    private void pauseOtherMediaSessionsIfNeeded(IMediaPlaybackSession mediaSession)
    {
        if (SettingsManager.Current.PauseOtherSessionsEnabled && mediaSession.IsPlaying)
        {
            PauseOtherSessions(mediaSession);
        }
    }

    private void CurrentSession_OnPlaybackStateChanged(MediaSession mediaSession, GlobalSystemMediaTransportControlsSessionPlaybackInfo? playbackInfo = null)
    {
        var newStatus = playbackInfo?.PlaybackStatus ?? mediaSession.ControlSession?.GetPlaybackInfo()?.PlaybackStatus;
#if DEBUG
        Logger.Debug("Playback state changed: " + mediaSession.Id + " " + newStatus);
#endif
        var wrapped = new SmtcMediaPlaybackSession(mediaSession);
        pauseOtherMediaSessionsIfNeeded(wrapped);

        if (newStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
        {
            PromoteSessionHistory(mediaSession.Id, isPlaying: true);
        }
        else if (newStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused)
        {
            PromoteSessionHistory(mediaSession.Id);

            if (GetActiveMediaSession()?.Id == mediaSession.Id)
            {
                var otherPlaying = GetRawAllowedPlaybackSessions()
                    .FirstOrDefault(s => s.Id != mediaSession.Id && s.IsPlaying);

                if (otherPlaying != null)
                {
                    PromoteSessionHistory(otherPlaying.Id, isPlaying: true);
                }
            }
        }

        UpdateTaskbar();

        if (IsVisible)
        {
            UpdateUI(GetActiveMediaSession());
        }
    }

    // for determining whether MediaPropertyChanged has no changes
    private string previousMediaProperty = "";
    private int previousMediaPropertyThumbnail = 0;
    private void MediaManager_OnAnyMediaPropertyChanged(MediaSession mediaSession, GlobalSystemMediaTransportControlsSessionMediaProperties mediaProperties)
    {
        // sometimes mediaSession.ControlSession can be null
        if (mediaSession.ControlSession == null)
            return;

#if DEBUG
        Logger.Debug("Media property changed: " + mediaProperties.Title + " " + mediaSession.ControlSession.GetPlaybackInfo().PlaybackStatus);
#endif
        var currentActiveSession = GetActiveMediaSession();
        if (currentActiveSession == null)
        {
            taskbarWindow?.UpdateUi("-", "-", null, GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed);
            return;
        }

        if (currentActiveSession is not SmtcMediaPlaybackSession smtcActive || smtcActive.RawSession.ControlSession == null)
        {
            UpdateTaskbar();
            return;
        }

        var songInfo = TryGetMediaProperties(smtcActive.RawSession.ControlSession);
        if (songInfo == null)
            return;

        var playbackInfo = smtcActive.RawSession.ControlSession.GetPlaybackInfo();

        string check = currentActiveSession.Id + songInfo.Title + songInfo.Artist + playbackInfo.PlaybackStatus;
        int checkThumbnail = BitmapHelper.GetStableThumbnailHash(songInfo.Thumbnail);
        bool onlyThumbnailChanged = false;
        bool isActiveDuplicate = false;
        if (previousMediaProperty == check)
        {
            onlyThumbnailChanged = true;
            if (previousMediaPropertyThumbnail == checkThumbnail)
                isActiveDuplicate = true;
        }

        if (!isActiveDuplicate)
        {
            previousMediaProperty = check;
            previousMediaPropertyThumbnail = checkThumbnail;

            var thumbnail = BitmapHelper.GetThumbnail(songInfo.Thumbnail);
            BitmapHelper.GetDominantColors(1);

            taskbarWindow?.UpdateUi(songInfo.Title, songInfo.Artist, thumbnail, playbackInfo.PlaybackStatus, playbackInfo.Controls);

            pauseOtherMediaSessionsIfNeeded(new SmtcMediaPlaybackSession(mediaSession));

            if (SettingsManager.Current.NextUpEnabled && !FullscreenDetector.IsFullscreenApplicationRunning()) // show NextUpWindow if enabled in settings
            {
                void createNewNextUpWindow()
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (nextUpWindow == null && playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) // double-check within the Dispatcher to prevent race conditions
                        {
                            nextUpWindow = new NextUpWindow(songInfo.Title, songInfo.Artist, thumbnail);
                            currentTitle = songInfo.Title;
                            nextUpWindow.Closed += (s, e) => nextUpWindow = null; // set nextUpWindow to null when closed
                        }
                    });
                }

                if (nextUpWindow == null && IsVisible == false && songInfo.Thumbnail != null && currentTitle != songInfo.Title)
                {
                    createNewNextUpWindow();
                }
                else if (nextUpWindow != null && !onlyThumbnailChanged)
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (nextUpWindow != null)
                        {
                            WindowHelper.SetVisibility(nextUpWindow, false); // prevents rare flickering during rapid closing
                            nextUpWindow.Close(); // must be cleared by the Closed event
                        }
                    });
                    createNewNextUpWindow();
                }
                else if (nextUpWindow != null && songInfo.Thumbnail != null)
                {
                    Dispatcher.Invoke(() =>
                    {
                        nextUpWindow?.UpdateThumbnail(thumbnail);
                    });
                }
            }
        }

        // Refresh multi-session cards even if a non-active background session changed while the flyout is open
        if (IsVisible && (!isActiveDuplicate || mediaSession.Id != currentActiveSession.Id))
        {
            UpdateUI(currentActiveSession);
        }
    }

    private void MediaManager_OnAnySessionClosed(MediaSession mediaSession)
    {
#if DEBUG
        Logger.Debug("Session closed: " + (mediaSession.Id).ToString());
#endif
        RemoveSessionHistory(mediaSession.Id);

        if (!string.IsNullOrEmpty(SettingsManager.Current.PinnedSessionId)
            && SettingsManager.Current.PinnedSessionId == mediaSession.Id)
        {
            SettingsManager.Current.PinnedSessionId = string.Empty;
        }
        UpdateTaskbar();

        if (IsVisible)
        {
            UpdateUI(GetActiveMediaSession());
        }
    }

    private static IntPtr SetHook(LowLevelKeyboardProc proc) // set the keyboard hook
    {
        using Process curProcess = Process.GetCurrentProcess();
        using ProcessModule? curModule = curProcess.MainModule;
        if (curModule == null)
        {
            Logger.Warn("Failed to set keyboard hook - FluentFlyout will now rely on WndProc only");
            return IntPtr.Zero;
        }
        return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(curModule.ModuleName), 0);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == WM_KEYDOWN || wParam == WM_KEYUP || wParam == WM_SYSKEYDOWN || wParam == WM_SYSKEYUP))
        {
            int vkCode = Marshal.ReadInt32(lParam);
            bool isKeyDown = wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN;
            bool isKeyUp = wParam == WM_KEYUP || wParam == WM_SYSKEYUP;

            if (vkCode == VK_V)
            {
                if (isKeyUp)
                {
                    _volumeShortcutKeyDown = false;
                }
                else if (isKeyDown && !_volumeShortcutKeyDown)
                {
                    _volumeShortcutKeyDown = true;

                    if (SettingsManager.Current.VolumeFlyoutShortcutEnabled
                        && (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0
                        && (GetAsyncKeyState(VK_MENU) & 0x8000) != 0)
                    {
                        var activeSession = GetActiveMediaSession();
                        if (activeSession == null) ShowVolumeFlyout();
                        else ShowMediaFlyout(withVolume: true);
                    }
                }
            }

            // Don't check media/volume: WndProc() already handles media and volume keys, so it's redundant and can cause triggers even on rebound keys.

            if (SettingsManager.Current.LockKeysEnabled
                && !FullscreenDetector.IsFullscreenApplicationRunning()
                && isKeyUp)
            {
                if (vkCode == 0x14 && SettingsManager.Current.LockKeysCapsEnabled) // Caps Lock
                {
                    lockWindow ??= new LockWindow();
                    lockWindow.ShowLockFlyout(FindResource("LockWindow_CapsLock").ToString(), Keyboard.IsKeyToggled(Key.CapsLock));
                }
                else if (vkCode == 0x90 && SettingsManager.Current.LockKeysNumEnabled) // Num Lock
                {
                    lockWindow ??= new LockWindow();
                    lockWindow.ShowLockFlyout(FindResource("LockWindow_NumLock").ToString(), Keyboard.IsKeyToggled(Key.NumLock));
                }
                else if (vkCode == 0x91 && SettingsManager.Current.LockKeysScrollEnabled) // Scroll Lock
                {
                    lockWindow ??= new LockWindow();
                    lockWindow.ShowLockFlyout(FindResource("LockWindow_ScrollLock").ToString(), Keyboard.IsKeyToggled(Key.Scroll));
                }
                else if (vkCode == 0x2D && SettingsManager.Current.LockKeysInsertEnabled) // Insert
                {
                    lockWindow ??= new LockWindow();
                    lockWindow.ShowLockFlyout("Insert", Keyboard.IsKeyToggled(Key.Insert));
                }
            }
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    // show the media flyout with debounce
    private bool TryShowMediaFlyoutDebounced()
    {
        long currentTime = Environment.TickCount64;
        // debounce to prevent hangs with rapid key presses
        if ((currentTime - _lastFlyoutTime) < 500) // 500ms debounce time
        {
            return false;
        }
        _lastFlyoutTime = currentTime;
        ShowMediaFlyout();
        return true;
    }

    private void ShowVolumeFlyout()
    {
        if (!SettingsManager.Current.VolumeControlEnabled)
            return;

        volumeMixerWindow?.ViewModel.SyncMasterFromDevice();
        volumeMixerWindow?.ShowFlyout();
    }

    /// <summary>
    /// Shows the media flyout.
    /// </summary>
    /// <param name="withVolume">
    /// Also shows the volume flyout, and hides it again when this call hides the media flyout.
    /// Passed by the manual triggers (tray icon, taskbar widget) so the two stay in sync;
    /// the media and volume key handlers already show both on their own.
    /// </param>
    public async void ShowMediaFlyout(bool toggleMode = false, bool forceShow = false, bool withVolume = false)
    {
        var activeSession = GetActiveMediaSession();
        if (activeSession == null ||
            (!forceShow && !SettingsManager.Current.MediaFlyoutEnabled) ||
            FullscreenDetector.IsFullscreenApplicationRunning())
            return;

        bool showVolumeTogether = withVolume && SettingsManager.Current.VolumeControlEnabled;

        // If in toggle mode and flyout is visible, close it
        if (toggleMode && Visibility == Visibility.Visible && !_isHiding)
        {
            if (showVolumeTogether)
                volumeMixerWindow?.HideFlyout(); // start both close animations back to back

            CloseAnimation(this);
            _isHiding = true;
            cts.Cancel();
            await Task.Delay(getDuration());
            if (_isHiding)
            {
                Hide();
            }
            return;
        }

        UpdateUI(activeSession);

        if (nextUpWindow != null) // close NextUpWindow if it's open
        {
            nextUpWindow.Close();
            nextUpWindow = null;
        }

        if (_isHiding == true)
        {
            _isHiding = false;
            OpenAnimation(this, reserveNativeVolumeOsdSpace: true);
        }
        cts.Cancel();
        cts = new CancellationTokenSource();
        var token = cts.Token;
        Visibility = Visibility.Visible;
        WindowHelper.SetTopmost(this);

        if (showVolumeTogether)
            ShowVolumeFlyout(); // the media flyout is already on screen: both appear together

        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(100, token); // check if mouse is over every 100ms

                bool mouseOverMedia = WindowHelper.IsMouseOverWindow(this);
                bool mouseOverVolume = SettingsManager.Current.VolumeControlAboveMediaFlyout
                    && SettingsManager.Current.VolumeControlEnabled
                    && volumeMixerWindow != null
                    && volumeMixerWindow.IsVisible
                    && WindowHelper.IsMouseOverWindow(volumeMixerWindow); // sync with VolumeMixerWindow

                if (!mouseOverMedia && !mouseOverVolume && !SettingsManager.Current.MediaFlyoutAlwaysDisplay)
                {
                    await Task.Delay(SettingsManager.Current.Duration, token);

                    mouseOverMedia = WindowHelper.IsMouseOverWindow(this);
                    mouseOverVolume = SettingsManager.Current.VolumeControlAboveMediaFlyout
                        && SettingsManager.Current.VolumeControlEnabled
                        && volumeMixerWindow != null
                        && volumeMixerWindow.IsVisible
                        && WindowHelper.IsMouseOverWindow(volumeMixerWindow);

                    if (!mouseOverMedia && !mouseOverVolume)
                    {
                        CloseAnimation(this);
                        _isHiding = true;
                        await Task.Delay(getDuration());
                        if (_isHiding == false) return;
                        Hide();
                        break;
                    }
                }
            }
        }
        catch (TaskCanceledException)
        {
            // task was canceled, do nothing
        }
    }

    private void UpdateMediaFlyoutCloseButtonVisibility()
    {
        MediaFlyoutCloseButton.Visibility = SettingsManager.Current.MediaFlyoutAlwaysDisplay && !SettingsManager.Current.CompactLayout
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void SessionControl_SessionSwitcherRequested(object? sender, UIElement placementTarget)
        => ShowSessionSwitcherMenu(placementTarget);

    private void SessionControl_CloseFlyoutRequested(object? sender, EventArgs e)
        => ShowMediaFlyout(toggleMode: true, withVolume: true);

    private void AttachSessionControl(MediaSessionControl control)
    {
        control.SessionSwitcherRequested += SessionControl_SessionSwitcherRequested;
        control.CloseFlyoutRequested += SessionControl_CloseFlyoutRequested;
    }

    private void DetachSessionControl(MediaSessionControl control)
    {
        control.SessionSwitcherRequested -= SessionControl_SessionSwitcherRequested;
        control.CloseFlyoutRequested -= SessionControl_CloseFlyoutRequested;
    }

    private void UpdateFlyoutBackgroundBlur(string? sessionId, System.Windows.Media.Imaging.BitmapImage? image, int thumbnailHash)
    {
        int blurStyle = SettingsManager.Current.MediaFlyoutBackgroundBlur;
        BackgroundImageStyle1.Visibility = blurStyle == 1 ? Visibility.Visible : Visibility.Collapsed;
        BackgroundImageStyle2.Visibility = blurStyle == 2 ? Visibility.Visible : Visibility.Collapsed;
        BackgroundImageStyle3.Visibility = blurStyle == 3 ? Visibility.Visible : Visibility.Collapsed;

        if (blurStyle == 0 || image == null)
        {
            BackgroundImageStyle1.Source = null;
            BackgroundImageStyle2.Source = null;
            BackgroundImageStyle3.Source = null;
            _lastBlurSessionId = sessionId;
            _lastBlurThumbnailHash = 0;
            _lastBackgroundBlurStyle = blurStyle;
            return;
        }

        if (_lastBlurSessionId == sessionId &&
            _lastBlurThumbnailHash == thumbnailHash &&
            _lastBackgroundBlurStyle == blurStyle)
            return;

        _lastBlurSessionId = sessionId;
        _lastBlurThumbnailHash = thumbnailHash;
        _lastBackgroundBlurStyle = blurStyle;

        var croppedImage = BitmapHelper.CropToSquare(image);
        switch (blurStyle)
        {
            case 1:
                BackgroundImageStyle1.Source = croppedImage;
                break;
            case 2:
                BackgroundImageStyle2.Source = croppedImage;
                break;
            case 3:
                BackgroundImageStyle3.Source = croppedImage;
                break;
        }
    }

    private void UpdateUI(IMediaPlaybackSession? activeSession)
    {
        if (_layout != SettingsManager.Current.CompactLayout ||
            _shuffleEnabled != SettingsManager.Current.ShuffleEnabled ||
            _repeatEnabled != SettingsManager.Current.RepeatEnabled ||
            _playerInfoEnabled != SettingsManager.Current.PlayerInfoEnabled ||
            _centerTitleArtist != SettingsManager.Current.CenterTitleArtist ||
            _alwaysDisplay != SettingsManager.Current.MediaFlyoutAlwaysDisplay)
            UpdateUILayout();

        List<IMediaPlaybackSession> validSessions = [];
        List<(IMediaPlaybackSession Session, MediaSessionControl Control)> activePairs = [];

        Dispatcher.Invoke(() =>
        {
            UpdateMediaFlyoutCloseButtonVisibility();
            this.EnableBackdrop();

            if (SettingsManager.Current.MediaFlyoutAcrylicWindowEnabled != _acrylicEnabled
                || SettingsManager.Current.AppTheme != _themeOption)
            {
                _acrylicEnabled = SettingsManager.Current.MediaFlyoutAcrylicWindowEnabled;
                _themeOption = SettingsManager.Current.AppTheme;
                ToggleBlur();
            }

            validSessions = GetAllowedPlaybackSessions().ToList();

            if (validSessions.Count == 0)
            {
                foreach (var existing in _sessionControls.Values)
                {
                    DetachSessionControl(existing);
                }
                _sessionControls.Clear();
                MediaSessionsStackPanel.Children.Clear();

                _noMediaControl ??= new MediaSessionControl();
                _noMediaControl.ApplyLayout(isPrimaryCard: true);
                _noMediaControl.ShowNoMediaState();
                MediaSessionsStackPanel.Children.Add(_noMediaControl);

                UpdateFlyoutBackgroundBlur(null, null, 0);
            }
            else
            {
                _noMediaControl = null;
                var validIds = validSessions.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);

                // Remove closed sessions cleanly
                foreach (var staleId in _sessionControls.Keys.Where(id => !validIds.Contains(id)).ToList())
                {
                    var staleControl = _sessionControls[staleId];
                    DetachSessionControl(staleControl);
                    _sessionControls.Remove(staleId);
                    MediaSessionsStackPanel.Children.Remove(staleControl);
                }

                // Clear placeholder if transitioning from 0 -> N sessions
                if (MediaSessionsStackPanel.Children.Count == 1 &&
                    MediaSessionsStackPanel.Children[0] is MediaSessionControl { Session: null } placeholder)
                {
                    MediaSessionsStackPanel.Children.Remove(placeholder);
                }

                // Reuse or create MediaSessionControl instances without visual tree churn
                for (int i = 0; i < validSessions.Count; i++)
                {
                    var session = validSessions[i];
                    if (!_sessionControls.TryGetValue(session.Id, out var control))
                    {
                        control = new MediaSessionControl();
                        AttachSessionControl(control);
                        _sessionControls[session.Id] = control;
                    }

                    if (MediaSessionsStackPanel.Children.Count <= i || !ReferenceEquals(MediaSessionsStackPanel.Children[i], control))
                    {
                        MediaSessionsStackPanel.Children.Remove(control);
                        MediaSessionsStackPanel.Children.Insert(Math.Min(i, MediaSessionsStackPanel.Children.Count), control);
                    }

                    control.ApplyLayout(isPrimaryCard: i == 0);
                    control.ApplyPlaybackState(session);
                    activePairs.Add((session, control));
                }
            }

            int visibleRows = Math.Clamp(Math.Max(1, validSessions.Count), 1, 5);
            bool compact = SettingsManager.Current.CompactLayout;
            Width = MediaSessionControl.GetFlyoutWidth(compact);
            double cardHeight = MediaSessionControl.GetCardHeight(compact);
            Height = cardHeight * visibleRows + 12;
        });

        _flyoutMetadataCts?.Cancel();
        _flyoutMetadataCts?.Dispose();
        _flyoutMetadataCts = null;

        if (activePairs.Count == 0)
            return;

        _flyoutMetadataCts = new CancellationTokenSource();
        var token = _flyoutMetadataCts.Token;
        string? blurSessionId = activeSession?.Id ?? validSessions[0].Id;

        _ = RefreshFlyoutMetadataAsync(activePairs, blurSessionId, token);
    }

    private async Task RefreshFlyoutMetadataAsync(
        List<(IMediaPlaybackSession Session, MediaSessionControl Control)> pairs,
        string? blurSessionId,
        CancellationToken token)
    {
        try
        {
            var tasks = pairs.Select(async pair =>
            {
                var meta = await pair.Session.GetMetadataAsync(token);
                if (token.IsCancellationRequested)
                    return (pair.Session.Id, pair.Control, Title: string.Empty, Artist: string.Empty, Image: (System.Windows.Media.Imaging.BitmapImage?)null, Hash: 0, Accent: (SolidColorBrush?)null);

                System.Windows.Media.Imaging.BitmapImage? image = null;
                int hash = 0;
                SolidColorBrush? accent = null;

                if (meta.SmtcThumbnail != null)
                {
                    (image, hash, accent) = await Task.Run(() => BitmapHelper.GetThumbnailAndAccent(meta.SmtcThumbnail), token);
                }
                else if (!string.IsNullOrEmpty(meta.ArtworkUrl))
                {
                    (image, hash, accent) = await BitmapHelper.GetThumbnailAndAccentFromUrlAsync(meta.ArtworkUrl, token);
                }

                return (pair.Session.Id, pair.Control, meta.Title, meta.Artist, Image: image, Hash: hash, Accent: accent);
            }).ToArray();

            var snapshots = await Task.WhenAll(tasks);
            if (token.IsCancellationRequested)
                return;

            await Dispatcher.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested)
                    return;

                foreach (var snap in snapshots)
                {
                    if (!_sessionControls.TryGetValue(snap.Id, out var currentControl) || !ReferenceEquals(currentControl, snap.Control))
                        continue;

                    snap.Control.ApplyMediaMetadata(snap.Title, snap.Artist, snap.Image, snap.Hash, snap.Accent);

                    if (snap.Id == blurSessionId)
                    {
                        UpdateFlyoutBackgroundBlur(snap.Id, snap.Image, snap.Hash);
                    }
                }
            });
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer update
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed refreshing flyout session metadata");
        }
    }

    private void UpdateUILayout() // update the layout based on the settings
    {
        Dispatcher.Invoke(() =>
        {
            bool compact = SettingsManager.Current.CompactLayout;
            Width = MediaSessionControl.GetFlyoutWidth(compact);

            for (int i = 0; i < MediaSessionsStackPanel.Children.Count; i++)
            {
                if (MediaSessionsStackPanel.Children[i] is MediaSessionControl card)
                {
                    card.ApplyLayout(isPrimaryCard: i == 0);
                }
            }
        });

        _layout = SettingsManager.Current.CompactLayout;
        _repeatEnabled = SettingsManager.Current.RepeatEnabled;
        _shuffleEnabled = SettingsManager.Current.ShuffleEnabled;
        _playerInfoEnabled = SettingsManager.Current.PlayerInfoEnabled;
        _centerTitleArtist = SettingsManager.Current.CenterTitleArtist;
        _alwaysDisplay = SettingsManager.Current.MediaFlyoutAlwaysDisplay;
    }

    private void CleanupResources()
    {
        // try saving settings before exiting if window is still open
        // disabled because it caused too many issues (race conditions, shutdown exceptions), could look into another time
        //try
        //{
        //    SettingsManager.SaveSettings();
        //    Logger.Info("Settings saved successfully on cleanup");
        //}
        //catch (Exception ex)
        //{
        //    Logger.Error(ex, "Error while saving settings on cleanup");
        //}

        // should be handled automatically on app exit but just in case
        try
        {
            _isCleaningUp = true;
            _displayRefreshTimer.Stop();
            _displayRefreshTimer.Tick -= DisplayRefreshTimer_Tick;

            // unsubscribe from events
            mediaManager.OnAnyMediaPropertyChanged -= MediaManager_OnAnyMediaPropertyChanged;
            mediaManager.OnAnyPlaybackStateChanged -= CurrentSession_OnPlaybackStateChanged;
            mediaManager.OnAnySessionClosed -= MediaManager_OnAnySessionClosed;
            _zenBridgeService.SessionsChanged -= ZenBridge_SessionsChanged;
            _zenBridgeService.Dispose();
            _vlcBridgeService.SessionChanged -= VlcBridge_SessionChanged;
            _vlcBridgeService.Dispose();

            DisposeHeadsetControls();

            // dispose managed resources
            _flyoutMetadataCts?.Cancel();
            _flyoutMetadataCts?.Dispose();
            foreach (var control in _sessionControls.Values)
            {
                DetachSessionControl(control);
            }
            _sessionControls.Clear();
            cts?.Cancel();
            cts?.Dispose();

            TaskbarVisualizerControl.DisposeVisualizer();

            // unhook hooks
            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }

            DeregisterShellHookWindow(new WindowInteropHelper(this).Handle);

            // clean up other resources
            if (lockWindow?.IsLoaded == true)
                lockWindow.Close();

            if (nextUpWindow?.IsLoaded == true)
                nextUpWindow.Close();

            if (taskbarWindow?.IsLoaded == true)
                taskbarWindow.Close();

            if (volumeMixerWindow?.IsLoaded == true)
                volumeMixerWindow.Close();

            // restore native volume OSD
            VolumeMixerWindow.ShowVolumeOsd();

            // dispose mutex
            singleton?.Dispose();

            // flush and close NLog
            NLog.LogManager.Shutdown();
        }
        catch (ObjectDisposedException)
        {
            // harmless shutdown exceptions
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        try
        {
            CleanupResources();
        }
        finally
        {
            base.OnClosed(e);
        }
    }

    private void MicaWindow_MouseEnter(object sender, MouseEventArgs e) // keep the flyout open when mouse is over
    {
        ShowMediaFlyout();
    }

    private void NotifyIconQuit_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            CleanupResources();
        }
        finally
        {
            Application.Current.Shutdown();
        }
    }

    private async Task<bool> WaitForExplorerReadyAsync(int timeoutMs = 60000)
    {
        var sw = Stopwatch.StartNew();

        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            IntPtr taskbar = FindWindow("Shell_TrayWnd", null);
            if (taskbar != IntPtr.Zero &&
                GetWindowRect(taskbar, out NativeMethods.RECT rect) &&
                rect.Right > rect.Left &&
                rect.Bottom > rect.Top)
            {
                return true; // taskbar exists and has geometry
            }

            await Task.Delay(200);
        }

        return false;
    }

    private void ScheduleDisplayEnvironmentRefresh(string reason)
    {
        if (_isCleaningUp)
            return;

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ScheduleDisplayEnvironmentRefresh(reason));
            return;
        }

        _pendingDisplayRefreshReason = reason;
        _displayRefreshTimer.Stop();
        _displayRefreshTimer.Start();
        Logger.Debug($"Scheduled display environment refresh: {reason}");
    }

    private void DisplayRefreshTimer_Tick(object? sender, EventArgs e)
    {
        _displayRefreshTimer.Stop();

        if (_displayRefreshInProgress || _isCleaningUp)
            return;

        _displayRefreshInProgress = true;
        try
        {
            RefreshDisplayEnvironment(_pendingDisplayRefreshReason);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to refresh windows after a display environment change");
        }
        finally
        {
            _displayRefreshInProgress = false;
        }
    }

    private void RefreshDisplayEnvironment(string reason)
    {
        var monitors = MonitorUtil.GetMonitors();
        if (monitors.Count == 0)
        {
            Logger.Warn($"Display environment refresh skipped because no monitors were found ({reason})");
            return;
        }

        Logger.Info($"Refreshing window DPI and placement after display change ({reason}); monitors={monitors.Count}");

        // Stop placement animations that were calculated for the previous work area.
        cts.Cancel();
        _isHiding = true;
        Hide();

        // Move the reusable main flyout to its current target monitor while hidden. This
        // makes WPF process the per-monitor DPI transition before the next animation.
        var targetMonitor = getSelectedMonitor();
        WindowHelper.SetPosition(this, targetMonitor.workArea.Left, targetMonitor.workArea.Top);
        InvalidateMeasure();
        InvalidateArrange();
        InvalidateVisual();
        UpdateLayout();
        SyncMainFlyoutSizeToCurrentDpi(new WindowInteropHelper(this).Handle);

        // These windows retain an HWND for their lifetime. Recreate them so they cannot
        // keep DPI, work-area, taskbar-parent, or UI Automation state from the old topology.
        if (lockWindow != null)
        {
            lockWindow.Close();
            lockWindow = null;
        }

        if (nextUpWindow != null)
        {
            nextUpWindow.Close();
            nextUpWindow = null;
        }

        if (volumeMixerWindow != null)
        {
            volumeMixerWindow.Close();
            volumeMixerWindow = new VolumeMixerWindow();
        }

        RecreateTaskbarWindow();
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        // detect key presses from both keyboard hook and shell hook to show flyouts
        if (msg == WM_SHELLHOOK && wParam == HSHELL_APPCOMMAND)
        {
            int highWord = (int)(lParam >> 16);
            int cmd = highWord & 0x0FFF;
            int device = highWord & 0xF000;

            bool isMediaCommand = cmd switch
            {
                APPCOMMAND_MEDIA_PLAY_PAUSE => true,
                APPCOMMAND_MEDIA_NEXTTRACK => true,
                APPCOMMAND_MEDIA_PREVIOUSTRACK => true,
                APPCOMMAND_MEDIA_STOP => true,
                _ => false
            };

            bool isVolumeCommand = cmd switch
            {
                APPCOMMAND_VOLUME_MUTE => true,
                APPCOMMAND_VOLUME_DOWN => true,
                APPCOMMAND_VOLUME_UP => true,
                _ => false
            };

            if (!isMediaCommand && !isVolumeCommand)
                return 0;

            bool isKeyCommand = device == FAPPCOMMAND_KEY;
            bool isHeadsetCommand = device == FAPPCOMMAND_OEM;

            if (!isKeyCommand && !isHeadsetCommand)
                return 0;

            bool result = false;

            if (isMediaCommand || isVolumeCommand)
            {
                if (isMediaCommand || (!SettingsManager.Current.MediaFlyoutVolumeKeysExcluded && isVolumeCommand))
                    result = TryShowMediaFlyoutDebounced();

                ShowVolumeFlyout();
            }

            if (!result && !isVolumeCommand)
            {
                return 0;
            }

            handled = true;
        }
        else if (msg == WM_TASKBARCREATED)
        {
            Logger.Warn("Explorer restart detected (TaskbarCreated)");

            ExplorerRestarting = true;

            // Defer recovery, do NOT touch tray/taskbar immediately
            Dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    // Wait for Explorer to actually stabilize
                    if (await WaitForExplorerReadyAsync())
                    {
                        ExplorerRestarting = false;
                        Logger.Info("Explorer stabilized, resuming taskbar integration");

                        // Now it is safe to recreate tray icon
                        RecreateTrayIconSafely();
                    }
                    else
                    {
                        Logger.Warn("Explorer did not stabilize within timeout; keeping integration disabled");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Explorer recovery failed");
                }
            }, DispatcherPriority.Background);

            handled = true;
            return 0;
        }
        else if (msg == WM_DISPLAYCHANGE)
        {
            ScheduleDisplayEnvironmentRefresh("WM_DISPLAYCHANGE");
            return 0;
        }
        else if (msg == WM_DPICHANGED)
        {
            // Leave the message unhandled so WPF can update its internal DPI state,
            // then size the native HWND from the logical WPF dimensions exactly once.
            Dispatcher.BeginInvoke(() =>
            {
                InvalidateMeasure();
                InvalidateArrange();
                InvalidateVisual();
                UpdateLayout();
                SyncMainFlyoutSizeToCurrentDpi(hwnd);
            }, DispatcherPriority.Loaded);
            return 0;
        }
        else if (msg == WM_SETTINGCHANGE) // system settings changed
        {
            if (wParam.ToInt64() == SPI_SETWORKAREA)
            {
                ScheduleDisplayEnvironmentRefresh("WM_SETTINGCHANGE/SPI_SETWORKAREA");
                return 0;
            }

            string? changedSetting = lParam == IntPtr.Zero ? null : Marshal.PtrToStringUni(lParam);
            if (changedSetting == "SystemDockMode")
            {
                ScheduleDisplayEnvironmentRefresh($"WM_SETTINGCHANGE/{changedSetting}");
                return 0;
            }

            // check if the changed setting is related to theme or accent color
            if (changedSetting != "ImmersiveColorSet" && changedSetting != "WindowsThemeElement")
                return 0;

            Logger.Info($"System setting changed: {changedSetting}, from {msg}");

            try
            {
                // update theme for taskbar widget since it's independent from the main app theme
                ThemeManager.UpdateTaskbarWidget();
                // update Acrylic windows background colors
                WindowBlurHelper.AdjustBlurOpacityForAllWindows(SettingsManager.Current.AcrylicBlurOpacity);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to apply theme changes to taskbar widgets or Acrylic windows");
            }
            return 0;
        }

        return 0;
    }

    private void SyncMainFlyoutSizeToCurrentDpi(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return;

        uint dpi = GetDpiForWindow(hwnd);
        if (dpi == 0)
            return;

        double dpiScale = dpi / 96.0;
        int pixelWidth = (int)Math.Ceiling(Width * dpiScale);
        int pixelHeight = (int)Math.Ceiling(Height * dpiScale);

        if (!SetWindowPos(
            hwnd,
            0,
            0,
            0,
            pixelWidth,
            pixelHeight,
            SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE))
        {
            Logger.Warn($"Failed to resize MainWindow for DPI; HWND=0x{hwnd.ToInt64():X}, DPI={dpi}, Size={pixelWidth}x{pixelHeight}, Win32Error={Marshal.GetLastWin32Error()}");
        }
    }

    private void RecreateTrayIconSafely()
    {
        try
        {
            nIcon.Visibility = Visibility.Collapsed;

            if (!SettingsManager.Current.NIconHide)
            {
                nIcon.Visibility = Visibility.Visible;
                nIcon.Register();
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to recreate tray icon safely");
        }
    }

    private async void MicaWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Hide();
        UpdateUILayout();
        ThemeManager.ApplySavedTheme();

        // add tray icon hook when taskbar resets
        try
        {
            HwndSource? source = PresentationSource.FromVisual(this) as HwndSource;
            if (source != null)
            {
                source.AddHook(WndProc);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to initialize tray icon");
        }

        try
        {
            await LicenseManager.Instance.InitializeAsync();

            // Sync license status from LicenseManager to SettingsManager
            SettingsManager.Current.IsPremiumUnlocked = LicenseManager.Instance.IsPremiumUnlocked;
            SettingsManager.Current.IsStoreVersion = LicenseManager.Instance.IsStoreVersion;
            SettingsManager.SaveSettings();

            Logger.Info($"License synced on startup - Store: {SettingsManager.Current.IsStoreVersion}, Premium: {SettingsManager.Current.IsPremiumUnlocked}");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to initialize license");
        }

        // Add the experiments loading here
        await ExperimentsService.GetExperimentsAsync();

        BitmapHelper.GetDominantColors(1);
        volumeMixerWindow = new VolumeMixerWindow();
        taskbarWindow = new TaskbarWindow();
        UpdateTaskbar();
    }

    public void RecreateTaskbarWindow()
    {
        try
        {
            Logger.Info("Recreating Taskbar Widget window");

            if (taskbarWindow != null)
            {
                try
                {
                    taskbarWindow.Close();
                }
                catch { }

                taskbarWindow = null;
            }

            taskbarWindow = new();
            UpdateTaskbar();

            Logger.Info("Taskbar Widget window recreated successfully");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to recreate Taskbar Widget window");
        }
    }

    private void nIcon_LeftClick(Wpf.Ui.Tray.Controls.NotifyIcon sender, RoutedEventArgs e) // change the behavior of the tray icon
    {
        if (SettingsManager.Current.NIconLeftClick == 0)
        {
            openSettings(sender, e);
            //Wpf.Ui.Appearance.ApplicationThemeManager.Apply(ApplicationTheme.Light, WindowBackdropType.Mica); // to change the theme
            //ThemeService themeService = new ThemeService();
            //themeService.ChangeTheme(MicaWPF.Core.Enums.WindowsTheme.Light);
        }
        else if (SettingsManager.Current.NIconLeftClick == 1) ShowMediaFlyout(withVolume: true);
    }

    private Task PauseOtherSessions(IMediaPlaybackSession currentMediaSession)
    {
        return Task.WhenAll(
            GetAllowedPlaybackSessions().Select(session =>
            {
                if (session.Id != currentMediaSession.Id && session.IsPlaying)
                {
                    return session.TryTogglePlayPauseAsync();
                }
                return Task.CompletedTask;
            })
        );
    }
    internal void ToggleBlur()
    {
        if (SettingsManager.Current.MediaFlyoutAcrylicWindowEnabled)
        {
            WindowBlurHelper.EnableBlur(this);
        }
        else
        {
            WindowBlurHelper.DisableBlur(this);
        }
    }

    private void MediaFlyoutCloseButton_Click(object sender, RoutedEventArgs e)
    {
        // Use the updated ShowMediaFlyout method with toggle mode to close the flyout
        ShowMediaFlyout(toggleMode: true, withVolume: true);
    }
}