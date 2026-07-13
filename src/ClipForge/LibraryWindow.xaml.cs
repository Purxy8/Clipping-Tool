using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ClipForge.Models;
using ClipForge.Services;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MenuItem = System.Windows.Controls.MenuItem;
using MessageBox = System.Windows.MessageBox;

namespace ClipForge;

/// <summary>
/// A bounded, virtualized view of the local ClipForge library. Only the selected
/// clip is ever connected to a decoder, and that decoder is released whenever
/// the window is not foreground so it cannot compete with capture or a game.
/// </summary>
public partial class LibraryWindow : Window
{
    private const int InitialClipLimit = 100;

    private readonly ClipLibraryService _clipLibraryService;
    private readonly string _saveDirectory;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly DispatcherTimer _playerTimer;
    private readonly NativeWindowThemeService _nativeWindowThemeService;
    private readonly string? _initialPreferredPath;
    private readonly object _refreshCancellationGate = new();

    private CancellationTokenSource? _activeRefreshCancellation;
    private ClipLibraryItem? _currentClip;
    private TimeSpan? _positionToRestore;
    private bool _isLoaded;
    private bool _isClosing;
    private bool _refreshPending;
    private bool _suppressSelectionAutoplay;
    private bool _sourceReleasedForBackground;
    private bool _playWhenOpened;
    private bool _isPlaying;
    private bool _isSeeking;
    private bool _resumeAfterSeek;
    private bool _isUpdatingControls;
    private bool _isMuted;
    private double _volumeBeforeMute = 0.8;
    private long _refreshGeneration;
    private LibraryMediaOpenPlan? _pendingOpenPlan;

    public LibraryWindow(
        ClipLibraryService clipLibraryService,
        string saveDirectory,
        ClipLibraryItem? initiallySelectedClip = null)
    {
        ArgumentNullException.ThrowIfNull(clipLibraryService);
        ArgumentException.ThrowIfNullOrWhiteSpace(saveDirectory);

        _clipLibraryService = clipLibraryService;
        _saveDirectory = Path.GetFullPath(saveDirectory);
        _initialPreferredPath = initiallySelectedClip?.FullPath;

        InitializeComponent();
        _nativeWindowThemeService = new NativeWindowThemeService(this);
        _playerTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };
        _playerTimer.Tick += PlayerTimer_Tick;

        Loaded += LibraryWindow_Loaded;
        Activated += LibraryWindow_Activated;
        Deactivated += LibraryWindow_Deactivated;
        IsVisibleChanged += LibraryWindow_IsVisibleChanged;
        Closing += LibraryWindow_Closing;
        Closed += LibraryWindow_Closed;
    }

    /// <summary>
    /// Signals that the main gallery should refresh after this window closes.
    /// </summary>
    public bool LibraryChanged { get; private set; }

    private async void LibraryWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= LibraryWindow_Loaded;
        _isLoaded = true;
        await RefreshLibraryAsync(_initialPreferredPath);
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        await RefreshLibraryAsync(_currentClip?.FullPath);

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private async Task RefreshLibraryAsync(string? preferredPath = null)
    {
        if (_isClosing || !_isLoaded)
        {
            return;
        }

        if (!IsVisible || !IsActive)
        {
            _refreshPending = true;
            return;
        }

        var generation = Interlocked.Increment(ref _refreshGeneration);
        var refreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        lock (_refreshCancellationGate)
        {
            _activeRefreshCancellation?.Cancel();
            _activeRefreshCancellation = refreshCancellation;
        }

        RefreshButton.IsEnabled = false;
        LoadingState.Visibility = Visibility.Visible;
        EmptyLibraryState.Visibility = Visibility.Collapsed;
        LibraryStatusText.Text = "Loading up to 100 local clips…";

        try
        {
            var clips = await _clipLibraryService.GetRecentClipsAsync(
                _saveDirectory,
                count: InitialClipLimit,
                includeThumbnails: true,
                refreshCancellation.Token);

            refreshCancellation.Token.ThrowIfCancellationRequested();
            if (generation != Volatile.Read(ref _refreshGeneration) || !IsVisible || !IsActive)
            {
                _refreshPending = true;
                return;
            }

            var selectedPath = preferredPath ?? _currentClip?.FullPath;
            ClipList.ItemsSource = clips;
            ClipCountText.Text = clips.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            LibraryStatusText.Text = clips.Count switch
            {
                0 => "No saved clips found in the current folder.",
                1 => "1 local clip · newest first",
                _ when clips.Count == InitialClipLimit =>
                    $"Showing the newest {clips.Count} local clips",
                _ => $"{clips.Count} local clips · newest first"
            };
            LoadingState.Visibility = Visibility.Collapsed;
            EmptyLibraryState.Visibility = clips.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;

            var selected = selectedPath is { Length: > 0 }
                ? clips.FirstOrDefault(clip => clip.FullPath.Equals(
                    selectedPath,
                    StringComparison.OrdinalIgnoreCase))
                : null;
            selected ??= clips.FirstOrDefault();

            var restorePreviousPosition = _sourceReleasedForBackground &&
                                          _currentClip is not null &&
                                          selected is not null &&
                                          _currentClip.FullPath.Equals(
                                              selected.FullPath,
                                              StringComparison.OrdinalIgnoreCase);
            _suppressSelectionAutoplay = true;
            try
            {
                ClipList.SelectedItem = selected;
                if (selected is not null)
                {
                    ClipList.ScrollIntoView(selected);
                }
            }
            finally
            {
                _suppressSelectionAutoplay = false;
            }

            if (selected is null)
            {
                ClearPlayer();
            }
            else
            {
                OpenClip(
                    selected,
                    autoplay: false,
                    preserveRestorePosition: restorePreviousPosition);
            }

            _refreshPending = false;
        }
        catch (OperationCanceledException) when (refreshCancellation.IsCancellationRequested)
        {
            // A focus change, close, or newer refresh superseded this work.
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException or
                ArgumentException or NotSupportedException)
        {
            LoadingState.Visibility = Visibility.Collapsed;
            LibraryStatusText.Text = $"The library could not be loaded. {exception.Message}";
        }
        finally
        {
            var wasActiveRefresh = false;
            lock (_refreshCancellationGate)
            {
                if (ReferenceEquals(_activeRefreshCancellation, refreshCancellation))
                {
                    _activeRefreshCancellation = null;
                    wasActiveRefresh = true;
                }
            }

            if (wasActiveRefresh)
            {
                RefreshButton.IsEnabled = !_isClosing && IsVisible && IsActive;
            }

            refreshCancellation.Dispose();
        }
    }

    private void ClipList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isClosing ||
            _suppressSelectionAutoplay ||
            ClipList.SelectedItem is not ClipLibraryItem clip)
        {
            return;
        }

        OpenClip(clip, autoplay: true);
    }

    private void OpenClip(
        ClipLibraryItem clip,
        bool autoplay,
        bool preserveRestorePosition = false)
    {
        if (!ClipLibraryService.TryGetCurrentClipPath(
                _saveDirectory,
                clip,
                out var validatedClipPath))
        {
            ClearPlayer();
            ShowError("The selected clip changed or is no longer a safe local ClipForge recording. Refresh the library and try again.");
            _refreshPending = true;
            return;
        }

        ReleasePlayerSource(rememberPosition: preserveRestorePosition);
        _currentClip = clip;
        _sourceReleasedForBackground = false;
        _playWhenOpened = autoplay;
        SelectedClipNameText.Text = clip.FileName;
        SelectedClipDetailsText.Text =
            $"{clip.RecordedAtLocal:dd MMM yyyy · HH:mm} · {clip.FileSizeDisplay}";
        PlayerEmptyState.Visibility = Visibility.Collapsed;
        SurfacePlayButton.Visibility = Visibility.Visible;
        SetControlsEnabled(false);
        SetPlaying(false);
        SetSeekUi(TimeSpan.Zero, clip.Duration ?? TimeSpan.Zero);
        TimeText.Text = clip.Duration is { } duration
            ? $"0:00 / {FormatDuration(duration)}"
            : "0:00 / --:--";

        // LoadedBehavior=Manual lets ClipForge own playback state. Assigning
        // Source alone does not reliably build the WPF media graph on every
        // Windows media path, so waiting for MediaOpened before enabling Play can
        // deadlock. Prime explicitly while muted; MediaOpened either keeps playing
        // for a user selection or pauses for a background/restore load.
        var openPlan = LibraryMediaOpenPlan.Create(
            autoplay,
            VolumeSlider.Value / 100);
        _pendingOpenPlan = openPlan;
        LibraryPlayer.Volume = openPlan.PrimeVolume;
        LibraryPlayer.Source = new Uri(validatedClipPath, UriKind.Absolute);
        if (openPlan.MustPrimeWithPlay)
        {
            LibraryPlayer.Play();
        }
    }

    private void ClearPlayer()
    {
        ReleasePlayerSource(rememberPosition: false);
        _currentClip = null;
        _positionToRestore = null;
        _sourceReleasedForBackground = false;
        _playWhenOpened = false;
        SelectedClipNameText.Text = "Select a clip";
        SelectedClipDetailsText.Text = "Choose any recording from the library to watch it here.";
        PlayerEmptyState.Visibility = Visibility.Visible;
        SurfacePlayButton.Visibility = Visibility.Collapsed;
        SetControlsEnabled(false);
        SetPlaying(false);
        SetSeekUi(TimeSpan.Zero, TimeSpan.Zero);
        TimeText.Text = "0:00 / 0:00";
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentClip is null || LibraryPlayer.Source is null)
        {
            return;
        }

        if (_isPlaying)
        {
            LibraryPlayer.Pause();
            SetPlaying(false);
            return;
        }

        var duration = GetPlayerDuration();
        if (duration > TimeSpan.Zero &&
            LibraryPlayer.Position >= duration - TimeSpan.FromMilliseconds(250))
        {
            LibraryPlayer.Position = TimeSpan.Zero;
        }

        LibraryPlayer.Play();
        SetPlaying(true);
    }

    private void LibraryPlayer_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        PlayPauseButton_Click(sender, e);
        e.Handled = true;
    }

    private void RestartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentClip is null || LibraryPlayer.Source is null)
        {
            return;
        }

        LibraryPlayer.Position = TimeSpan.Zero;
        LibraryPlayer.Play();
        SetPlaying(true);
    }

    private void BackTenButton_Click(object sender, RoutedEventArgs e) =>
        SeekPlayerTo(LibraryPlayer.Position - TimeSpan.FromSeconds(10));

    private void ForwardTenButton_Click(object sender, RoutedEventArgs e) =>
        SeekPlayerTo(LibraryPlayer.Position + TimeSpan.FromSeconds(10));

    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentClip is null)
        {
            return;
        }

        if (_isMuted || VolumeSlider.Value <= 0)
        {
            _isMuted = false;
            VolumeSlider.Value = Math.Max(5, _volumeBeforeMute * 100);
        }
        else
        {
            _volumeBeforeMute = Math.Clamp(VolumeSlider.Value / 100, 0.05, 1);
            _isMuted = true;
            VolumeSlider.Value = 0;
        }

        ApplyVolume();
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        ApplyVolume();

    private void ApplyVolume(double? requestedVolume = null)
    {
        if (LibraryPlayer is null || MuteButton is null)
        {
            return;
        }

        var volume = Math.Clamp(requestedVolume ?? VolumeSlider.Value / 100, 0, 1);
        LibraryPlayer.Volume = volume;
        _isMuted = volume <= 0.001;
        if (!_isMuted)
        {
            _volumeBeforeMute = volume;
        }

        MuteButton.Content = _isMuted ? "🔇" : "🔊";
        MuteButton.ToolTip = _isMuted ? "Unmute clip audio" : "Mute clip audio";
    }

    private void LibraryPlayer_MediaOpened(object sender, RoutedEventArgs e)
    {
        var openPlan = _pendingOpenPlan;
        _pendingOpenPlan = null;
        if (_isClosing ||
            !IsVisible ||
            !IsActive ||
            LibraryPlayer.Source is null ||
            _currentClip is null ||
            openPlan is null)
        {
            // A late graph-open event must never re-enable playback or restore
            // audible volume after Alt-Tab, owner hide, or shutdown.
            LibraryPlayer.Volume = 0;
            ReleasePlayerForBackground();
            return;
        }

        // Stop the muted priming playback before restoring position or volume.
        LibraryPlayer.Pause();
        PlayerEmptyState.Visibility = Visibility.Collapsed;
        SetControlsEnabled(true);

        if (_positionToRestore is { } restorePosition)
        {
            var duration = GetPlayerDuration();
            LibraryPlayer.Position = duration > TimeSpan.Zero
                ? TimeSpan.FromSeconds(Math.Clamp(
                    restorePosition.TotalSeconds,
                    0,
                    duration.TotalSeconds))
                : restorePosition;
            _positionToRestore = null;
        }

        var shouldAutoplay = _playWhenOpened && openPlan.Value.ContinueAfterOpened;
        _playWhenOpened = false;
        ApplyVolume(openPlan.Value.PlaybackVolume);
        if (shouldAutoplay && IsVisible && IsActive)
        {
            LibraryPlayer.Play();
            SetPlaying(true);
        }
        else
        {
            SetPlaying(false);
        }

        UpdatePlayerTime();
    }

    private void LibraryPlayer_MediaEnded(object sender, RoutedEventArgs e)
    {
        LibraryPlayer.Pause();
        LibraryPlayer.Position = TimeSpan.Zero;
        SetPlaying(false);
        UpdatePlayerTime();
    }

    private void LibraryPlayer_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        _pendingOpenPlan = null;
        _playWhenOpened = false;
        if (_isClosing ||
            !IsVisible ||
            !IsActive ||
            LibraryPlayer.Source is null ||
            _currentClip is null)
        {
            LibraryPlayer.Volume = 0;
            ReleasePlayerForBackground();
            return;
        }

        // Detach a failed graph before the owner warning deactivates this
        // window. Otherwise the background lifecycle can mark the failed
        // source for restore and reopen it as soon as the dialog closes.
        ReleasePlayerSource(rememberPosition: false);
        _sourceReleasedForBackground = false;
        SetControlsEnabled(false);
        SurfacePlayButton.Visibility = Visibility.Collapsed;
        ShowError($"This clip could not be played inside ClipForge. {e.ErrorException.Message}");
    }

    private void SetPlaying(bool isPlaying)
    {
        _isPlaying = isPlaying;
        PlayPauseButton.Content = isPlaying ? "⏸" : "▶";
        PlayPauseButton.ToolTip = isPlaying ? "Pause clip" : "Play clip";
        SurfacePlayButton.Visibility = !isPlaying &&
                                       _currentClip is not null &&
                                       LibraryPlayer.Source is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (isPlaying && IsVisible && IsActive)
        {
            _playerTimer.Start();
        }
        else
        {
            _playerTimer.Stop();
        }
    }

    private void PlayerTimer_Tick(object? sender, EventArgs e) => UpdatePlayerTime();

    private void UpdatePlayerTime()
    {
        if (_currentClip is null || LibraryPlayer.Source is null)
        {
            return;
        }

        var total = GetPlayerDuration();
        var position = total > TimeSpan.Zero && LibraryPlayer.Position > total
            ? total
            : LibraryPlayer.Position;
        TimeText.Text =
            $"{FormatDuration(position)} / {(total > TimeSpan.Zero ? FormatDuration(total) : "--:--")}";
        if (!_isSeeking)
        {
            SetSeekUi(position, total);
        }
    }

    private void SetControlsEnabled(bool isEnabled)
    {
        PlayPauseButton.IsEnabled = isEnabled;
        RestartButton.IsEnabled = isEnabled;
        BackTenButton.IsEnabled = isEnabled;
        ForwardTenButton.IsEnabled = isEnabled;
        MuteButton.IsEnabled = isEnabled;
        VolumeSlider.IsEnabled = isEnabled;
        SurfacePlayButton.IsEnabled = isEnabled;
        SeekSlider.IsEnabled = isEnabled && GetPlayerDuration() > TimeSpan.Zero;
    }

    private TimeSpan GetPlayerDuration() => LibraryPlayer.NaturalDuration.HasTimeSpan
        ? LibraryPlayer.NaturalDuration.TimeSpan
        : _currentClip?.Duration ?? TimeSpan.Zero;

    private void SetSeekUi(TimeSpan position, TimeSpan total)
    {
        _isUpdatingControls = true;
        try
        {
            var maximumSeconds = total > TimeSpan.Zero ? total.TotalSeconds : 1;
            SeekSlider.Maximum = maximumSeconds;
            SeekSlider.Value = Math.Clamp(position.TotalSeconds, 0, maximumSeconds);
            SeekSlider.IsEnabled = _currentClip is not null &&
                                   LibraryPlayer.Source is not null &&
                                   total > TimeSpan.Zero;
        }
        finally
        {
            _isUpdatingControls = false;
        }
    }

    private void SeekPlayerTo(TimeSpan requestedPosition, bool updateSlider = true)
    {
        if (_currentClip is null || LibraryPlayer.Source is null)
        {
            return;
        }

        var total = GetPlayerDuration();
        if (total <= TimeSpan.Zero)
        {
            return;
        }

        var target = TimeSpan.FromSeconds(Math.Clamp(
            requestedPosition.TotalSeconds,
            0,
            total.TotalSeconds));
        LibraryPlayer.Position = target;
        TimeText.Text = $"{FormatDuration(target)} / {FormatDuration(total)}";
        if (updateSlider)
        {
            SetSeekUi(target, total);
        }
    }

    private void SeekSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!SeekSlider.IsEnabled || _currentClip is null)
        {
            return;
        }

        _isSeeking = true;
        _resumeAfterSeek = _isPlaying;
        if (_resumeAfterSeek)
        {
            LibraryPlayer.Pause();
            _playerTimer.Stop();
        }

        if (SeekSlider.ActualWidth > 0)
        {
            var pointer = e.GetPosition(SeekSlider);
            SeekSlider.Value = Math.Clamp(
                pointer.X / SeekSlider.ActualWidth * SeekSlider.Maximum,
                SeekSlider.Minimum,
                SeekSlider.Maximum);
        }
    }

    private void SeekSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) =>
        FinishPlayerSeek();

    private void FinishPlayerSeek()
    {
        if (!_isSeeking)
        {
            return;
        }

        SeekPlayerTo(TimeSpan.FromSeconds(SeekSlider.Value));
        _isSeeking = false;
        if (_resumeAfterSeek)
        {
            LibraryPlayer.Play();
            SetPlaying(true);
        }
        else
        {
            SetPlaying(false);
            UpdatePlayerTime();
        }

        _resumeAfterSeek = false;
    }

    private void SeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingControls || !_isSeeking || _currentClip is null)
        {
            return;
        }

        SeekPlayerTo(TimeSpan.FromSeconds(e.NewValue), updateSlider: false);
    }

    private void SeekSlider_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!SeekSlider.IsEnabled || _currentClip is null)
        {
            return;
        }

        var requested = e.Key switch
        {
            Key.Left or Key.Down => LibraryPlayer.Position - TimeSpan.FromSeconds(5),
            Key.Right or Key.Up => LibraryPlayer.Position + TimeSpan.FromSeconds(5),
            Key.PageDown => LibraryPlayer.Position - TimeSpan.FromSeconds(10),
            Key.PageUp => LibraryPlayer.Position + TimeSpan.FromSeconds(10),
            Key.Home => TimeSpan.Zero,
            Key.End => GetPlayerDuration(),
            _ => (TimeSpan?)null
        };
        if (requested is null)
        {
            return;
        }

        SeekPlayerTo(requested.Value);
        e.Handled = true;
    }

    private async void DeleteClipMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_isClosing || sender is not MenuItem { Tag: ClipLibraryItem clip })
        {
            return;
        }

        var confirmation = MessageBox.Show(
            this,
            $"Permanently delete {clip.FileName}?\n\nThis cannot be undone.",
            "Delete ClipForge clip",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        var deletingCurrentClip = _currentClip is not null &&
                                  _currentClip.FullPath.Equals(
                                      clip.FullPath,
                                      StringComparison.OrdinalIgnoreCase);
        if (deletingCurrentClip)
        {
            ClearPlayer();
        }

        var result = ClipLibraryService.DeleteCurrentClip(_saveDirectory, clip);
        if (result == ClipDeletionResult.Deleted)
        {
            ClipList.ItemsSource = null;
            _clipLibraryService.RemoveCachedThumbnail(clip);
            LibraryChanged = true;
            await RefreshLibraryAsync();
            return;
        }

        await RefreshLibraryAsync(_currentClip?.FullPath);
        ShowError(result == ClipDeletionResult.ChangedOrUnsafe
            ? "The clip changed or is no longer a safe local ClipForge recording, so it was not deleted."
            : "The clip could not be deleted. Close any app using it, then try again.");
    }

    private void ShowInExplorerMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: ClipLibraryItem clip })
        {
            return;
        }

        try
        {
            if (!ClipLibraryService.TryGetCurrentClipPath(
                    _saveDirectory,
                    clip,
                    out var validatedPath))
            {
                ShowError("That clip changed or is no longer a safe local ClipForge recording. Refresh the library and try again.");
                _refreshPending = true;
                return;
            }

            RevealClipInExplorer(validatedPath);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException or
                Win32Exception or ArgumentException or NotSupportedException)
        {
            ShowError($"The clip could not be shown in File Explorer. {exception.Message}");
        }
    }

    private static void RevealClipInExplorer(string validatedClipPath)
    {
        if (!Path.IsPathFullyQualified(validatedClipPath) || !File.Exists(validatedClipPath))
        {
            throw new FileNotFoundException(
                "The requested local clip does not exist.",
                validatedClipPath);
        }

        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var explorerPath = Path.Combine(windowsDirectory, "explorer.exe");
        var explorer = new FileInfo(explorerPath);
        if (!explorer.Exists ||
            (explorer.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new FileNotFoundException(
                "The trusted Windows File Explorer executable is unavailable.",
                explorerPath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = explorer.FullName,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add($"/select,{Path.GetFullPath(validatedClipPath)}");
        _ = Process.Start(startInfo);
    }

    private void LibraryWindow_Activated(object? sender, EventArgs e)
    {
        if (_isClosing || !_isLoaded)
        {
            return;
        }

        lock (_refreshCancellationGate)
        {
            RefreshButton.IsEnabled = _activeRefreshCancellation is null;
        }
        if (_refreshPending)
        {
            _ = RefreshLibraryAsync(_currentClip?.FullPath ?? _initialPreferredPath);
            return;
        }

        if (_sourceReleasedForBackground && _currentClip is { } releasedClip)
        {
            OpenClip(
                releasedClip,
                autoplay: false,
                preserveRestorePosition: true);
        }
    }

    private void LibraryWindow_Deactivated(object? sender, EventArgs e)
    {
        CancelRefreshForBackground();
        ReleasePlayerForBackground();
    }

    private void LibraryWindow_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false)
        {
            CancelRefreshForBackground();
            ReleasePlayerForBackground();
        }
    }

    private void CancelRefreshForBackground()
    {
        lock (_refreshCancellationGate)
        {
            if (_activeRefreshCancellation is null)
            {
                return;
            }

            _refreshPending = true;
            _activeRefreshCancellation.Cancel();
        }
    }

    private void ReleasePlayerForBackground()
    {
        _playWhenOpened = false;
        _isSeeking = false;
        _resumeAfterSeek = false;
        if (_currentClip is null || LibraryPlayer.Source is null)
        {
            SetPlaying(false);
            return;
        }

        _positionToRestore = LibraryPlayer.Position;
        ReleasePlayerSource(rememberPosition: true);
        _sourceReleasedForBackground = true;
        SetControlsEnabled(false);
    }

    private void ReleasePlayerSource(bool rememberPosition)
    {
        _playWhenOpened = false;
        _pendingOpenPlan = null;
        if (!rememberPosition)
        {
            _positionToRestore = null;
        }

        if (LibraryPlayer.Source is not null)
        {
            LibraryPlayer.Volume = 0;
            LibraryPlayer.Stop();
            LibraryPlayer.Close();
            LibraryPlayer.Source = null;
        }

        SetPlaying(false);
    }

    private void LibraryWindow_Closing(object? sender, CancelEventArgs e)
    {
        _isClosing = true;
        _lifetimeCancellation.Cancel();
        lock (_refreshCancellationGate)
        {
            _activeRefreshCancellation?.Cancel();
        }
        ReleasePlayerSource(rememberPosition: false);
    }

    private void LibraryWindow_Closed(object? sender, EventArgs e)
    {
        Loaded -= LibraryWindow_Loaded;
        Activated -= LibraryWindow_Activated;
        Deactivated -= LibraryWindow_Deactivated;
        IsVisibleChanged -= LibraryWindow_IsVisibleChanged;
        Closing -= LibraryWindow_Closing;
        Closed -= LibraryWindow_Closed;
        _playerTimer.Stop();
        _playerTimer.Tick -= PlayerTimer_Tick;
        _nativeWindowThemeService.Dispose();
        _lifetimeCancellation.Dispose();
    }

    private void ShowError(string message)
    {
        if (_isClosing)
        {
            return;
        }

        MessageBox.Show(
            this,
            message,
            "ClipForge",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{(int)duration.TotalMinutes}:{duration.Seconds:00}";
}

/// <summary>
/// Pure, testable policy for opening a manually controlled WPF MediaElement.
/// The graph is always primed with Play at zero volume; only a foreground user
/// selection may continue after MediaOpened.
/// </summary>
internal readonly record struct LibraryMediaOpenPlan(
    double PrimeVolume,
    double PlaybackVolume,
    bool MustPrimeWithPlay,
    bool ContinueAfterOpened)
{
    public static LibraryMediaOpenPlan Create(bool autoplay, double playbackVolume) =>
        new(
            PrimeVolume: 0,
            PlaybackVolume: Math.Clamp(playbackVolume, 0, 1),
            MustPrimeWithPlay: true,
            ContinueAfterOpened: autoplay);
}
