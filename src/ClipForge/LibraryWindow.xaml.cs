using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ClipForge.Controls;
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
    private static readonly TimeSpan NormalPlayerTimerInterval = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan TrimPreviewTimerInterval = TimeSpan.FromMilliseconds(50);
    private static readonly LibraryFilterOption[] LibraryFilterOptions =
    [
        new(ClipLibraryFilter.All, "All clips"),
        new(ClipLibraryFilter.Original, "Normal clips"),
        new(ClipLibraryFilter.Trimmed, "Trimmed clips")
    ];

    private readonly ClipLibraryService _clipLibraryService;
    private readonly ClipTrimService _clipTrimService;
    private readonly string _saveDirectory;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly DispatcherTimer _playerTimer;
    private readonly NativeWindowThemeService _nativeWindowThemeService;
    private readonly string? _initialPreferredPath;
    private readonly IReadOnlyList<ClipLibraryItem> _initialCachedClips;
    private readonly Action<ClipLibraryItem>? _cachedClipUpserted;
    private readonly Action<ClipLibraryItem>? _cachedClipRemoved;
    private readonly object _refreshCancellationGate = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private CancellationTokenSource? _activeRefreshCancellation;
    private CancellationTokenSource? _activeTrimCancellation;
    private ClipLibraryItem? _currentClip;
    private TimeSpan? _positionToRestore;
    private bool _isLoaded;
    private bool _isClosing;
    private bool _refreshPending;
    private bool _suppressSelectionAutoplay;
    private bool _sourceReleasedForBackground;
    private int _playerHostIndex = 1;
    private bool _isMediaReady;
    private bool _isMediaOpenDeferred;
    private bool _playWhenOpened;
    private bool _isPlaying;
    private bool _isSeeking;
    private bool _resumeAfterSeek;
    private bool _isUpdatingControls;
    private bool _isMuted;
    private bool _isReplayRunning;
    private bool _isPresentationSuspended;
    private bool _replayPlaybackAudioOptIn;
    private bool _isTrimMode;
    private bool _isTrimInProgress;
    private bool _isPreviewingTrim;
    private bool _isUpdatingTrimRange;
    private bool _trimRangeInitialized;
    private bool _beginTrimWhenReady;
    private bool _suppressFilterChange;
    private double _volumeBeforeMute = 0.8;
    private long _refreshGeneration;
    private LibraryMediaOpenPlan? _pendingOpenPlan;
    private ClipLibraryFilter _activeFilter = ClipLibraryFilter.All;
    private ClipTrimExecutionMode _activeTrimExecutionMode = ClipTrimExecutionMode.Standard;
    private string? _requestedPreferredPath;

    public LibraryWindow(
        ClipLibraryService clipLibraryService,
        ClipTrimService clipTrimService,
        string saveDirectory,
        bool replayRunning,
        ClipLibraryItem? initiallySelectedClip = null,
        bool beginTrim = false,
        bool presentationSuspended = false,
        IReadOnlyList<ClipLibraryItem>? initialCachedClips = null,
        Action<ClipLibraryItem>? cachedClipUpserted = null,
        Action<ClipLibraryItem>? cachedClipRemoved = null)
    {
        ArgumentNullException.ThrowIfNull(clipLibraryService);
        ArgumentNullException.ThrowIfNull(clipTrimService);
        ArgumentException.ThrowIfNullOrWhiteSpace(saveDirectory);

        _clipLibraryService = clipLibraryService;
        _clipTrimService = clipTrimService;
        _saveDirectory = Path.GetFullPath(saveDirectory);
        _initialPreferredPath = initiallySelectedClip?.FullPath;
        _initialCachedClips = initialCachedClips?.Take(InitialClipLimit).ToArray() ?? [];
        _cachedClipUpserted = cachedClipUpserted;
        _cachedClipRemoved = cachedClipRemoved;
        _requestedPreferredPath = beginTrim ? _initialPreferredPath : null;
        _beginTrimWhenReady = beginTrim;
        _isReplayRunning = replayRunning;
        _isPresentationSuspended = presentationSuspended;

        InitializeComponent();
        ReplayPlaybackHintText.Visibility = _isReplayRunning
            ? Visibility.Visible
            : Visibility.Collapsed;
        LibraryFilterComboBox.ItemsSource = LibraryFilterOptions;
        LibraryFilterComboBox.SelectedItem = LibraryFilterOptions[0];
        _nativeWindowThemeService = new NativeWindowThemeService(this);
        _playerTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = NormalPlayerTimerInterval
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

    /// <summary>
    /// Prevents Instant Replay from starting while a standard trim may own a
    /// hardware encoder. A trim started during replay uses the coexistence
    /// profile and can safely survive a capture restart.
    /// </summary>
    public bool IsTrimInProgress => _isTrimInProgress;

    public bool IsReplayCompatibleTrimInProgress =>
        _isTrimInProgress &&
        _activeTrimExecutionMode == ClipTrimExecutionMode.ReplayCoexisting;

    public void UpdateReplayRunningState(bool isRunning)
    {
        if (_isClosing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => UpdateReplayRunningState(isRunning));
            return;
        }

        if (_isReplayRunning == isRunning)
        {
            return;
        }

        _isReplayRunning = isRunning;
        if (isRunning)
        {
            // Keep the already-bound cards, but cancel discovery and wait for
            // replay to stop before starting any more ffprobe/thumbnail work.
            // The foreground player remains available by explicit user action.
            _replayPlaybackAudioOptIn = false;
            if (GetAttachedPlayer() is not null)
            {
                ApplyVolume(0);
            }

            CancelRefreshForBackground();
            _refreshPending = true;
            SetReplayDeferredRefreshStatus();
        }

        ReplayPlaybackHintText.Visibility = isRunning
            ? Visibility.Visible
            : Visibility.Collapsed;
        ClipList.IsEnabled = !_isTrimInProgress && !_isPresentationSuspended;
        LibraryFilterComboBox.IsEnabled = !isRunning &&
                                          !_isTrimInProgress &&
                                          !_isPresentationSuspended;
        lock (_refreshCancellationGate)
        {
            RefreshButton.IsEnabled = !isRunning &&
                                      !_isTrimInProgress &&
                                      !_isPresentationSuspended &&
                                      _activeRefreshCancellation is null &&
                                      IsVisible &&
                                      IsActive;
        }

        if (!isRunning)
        {
            if (_isTrimInProgress ||
                !_isLoaded ||
                !IsVisible ||
                !IsActive ||
                _isPresentationSuspended)
            {
                _refreshPending = true;
            }
            else
            {
                _ = RefreshLibraryAsync(
                    _requestedPreferredPath ?? _currentClip?.FullPath ?? _initialPreferredPath);
            }
        }

        UpdateTrimAvailability();
    }

    public void UpdatePresentationSuspended(bool isSuspended)
    {
        if (_isClosing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => UpdatePresentationSuspended(isSuspended));
            return;
        }

        if (_isPresentationSuspended == isSuspended)
        {
            return;
        }

        _isPresentationSuspended = isSuspended;
        if (isSuspended)
        {
            CancelRefreshForBackground();
            ReleasePlayerForBackground();
            RefreshButton.IsEnabled = false;
            ClipList.IsEnabled = false;
            LibraryFilterComboBox.IsEnabled = false;
            LibraryStatusText.Text = "Capture is completing a short critical operation...";
            UpdateTrimAvailability();
            return;
        }

        ClipList.IsEnabled = !_isTrimInProgress;
        LibraryFilterComboBox.IsEnabled = !_isReplayRunning && !_isTrimInProgress;
        SetPlaying(_isPlaying);
        if (_isTrimInProgress)
        {
            UpdateTrimAvailability();
            return;
        }

        if (!_isLoaded || !IsVisible || !IsActive)
        {
            UpdateTrimAvailability();
            return;
        }

        if (_isReplayRunning)
        {
            if (_beginTrimWhenReady && _currentClip is { } requestedClip)
            {
                OpenClip(requestedClip, autoplay: false);
                TryBeginRequestedTrim();
            }
            else if (_sourceReleasedForBackground && _currentClip is { } releasedClip)
            {
                // Keep the selected poster/card, but require a fresh Play click
                // before recreating a WPF decoder during replay.
                SelectClipWithoutOpening(releasedClip);
            }

            UpdateTrimAvailability();
            return;
        }

        if (_refreshPending)
        {
            _ = RefreshLibraryAsync(
                _requestedPreferredPath ?? _currentClip?.FullPath ?? _initialPreferredPath);
        }
        else if (_sourceReleasedForBackground && _currentClip is { } releasedClip)
        {
            OpenClip(releasedClip, autoplay: false, preserveRestorePosition: true);
        }

        UpdateTrimAvailability();
    }

    public void SelectClipAndBeginTrim(ClipLibraryItem clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (_isClosing || _isTrimInProgress)
        {
            return;
        }

        if (_activeFilter != ClipLibraryFilter.All)
        {
            SetLibraryFilter(ClipLibraryFilter.All);
        }

        _requestedPreferredPath = clip.FullPath;
        _beginTrimWhenReady = true;
        _refreshPending = true;
        if (_isLoaded && IsVisible && IsActive)
        {
            if (_isReplayRunning && !_isPresentationSuspended)
            {
                _suppressSelectionAutoplay = true;
                try
                {
                    ClipList.SelectedItem = ClipList.Items
                        .OfType<ClipLibraryItem>()
                        .FirstOrDefault(item => item.FullPath.Equals(
                            clip.FullPath,
                            StringComparison.OrdinalIgnoreCase));
                }
                finally
                {
                    _suppressSelectionAutoplay = false;
                }

                OpenClip(clip, autoplay: false);
                TryBeginRequestedTrim();
                return;
            }

            _ = RefreshLibraryAsync(clip.FullPath);
        }
    }

    private async void LibraryWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= LibraryWindow_Loaded;
        _isLoaded = true;
        if (_isReplayRunning)
        {
            BindInitialCachedReplayClips();
            _refreshPending = true;
            if (_isPresentationSuspended)
            {
                LibraryStatusText.Text = "Capture is completing a short critical operation...";
            }

            return;
        }

        if (_isPresentationSuspended)
        {
            _refreshPending = true;
            LoadingState.Visibility = Visibility.Collapsed;
            EmptyLibraryState.Visibility = Visibility.Collapsed;
            LibraryStatusText.Text = "Capture is completing a short critical operation...";
            return;
        }

        await RefreshLibraryAsync(_initialPreferredPath);
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        await RefreshLibraryAsync(_currentClip?.FullPath);

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private async void LibraryFilterComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_suppressFilterChange ||
            !_isLoaded ||
            _isClosing ||
            _isTrimInProgress ||
            _isPresentationSuspended ||
            LibraryFilterComboBox.SelectedItem is not LibraryFilterOption option ||
            option.Filter == _activeFilter)
        {
            return;
        }

        _activeFilter = option.Filter;
        CancelTrimMode();
        await RefreshLibraryAsync();
    }

    private async Task RefreshLibraryAsync(string? preferredPath = null)
    {
        if (_isClosing || !_isLoaded)
        {
            return;
        }

        if (ShouldSuppressAutomaticRefresh(
                _isReplayRunning,
                _isPresentationSuspended,
                _isTrimInProgress))
        {
            _refreshPending = true;
            if (_isReplayRunning && !_isPresentationSuspended)
            {
                SetReplayDeferredRefreshStatus();
            }

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

        var gateEntered = false;
        RefreshButton.IsEnabled = false;
        LoadingState.Visibility = Visibility.Visible;
        EmptyLibraryState.Visibility = Visibility.Collapsed;
        LibraryStatusText.Text = "Loading up to 100 local clips…";

        try
        {
            await _refreshGate.WaitAsync(refreshCancellation.Token);
            gateEntered = true;
            if (ShouldSuppressAutomaticRefresh(
                    _isReplayRunning,
                    _isPresentationSuspended,
                    _isTrimInProgress))
            {
                _refreshPending = true;
                return;
            }

            var clips = await _clipLibraryService.GetRecentClipsAsync(
                _saveDirectory,
                count: InitialClipLimit,
                includeThumbnails: true,
                filter: _activeFilter,
                thumbnailPolicy: ClipThumbnailPolicy.GenerateMissing,
                cancellationToken: refreshCancellation.Token);

            refreshCancellation.Token.ThrowIfCancellationRequested();
            if (generation != Volatile.Read(ref _refreshGeneration) ||
                ShouldSuppressAutomaticRefresh(
                    _isReplayRunning,
                    _isPresentationSuspended,
                    _isTrimInProgress) ||
                !IsVisible ||
                !IsActive)
            {
                _refreshPending = true;
                return;
            }

            var selectedPath = preferredPath ?? _currentClip?.FullPath;
            ClipList.ItemsSource = clips;
            ClipCountText.Text = clips.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            LibraryStatusText.Text = clips.Count switch
            {
                0 => _activeFilter switch
                {
                    ClipLibraryFilter.Original => "No normal clips found in the current folder.",
                    ClipLibraryFilter.Trimmed => "No trimmed clips yet.",
                    _ => "No saved clips found in the current folder."
                },
                1 => $"1 {GetFilterDescription(_activeFilter)} · newest first",
                _ when clips.Count == InitialClipLimit =>
                    $"Showing the newest {clips.Count} {GetFilterDescription(_activeFilter)}",
                _ => $"{clips.Count} {GetFilterDescription(_activeFilter)} · newest first"
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
            else if (ShouldDeferAutomaticMediaOpen(
                         _isReplayRunning,
                         _beginTrimWhenReady))
            {
                SelectClipWithoutOpening(selected);
            }
            else
            {
                OpenClip(
                    selected,
                    autoplay: false,
                    preserveRestorePosition: restorePreviousPosition);
            }

            var requestedTrimPath = _requestedPreferredPath;
            var currentPath = _currentClip?.FullPath;
            if (_beginTrimWhenReady &&
                (currentPath is null ||
                 (requestedTrimPath is not null && !currentPath.Equals(
                     requestedTrimPath,
                     StringComparison.OrdinalIgnoreCase))))
            {
                _beginTrimWhenReady = false;
                _requestedPreferredPath = null;
                LibraryStatusText.Text = "The requested clip is no longer available to trim.";
            }
            else
            {
                // OpenClip builds the MediaElement graph asynchronously. The
                // direct MainWindow trim request stays pending until MediaOpened
                // supplies the same duration used by the normal Library path.
                TryBeginRequestedTrim();
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
            if (gateEntered)
            {
                _refreshGate.Release();
            }

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
                RefreshButton.IsEnabled = !_isReplayRunning &&
                                          !_isClosing &&
                                          !_isTrimInProgress &&
                                          !_isPresentationSuspended &&
                                          IsVisible &&
                                          IsActive;
            }

            refreshCancellation.Dispose();
        }
    }

    internal static bool ShouldSuppressAutomaticRefresh(
        bool replayRunning,
        bool presentationSuspended,
        bool trimInProgress) =>
        replayRunning || presentationSuspended || trimInProgress;

    internal async Task WaitForAutomaticRefreshIdleAsync(CancellationToken cancellationToken)
    {
        CancelRefreshForBackground();
        await _refreshGate.WaitAsync(cancellationToken);
        _refreshGate.Release();
    }

    internal void UpsertKnownReplayClip(
        ClipLibraryItem clip,
        bool selectClip,
        bool openForExplicitAction)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (_isClosing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => UpsertKnownReplayClip(
                clip,
                selectClip,
                openForExplicitAction));
            return;
        }

        var selectedPath = (ClipList.SelectedItem as ClipLibraryItem)?.FullPath ??
                           _currentClip?.FullPath;
        var cached = ClipList.Items.OfType<ClipLibraryItem>().ToArray();
        var previous = cached.FirstOrDefault(item => item.FullPath.Equals(
            clip.FullPath,
            StringComparison.OrdinalIgnoreCase));
        var mergedClip = clip with
        {
            Duration = clip.Duration ?? previous?.Duration,
            ThumbnailPath = clip.ThumbnailPath ?? previous?.ThumbnailPath
        };
        if (MatchesActiveFilter(mergedClip))
        {
            var merged = cached
                .Where(MatchesActiveFilter)
                .Where(item => !item.FullPath.Equals(
                    mergedClip.FullPath,
                    StringComparison.OrdinalIgnoreCase))
                .Append(mergedClip)
                .OrderByDescending(item => item.RecordedAtUtc)
                .ThenBy(item => item.FileName, StringComparer.OrdinalIgnoreCase)
                .Take(InitialClipLimit)
                .ToArray();
            ClipList.ItemsSource = merged;
            ClipCountText.Text = merged.Length.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
            EmptyLibraryState.Visibility = merged.Length == 0
                ? Visibility.Visible
                : Visibility.Collapsed;

            if (selectClip)
            {
                _suppressSelectionAutoplay = true;
                try
                {
                    ClipList.SelectedItem = mergedClip;
                    ClipList.ScrollIntoView(mergedClip);
                }
                finally
                {
                    _suppressSelectionAutoplay = false;
                }

                if (openForExplicitAction && !_isPresentationSuspended)
                {
                    OpenClip(mergedClip, autoplay: false);
                }
                else
                {
                    SelectClipWithoutOpening(mergedClip);
                }
            }
            else if (selectedPath is not null &&
                     merged.FirstOrDefault(item => item.FullPath.Equals(
                         selectedPath,
                         StringComparison.OrdinalIgnoreCase)) is { } retainedSelection)
            {
                _suppressSelectionAutoplay = true;
                try
                {
                    ClipList.SelectedItem = retainedSelection;
                }
                finally
                {
                    _suppressSelectionAutoplay = false;
                }
            }
        }

        _refreshPending = true;
        if (_isReplayRunning)
        {
            SetReplayDeferredRefreshStatus();
        }
    }

    internal void RemoveKnownReplayClip(
        ClipLibraryItem clip,
        bool removedWasCurrent = false)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (_isClosing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => RemoveKnownReplayClip(
                clip,
                removedWasCurrent));
            return;
        }

        var selectedPath = (ClipList.SelectedItem as ClipLibraryItem)?.FullPath ??
                           _currentClip?.FullPath;
        var removedCurrent = removedWasCurrent || _currentClip?.FullPath.Equals(
            clip.FullPath,
            StringComparison.OrdinalIgnoreCase) == true;
        var remaining = ClipList.Items
            .OfType<ClipLibraryItem>()
            .Where(item => !item.FullPath.Equals(
                clip.FullPath,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        ClipList.ItemsSource = remaining;
        ClipCountText.Text = remaining.Length.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        EmptyLibraryState.Visibility = remaining.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        _refreshPending = true;

        if (removedCurrent)
        {
            if (remaining.FirstOrDefault() is { } nextClip)
            {
                _suppressSelectionAutoplay = true;
                try
                {
                    ClipList.SelectedItem = nextClip;
                }
                finally
                {
                    _suppressSelectionAutoplay = false;
                }

                SelectClipWithoutOpening(nextClip);
            }
            else
            {
                ClearPlayer();
            }
        }
        else if (selectedPath is not null &&
                 remaining.FirstOrDefault(item => item.FullPath.Equals(
                     selectedPath,
                     StringComparison.OrdinalIgnoreCase)) is { } retainedSelection)
        {
            _suppressSelectionAutoplay = true;
            try
            {
                ClipList.SelectedItem = retainedSelection;
            }
            finally
            {
                _suppressSelectionAutoplay = false;
            }
        }

        if (_isReplayRunning)
        {
            SetReplayDeferredRefreshStatus();
        }
    }

    private bool MatchesActiveFilter(ClipLibraryItem clip) => _activeFilter switch
    {
        ClipLibraryFilter.Original => !clip.IsTrimmed,
        ClipLibraryFilter.Trimmed => clip.IsTrimmed,
        _ => true
    };

    private void BindInitialCachedReplayClips()
    {
        var clips = _initialCachedClips
            .Where(clip => _activeFilter switch
            {
                ClipLibraryFilter.Original => !clip.IsTrimmed,
                ClipLibraryFilter.Trimmed => clip.IsTrimmed,
                _ => true
            })
            .ToArray();
        ClipList.ItemsSource = clips;
        ClipCountText.Text = clips.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        LoadingState.Visibility = Visibility.Collapsed;
        EmptyLibraryState.Visibility = clips.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        var selected = _initialPreferredPath is { Length: > 0 }
            ? clips.FirstOrDefault(clip => clip.FullPath.Equals(
                _initialPreferredPath,
                StringComparison.OrdinalIgnoreCase))
            : null;
        selected ??= clips.FirstOrDefault();

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
        else if (_beginTrimWhenReady && !_isPresentationSuspended)
        {
            // Opening the explicitly requested trim source is foreground user
            // intent; no discovery, ffprobe, or thumbnail helper is launched.
            OpenClip(selected, autoplay: false);
            TryBeginRequestedTrim();
        }
        else
        {
            SelectClipWithoutOpening(selected);
        }

        RefreshButton.IsEnabled = false;
        LibraryFilterComboBox.IsEnabled = false;
        SetReplayDeferredRefreshStatus();
    }

    private void SetReplayDeferredRefreshStatus()
    {
        LoadingState.Visibility = Visibility.Collapsed;
        LibraryStatusText.Text = ClipList.Items.Count == 0
            ? "Replay is active - no cached clips are available yet. Stop replay to load the library."
            : $"Replay is active - showing {ClipList.Items.Count} cached clips. Full refresh resumes when replay stops.";
    }

    private void ClipList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isClosing ||
            _suppressSelectionAutoplay ||
            _isTrimInProgress ||
            _isPresentationSuspended ||
            ClipList.SelectedItem is not ClipLibraryItem clip)
        {
            return;
        }

        if (_beginTrimWhenReady &&
            _requestedPreferredPath is { } requestedPath &&
            !clip.FullPath.Equals(requestedPath, StringComparison.OrdinalIgnoreCase))
        {
            _beginTrimWhenReady = false;
            _requestedPreferredPath = null;
        }

        CancelTrimMode();
        OpenClip(clip, autoplay: true);
    }

    internal static bool ShouldDeferAutomaticMediaOpen(
        bool replayRunning,
        bool beginTrimWhenReady) =>
        replayRunning && !beginTrimWhenReady;

    private void SelectClipWithoutOpening(ClipLibraryItem clip)
    {
        ReleasePlayerSource(rememberPosition: false);
        _currentClip = clip;
        PlayerPosterImage.DataContext = clip;
        _isMediaOpenDeferred = true;
        _sourceReleasedForBackground = false;
        _playWhenOpened = false;
        SelectedClipNameText.Text = clip.FileName;
        SelectedClipDetailsText.Text =
            $"{(clip.IsTrimmed ? "Trimmed copy · " : string.Empty)}{clip.RecordedAtLocal:dd MMM yyyy · HH:mm} · {clip.FileSizeDisplay}";
        PlayerEmptyState.Visibility = Visibility.Collapsed;
        SurfacePlayButton.Visibility = Visibility.Visible;
        SetControlsEnabled(false);
        SurfacePlayButton.IsEnabled = !_isPresentationSuspended;
        SetPlaying(false);
        SetSeekUi(TimeSpan.Zero, clip.Duration ?? TimeSpan.Zero);
        TimeText.Text = clip.Duration is { } duration
            ? $"0:00 / {FormatDuration(duration)}"
            : "0:00 / --:--";
    }

    private void OpenClip(
        ClipLibraryItem clip,
        bool autoplay,
        bool preserveRestorePosition = false)
    {
        if (_isPresentationSuspended)
        {
            _refreshPending = true;
            return;
        }

        _isMediaReady = false;
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
        PlayerPosterImage.DataContext = clip;
        _isMediaOpenDeferred = false;
        _sourceReleasedForBackground = false;
        _playWhenOpened = autoplay;
        SelectedClipNameText.Text = clip.FileName;
        SelectedClipDetailsText.Text =
            $"{(clip.IsTrimmed ? "Trimmed copy · " : string.Empty)}{clip.RecordedAtLocal:dd MMM yyyy · HH:mm} · {clip.FileSizeDisplay}";
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
            _isReplayRunning && !_replayPlaybackAudioOptIn
                ? 0
                : VolumeSlider.Value / 100);
        _pendingOpenPlan = openPlan;
        var player = EnsurePlayerElement();
        player.Volume = openPlan.PrimeVolume;
        player.Source = new Uri(validatedClipPath, UriKind.Absolute);
        if (openPlan.MustPrimeWithPlay)
        {
            player.Play();
        }
    }

    private void ClearPlayer()
    {
        if (!_isTrimInProgress)
        {
            CancelTrimMode();
        }

        ReleasePlayerSource(rememberPosition: false);
        _currentClip = null;
        PlayerPosterImage.DataContext = null;
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
        if (_currentClip is null)
        {
            return;
        }

        var player = GetAttachedPlayer();
        if (player?.Source is null)
        {
            if (_isPresentationSuspended)
            {
                return;
            }

            OpenClip(_currentClip, autoplay: true);
            return;
        }

        if (_isPlaying)
        {
            player.Pause();
            SetPlaying(false);
            return;
        }

        var duration = GetPlayerDuration();
        if (duration > TimeSpan.Zero &&
            player.Position >= duration - TimeSpan.FromMilliseconds(250))
        {
            player.Position = TimeSpan.Zero;
        }

        player.Play();
        SetPlaying(true);
    }

    private void LibraryPlayer_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        PlayPauseButton_Click(sender, e);
        e.Handled = true;
    }

    private void RestartButton_Click(object sender, RoutedEventArgs e)
    {
        var player = GetAttachedPlayer();
        if (_currentClip is null || player?.Source is null)
        {
            return;
        }

        player.Position = TimeSpan.Zero;
        player.Play();
        SetPlaying(true);
    }

    private void BackTenButton_Click(object sender, RoutedEventArgs e)
    {
        if (GetAttachedPlayer() is { } player)
        {
            SeekPlayerTo(player.Position - TimeSpan.FromSeconds(10));
        }
    }

    private void ForwardTenButton_Click(object sender, RoutedEventArgs e)
    {
        if (GetAttachedPlayer() is { } player)
        {
            SeekPlayerTo(player.Position + TimeSpan.FromSeconds(10));
        }
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentClip is null)
        {
            return;
        }

        if (_isMuted || VolumeSlider.Value <= 0)
        {
            _isMuted = false;
            if (_isReplayRunning)
            {
                _replayPlaybackAudioOptIn = true;
            }
            VolumeSlider.Value = Math.Max(5, _volumeBeforeMute * 100);
        }
        else
        {
            _volumeBeforeMute = Math.Clamp(VolumeSlider.Value / 100, 0.05, 1);
            _isMuted = true;
            _replayPlaybackAudioOptIn = false;
            VolumeSlider.Value = 0;
        }

        ApplyVolume();
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isLoaded &&
            _isReplayRunning &&
            e.NewValue > 0 &&
            e.NewValue != e.OldValue)
        {
            _replayPlaybackAudioOptIn = true;
        }

        ApplyVolume();
    }

    private void ApplyVolume(double? requestedVolume = null)
    {
        if (MuteButton is null)
        {
            return;
        }

        var volume = Math.Clamp(requestedVolume ?? VolumeSlider.Value / 100, 0, 1);
        if (GetAttachedPlayer() is { } player)
        {
            player.Volume = volume;
        }

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
        var player = GetAttachedPlayer();
        if (player is null || !ReferenceEquals(sender, player))
        {
            return;
        }

        var openPlan = _pendingOpenPlan;
        _pendingOpenPlan = null;
        if (_isClosing ||
            !IsVisible ||
            !IsActive ||
            _isPresentationSuspended ||
            player.Source is null ||
            _currentClip is null ||
            openPlan is null)
        {
            // A late graph-open event must never re-enable playback or restore
            // audible volume after Alt-Tab, owner hide, or shutdown.
            player.Volume = 0;
            ReleasePlayerForBackground();
            return;
        }

        // Stop the muted priming playback before restoring position or volume.
        player.Pause();
        _isMediaReady = true;
        PlayerEmptyState.Visibility = Visibility.Collapsed;
        SetControlsEnabled(true);

        if (_positionToRestore is { } restorePosition)
        {
            var duration = GetPlayerDuration();
            player.Position = duration > TimeSpan.Zero
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
            player.Play();
            SetPlaying(true);
        }
        else
        {
            SetPlaying(false);
        }

        if (_isTrimMode)
        {
            InitializeTrimRange(GetPlayerDuration(), preserveSelection: _trimRangeInitialized);
        }

        TryBeginRequestedTrim();

        UpdatePlayerTime();
    }

    private void LibraryPlayer_MediaEnded(object sender, RoutedEventArgs e)
    {
        var player = GetAttachedPlayer();
        if (player is null || !ReferenceEquals(sender, player))
        {
            return;
        }

        _isPreviewingTrim = false;
        player.Pause();
        player.Position = TimeSpan.Zero;
        SetPlaying(false);
        UpdatePlayerTime();
    }

    private void LibraryPlayer_MediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        var player = GetAttachedPlayer();
        if (player is null || !ReferenceEquals(sender, player))
        {
            return;
        }

        _pendingOpenPlan = null;
        _playWhenOpened = false;
        if (_isClosing ||
            !IsVisible ||
            !IsActive ||
            player.Source is null ||
            _currentClip is null)
        {
            player.Volume = 0;
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
        if (!isPlaying)
        {
            _isPreviewingTrim = false;
            _playerTimer.Interval = NormalPlayerTimerInterval;
        }

        PlayPauseButton.Content = isPlaying ? "⏸" : "▶";
        PlayPauseButton.ToolTip = isPlaying ? "Pause clip" : "Play clip";
        var canOpenDeferredSource = _isMediaOpenDeferred &&
                                    !_isPresentationSuspended &&
                                    !_isTrimInProgress;
        SurfacePlayButton.Visibility = !isPlaying &&
                                       _currentClip is not null &&
                                       (GetAttachedPlayer()?.Source is not null || canOpenDeferredSource)
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (_isMediaOpenDeferred)
        {
            SurfacePlayButton.IsEnabled = canOpenDeferredSource;
        }
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
        var player = GetAttachedPlayer();
        if (_currentClip is null || player?.Source is null)
        {
            return;
        }

        var total = GetPlayerDuration();
        var position = total > TimeSpan.Zero && player.Position > total
            ? total
            : player.Position;
        if (_isPreviewingTrim &&
            _isTrimMode &&
            position.TotalSeconds >= TrimRangeSelector.UpperValue - 0.03)
        {
            var trimEnd = TimeSpan.FromSeconds(TrimRangeSelector.UpperValue);
            var previewEnd = total > TimeSpan.FromMilliseconds(50) && trimEnd >= total
                ? total - TimeSpan.FromMilliseconds(50)
                : trimEnd;
            player.Pause();
            player.Position = previewEnd;
            _isPreviewingTrim = false;
            SetPlaying(false);
            TimeText.Text = $"{FormatDuration(trimEnd)} / {(total > TimeSpan.Zero ? FormatDuration(total) : "--:--")}";
            SetSeekUi(trimEnd, total);
            return;
        }

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
        BeginTrimButton.IsEnabled = isEnabled &&
                                    !_isTrimInProgress &&
                                    !_isPresentationSuspended;
    }

    private TimeSpan GetPlayerDuration()
    {
        var player = GetAttachedPlayer();
        return player is not null && player.NaturalDuration.HasTimeSpan
            ? player.NaturalDuration.TimeSpan
            : _currentClip?.Duration ?? TimeSpan.Zero;
    }

    private void SetSeekUi(TimeSpan position, TimeSpan total)
    {
        _isUpdatingControls = true;
        try
        {
            var maximumSeconds = total > TimeSpan.Zero ? total.TotalSeconds : 1;
            SeekSlider.Maximum = maximumSeconds;
            SeekSlider.Value = Math.Clamp(position.TotalSeconds, 0, maximumSeconds);
            SeekSlider.IsEnabled = _currentClip is not null &&
                                   GetAttachedPlayer()?.Source is not null &&
                                   total > TimeSpan.Zero;
        }
        finally
        {
            _isUpdatingControls = false;
        }
    }

    private void SeekPlayerTo(TimeSpan requestedPosition, bool updateSlider = true)
    {
        var player = GetAttachedPlayer();
        if (_currentClip is null || player?.Source is null)
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
        player.Position = target;
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
        if (_resumeAfterSeek && GetAttachedPlayer() is { } player)
        {
            player.Pause();
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
        if (_resumeAfterSeek && GetAttachedPlayer() is { } player)
        {
            player.Play();
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
        var player = GetAttachedPlayer();
        if (!SeekSlider.IsEnabled || _currentClip is null || player?.Source is null)
        {
            return;
        }

        var requested = e.Key switch
        {
            Key.Left or Key.Down => player.Position - TimeSpan.FromSeconds(5),
            Key.Right or Key.Up => player.Position + TimeSpan.FromSeconds(5),
            Key.PageDown => player.Position - TimeSpan.FromSeconds(10),
            Key.PageUp => player.Position + TimeSpan.FromSeconds(10),
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

    private void BeginTrimButton_Click(object sender, RoutedEventArgs e) => BeginTrimMode();

    private void TryBeginRequestedTrim()
    {
        if (!ShouldBeginRequestedTrim(
                _beginTrimWhenReady,
                _isMediaReady,
                _currentClip?.FullPath,
                _requestedPreferredPath))
        {
            return;
        }

        _beginTrimWhenReady = false;
        _requestedPreferredPath = null;
        BeginTrimMode();
    }

    internal static bool ShouldBeginRequestedTrim(
        bool requestPending,
        bool mediaReady,
        string? currentClipPath,
        string? requestedClipPath) =>
        requestPending &&
        mediaReady &&
        !string.IsNullOrWhiteSpace(currentClipPath) &&
        (requestedClipPath is null || currentClipPath.Equals(
            requestedClipPath,
            StringComparison.OrdinalIgnoreCase));

    private void BeginTrimMode()
    {
        var player = GetAttachedPlayer();
        if (_isClosing ||
            _isTrimInProgress ||
            _currentClip is null ||
            !_isMediaReady ||
            player?.Source is null ||
            !player.NaturalDuration.HasTimeSpan)
        {
            return;
        }

        var duration = player.NaturalDuration.TimeSpan;

        if (duration < TimeSpan.FromSeconds(0.25))
        {
            ShowError("This clip is too short to trim, or its duration is unavailable.");
            return;
        }

        _isTrimMode = true;
        _trimRangeInitialized = false;
        _isPreviewingTrim = false;
        TrimEditorPanel.Visibility = Visibility.Visible;
        InitializeTrimRange(duration, preserveSelection: false);
        UpdateTrimAvailability();
        _ = TrimRangeSelector.FocusStartHandle();
    }

    private void CancelTrimModeButton_Click(object sender, RoutedEventArgs e) => CancelTrimMode();

    private void CancelTrimMode()
    {
        if (_isTrimInProgress)
        {
            return;
        }

        _isTrimMode = false;
        _isPreviewingTrim = false;
        _trimRangeInitialized = false;
        TrimEditorPanel.Visibility = Visibility.Collapsed;
        BeginTrimButton.Content = "Trim clip";
        BeginTrimButton.IsEnabled = _isMediaReady &&
                                    !_isPresentationSuspended &&
                                    _currentClip is not null &&
                                    GetAttachedPlayer()?.Source is not null;
    }

    private void InitializeTrimRange(TimeSpan duration, bool preserveSelection)
    {
        if (!_isTrimMode || duration <= TimeSpan.Zero)
        {
            return;
        }

        var maximum = Math.Max(0.25, duration.TotalSeconds);
        var previousMaximum = TrimRangeSelector.Maximum;
        var previousLower = TrimRangeSelector.LowerValue;
        var previousUpper = TrimRangeSelector.UpperValue;
        var keptFullRange = !_trimRangeInitialized ||
                            previousUpper >= previousMaximum - 0.03;

        _isUpdatingTrimRange = true;
        try
        {
            TrimRangeSelector.Minimum = 0;
            TrimRangeSelector.Maximum = maximum;
            TrimRangeSelector.MinimumSpan = Math.Min(0.25, maximum);
            TrimRangeSelector.LowerValue = preserveSelection
                ? Math.Clamp(previousLower, 0, maximum - TrimRangeSelector.MinimumSpan)
                : 0;
            TrimRangeSelector.UpperValue = preserveSelection && !keptFullRange
                ? Math.Clamp(previousUpper, TrimRangeSelector.LowerValue + TrimRangeSelector.MinimumSpan, maximum)
                : maximum;
        }
        finally
        {
            _isUpdatingTrimRange = false;
        }

        _trimRangeInitialized = true;
        UpdateTrimLabels();
        UpdateTrimAvailability();
    }

    private void TrimRangeSelector_RangeChanged(object? sender, TrimRangeChangedEventArgs e)
    {
        UpdateTrimLabels();
        UpdateTrimAvailability();
        var player = GetAttachedPlayer();
        if (_isUpdatingTrimRange ||
            e.Handle == TrimRangeHandle.None ||
            player?.Source is null)
        {
            return;
        }

        player.Pause();
        SetPlaying(false);
        SeekPlayerTo(GetTrimBoundaryPreviewPosition(e.Handle, e.LowerValue, e.UpperValue));
    }

    private void TrimRangeSelector_RangeChangeCompleted(object? sender, TrimRangeChangedEventArgs e)
    {
        UpdateTrimLabels();
        if (_isUpdatingTrimRange || GetAttachedPlayer()?.Source is null)
        {
            return;
        }

        SeekPlayerTo(GetTrimBoundaryPreviewPosition(e.Handle, e.LowerValue, e.UpperValue));
    }

    private TimeSpan GetTrimBoundaryPreviewPosition(
        TrimRangeHandle handle,
        double lowerValue,
        double upperValue)
    {
        var requested = handle == TrimRangeHandle.End ? upperValue : lowerValue;
        var total = GetPlayerDuration();
        if (handle == TrimRangeHandle.End &&
            total > TimeSpan.FromMilliseconds(50) &&
            requested >= total.TotalSeconds - 0.001)
        {
            requested = total.TotalSeconds - 0.05;
        }

        return TimeSpan.FromSeconds(Math.Max(0, requested));
    }

    private void PreviewTrimButton_Click(object sender, RoutedEventArgs e)
    {
        var player = GetAttachedPlayer();
        if (!_isTrimMode ||
            _isTrimInProgress ||
            _currentClip is null ||
            player?.Source is null)
        {
            return;
        }

        player.Position = TimeSpan.FromSeconds(TrimRangeSelector.LowerValue);
        _playerTimer.Interval = TrimPreviewTimerInterval;
        _isPreviewingTrim = true;
        player.Play();
        SetPlaying(true);
        UpdatePlayerTime();
    }

    private async void SaveTrimButton_Click(object sender, RoutedEventArgs e)
    {
        var player = GetAttachedPlayer();
        if (!_isTrimMode ||
            _isTrimInProgress ||
            _currentClip is null ||
            !_isMediaReady ||
            player?.Source is null)
        {
            return;
        }

        if (_isPresentationSuspended)
        {
            UpdateTrimAvailability();
            ShowError("Wait for the current capture operation to finish, then save the trimmed copy.");
            return;
        }

        var source = _currentClip;
        var start = TimeSpan.FromSeconds(TrimRangeSelector.LowerValue);
        var end = TimeSpan.FromSeconds(TrimRangeSelector.UpperValue);
        if (end - start < TimeSpan.FromSeconds(0.25))
        {
            ShowError("Choose at least 0.25 seconds to keep.");
            return;
        }

        player.Pause();
        _isPreviewingTrim = false;
        ReleasePlayerSource(rememberPosition: false);
        SetControlsEnabled(false);
        SurfacePlayButton.Visibility = Visibility.Collapsed;

        var trimCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        _activeTrimCancellation = trimCancellation;
        _activeTrimExecutionMode = _isReplayRunning
            ? ClipTrimExecutionMode.ReplayCoexisting
            : ClipTrimExecutionMode.Standard;
        SetTrimBusy(true);

        try
        {
            var result = await _clipTrimService.TrimAsync(
                _saveDirectory,
                source,
                start,
                end,
                _activeTrimExecutionMode,
                trimCancellation.Token);
            if (_isClosing)
            {
                return;
            }

            if (!result.Succeeded || string.IsNullOrWhiteSpace(result.OutputPath))
            {
                SetTrimBusy(false);
                if (!IsVisible || !IsActive)
                {
                    _requestedPreferredPath = source.FullPath;
                    _refreshPending = true;
                    return;
                }

                if (!trimCancellation.IsCancellationRequested)
                {
                    ShowError(result.Message);
                }

                if (_refreshPending)
                {
                    await RefreshLibraryAsync(source.FullPath);
                }
                else
                {
                    OpenClip(source, autoplay: false);
                }

                return;
            }

            if (!await WaitForForegroundAsync(trimCancellation.Token))
            {
                return;
            }

            LibraryChanged = true;
            var outputPath = result.OutputPath;
            var deleteOriginal = MessageBox.Show(
                this,
                $"The trimmed clip was saved successfully.\n\nDelete the source clip {source.FileName}?\n\nChoose No to keep both clips.",
                "Trimmed clip saved",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No) == MessageBoxResult.Yes;

            var sourceDeleted = false;
            if (deleteOriginal)
            {
                var deletion = ClipLibraryService.DeleteCurrentClip(_saveDirectory, source);
                if (deletion == ClipDeletionResult.Deleted)
                {
                    sourceDeleted = true;
                    _clipLibraryService.RemoveCachedThumbnail(source);
                }
                else
                {
                    ShowError(deletion == ClipDeletionResult.ChangedOrUnsafe
                        ? "The source clip changed, so ClipForge kept it. Your trimmed copy is safe."
                        : "The source clip could not be deleted. Your trimmed copy is safe, and both clips were kept.");
                }
            }

            SetTrimBusy(false);
            CancelTrimMode();
            SetLibraryFilter(ClipLibraryFilter.Trimmed);
            if (_isReplayRunning && sourceDeleted)
            {
                RemoveKnownReplayClip(source);
                _cachedClipRemoved?.Invoke(source);
            }

            if (_isReplayRunning &&
                ClipLibraryService.TryCreateKnownOutputItem(
                    _saveDirectory,
                    outputPath,
                    end - start,
                    out var trimmedClip) &&
                trimmedClip is not null)
            {
                UpsertKnownReplayClip(
                    trimmedClip,
                    selectClip: true,
                    openForExplicitAction: true);
                _cachedClipUpserted?.Invoke(trimmedClip);
            }
            else
            {
                await RefreshLibraryAsync(outputPath);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException or
                ArgumentException or NotSupportedException)
        {
            if (!_isClosing)
            {
                SetTrimBusy(false);
                if (IsVisible && IsActive)
                {
                    ShowError($"The trimmed clip could not be created. {exception.Message}");
                    if (_refreshPending)
                    {
                        await RefreshLibraryAsync(source.FullPath);
                    }
                    else
                    {
                        OpenClip(source, autoplay: false);
                    }
                }
                else
                {
                    _requestedPreferredPath = source.FullPath;
                    _refreshPending = true;
                }
            }
        }
        finally
        {
            if (ReferenceEquals(_activeTrimCancellation, trimCancellation))
            {
                _activeTrimCancellation = null;
            }

            trimCancellation.Dispose();
            _activeTrimExecutionMode = ClipTrimExecutionMode.Standard;
            if (!_isClosing && _isTrimInProgress)
            {
                SetTrimBusy(false);
            }
        }
    }

    private async Task<bool> WaitForForegroundAsync(CancellationToken cancellationToken)
    {
        while (!_isClosing)
        {
            if (IsVisible && IsActive && !_isPresentationSuspended)
            {
                return true;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
        }

        return false;
    }

    private void CancelTrimOperationButton_Click(object sender, RoutedEventArgs e)
    {
        CancelTrimOperationButton.IsEnabled = false;
        CancelTrimOperationButton.Content = "Cancelling…";
        _activeTrimCancellation?.Cancel();
    }

    private void SetTrimBusy(bool isBusy)
    {
        _isTrimInProgress = isBusy;
        ClipList.IsEnabled = !isBusy && !_isPresentationSuspended;
        LibraryFilterComboBox.IsEnabled = !_isReplayRunning &&
                                          !isBusy &&
                                          !_isPresentationSuspended;
        RefreshButton.IsEnabled = !_isReplayRunning &&
                                  !isBusy &&
                                  !_isPresentationSuspended &&
                                  IsVisible &&
                                  IsActive;
        TrimRangeSelector.IsEnabled = !isBusy;
        CancelTrimModeButton.IsEnabled = !isBusy;
        TrimActionsPanel.Visibility = isBusy ? Visibility.Collapsed : Visibility.Visible;
        TrimProgressPanel.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        CancelTrimOperationButton.IsEnabled = isBusy;
        CancelTrimOperationButton.Content = "Cancel";
        BeginTrimButton.IsEnabled = !isBusy &&
                                    !_isPresentationSuspended &&
                                    _isMediaReady &&
                                    _currentClip is not null &&
                                    GetAttachedPlayer()?.Source is not null;
        UpdateTrimAvailability();
    }

    private void UpdateTrimLabels()
    {
        if (TrimStartText is null)
        {
            return;
        }

        var start = TimeSpan.FromSeconds(Math.Max(0, TrimRangeSelector.LowerValue));
        var end = TimeSpan.FromSeconds(Math.Max(start.TotalSeconds, TrimRangeSelector.UpperValue));
        TrimStartText.Text = FormatTrimDuration(start);
        TrimEndText.Text = FormatTrimDuration(end);
        TrimLengthText.Text = FormatTrimDuration(end - start);
    }

    private void UpdateTrimAvailability()
    {
        if (SaveTrimButton is null)
        {
            return;
        }

        var validRange = _isTrimMode &&
                         TrimRangeSelector.UpperValue - TrimRangeSelector.LowerValue >= 0.25 &&
                         (TrimRangeSelector.LowerValue > 0.01 ||
                          TrimRangeSelector.UpperValue < TrimRangeSelector.Maximum - 0.01);
        TrimContentionText.Visibility = _isTrimMode && _isReplayRunning
            ? Visibility.Visible
            : Visibility.Collapsed;
        SaveTrimButton.IsEnabled = validRange &&
                                   !_isTrimInProgress &&
                                   !_isPresentationSuspended &&
                                   _isMediaReady &&
                                   _currentClip is not null &&
                                   GetAttachedPlayer()?.Source is not null;
        PreviewTrimButton.IsEnabled = _isTrimMode &&
                                      !_isTrimInProgress &&
                                      _isMediaReady &&
                                      _currentClip is not null &&
                                      GetAttachedPlayer()?.Source is not null &&
                                      SeekSlider.IsEnabled;
        BeginTrimButton.Content = _isTrimMode ? "Editing trim" : "Trim clip";
        if (_isTrimMode)
        {
            BeginTrimButton.IsEnabled = false;
        }
    }

    private void SetLibraryFilter(ClipLibraryFilter filter)
    {
        _activeFilter = filter;
        _suppressFilterChange = true;
        try
        {
            LibraryFilterComboBox.SelectedItem = LibraryFilterOptions.First(option => option.Filter == filter);
        }
        finally
        {
            _suppressFilterChange = false;
        }
    }

    private static string GetFilterDescription(ClipLibraryFilter filter) => filter switch
    {
        ClipLibraryFilter.Original => "normal clips",
        ClipLibraryFilter.Trimmed => "trimmed clips",
        _ => "local clips"
    };

    private static string FormatTrimDuration(TimeSpan duration)
    {
        var totalTenths = Math.Max(0, (long)Math.Round(duration.TotalSeconds * 10));
        var tenths = totalTenths % 10;
        var totalSeconds = totalTenths / 10;
        var seconds = totalSeconds % 60;
        var totalMinutes = totalSeconds / 60;
        return totalMinutes >= 60
            ? $"{totalMinutes / 60}:{totalMinutes % 60:00}:{seconds:00}.{tenths}"
            : $"{totalMinutes}:{seconds:00}.{tenths}";
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
            RemoveKnownReplayClip(clip, removedWasCurrent: deletingCurrentClip);
            _cachedClipRemoved?.Invoke(clip);
            _clipLibraryService.RemoveCachedThumbnail(clip);
            LibraryChanged = true;
            await RefreshLibraryAsync();
            return;
        }

        if (deletingCurrentClip && _isReplayRunning && !_isPresentationSuspended)
        {
            OpenClip(clip, autoplay: false);
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

        if (_isPresentationSuspended)
        {
            RefreshButton.IsEnabled = false;
            ReleasePlayerForBackground();
            return;
        }

        lock (_refreshCancellationGate)
        {
            RefreshButton.IsEnabled = !_isReplayRunning &&
                                      !_isTrimInProgress &&
                                      _activeRefreshCancellation is null;
        }
        if (_refreshPending)
        {
            _ = RefreshLibraryAsync(
                _requestedPreferredPath ?? _currentClip?.FullPath ?? _initialPreferredPath);
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
        _ = WindowInputReleaseService.ReleaseMouseCaptureWithin(this);
        _playWhenOpened = false;
        _isSeeking = false;
        _resumeAfterSeek = false;
        var player = GetAttachedPlayer();
        var hadOpenSource = player?.Source is not null;
        if (_currentClip is null || !hadOpenSource)
        {
            ReleasePlayerElement();
            SetPlaying(false);
            return;
        }

        _positionToRestore = player!.Position;
        ReleasePlayerSource(rememberPosition: true);
        _sourceReleasedForBackground = true;
        SetControlsEnabled(false);
    }

    private void ReleasePlayerSource(bool rememberPosition)
    {
        _isMediaReady = false;
        _isMediaOpenDeferred = false;
        _playWhenOpened = false;
        _pendingOpenPlan = null;
        if (!rememberPosition)
        {
            _positionToRestore = null;
        }

        ReleasePlayerElement();

        SetPlaying(false);
    }

    private void ReleasePlayerElement()
    {
        var previousPlayer = GetAttachedPlayer();
        if (previousPlayer is null)
        {
            LibraryPlayer = null!;
            return;
        }

        previousPlayer.MouseLeftButtonUp -= LibraryPlayer_MouseLeftButtonUp;
        previousPlayer.MediaOpened -= LibraryPlayer_MediaOpened;
        previousPlayer.MediaEnded -= LibraryPlayer_MediaEnded;
        previousPlayer.MediaFailed -= LibraryPlayer_MediaFailed;
        previousPlayer.Volume = 0;
        previousPlayer.Stop();
        previousPlayer.Close();
        previousPlayer.Source = null;
        _playerHostIndex = LibraryPlayerHost.Children.IndexOf(previousPlayer);
        LibraryPlayerHost.Children.Remove(previousPlayer);
        LibraryPlayer = null!;
    }

    private MediaElement EnsurePlayerElement()
    {
        if (GetAttachedPlayer() is { } player)
        {
            return player;
        }

        // A hidden Library window owns no media graph. Recreate one only for a
        // foreground open/play, and detach old handlers before close so queued
        // events cannot consume the new source's open plan.
        var replacement = new MediaElement
        {
            LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Manual,
            Stretch = System.Windows.Media.Stretch.Uniform,
            ScrubbingEnabled = true,
            Volume = 0.8,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        replacement.MouseLeftButtonUp += LibraryPlayer_MouseLeftButtonUp;
        replacement.MediaOpened += LibraryPlayer_MediaOpened;
        replacement.MediaEnded += LibraryPlayer_MediaEnded;
        replacement.MediaFailed += LibraryPlayer_MediaFailed;
        System.Windows.Automation.AutomationProperties.SetName(
            replacement,
            "Library clip player");
        System.Windows.Automation.AutomationProperties.SetHelpText(
            replacement,
            "Click to play or pause the selected clip");
        var insertionIndex = Math.Clamp(
            _playerHostIndex,
            0,
            LibraryPlayerHost.Children.Count);
        LibraryPlayerHost.Children.Insert(insertionIndex, replacement);
        LibraryPlayer = replacement;
        return replacement;
    }

    private MediaElement? GetAttachedPlayer() =>
        LibraryPlayer is { } player && LibraryPlayerHost.Children.Contains(player)
            ? player
            : null;

    private void LibraryWindow_Closing(object? sender, CancelEventArgs e)
    {
        _isClosing = true;
        _ = WindowInputReleaseService.ReleaseMouseCaptureWithin(this);
        _isSeeking = false;
        _resumeAfterSeek = false;
        _isPreviewingTrim = false;
        _lifetimeCancellation.Cancel();
        _activeTrimCancellation?.Cancel();
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

internal sealed record LibraryFilterOption(ClipLibraryFilter Filter, string Label)
{
    public override string ToString() => Label;
}
