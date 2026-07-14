using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ClipForge.Capture;
using ClipForge.Models;
using ClipForge.Services;
using Microsoft.Win32;
using Button = System.Windows.Controls.Button;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MenuItem = System.Windows.Controls.MenuItem;
using MessageBox = System.Windows.MessageBox;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using Forms = System.Windows.Forms;

namespace ClipForge;

public partial class MainWindow : Window
{
    private static readonly int[] FrameRateOptions = [30, 60];
    private static readonly int[] RecentClipCountOptions = [4, 8, 10, 15];
    private static readonly AppearanceTargetOption[] AppearanceTargetOptions =
    [
        new(AppearanceColorTarget.Background, "App background"),
        new(AppearanceColorTarget.Accent, "Accent & buttons"),
        new(AppearanceColorTarget.Surface, "Panels & controls")
    ];

    private readonly SettingsService _settingsService = new();
    private readonly DeviceDiscoveryService _deviceDiscoveryService = new();
    private readonly FfmpegSetupService _ffmpegSetupService = new();
    private readonly AppUpdateService _appUpdateService = new();
    private readonly ReplayBufferService _replayBufferService;
    private readonly ClipLibraryService _clipLibraryService;
    private readonly ClipTrimService _clipTrimService;
    private readonly GlobalHotkeyService _hotkeyService = new();
    private readonly StartupRegistrationService _startupRegistrationService = new();
    private readonly TrayIconService _trayIconService;
    private readonly NativeWindowThemeService _nativeWindowThemeService;
    private readonly ClipSavedSoundService _clipSavedSoundService = new();
    private readonly AppLaunchOptions _launchOptions;
    private readonly DispatcherTimer _playerTimer;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly SemaphoreSlim _captureCommandGate = new(1, 1);
    private readonly SemaphoreSlim _libraryRefreshGate = new(1, 1);
    private readonly object _libraryRefreshCancellationGate = new();

    private AppSettings _settings = new();
    private ReplayStateSnapshot _latestState = new(
        ReplayState.Stopped,
        TimeSpan.Zero,
        TimeSpan.FromMinutes(2),
        0);
    private bool _isInitializing = true;
    private bool _isClosing;
    private bool _exitRequested;
    private bool _engineReady;
    private bool _backgroundHintShown;
    private bool _libraryRefreshPending;
    private bool _captureCriticalPresentationActive;
    private bool _captureRestartInProgress;
    private bool _captureRecoveryQueued;
    private bool _automaticCaptureSourceFallback;
    private bool _replayStartRequested;
    private bool _playerSourceReleasedForBackground;
    private bool _isPlayerPlaying;
    private bool _playWhenOpened;
    private bool _isPlayerSeeking;
    private bool _resumePlayerAfterSeek;
    private bool _isUpdatingPlayerControls;
    private bool _isPlayerMuted;
    private bool _replayPlaybackAudioOptIn;
    private double _playerVolumeBeforeMute = 0.8;
    private string? _pendingLibraryPreferredPath;
    private string? _lastSavedPath;
    private ClipLibraryItem? _currentClip;
    private LibraryWindow? _libraryWindow;
    private OverlayWindow? _overlayWindow;
    private GlobalHotkeyAction? _capturingHotkeyAction;
    private CancellationTokenSource? _activeLibraryRefreshCancellation;
    private CancellationTokenSource? _displayModeChangeCancellation;
    private bool _refreshingDisplaySelection;
    private string? _lastTrayStatus;
    private bool? _lastTrayCanSave;
    private int _automaticCaptureRecoveryCount;

    public MainWindow()
        : this(AppLaunchOptions.Interactive)
    {
    }

    internal MainWindow(AppLaunchOptions launchOptions)
    {
        _launchOptions = launchOptions ?? throw new ArgumentNullException(nameof(launchOptions));
        _replayBufferService = new ReplayBufferService(_ffmpegSetupService);
        _clipLibraryService = new ClipLibraryService(_ffmpegSetupService);
        _clipTrimService = new ClipTrimService(_ffmpegSetupService);
        _replayBufferService.StateChanged += ReplayBufferService_StateChanged;
        _replayBufferService.CaptureRecoveryRequested +=
            ReplayBufferService_CaptureRecoveryRequested;
        _appUpdateService.StateChanged += AppUpdateService_StateChanged;

        InitializeComponent();
        _nativeWindowThemeService = new NativeWindowThemeService(this);
        _trayIconService = new TrayIconService();
        _trayIconService.ShowRequested += TrayIconService_ShowRequested;
        _trayIconService.SaveClipRequested += TrayIconService_SaveClipRequested;
        _trayIconService.ExitRequested += TrayIconService_ExitRequested;

        _playerTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };
        _playerTimer.Tick += PlayerTimer_Tick;

        Loaded += MainWindow_Loaded;
        Activated += MainWindow_Activated;
        Deactivated += MainWindow_Deactivated;
        Closing += MainWindow_Closing;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        System.Windows.Application.Current.SessionEnding += Application_SessionEnding;
        SystemEvents.DisplaySettingsChanged += SystemEvents_DisplaySettingsChanged;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        var initializationCompleted = false;
        if (!_launchOptions.StartInBackground)
        {
            RunStartupMotion();
        }

        try
        {
            _settings = await _settingsService.LoadAsync(_lifetimeCancellation.Token);
            _settings.SaveClipHotkey ??= HotkeyGesture.DefaultSaveClip;
            _settings.ToggleOverlayHotkey ??= HotkeyGesture.DefaultToggleOverlay;
            _settings.RecentClipCount = AppSettings.NormalizeRecentClipCount(_settings.RecentClipCount);
            PopulateControls();
            EnsureSaveDirectory();
            RefreshEngineState();
            UpdateStorageText();
            InitializeUpdateControls();

            _hotkeyService.SaveClipPressed += HotkeyService_SaveClipPressed;
            _hotkeyService.ToggleOverlayPressed += HotkeyService_ToggleOverlayPressed;
            _hotkeyService.RegistrationFailed += HotkeyService_RegistrationFailed;
            try
            {
                _hotkeyService.Register(
                    this,
                    _settings.SaveClipHotkey,
                    _settings.ToggleOverlayHotkey);
            }
            catch (Exception exception)
            {
                ShowError($"The global shortcuts could not be registered. {exception.Message}");
            }

            initializationCompleted = true;
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // The window was closed during startup.
        }
        catch (Exception exception)
        {
            ShowError($"ClipForge could not finish starting. {exception.Message}");
        }
        finally
        {
            _isInitializing = false;
            UpdateControlsForState(_latestState);

            var staleAutoStartLaunch =
                _launchOptions.IsAutoStart &&
                initializationCompleted &&
                !_settings.StartReplayWithWindows;
            if (staleAutoStartLaunch)
            {
                try
                {
                    _startupRegistrationService.SetEnabled(false);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or InvalidOperationException or
                    ArgumentException or NotSupportedException or Win32Exception or
                    System.Runtime.InteropServices.COMException)
                {
                    // The saved opt-out still wins even if a stale shortcut cannot be removed.
                }

                _exitRequested = true;
                Close();
            }
            else if (!_isClosing)
            {
                if (_launchOptions.IsAutoStart && !initializationCompleted)
                {
                    _trayIconService.ShowReplayStartupFailure(
                        "ClipForge could not finish initialization. Open it from the tray to review the error.");
                }
                else if (_launchOptions.IsAutoStart && _settings.StartReplayWithWindows && !_engineReady)
                {
                    _trayIconService.ShowReplayStartupFailure(
                        "Install the ClipForge capture engine, then start replay once from the app.");
                }
                else if (ShouldAutoStartReplay(
                             _launchOptions.IsAutoStart,
                             _settings.StartReplayWithWindows,
                             initializationCompleted,
                             _engineReady,
                             _replayBufferService.IsRunning,
                             _isClosing))
                {
                    await StartReplayAfterWindowsLoginAsync();
                }

                _ = RunAutomaticUpdateCheckAsync();
                _ = RefreshClipLibraryAsync();
            }
        }
    }

    private void PopulateControls()
    {
        ReplayLengthComboBox.ItemsSource = ReplayLengthOption.All;
        ReplayLengthComboBox.SelectedItem = ReplayLengthOption.All.FirstOrDefault(
            option => (int)option.Duration.TotalSeconds == _settings.ReplaySeconds)
            ?? ReplayLengthOption.All.Single(option => option.Duration == TimeSpan.FromMinutes(2));

        ResolutionComboBox.ItemsSource = ResolutionOption.All;
        ResolutionComboBox.SelectedItem = ResolutionOption.All.FirstOrDefault(
            option => string.Equals(option.Id, _settings.ResolutionId, StringComparison.OrdinalIgnoreCase))
            ?? ResolutionOption.All.Single(option => option.Id == "1080p");

        FpsComboBox.ItemsSource = FrameRateOptions;
        FpsComboBox.SelectedItem = FrameRateOptions.Contains(_settings.FramesPerSecond)
            ? _settings.FramesPerSecond
            : 30;

        var displays = _deviceDiscoveryService.GetDisplays();
        DisplayComboBox.ItemsSource = displays;
        DisplayComboBox.SelectedItem = displays.FirstOrDefault(display =>
            string.Equals(display.DeviceName, _settings.DisplayDeviceName, StringComparison.OrdinalIgnoreCase))
            ?? displays.FirstOrDefault(display => display.IsPrimary)
            ?? displays.FirstOrDefault();

        var outputDevices = GetDevicesSafely(_deviceDiscoveryService.GetOutputDevices, "desktop audio");
        OutputDeviceComboBox.ItemsSource = outputDevices;
        OutputDeviceComboBox.SelectedItem = SelectAudioDevice(outputDevices, _settings.OutputAudioDeviceId);
        SystemAudioCheckBox.IsEnabled = outputDevices.Count > 0;
        SystemAudioCheckBox.IsChecked = outputDevices.Count > 0 && _settings.CaptureSystemAudio;
        OutputDeviceComboBox.IsEnabled = SystemAudioCheckBox.IsChecked == true;

        var microphones = GetDevicesSafely(_deviceDiscoveryService.GetMicrophones, "microphones");
        MicrophoneComboBox.ItemsSource = microphones;
        MicrophoneComboBox.SelectedItem = SelectAudioDevice(microphones, _settings.MicrophoneDeviceId);
        MicrophoneCheckBox.IsEnabled = microphones.Count > 0;
        MicrophoneCheckBox.IsChecked = microphones.Count > 0 && _settings.CaptureMicrophone;
        MicrophoneComboBox.IsEnabled = MicrophoneCheckBox.IsChecked == true;

        SavePathTextBox.Text = string.IsNullOrWhiteSpace(_settings.SaveDirectory)
            ? AppSettings.GetDefaultSaveDirectory()
            : _settings.SaveDirectory;
        AutoUpdateCheckBox.IsChecked = _settings.CheckForUpdatesAutomatically;
        ClipSavedSoundCheckBox.IsChecked = _settings.PlayClipSavedSound;
        StartReplayWithWindowsCheckBox.IsChecked = _settings.StartReplayWithWindows;
        StartReplayWithWindowsCheckBox.IsEnabled = _startupRegistrationService.IsSupported;
        StartupHintText.Text = !StartReplayWithWindowsCheckBox.IsEnabled
            ? "Install ClipForge Setup to enable automatic Windows startup."
            : _settings.StartReplayWithWindows
                ? "Enabled. ClipForge will start hidden and begin replay after you sign in."
                : "ClipForge starts hidden in the tray and uses your saved capture settings.";
        RecentClipCountComboBox.ItemsSource = RecentClipCountOptions;
        RecentClipCountComboBox.SelectedItem = _settings.RecentClipCount;
        AppearanceTargetComboBox.ItemsSource = AppearanceTargetOptions;
        AppearanceTargetComboBox.SelectedIndex = 0;
        HotkeyText.Text = _settings.SaveClipHotkey.DisplayText;
        OverlayHotkeyText.Text = _settings.ToggleOverlayHotkey.DisplayText;
        ApplyAppearanceTheme();
        UpdateAppearanceEditor();

        UpdatePrimaryActionText();
    }

    private IReadOnlyList<AudioDeviceOption> GetDevicesSafely(
        Func<IReadOnlyList<AudioDeviceOption>> discover,
        string description)
    {
        try
        {
            return discover();
        }
        catch (Exception exception)
        {
            ShowError($"ClipForge could not list {description}. {exception.Message}");
            return [];
        }
    }

    private static AudioDeviceOption? SelectAudioDevice(
        IReadOnlyList<AudioDeviceOption> devices,
        string? preferredId) =>
        devices.FirstOrDefault(device =>
            string.Equals(device.Id, preferredId, StringComparison.OrdinalIgnoreCase))
        ?? devices.FirstOrDefault(device => device.IsDefault)
        ?? devices.FirstOrDefault();

    private async void BufferToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || _isClosing)
        {
            return;
        }

        await RunCaptureCommandAsync(async () =>
        {
            if (_replayBufferService.IsRunning)
            {
                _replayStartRequested = false;
                ResetAutomaticCaptureRecovery();
                await _replayBufferService.StopAsync();
            }
            else
            {
                ResetAutomaticCaptureRecovery();
                await StartReplayCoreAsync();
            }
        });
    }

    private async void SaveClipButton_Click(object sender, RoutedEventArgs e) =>
        await SaveClipAsync();

    private async Task SaveClipAsync()
    {
        if (_isClosing)
        {
            return;
        }

        if (!_replayBufferService.IsRunning)
        {
            ShowError("Start Instant Replay before saving a clip.");
            return;
        }

        if (ReplayLengthComboBox.SelectedItem is not ReplayLengthOption replayLength)
        {
            ShowError("Choose a replay length before saving a clip.");
            return;
        }

        SyncSettingsFromControls();
        EnsureSaveDirectory();
        HideError();
        SaveClipButton.IsEnabled = false;

        try
        {
            var path = await _replayBufferService.SaveClipAsync(
                replayLength.Duration,
                _settings.SaveDirectory,
                _lifetimeCancellation.Token);
            ShowLastSaved(path);
            _clipSavedSoundService.TryPlay(_settings.PlayClipSavedSound);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // Application shutdown cancels an in-progress export.
        }
        catch (Exception exception)
        {
            ShowError($"The clip could not be saved. {exception.Message}");
        }
        finally
        {
            if (IsVisible && IsActive)
            {
                UpdateControlsForState(_latestState);
            }
            else
            {
                UpdateBackgroundIndicators(_latestState);
            }
        }
    }

    private async void InstallEngineButton_Click(object sender, RoutedEventArgs e)
    {
        InstallEngineButton.IsEnabled = false;
        InstallProgressBar.Value = 0;
        InstallStatusText.Text = "Downloading the capture engine…";
        HideError();

        var progress = new Progress<double>(value =>
        {
            InstallProgressBar.Value = Math.Clamp(value * 100, 0, 100);
            InstallStatusText.Text = value < 0.92
                ? $"Downloading… {value:P0}"
                : "Finishing installation…";
        });

        try
        {
            await _ffmpegSetupService.DownloadAsync(progress, _lifetimeCancellation.Token);
            InstallProgressBar.Value = 100;
            InstallStatusText.Text = "Capture engine installed";
            RefreshEngineState();
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // The window closed during the download.
        }
        catch (Exception exception)
        {
            InstallStatusText.Text = "Installation did not finish";
            ShowError($"The capture engine could not be installed. {exception.Message}");
        }
        finally
        {
            InstallEngineButton.IsEnabled = true;
        }
    }

    private async void DisplayComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingDisplaySelection)
        {
            return;
        }

        await CaptureConfigurationChangedAsync(restartRequired: true);
    }

    private async void ReplayLengthComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        SyncSettingsFromControls();
        UpdatePrimaryActionText();
        UpdateStorageText();
        if (ReplayLengthComboBox.SelectedItem is ReplayLengthOption replayLength &&
            _replayBufferService.IsRunning)
        {
            _replayBufferService.UpdateRetention(replayLength.Duration);
        }

        await PersistSettingsAsync();
    }

    private async void FpsComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        await CaptureConfigurationChangedAsync(restartRequired: true);

    private async void ResolutionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        await CaptureConfigurationChangedAsync(restartRequired: true);

    private async void SystemAudioCheckBox_Checked(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        OutputDeviceComboBox.IsEnabled = true;
        await CaptureConfigurationChangedAsync(restartRequired: true);
    }

    private async void SystemAudioCheckBox_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        OutputDeviceComboBox.IsEnabled = false;
        await CaptureConfigurationChangedAsync(restartRequired: true);
    }

    private async void OutputDeviceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        await CaptureConfigurationChangedAsync(restartRequired: SystemAudioCheckBox.IsChecked == true);

    private async void MicrophoneCheckBox_Checked(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        MicrophoneComboBox.IsEnabled = true;
        await CaptureConfigurationChangedAsync(restartRequired: true);
    }

    private async void MicrophoneCheckBox_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        MicrophoneComboBox.IsEnabled = false;
        await CaptureConfigurationChangedAsync(restartRequired: true);
    }

    private async void MicrophoneComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        await CaptureConfigurationChangedAsync(restartRequired: MicrophoneCheckBox.IsChecked == true);

    private async Task CaptureConfigurationChangedAsync(bool restartRequired)
    {
        if (_isInitializing || _isClosing)
        {
            return;
        }

        SyncSettingsFromControls();
        UpdateStorageText();
        await PersistSettingsAsync();

        if (restartRequired &&
            (_replayBufferService.IsRunning ||
             _captureRestartInProgress ||
             _replayStartRequested))
        {
            ResetAutomaticCaptureRecovery();
            await RunCaptureCommandAsync(async () =>
            {
                if (_isClosing)
                {
                    return;
                }

                _captureRestartInProgress = true;
                SetCaptureCriticalPresentationState(isActive: true);
                try
                {
                    await _replayBufferService.StopAsync();
                    if (!_isClosing && _replayStartRequested)
                    {
                        await StartReplayCoreAsync();
                    }
                }
                finally
                {
                    _captureRestartInProgress = false;
                    SetCaptureCriticalPresentationState(
                        IsCapturePresentationSuspendedState(_latestState));
                }
            });
        }
    }

    private async Task<Exception?> RunCaptureCommandAsync(Func<Task> command, bool showError = true)
    {
        var gateEntered = false;
        Exception? commandError = null;

        try
        {
            await _captureCommandGate.WaitAsync(_lifetimeCancellation.Token);
            gateEntered = true;
            if (_isClosing)
            {
                return null;
            }

            BufferToggleButton.IsEnabled = false;
            HideError();
            await command();
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // Application shutdown.
        }
        catch (Exception exception)
        {
            commandError = exception;
            if (showError)
            {
                ShowError(exception.Message);
            }
        }
        finally
        {
            if (gateEntered)
            {
                _captureCommandGate.Release();
                UpdateControlsForState(_latestState);
            }
        }

        return commandError;
    }

    internal static bool ShouldAutoStartReplay(
        bool isAutoStartLaunch,
        bool preferenceEnabled,
        bool initializationCompleted,
        bool engineReady,
        bool replayRunning,
        bool isClosing) =>
        isAutoStartLaunch &&
        preferenceEnabled &&
        initializationCompleted &&
        engineReady &&
        !replayRunning &&
        !isClosing;

    private async Task StartReplayAfterWindowsLoginAsync()
    {
        try
        {
            ResetAutomaticCaptureRecovery();
            // Windows can report the signed-in desktop before GPU and audio
            // endpoints are fully ready. Give them a short bounded window.
            await Task.Delay(TimeSpan.FromSeconds(3), _lifetimeCancellation.Token);

            var error = await RunCaptureCommandAsync(
                () => StartReplayCoreAsync(),
                showError: false);
            if (error is not null && !_lifetimeCancellation.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(4), _lifetimeCancellation.Token);
                error = await RunCaptureCommandAsync(
                    () => StartReplayCoreAsync(),
                    showError: false);
            }

            if (error is not null && !_lifetimeCancellation.IsCancellationRequested)
            {
                var message = $"Automatic replay could not start. {error.Message}";
                ShowError(message);
                _trayIconService.ShowReplayStartupFailure(error.Message);
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // Windows is signing out or ClipForge is exiting.
        }
    }

    private async Task StartReplayCoreAsync(
        bool sourceSafetyMode = false,
        VideoEncodingStrategy? sessionStrategyOverride = null)
    {
        if (_clipTrimService.HasReplayBlockingTrimWork)
        {
            throw new InvalidOperationException(
                "Wait for the trim export to finish or cancel it before starting Instant Replay.");
        }

        if (_ffmpegSetupService.FindExecutable() is null)
        {
            InstallEnginePanel.Visibility = Visibility.Visible;
            throw new InvalidOperationException("Install the capture engine before starting Instant Replay.");
        }

        SyncSettingsFromControls();
        EnsureSaveDirectory();
        await PersistSettingsAsync();
        if (_clipTrimService.HasReplayBlockingTrimWork)
        {
            throw new InvalidOperationException(
                "Wait for the trim export to finish or cancel it before starting Instant Replay.");
        }

        var configuration = BuildCaptureConfiguration();
        if (sourceSafetyMode)
        {
            if (sessionStrategyOverride is not null)
            {
                throw new InvalidOperationException(
                    "Source safety recovery must runtime-verify the native capture geometry.");
            }

            var sourceResolution = ResolutionOption.All.First(option =>
                option.Width is null && option.Height is null);
            configuration = configuration with { Resolution = sourceResolution };
        }

        // Decoder graphs, media helpers, and thumbnail refreshes are presentation
        // work. Release them before FFmpeg starts so capture owns the available
        // GPU/CPU headroom and cannot feed preview audio back into desktop capture.
        SetCaptureCriticalPresentationState(isActive: true);
        try
        {
            _replayStartRequested = true;
            if (sessionStrategyOverride is null && !sourceSafetyMode)
            {
                await _replayBufferService.StartAsync(
                    configuration,
                    _lifetimeCancellation.Token);
            }
            else
            {
                await _replayBufferService.StartAsync(
                    configuration,
                    _lifetimeCancellation.Token,
                    sessionStrategyOverride,
                    sourceSafetyMode);
            }
        }
        catch
        {
            if (!_replayBufferService.IsRunning)
            {
                SetCaptureCriticalPresentationState(_captureRestartInProgress);
            }

            throw;
        }
    }

    private CaptureConfiguration BuildCaptureConfiguration()
    {
        var selectedDisplay = DisplayComboBox.SelectedItem as DisplayOption
            ?? throw new InvalidOperationException("No display is available to capture.");
        var display = FindDisplayByDeviceName(
                          _deviceDiscoveryService.GetDisplays(),
                          selectedDisplay.DeviceName)
                      ?? throw new InvalidOperationException(
                          "The selected display is no longer available. Choose another display and try again.");
        var resolution = ResolutionComboBox.SelectedItem as ResolutionOption
            ?? throw new InvalidOperationException("Choose a recording resolution.");
        var replayLength = ReplayLengthComboBox.SelectedItem as ReplayLengthOption
            ?? throw new InvalidOperationException("Choose a replay length.");
        var framesPerSecond = FpsComboBox.SelectedItem is int fps
            ? fps
            : throw new InvalidOperationException("Choose a frame rate.");

        var captureSystemAudio = SystemAudioCheckBox.IsChecked == true;
        var outputDevice = OutputDeviceComboBox.SelectedItem as AudioDeviceOption;
        if (captureSystemAudio && outputDevice is null)
        {
            throw new InvalidOperationException("No desktop audio output is available. Turn desktop audio off or connect an output device.");
        }

        var captureMicrophone = MicrophoneCheckBox.IsChecked == true;
        var microphone = MicrophoneComboBox.SelectedItem as AudioDeviceOption;
        if (captureMicrophone && microphone is null)
        {
            throw new InvalidOperationException("No microphone is available. Turn microphone capture off or connect a microphone.");
        }

        return new CaptureConfiguration(
            display,
            resolution,
            framesPerSecond,
            replayLength.Duration,
            captureSystemAudio,
            outputDevice,
            captureMicrophone,
            microphone,
            _settings.SaveDirectory);
    }

    internal static DisplayOption? FindDisplayByDeviceName(
        IReadOnlyList<DisplayOption> displays,
        string? deviceName)
    {
        ArgumentNullException.ThrowIfNull(displays);
        return displays.FirstOrDefault(display => string.Equals(
            display.DeviceName,
            deviceName,
            StringComparison.OrdinalIgnoreCase));
    }

    private void SystemEvents_DisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (_isClosing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(QueueDisplayModeRefresh);
    }

    private void QueueDisplayModeRefresh()
    {
        if (_isClosing)
        {
            return;
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        var previous = Interlocked.Exchange(
            ref _displayModeChangeCancellation,
            cancellation);
        previous?.Cancel();
        _ = RefreshDisplayModeAfterDelayAsync(cancellation);
    }

    private async Task RefreshDisplayModeAfterDelayAsync(
        CancellationTokenSource refreshCancellation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(900), refreshCancellation.Token);
            if (_isClosing ||
                DisplayComboBox.SelectedItem is not DisplayOption selectedDisplay)
            {
                return;
            }

            var displays = _deviceDiscoveryService.GetDisplays();
            var currentDisplay = FindDisplayByDeviceName(displays, selectedDisplay.DeviceName);
            if (currentDisplay is null)
            {
                return;
            }

            var geometryChanged = currentDisplay.Left != selectedDisplay.Left ||
                                  currentDisplay.Top != selectedDisplay.Top ||
                                  currentDisplay.Width != selectedDisplay.Width ||
                                  currentDisplay.Height != selectedDisplay.Height ||
                                  currentDisplay.MonitorIndex != selectedDisplay.MonitorIndex;
            if (!geometryChanged)
            {
                return;
            }

            _refreshingDisplaySelection = true;
            try
            {
                DisplayComboBox.ItemsSource = displays;
                DisplayComboBox.SelectedItem = currentDisplay;
            }
            finally
            {
                _refreshingDisplaySelection = false;
            }

            // Preserve the rolling buffer when direct WGC can keep the same
            // encoded size. Source/output-size changes and GDI's baked desktop
            // geometry still require a restart; a mode-change fault is also
            // recovered while the user's replay intent remains active.
            var restartRequired = ShouldRestartReplayAfterDisplayChange(
                _replayStartRequested,
                _replayBufferService.IsRunning,
                _captureRestartInProgress,
                _replayBufferService.LastCapturePlan,
                currentDisplay);
            await CaptureConfigurationChangedAsync(restartRequired);
        }
        catch (OperationCanceledException) when (refreshCancellation.IsCancellationRequested)
        {
            // A newer display transition superseded this debounce.
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or ArgumentException or Win32Exception)
        {
            if (!_isClosing)
            {
                ShowError($"ClipForge could not adapt to the new display mode. {exception.Message}");
            }
        }
        finally
        {
            if (ReferenceEquals(_displayModeChangeCancellation, refreshCancellation))
            {
                _displayModeChangeCancellation = null;
            }

            refreshCancellation.Dispose();
        }
    }

    internal static bool ShouldRestartReplayAfterDisplayChange(
        bool replayStartRequested,
        bool replayRunning,
        bool captureRestartInProgress,
        CaptureSessionPlan? capturePlan,
        DisplayOption currentDisplay)
    {
        ArgumentNullException.ThrowIfNull(currentDisplay);
        if (!replayStartRequested)
        {
            return false;
        }

        // A mode transition may fault WGC before the debounce completes. The
        // user's explicit replay intent survives that transient failure.
        if (!replayRunning && !captureRestartInProgress)
        {
            return true;
        }

        return capturePlan is null ||
               CaptureGeometry.RequiresRestartForDisplayChange(
                   capturePlan,
                   currentDisplay);
    }

    private void SyncSettingsFromControls()
    {
        if (ReplayLengthComboBox.SelectedItem is ReplayLengthOption replayLength)
        {
            _settings.ReplaySeconds = checked((int)replayLength.Duration.TotalSeconds);
        }

        if (ResolutionComboBox.SelectedItem is ResolutionOption resolution)
        {
            _settings.ResolutionId = resolution.Id;
        }

        if (FpsComboBox.SelectedItem is int fps)
        {
            _settings.FramesPerSecond = fps;
        }

        _settings.DisplayDeviceName = (DisplayComboBox.SelectedItem as DisplayOption)?.DeviceName;
        _settings.CaptureSystemAudio = SystemAudioCheckBox.IsChecked == true;
        _settings.OutputAudioDeviceId = (OutputDeviceComboBox.SelectedItem as AudioDeviceOption)?.Id;
        _settings.CaptureMicrophone = MicrophoneCheckBox.IsChecked == true;
        _settings.MicrophoneDeviceId = (MicrophoneComboBox.SelectedItem as AudioDeviceOption)?.Id;
        _settings.StartReplayWithWindows = StartReplayWithWindowsCheckBox.IsChecked == true;
        _settings.CheckForUpdatesAutomatically = AutoUpdateCheckBox.IsChecked == true;
        _settings.PlayClipSavedSound = ClipSavedSoundCheckBox.IsChecked == true;
        _settings.BackgroundColor = AppSettings.NormalizeBackgroundColor(_settings.BackgroundColor);
        _settings.AccentColor = AppSettings.NormalizeAccentColor(_settings.AccentColor);
        _settings.SurfaceColor = AppSettings.NormalizeSurfaceColor(_settings.SurfaceColor);
        _settings.RecentClipCount = RecentClipCountComboBox.SelectedItem is int recentClipCount
            ? AppSettings.NormalizeRecentClipCount(recentClipCount)
            : 4;
        _settings.SaveDirectory = string.IsNullOrWhiteSpace(SavePathTextBox.Text)
            ? AppSettings.GetDefaultSaveDirectory()
            : SavePathTextBox.Text;
    }

    private async Task PersistSettingsAsync()
    {
        try
        {
            await _settingsService.SaveAsync(_settings, _lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // Application shutdown.
        }
        catch (Exception exception)
        {
            ShowError($"Your settings could not be saved. {exception.Message}");
        }
    }

    private void AppearanceTargetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitializing && !_isClosing)
        {
            UpdateAppearanceEditor();
        }
    }

    private async void AppearanceSwatchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || _isClosing || sender is not Button { Tag: string color })
        {
            return;
        }

        ApplySelectedAppearanceColor(color);
        await PersistSettingsAsync();
    }

    private async void CustomAppearanceColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || _isClosing)
        {
            return;
        }

        var current = ParseThemeColor(GetSelectedAppearanceColor());
        using var dialog = new Forms.ColorDialog
        {
            AllowFullOpen = true,
            AnyColor = true,
            FullOpen = true,
            SolidColorOnly = true,
            Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B)
        };

        var dialogOwner = new ColorDialogOwner(
            new System.Windows.Interop.WindowInteropHelper(this).Handle);
        if (dialog.ShowDialog(dialogOwner) != Forms.DialogResult.OK)
        {
            return;
        }

        ApplySelectedAppearanceColor($"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}");
        await PersistSettingsAsync();
    }

    private async void ResetAppearanceColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || _isClosing)
        {
            return;
        }

        var defaultColor = GetSelectedAppearanceTarget() switch
        {
            AppearanceColorTarget.Accent => AppSettings.DefaultAccentColor,
            AppearanceColorTarget.Surface => AppSettings.DefaultSurfaceColor,
            _ => AppSettings.DefaultBackgroundColor
        };
        ApplySelectedAppearanceColor(defaultColor);
        await PersistSettingsAsync();
    }

    private void ApplySelectedAppearanceColor(string? requestedColor)
    {
        switch (GetSelectedAppearanceTarget())
        {
            case AppearanceColorTarget.Accent:
                _settings.AccentColor = AppSettings.NormalizeAccentColor(requestedColor);
                break;
            case AppearanceColorTarget.Surface:
                _settings.SurfaceColor = AppSettings.NormalizeSurfaceColor(requestedColor);
                break;
            default:
                _settings.BackgroundColor = AppSettings.NormalizeBackgroundColor(requestedColor);
                break;
        }

        ApplyAppearanceTheme();
        UpdateAppearanceEditor();
    }

    private void ApplyAppearanceTheme()
    {
        var palette = AppearanceThemePalette.Create(
            _settings.BackgroundColor,
            _settings.AccentColor,
            _settings.SurfaceColor);
        _settings.BackgroundColor = palette.BackgroundColor;
        _settings.AccentColor = palette.AccentColor;
        _settings.SurfaceColor = palette.SurfaceColor;

        SetThemeColorResource("WindowColor", palette.BackgroundColor);
        SetThemeColorResource("SurfaceColor", palette.SurfaceColor);
        SetThemeColorResource("SurfaceRaisedColor", palette.SurfaceRaisedColor);
        SetThemeColorResource("SurfaceHoverColor", palette.SurfaceHoverColor);
        SetThemeColorResource("SurfaceTranslucentColor", palette.SurfaceTranslucentColor);
        SetThemeColorResource("SurfaceOverlayColor", palette.SurfaceOverlayColor);
        SetThemeColorResource("BorderColor", palette.BorderColor);
        SetThemeColorResource("BorderStrongColor", palette.BorderStrongColor);
        SetThemeColorResource("AccentColor", palette.AccentColor);
        SetThemeColorResource("AccentHoverColor", palette.AccentHoverColor);
        SetThemeColorResource("AccentPressedColor", palette.AccentPressedColor);
        SetThemeColorResource("AccentSoftColor", palette.AccentSoftColor);
        SetThemeColorResource("AccentBorderColor", palette.AccentBorderColor);
        SetThemeColorResource("AccentGradientStartColor", palette.AccentGradientStartColor);
        SetThemeColorResource("AccentGradientEndColor", palette.AccentGradientEndColor);
        SetThemeColorResource("PrimaryButtonTextColor", palette.PrimaryButtonTextColor);
        SetThemeColorResource("HeroGradientStartColor", palette.HeroGradientStartColor);
        SetThemeColorResource("HeroGradientMiddleColor", palette.HeroGradientMiddleColor);
        SetThemeColorResource("HeroGradientEndColor", palette.HeroGradientEndColor);

        // Most controls consume the shared brush objects through StaticResource. Updating the
        // owning Color keys above keeps DynamicResource consumers correct; updating the brush
        // instances in place also refreshes controls/templates that already hold a brush reference.
        SetThemeBrushColorResource("WindowBrush", palette.BackgroundColor);
        SetThemeBrushColorResource("SurfaceBrush", palette.SurfaceColor);
        SetThemeBrushColorResource("SurfaceRaisedBrush", palette.SurfaceRaisedColor);
        SetThemeBrushColorResource("SurfaceHoverBrush", palette.SurfaceHoverColor);
        SetThemeBrushColorResource("SurfaceTranslucentBrush", palette.SurfaceTranslucentColor);
        SetThemeBrushColorResource("SurfaceOverlayBrush", palette.SurfaceOverlayColor);
        SetThemeBrushColorResource("BorderBrush", palette.BorderColor);
        SetThemeBrushColorResource("BorderStrongBrush", palette.BorderStrongColor);
        SetThemeBrushColorResource("AccentBrush", palette.AccentColor);
        SetThemeBrushColorResource("AccentHoverBrush", palette.AccentHoverColor);
        SetThemeBrushColorResource("AccentPressedBrush", palette.AccentPressedColor);
        SetThemeBrushColorResource("AccentSoftBrush", palette.AccentSoftColor);
        SetThemeBrushColorResource("AccentBorderBrush", palette.AccentBorderColor);
        SetThemeBrushColorResource("PrimaryButtonTextBrush", palette.PrimaryButtonTextColor);
    }

    private void UpdateAppearanceEditor()
    {
        if (AppearanceSwatchesPanel is null || AppearanceColorValueText is null)
        {
            return;
        }

        var target = GetSelectedAppearanceTarget();
        var colors = GetAppearancePresets(target);
        var selectedColor = GetSelectedAppearanceColor();
        var buttons = AppearanceSwatchesPanel.Children.OfType<Button>().ToArray();
        for (var index = 0; index < buttons.Length && index < colors.Count; index++)
        {
            var button = buttons[index];
            var color = colors[index];
            var swatchBrush = new SolidColorBrush(ParseThemeColor(color));
            swatchBrush.Freeze();
            button.Tag = color;
            button.Background = swatchBrush;
            button.ToolTip = $"Apply {color}";
            button.SetValue(
                System.Windows.Automation.AutomationProperties.NameProperty,
                $"Apply {color} to {target}");
            var isSelected = string.Equals(color, selectedColor, StringComparison.OrdinalIgnoreCase);
            button.BorderBrush = Brush(isSelected ? "AccentHoverBrush" : "BorderStrongBrush");
            button.BorderThickness = new Thickness(isSelected ? 2 : 1);
            button.SetValue(
                System.Windows.Automation.AutomationProperties.ItemStatusProperty,
                isSelected ? "Selected" : string.Empty);
        }

        AppearanceColorValueText.Text = selectedColor;
        AppearanceHintText.Text = target switch
        {
            AppearanceColorTarget.Accent =>
                "Changes primary buttons, toggles, sliders, focus rings, and highlights.",
            AppearanceColorTarget.Surface =>
                "Changes the Capture settings sidebar, cards, controls, menus, and the lightweight overlay.",
            _ =>
                "Changes the app canvas behind the panels. Bright custom colors are darkened for readability."
        };
    }

    private AppearanceColorTarget GetSelectedAppearanceTarget() =>
        AppearanceTargetComboBox.SelectedItem is AppearanceTargetOption option
            ? option.Target
            : AppearanceColorTarget.Background;

    private string GetSelectedAppearanceColor() => GetSelectedAppearanceTarget() switch
    {
        AppearanceColorTarget.Accent => AppSettings.NormalizeAccentColor(_settings.AccentColor),
        AppearanceColorTarget.Surface => AppSettings.NormalizeSurfaceColor(_settings.SurfaceColor),
        _ => AppSettings.NormalizeBackgroundColor(_settings.BackgroundColor)
    };

    private static IReadOnlyList<string> GetAppearancePresets(AppearanceColorTarget target) => target switch
    {
        AppearanceColorTarget.Accent => ["#7C6CF2", "#3B82F6", "#10B981", "#F97316", "#E5486D"],
        AppearanceColorTarget.Surface => ["#12151D", "#101927", "#17131F", "#101B19", "#181A20"],
        _ => ["#0B0D12", "#0D1422", "#161321", "#0D1A19", "#17191F"]
    };

    private static void SetThemeColorResource(string resourceKey, string colorValue)
    {
        var color = ParseThemeColor(colorValue);
        SetThemeResourceValue(System.Windows.Application.Current.Resources, resourceKey, color);
    }

    private static void SetThemeBrushColorResource(string resourceKey, string colorValue) =>
        SetThemeBrushColorResource(
            System.Windows.Application.Current.Resources,
            resourceKey,
            ParseThemeColor(colorValue));

    internal static bool SetThemeBrushColorResource(
        ResourceDictionary resources,
        object resourceKey,
        Color color)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(resourceKey);

        if (TryGetThemeResourceValue(resources, resourceKey, out var resourceValue) &&
            resourceValue is SolidColorBrush brush)
        {
            if (!brush.IsFrozen)
            {
                brush.Color = color;
                return true;
            }

            return SetThemeResourceValue(resources, resourceKey, new SolidColorBrush(color));
        }

        return false;
    }

    /// <summary>
    /// Replaces an existing resource in the dictionary that owns it. ResourceDictionary.Contains
    /// also searches merged dictionaries, so assigning to the root after a successful Contains
    /// check creates a shadow value that brushes declared inside the merged theme cannot see.
    /// Updating the owner preserves DynamicResource invalidation for every open window and overlay.
    /// </summary>
    internal static bool SetThemeResourceValue(
        ResourceDictionary resources,
        object resourceKey,
        object resourceValue)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(resourceKey);
        ArgumentNullException.ThrowIfNull(resourceValue);

        if (resources.Keys.Cast<object>().Any(key => Equals(key, resourceKey)))
        {
            resources[resourceKey] = resourceValue;
            return true;
        }

        foreach (var dictionary in resources.MergedDictionaries.Reverse())
        {
            if (SetThemeResourceValue(dictionary, resourceKey, resourceValue))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetThemeResourceValue(
        ResourceDictionary resources,
        object resourceKey,
        out object? resourceValue)
    {
        if (resources.Keys.Cast<object>().Any(key => Equals(key, resourceKey)))
        {
            resourceValue = resources[resourceKey];
            return true;
        }

        foreach (var dictionary in resources.MergedDictionaries.Reverse())
        {
            if (TryGetThemeResourceValue(dictionary, resourceKey, out resourceValue))
            {
                return true;
            }
        }

        resourceValue = null;
        return false;
    }

    internal static Color ParseThemeColor(string normalizedColor)
    {
        if (normalizedColor.Length == 9 && normalizedColor[0] == '#')
        {
            return Color.FromArgb(
                byte.Parse(normalizedColor.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(normalizedColor.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(normalizedColor.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(normalizedColor.AsSpan(7, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        }

        if (normalizedColor.Length == 7 && normalizedColor[0] == '#')
        {
            return Color.FromRgb(
                byte.Parse(normalizedColor.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(normalizedColor.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(normalizedColor.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        }

        throw new FormatException("Theme colors must use #RRGGBB or #AARRGGBB format.");
    }

    private enum AppearanceColorTarget
    {
        Background,
        Accent,
        Surface
    }

    private sealed record AppearanceTargetOption(AppearanceColorTarget Target, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed class ColorDialogOwner(nint handle) : Forms.IWin32Window
    {
        public nint Handle { get; } = handle;
    }

    private void ReplayBufferService_CaptureRecoveryRequested(
        object? sender,
        CaptureRecoveryRequestedEventArgs eventArgs)
    {
        if (_isClosing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() =>
                ReplayBufferService_CaptureRecoveryRequested(sender, eventArgs));
            return;
        }

        if (_captureRecoveryQueued)
        {
            return;
        }

        _captureRecoveryQueued = true;
        _ = RecoverCaptureAsync(eventArgs);
    }

    private async Task RecoverCaptureAsync(CaptureRecoveryRequestedEventArgs eventArgs)
    {
        try
        {
            while (!_isClosing &&
                   _replayBufferService.IsRunning &&
                   _latestState.State == ReplayState.Saving)
            {
                await Task.Delay(
                    TimeSpan.FromMilliseconds(250),
                    _lifetimeCancellation.Token);
            }

            if (_isClosing ||
                !_replayStartRequested ||
                !_replayBufferService.IsRunning ||
                eventArgs.ProcessId != _replayBufferService.CaptureProcessId)
            {
                return;
            }

            var strategy = _replayBufferService.LastCapturePlan?.Strategy;
            if (strategy?.CaptureBackend != DesktopCaptureBackend.WindowsGraphicsCapture)
            {
                return;
            }

            await RunCaptureCommandAsync(async () =>
            {
                if (_isClosing ||
                    !_replayStartRequested ||
                    !_replayBufferService.IsRunning ||
                    eventArgs.ProcessId != _replayBufferService.CaptureProcessId)
                {
                    return;
                }

                if (_automaticCaptureRecoveryCount >= 2)
                {
                    ShowError(
                        "Capture pacing is still unstable after automatic recovery. " +
                        "ClipForge kept replay running and will not restart it repeatedly.");
                    return;
                }

                var useSourceSafetyMode = _automaticCaptureRecoveryCount == 1;
                _automaticCaptureRecoveryCount++;

                _captureRestartInProgress = true;
                SetCaptureCriticalPresentationState(isActive: true);
                try
                {
                    await _replayBufferService.StopAsync();
                    if (!_isClosing && _replayStartRequested)
                    {
                        // The first recovery deliberately reacquires the exact
                        // verified path. The second changes geometry to Source,
                        // so it must probe that native geometry instead of
                        // reusing a strategy proven only for the fixed preset.
                        await StartReplayCoreAsync(
                            useSourceSafetyMode,
                            useSourceSafetyMode ? null : strategy);
                        _automaticCaptureSourceFallback = useSourceSafetyMode;
                    }
                }
                finally
                {
                    _captureRestartInProgress = false;
                    SetCaptureCriticalPresentationState(
                        IsCapturePresentationSuspendedState(_latestState));
                }
            });
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // Application shutdown supersedes a queued capture recovery.
        }
        finally
        {
            _captureRecoveryQueued = false;
        }
    }

    private void ResetAutomaticCaptureRecovery()
    {
        _automaticCaptureRecoveryCount = 0;
        _automaticCaptureSourceFallback = false;
    }

    private void ReplayBufferService_StateChanged(object? sender, ReplayStateSnapshot snapshot)
    {
        if (_isClosing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => ReplayBufferService_StateChanged(sender, snapshot));
            return;
        }

        var wasReplaySession = IsReplaySessionState(_latestState);
        var isReplaySession = IsReplaySessionState(snapshot);
        _latestState = snapshot;
        if (isReplaySession && !wasReplaySession)
        {
            // Desktop loopback would otherwise record ClipForge's own preview
            // audio into the rolling buffer. A deliberate volume/unmute action
            // opts back in for the remainder of this replay session.
            _replayPlaybackAudioOptIn = false;
            ApplyPlayerVolume(0);
        }

        var suspendPresentation =
            _captureRestartInProgress || IsCapturePresentationSuspendedState(snapshot);
        if (suspendPresentation)
        {
            // Suspend first so entering Starting/Saving/Stopping cannot launch
            // even a short-lived Library helper before the cancellation arrives.
            SetCaptureCriticalPresentationState(isActive: true);
        }

        _libraryWindow?.UpdateReplayRunningState(isReplaySession);
        if (!suspendPresentation)
        {
            // On the way back to Ready/Stopped, update the replay policy first;
            // the resumed refresh then immediately selects the correct cached
            // or full thumbnail mode instead of starting twice.
            SetCaptureCriticalPresentationState(isActive: false);
        }
        if (IsVisible && IsActive)
        {
            UpdateControlsForState(snapshot);
        }
        else
        {
            UpdateBackgroundIndicators(snapshot);
        }

        if (!string.IsNullOrWhiteSpace(snapshot.LastSavedPath))
        {
            ShowLastSaved(snapshot.LastSavedPath);
        }

        if (snapshot.State == ReplayState.Faulted && !string.IsNullOrWhiteSpace(snapshot.Message))
        {
            ShowError(snapshot.Message);
        }
    }

    internal static bool IsReplaySessionState(ReplayStateSnapshot snapshot) =>
        snapshot.State is ReplayState.Starting or ReplayState.Buffering or ReplayState.Ready or
            ReplayState.Saving or ReplayState.Stopping;

    internal static bool IsCapturePresentationSuspendedState(ReplayStateSnapshot snapshot) =>
        snapshot.State is ReplayState.Starting or ReplayState.Saving or ReplayState.Stopping;

    private void SetCaptureCriticalPresentationState(bool isActive)
    {
        if (_captureCriticalPresentationActive == isActive)
        {
            return;
        }

        _captureCriticalPresentationActive = isActive;
        _libraryWindow?.UpdatePresentationSuspended(isActive);

        if (isActive)
        {
            CancelActiveLibraryRefreshForBackground();
            ReleasePlayerForBackground();
            SetPlayerControlsEnabled(false);
            PlayerSurfacePlayButton.Visibility = Visibility.Collapsed;
            TrimCurrentClipButton.IsEnabled = false;
            return;
        }

        if (_isClosing || !IsVisible || !IsActive)
        {
            return;
        }

        if (_libraryRefreshPending || _pendingLibraryPreferredPath is not null)
        {
            _ = RefreshClipLibraryAsync(_pendingLibraryPreferredPath);
        }
        else if (_playerSourceReleasedForBackground && _currentClip is { } releasedClip)
        {
            SelectClip(releasedClip, autoplay: false);
        }
    }

    private void UpdateControlsForState(ReplayStateSnapshot snapshot)
    {
        if (_isInitializing || _isClosing)
        {
            return;
        }

        var isBusy = snapshot.State is ReplayState.Starting or ReplayState.Stopping;
        var isSaving = snapshot.State == ReplayState.Saving;
        var isRunning = _replayBufferService.IsRunning;
        var canSave = CanSaveClip(snapshot, isRunning);

        StatusText.Text = GetReplayStatusText(snapshot.State);
        StatusDot.Fill = snapshot.State switch
        {
            ReplayState.Starting or ReplayState.Saving => Brush("AccentBrush"),
            ReplayState.Buffering => Brush("WarningBrush"),
            ReplayState.Ready => Brush("SuccessBrush"),
            ReplayState.Faulted => Brush("ErrorBrush"),
            _ => Brush("TextMutedBrush")
        };

        BufferToggleButton.Content = isRunning ? "Stop replay" : "Start replay";
        BufferToggleButton.IsEnabled = _engineReady && !isBusy && !isSaving && !_isClosing;
        BufferToggleButton.SetValue(
            System.Windows.Automation.AutomationProperties.NameProperty,
            isRunning ? "Stop instant replay" : "Start instant replay");

        if (isRunning)
        {
            AvailableText.Text = snapshot.AvailableDuration > TimeSpan.Zero
                ? $"{FormatDuration(snapshot.AvailableDuration)} available of {FormatDuration(snapshot.Retention)}"
                : "Building replay buffer…";
        }
        else
        {
            AvailableText.Text = "No replay buffered";
        }

        SaveClipButton.IsEnabled = canSave;
        if (_replayBufferService.ActiveEncoderDescription is { Length: > 0 } encoderDescription)
        {
            var displayedEncoderDescription = _automaticCaptureSourceFallback &&
                                              !encoderDescription.Contains(
                                                  "Source safety mode",
                                                  StringComparison.OrdinalIgnoreCase)
                ? $"{encoderDescription} (Source safety mode)"
                : encoderDescription;
            EncoderStatusText.Text = $"Performance mode: {displayedEncoderDescription}";
            EncoderStatusText.Foreground = Brush(
                encoderDescription.Contains("software", StringComparison.OrdinalIgnoreCase)
                    ? "WarningBrush"
                    : "SuccessBrush");
        }

        UpdateTrayStatus(StatusText.Text, canSave);
        if (_overlayWindow is { IsVisible: true } overlay)
        {
            overlay.UpdateState(snapshot, isRunning, _settings.SaveClipHotkey.DisplayText);
        }
    }

    private void UpdateBackgroundIndicators(ReplayStateSnapshot snapshot)
    {
        var isRunning = _replayBufferService.IsRunning;
        UpdateTrayStatus(GetReplayStatusText(snapshot.State), CanSaveClip(snapshot, isRunning));
        if (_overlayWindow is { IsVisible: true } overlay)
        {
            overlay.UpdateState(snapshot, isRunning, _settings.SaveClipHotkey.DisplayText);
        }
    }

    private void UpdateTrayStatus(string status, bool canSave)
    {
        if (string.Equals(_lastTrayStatus, status, StringComparison.Ordinal) &&
            _lastTrayCanSave == canSave)
        {
            return;
        }

        _lastTrayStatus = status;
        _lastTrayCanSave = canSave;
        _trayIconService.UpdateStatus(status, canSave);
    }

    private bool CanSaveClip(ReplayStateSnapshot snapshot, bool isRunning) =>
        isRunning &&
        snapshot.State is not ReplayState.Starting and not ReplayState.Stopping and not ReplayState.Saving &&
        snapshot.AvailableDuration >= TimeSpan.FromSeconds(1) &&
        !_isClosing;

    private static string GetReplayStatusText(ReplayState state) => state switch
    {
        ReplayState.Starting => "Starting replay…",
        ReplayState.Buffering => "Building buffer",
        ReplayState.Ready => "Replay ready",
        ReplayState.Saving => "Saving clip…",
        ReplayState.Faulted => "Replay error",
        ReplayState.Stopping => "Stopping replay…",
        _ => "Replay off"
    };

    private Brush Brush(string resourceKey) => (Brush)FindResource(resourceKey);

    private void UpdatePrimaryActionText()
    {
        SaveClipButton.Content = ReplayLengthComboBox.SelectedItem is ReplayLengthOption option
            ? $"Save last {option.Label}"
            : "Save last clip";
    }

    private void RefreshEngineState()
    {
        _engineReady = _ffmpegSetupService.FindExecutable() is not null;
        InstallEnginePanel.Visibility = _engineReady ? Visibility.Collapsed : Visibility.Visible;
        BufferToggleButton.IsEnabled = _engineReady && !_isClosing;
    }

    private void UpdateStorageText()
    {
        if (_isInitializing ||
            DisplayComboBox.SelectedItem is not DisplayOption display ||
            ResolutionComboBox.SelectedItem is not ResolutionOption resolution ||
            ReplayLengthComboBox.SelectedItem is not ReplayLengthOption replayLength ||
            FpsComboBox.SelectedItem is not int framesPerSecond)
        {
            return;
        }

        try
        {
            var estimate = StorageEstimator.EstimateBufferBytes(
                display,
                resolution,
                framesPerSecond,
                replayLength.Duration,
                SystemAudioCheckBox.IsChecked == true || MicrophoneCheckBox.IsChecked == true);

            var savePath = string.IsNullOrWhiteSpace(SavePathTextBox.Text)
                ? AppSettings.GetDefaultSaveDirectory()
                : SavePathTextBox.Text;
            var root = Path.GetPathRoot(Path.GetFullPath(savePath));
            var freeSpace = !string.IsNullOrWhiteSpace(root)
                ? new DriveInfo(root).AvailableFreeSpace
                : (long?)null;
            var freeText = !string.IsNullOrWhiteSpace(root)
                ? $" · {StorageEstimator.FormatBytes(freeSpace!.Value)} free"
                : string.Empty;

            StorageText.Text = $"~{StorageEstimator.FormatBytes(estimate)} replay buffer{freeText}";
            StorageText.Foreground = freeSpace is { } availableFreeSpace &&
                                     availableFreeSpace < estimate * 2
                ? Brush("WarningBrush")
                : Brush("TextMutedBrush");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            StorageText.Text = "Storage information unavailable";
            StorageText.Foreground = Brush("TextMutedBrush");
        }
    }

    private void EnsureSaveDirectory()
    {
        SyncSettingsFromControls();
        Directory.CreateDirectory(_settings.SaveDirectory);
        SavePathTextBox.Text = _settings.SaveDirectory;
    }

    private async void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose where ClipForge should save videos",
            InitialDirectory = Directory.Exists(SavePathTextBox.Text)
                ? SavePathTextBox.Text
                : AppSettings.GetDefaultSaveDirectory()
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        SavePathTextBox.Text = dialog.FolderName;
        SyncSettingsFromControls();
        EnsureSaveDirectory();
        UpdateStorageText();
        await PersistSettingsAsync();
        await RefreshClipLibraryAsync();
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            EnsureSaveDirectory();
            OpenPath(_settings.SaveDirectory);
        }
        catch (Exception exception)
        {
            ShowError($"The clips folder could not be opened. {exception.Message}");
        }
    }

    private void OpenLastClipButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastSavedPath) || !File.Exists(_lastSavedPath))
        {
            ShowError("The latest clip is no longer available at its saved location.");
            return;
        }

        try
        {
            OpenPath(_lastSavedPath);
        }
        catch (Exception exception)
        {
            ShowError($"The clip could not be opened. {exception.Message}");
        }
    }

    private static void OpenPath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || (!File.Exists(path) && !Directory.Exists(path)))
        {
            throw new FileNotFoundException("The requested local file or folder does not exist.", path);
        }

        _ = Process.Start(new ProcessStartInfo
        {
            FileName = Path.GetFullPath(path),
            UseShellExecute = true
        });
    }

    private static void RevealClipInExplorer(string validatedClipPath)
    {
        if (!Path.IsPathFullyQualified(validatedClipPath) || !File.Exists(validatedClipPath))
        {
            throw new FileNotFoundException("The requested local clip does not exist.", validatedClipPath);
        }

        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var explorerPath = Path.Combine(windowsDirectory, "explorer.exe");
        var explorer = new FileInfo(explorerPath);
        if (!explorer.Exists ||
            (explorer.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new FileNotFoundException("The trusted Windows File Explorer executable is unavailable.", explorerPath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = explorer.FullName,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add($"/select,{Path.GetFullPath(validatedClipPath)}");
        _ = Process.Start(startInfo);
    }

    private void ShowLastSaved(string path)
    {
        var changed = !string.Equals(_lastSavedPath, path, StringComparison.OrdinalIgnoreCase);
        _lastSavedPath = path;
        LastSavedText.Text = $"{Path.GetFileName(path)} · saved";
        if (changed)
        {
            if (IsVisible && IsActive)
            {
                UiMotionService.ShowSavedToast(LastSavedPanel);
            }
            else
            {
                LastSavedPanel.Visibility = Visibility.Collapsed;
            }

            if (IsVisible && IsActive && !_captureCriticalPresentationActive)
            {
                _ = RefreshClipLibraryAsync(path);
            }
            else
            {
                // Do not launch probe/thumbnail/player work over a foreground game.
                // The newest saved clip is loaded when ClipForge becomes active again.
                _pendingLibraryPreferredPath = path;
            }
        }
    }

    private void RunStartupMotion()
    {
        UiMotionService.RevealStartup(AppHeader, sequenceIndex: 0);
        UiMotionService.RevealStartup(CaptureSidebar, sequenceIndex: 1);
        UiMotionService.RevealStartup(ReplayHeroCard, sequenceIndex: 2);
        UiMotionService.RevealStartup(PlayerCard, sequenceIndex: 3);
        UiMotionService.RevealStartup(RecentClipsCard, sequenceIndex: 4);
    }

    private void CancelSavedToast()
    {
        LastSavedPanel.Visibility = Visibility.Collapsed;
        LastSavedPanel.Opacity = 1;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorBanner.Visibility = Visibility.Visible;
    }

    private void HideError() => ErrorBanner.Visibility = Visibility.Collapsed;

    private void InitializeUpdateControls()
    {
        VersionText.Text = $"v{ReleaseInfo.Version}";
        var versionCore = ReleaseInfo.Version.Split('-', 2)[0].Split('+', 2)[0];
        var versionParts = versionCore.Split('.');
        SidebarVersionText.Text = versionParts.Length >= 2
            ? $"V{versionParts[0]}.{versionParts[1]}"
            : $"V{versionCore}";
        UpdateControlsForState(_appUpdateService.Snapshot);
    }

    private async Task RunAutomaticUpdateCheckAsync()
    {
        if (!_settings.CheckForUpdatesAutomatically ||
            !_appUpdateService.CanCheck ||
            _isClosing)
        {
            return;
        }

        try
        {
            await _appUpdateService.CheckAsync(_lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // Application shutdown.
        }
    }

    private async void UpdateActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isClosing)
        {
            return;
        }

        try
        {
            switch (_appUpdateService.Snapshot.State)
            {
                case AppUpdateState.Available:
                    await _appUpdateService.DownloadAsync(_lifetimeCancellation.Token);
                    break;

                case AppUpdateState.ReadyToRestart:
                    _appUpdateService.ScheduleApplyAndRestart();
                    _exitRequested = true;
                    Close();
                    break;

                default:
                    await _appUpdateService.CheckAsync(_lifetimeCancellation.Token);
                    break;
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // Application shutdown.
        }
        catch (Exception exception)
        {
            UpdateStatusText.Text = $"Update action failed: {exception.Message}";
        }
    }

    private async void AutoUpdateCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || _isClosing)
        {
            return;
        }

        SyncSettingsFromControls();
        await PersistSettingsAsync();
        if (_settings.CheckForUpdatesAutomatically)
        {
            await RunAutomaticUpdateCheckAsync();
        }
    }

    private async void ClipSavedSoundCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || _isClosing)
        {
            return;
        }

        SyncSettingsFromControls();
        await PersistSettingsAsync();
    }

    private async void StartReplayWithWindowsCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || _isClosing)
        {
            return;
        }

        var previous = _settings.StartReplayWithWindows;
        var requested = StartReplayWithWindowsCheckBox.IsChecked == true;

        try
        {
            _startupRegistrationService.SetEnabled(requested);
            SyncSettingsFromControls();
            _settings.StartReplayWithWindows = requested;
            await _settingsService.SaveAsync(_settings, _lifetimeCancellation.Token);
            StartupHintText.Text = requested
                ? "Enabled. ClipForge will start hidden and begin replay after you sign in."
                : "ClipForge starts hidden in the tray and uses your saved capture settings.";
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            RestoreStartupPreference(previous);
        }
        catch (Exception exception)
        {
            RestoreStartupPreference(previous);
            ShowError($"Windows startup could not be changed. {exception.Message}");
        }
    }

    private void RestoreStartupPreference(bool enabled)
    {
        _settings.StartReplayWithWindows = enabled;
        StartReplayWithWindowsCheckBox.IsChecked = enabled;

        try
        {
            _startupRegistrationService.SetEnabled(enabled);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException or
            ArgumentException or NotSupportedException or Win32Exception or
            System.Runtime.InteropServices.COMException)
        {
            // Preserve the previous in-app preference and surface the original failure.
        }
    }

    private void AppUpdateService_StateChanged(object? sender, AppUpdateSnapshot snapshot)
    {
        if (_isClosing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => AppUpdateService_StateChanged(sender, snapshot));
            return;
        }

        UpdateControlsForState(snapshot);
    }

    private void UpdateControlsForState(AppUpdateSnapshot snapshot)
    {
        VersionText.Text = $"v{snapshot.CurrentVersion}";
        UpdateStatusText.Text = snapshot.Message ?? "Update status unavailable.";
        UpdateProgressBar.Visibility = snapshot.State == AppUpdateState.Downloading
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateProgressBar.Value = snapshot.ProgressPercent;

        (UpdateActionButton.Content, UpdateActionButton.IsEnabled) = snapshot.State switch
        {
            AppUpdateState.Disabled => ("Feed not configured", false),
            AppUpdateState.NotInstalled => ("Use ClipForge Setup", false),
            AppUpdateState.Checking => ("Checking…", false),
            AppUpdateState.Available => ($"Download v{snapshot.TargetVersion}", true),
            AppUpdateState.Downloading => ($"Downloading {snapshot.ProgressPercent}%", false),
            AppUpdateState.ReadyToRestart => ("Restart to update", true),
            AppUpdateState.UpToDate => ("Check again", true),
            AppUpdateState.Failed => ("Try again", _appUpdateService.CanCheck),
            _ => ("Check for updates", _appUpdateService.CanCheck)
        };
    }

    private void HideToTrayButton_Click(object sender, RoutedEventArgs e) => HideToTray();

    private void DismissErrorButton_Click(object sender, RoutedEventArgs e) => HideError();

    private void TrayIconService_ShowRequested(object? sender, EventArgs e) =>
        DispatchToUi(ShowMainWindow);

    private void TrayIconService_SaveClipRequested(object? sender, EventArgs e) =>
        DispatchToUi(() => _ = SaveClipFromShortcutAsync());

    private void TrayIconService_ExitRequested(object? sender, EventArgs e)
        => DispatchToUi(RequestExit);

    private void RequestExit()
    {
        _exitRequested = true;
        Close();
    }

    private void Application_SessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        _exitRequested = true;
        Close();
    }

    private void HotkeyService_SaveClipPressed(object? sender, EventArgs e) =>
        _ = SaveClipFromShortcutAsync();

    private void HotkeyService_ToggleOverlayPressed(object? sender, EventArgs e) => ToggleOverlay();

    private void HotkeyService_RegistrationFailed(
        object? sender,
        GlobalHotkeyRegistrationFailedEventArgs e) =>
        ShowError(e.Error.Message);

    private async Task SaveClipFromShortcutAsync()
    {
        if (_isClosing)
        {
            return;
        }

        if (_replayBufferService.IsRunning)
        {
            await SaveClipAsync();
        }
        else
        {
            ShowError($"Instant Replay is off. Start it before using {_settings.SaveClipHotkey.DisplayText}.");
        }
    }

    private void SaveHotkeyButton_Click(object sender, RoutedEventArgs e) =>
        BeginHotkeyCapture(GlobalHotkeyAction.SaveClip, SaveHotkeyButton);

    private void OverlayHotkeyButton_Click(object sender, RoutedEventArgs e) =>
        BeginHotkeyCapture(GlobalHotkeyAction.ToggleOverlay, OverlayHotkeyButton);

    private void BeginHotkeyCapture(GlobalHotkeyAction action, Button sourceButton)
    {
        if (_isClosing)
        {
            return;
        }

        _capturingHotkeyAction = action;
        HotkeyCaptureHint.Text = action == GlobalHotkeyAction.SaveClip
            ? "Press the new Save Clip combination now. Esc cancels."
            : "Press the new Toggle Overlay combination now. Esc cancels.";
        HotkeyCaptureHint.Foreground = Brush("AccentHoverBrush");
        HotkeyCaptureHint.Visibility = Visibility.Visible;
        sourceButton.Focus();
    }

    private async void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_capturingHotkeyAction is not { } action)
        {
            return;
        }

        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        {
            EndHotkeyCapture("Shortcut change cancelled.", isError: false);
            return;
        }

        var gesture = new HotkeyGesture(GetHotkeyModifiers(Keyboard.Modifiers), key);
        if (!gesture.TryValidate(out var validationError))
        {
            HotkeyCaptureHint.Text = validationError ?? "Press at least one modifier and a non-modifier key.";
            HotkeyCaptureHint.Foreground = Brush("WarningBrush");
            return;
        }

        var saveGesture = action == GlobalHotkeyAction.SaveClip
            ? gesture
            : _settings.SaveClipHotkey;
        var overlayGesture = action == GlobalHotkeyAction.ToggleOverlay
            ? gesture
            : _settings.ToggleOverlayHotkey;

        try
        {
            if (_hotkeyService.IsSaveClipRegistered || _hotkeyService.IsToggleOverlayRegistered)
            {
                _hotkeyService.ReRegister(saveGesture, overlayGesture);
            }
            else
            {
                _hotkeyService.Register(this, saveGesture, overlayGesture);
            }

            _settings.SaveClipHotkey = saveGesture;
            _settings.ToggleOverlayHotkey = overlayGesture;
            HotkeyText.Text = saveGesture.DisplayText;
            OverlayHotkeyText.Text = overlayGesture.DisplayText;
            if (_overlayWindow is { IsVisible: true } overlay)
            {
                overlay.UpdateState(_latestState, _replayBufferService.IsRunning, saveGesture.DisplayText);
            }
            EndHotkeyCapture($"Shortcut set to {gesture.DisplayText}.", isError: false);
            await PersistSettingsAsync();
        }
        catch (Exception exception) when (
            exception is GlobalHotkeyRegistrationException or ArgumentException or InvalidOperationException)
        {
            EndHotkeyCapture(exception.Message, isError: true);
            ShowError(exception.Message);
        }
    }

    private void EndHotkeyCapture(string message, bool isError)
    {
        _capturingHotkeyAction = null;
        HotkeyCaptureHint.Text = message;
        HotkeyCaptureHint.Foreground = Brush(isError ? "ErrorBrush" : "TextMutedBrush");
        Keyboard.ClearFocus();
    }

    private static HotkeyModifiers GetHotkeyModifiers(ModifierKeys modifiers)
    {
        var result = HotkeyModifiers.None;
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            result |= HotkeyModifiers.Control;
        }

        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            result |= HotkeyModifiers.Alt;
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            result |= HotkeyModifiers.Shift;
        }

        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            result |= HotkeyModifiers.Windows;
        }

        return result;
    }

    private void ToggleOverlay()
    {
        if (_isClosing)
        {
            return;
        }

        var overlay = EnsureOverlayWindow();
        if (overlay.IsVisible)
        {
            overlay.Hide();
            return;
        }

        overlay.UpdateState(_latestState, _replayBufferService.IsRunning, _settings.SaveClipHotkey.DisplayText);
        overlay.Show();
    }

    private OverlayWindow EnsureOverlayWindow()
    {
        if (_overlayWindow is not null)
        {
            return _overlayWindow;
        }

        _overlayWindow = new OverlayWindow();
        _overlayWindow.SaveRequested += (_, _) => _ = SaveClipFromShortcutAsync();
        _overlayWindow.ShowAppRequested += (_, _) => ShowMainWindow();
        _overlayWindow.Closing += (_, args) =>
        {
            if (!_exitRequested)
            {
                args.Cancel = true;
                _overlayWindow.Hide();
            }
        };
        return _overlayWindow;
    }

    private void HideToTray()
    {
        if (_isClosing)
        {
            return;
        }

        ReleasePlayerForBackground();
        CancelActiveLibraryRefreshForBackground();
        CancelSavedToast();

        Hide();
        if (!_backgroundHintShown)
        {
            _backgroundHintShown = true;
            _trayIconService.ShowBackgroundHint();
        }
    }

    internal void ShowForLaunch()
    {
        if (!_launchOptions.StartInBackground)
        {
            Show();
            return;
        }

        // Build the HWND once so tray, hotkeys, and device initialization can
        // complete without painting or activating a login-time window.
        ShowActivated = false;
        ShowInTaskbar = false;
        Opacity = 0;
        Show();
        Hide();
        Opacity = 1;
        ShowInTaskbar = true;
        ShowActivated = true;
    }

    private void ShowMainWindow()
    {
        if (_isClosing)
        {
            return;
        }

        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    internal void ShowFromSecondaryInstance()
    {
        if (_isClosing)
        {
            return;
        }

        _overlayWindow?.Hide();
        ShowMainWindow();
    }

    private async void RecentClipCountComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_isInitializing || _isClosing || RecentClipCountComboBox.SelectedItem is not int requestedCount)
        {
            return;
        }

        var normalizedCount = AppSettings.NormalizeRecentClipCount(requestedCount);
        if (_settings.RecentClipCount == normalizedCount)
        {
            UpdateRecentGalleryCardWidth();
            return;
        }

        _settings.RecentClipCount = normalizedCount;
        UpdateRecentGalleryCardWidth();
        await PersistSettingsAsync();
        await RefreshClipLibraryAsync();
    }

    private void RecentClipsScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateRecentGalleryCardWidth();

    private void UpdateRecentGalleryCardWidth()
    {
        if (RecentClipsScrollViewer is null || RecentClipsItemsControl is null)
        {
            return;
        }

        var availableWidth = RecentClipsScrollViewer.ViewportWidth;
        if (!double.IsFinite(availableWidth) || availableWidth <= 0)
        {
            availableWidth = RecentClipsScrollViewer.ActualWidth;
        }

        if (!double.IsFinite(availableWidth) || availableWidth <= 0)
        {
            return;
        }

        var requestedCount = AppSettings.NormalizeRecentClipCount(_settings.RecentClipCount);
        var width = CalculateRecentGalleryCardWidth(
            availableWidth,
            requestedCount,
            RecentClipsItemsControl.Items.Count);
        if (RecentClipsItemsControl.Tag is not double currentWidth ||
            Math.Abs(currentWidth - width) >= 0.5)
        {
            RecentClipsItemsControl.Tag = width;
        }
    }

    internal static double CalculateRecentGalleryCardWidth(
        double availableWidth,
        int requestedCount,
        int actualItemCount)
    {
        if (!double.IsFinite(availableWidth) || availableWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(availableWidth));
        }

        const double itemHorizontalMargin = 6;
        const double minimumCardWidth = 168;
        var normalizedCount = AppSettings.NormalizeRecentClipCount(requestedCount);
        var desiredVisibleSlots = normalizedCount switch
        {
            8 => 5,
            10 => 6,
            15 => 7,
            _ => 4
        };
        var itemSlots = actualItemCount > 0
            ? Math.Min(actualItemCount, desiredVisibleSlots)
            : desiredVisibleSlots;
        var visibleSlots = Math.Max(1, itemSlots);
        var fittedWidth = (availableWidth - (itemHorizontalMargin * visibleSlots)) / visibleSlots;
        return Math.Max(minimumCardWidth, fittedWidth);
    }

    private void DispatchToUi(Action action)
    {
        if (_isClosing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (Dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _ = Dispatcher.BeginInvoke(action);
        }
    }

    private async void RefreshLibraryButton_Click(object sender, RoutedEventArgs e) =>
        await RefreshClipLibraryAsync();

    private void OpenLibraryButton_Click(object sender, RoutedEventArgs e) =>
        OpenLibraryWindow(_currentClip, beginTrim: false);

    private void TrimCurrentClipButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentClip is null)
        {
            ShowError("Select a saved clip before opening the trim editor.");
            return;
        }

        OpenLibraryWindow(_currentClip, beginTrim: true);
    }

    private void OpenLibraryWindow(ClipLibraryItem? preferredClip, bool beginTrim)
    {
        if (_isClosing)
        {
            return;
        }

        if (_libraryWindow is { } openLibrary)
        {
            // Moving focus back to Library must never leave both WPF media
            // graphs alive. Apply replay policy before Show/Activate so the
            // window cannot briefly reopen with stale capture assumptions.
            ReleasePlayerForBackground();
            _libraryRefreshPending = true;
            openLibrary.UpdateReplayRunningState(
                IsReplaySessionState(_latestState) || _replayBufferService.IsRunning);
            openLibrary.UpdatePresentationSuspended(_captureCriticalPresentationActive);

            if (!openLibrary.IsVisible)
            {
                openLibrary.Show();
            }

            if (openLibrary.WindowState == WindowState.Minimized)
            {
                openLibrary.WindowState = WindowState.Normal;
            }

            _ = openLibrary.Activate();
            if (beginTrim && preferredClip is not null)
            {
                openLibrary.SelectClipAndBeginTrim(preferredClip);
            }

            return;
        }

        // Keep exactly one in-process media decoder active. The main player is
        // restored paused through the normal gallery refresh after Library closes.
        ReleasePlayerForBackground();
        _libraryRefreshPending = true;
        var libraryWindow = new LibraryWindow(
            _clipLibraryService,
            _clipTrimService,
            _settings.SaveDirectory,
            IsReplaySessionState(_latestState) || _replayBufferService.IsRunning,
            preferredClip,
            beginTrim,
            presentationSuspended: _captureCriticalPresentationActive)
        {
            Owner = this
        };
        _libraryWindow = libraryWindow;
        libraryWindow.Closed += LibraryWindow_Closed;
        libraryWindow.Show();
    }

    private void LibraryWindow_Closed(object? sender, EventArgs e)
    {
        if (sender is not LibraryWindow libraryWindow)
        {
            return;
        }

        libraryWindow.Closed -= LibraryWindow_Closed;
        if (ReferenceEquals(_libraryWindow, libraryWindow))
        {
            _libraryWindow = null;
        }

        // Refresh even when no in-app delete occurred so recordings saved while
        // Library was open appear immediately in the compact gallery.
        _libraryRefreshPending = true;
        if (!_isClosing && IsVisible)
        {
            _ = Activate();
        }
    }

    private void OpenCurrentClipButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentClip is null || !File.Exists(_currentClip.FullPath))
        {
            ShowError("The selected clip is no longer available.");
            return;
        }

        OpenPath(_currentClip.FullPath);
    }

    private void RecentClipButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ClipLibraryItem clip })
        {
            SelectClip(clip, autoplay: true);
        }
    }

    private void ShowRecentClipInExplorerMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: ClipLibraryItem clip })
        {
            return;
        }

        try
        {
            if (!ClipLibraryService.TryGetCurrentClipPath(
                    _settings.SaveDirectory,
                    clip,
                    out var validatedPath))
            {
                ShowError("That clip changed or is no longer a safe local ClipForge recording. Refresh the gallery and try again.");
                _ = RefreshClipLibraryAsync();
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

    private async void DeleteRecentClipMenuItem_Click(object sender, RoutedEventArgs e)
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

        var result = ClipLibraryService.DeleteCurrentClip(_settings.SaveDirectory, clip);
        if (result == ClipDeletionResult.Deleted)
        {
            // Release gallery image bindings before removing the cached JPEG so
            // a decoded thumbnail cannot keep the privacy-sensitive file open.
            RecentClipsItemsControl.ItemsSource = null;
            _clipLibraryService.RemoveCachedThumbnail(clip);

            if (string.Equals(_lastSavedPath, clip.FullPath, StringComparison.OrdinalIgnoreCase))
            {
                _lastSavedPath = null;
                CancelSavedToast();
            }

            await RefreshClipLibraryAsync();
            return;
        }

        await RefreshClipLibraryAsync();
        ShowError(result == ClipDeletionResult.ChangedOrUnsafe
            ? "The clip changed or is no longer a safe local ClipForge recording, so it was not deleted."
            : "The clip could not be deleted. Close any app using it, then try again.");
    }

    private async Task RefreshClipLibraryAsync(string? preferredPath = null)
    {
        if (_isClosing)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(preferredPath))
        {
            // Keep the newest requested clip until a visible refresh completes.
            // A deactivation cancellation must not silently fall back to the
            // previously selected player item on the next activation.
            _pendingLibraryPreferredPath = preferredPath;
        }

        if (_captureCriticalPresentationActive)
        {
            _libraryRefreshPending = true;
            return;
        }

        if (!IsVisible || !IsActive)
        {
            _libraryRefreshPending = true;
            return;
        }

        var effectivePreferredPath = _pendingLibraryPreferredPath;

        var requestedCount = AppSettings.NormalizeRecentClipCount(_settings.RecentClipCount);
        var refreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        lock (_libraryRefreshCancellationGate)
        {
            _activeLibraryRefreshCancellation?.Cancel();
            _activeLibraryRefreshCancellation = refreshCancellation;
        }

        var gateEntered = false;
        try
        {
            await _libraryRefreshGate.WaitAsync(refreshCancellation.Token);
            gateEntered = true;
            var snapshot = await _clipLibraryService.LoadAsync(
                _settings.SaveDirectory,
                count: requestedCount,
                includeThumbnails: true,
                thumbnailPolicy: IsReplaySessionState(_latestState)
                    ? ClipThumbnailPolicy.CachedOnly
                    : ClipThumbnailPolicy.GenerateMissing,
                refreshCancellation.Token);

            refreshCancellation.Token.ThrowIfCancellationRequested();
            if (_captureCriticalPresentationActive || !IsVisible || !IsActive)
            {
                _libraryRefreshPending = true;
                return;
            }

            RecentClipsItemsControl.ItemsSource = snapshot.Clips;
            UpdateRecentGalleryCardWidth();
            if (IsVisible && IsActive)
            {
                UiMotionService.CrossFadeRefresh(RecentClipsItemsControl);
            }

            var selected = effectivePreferredPath is { Length: > 0 }
                ? snapshot.Clips.FirstOrDefault(clip =>
                    clip.FullPath.Equals(effectivePreferredPath, StringComparison.OrdinalIgnoreCase))
                : _currentClip is not null
                    ? snapshot.Clips.FirstOrDefault(clip =>
                        clip.FullPath.Equals(_currentClip.FullPath, StringComparison.OrdinalIgnoreCase))
                    : null;
            selected ??= snapshot.LatestClip;

            if (selected is null)
            {
                ClearPlayer();
            }
            else if (_currentClip is null ||
                     !_currentClip.FullPath.Equals(selected.FullPath, StringComparison.OrdinalIgnoreCase) ||
                     effectivePreferredPath is not null ||
                     _playerSourceReleasedForBackground)
            {
                // Automatic save refreshes select the new recording without decoding
                // and playing it over the still-running capture session.
                SelectClip(selected, autoplay: false);
            }

            _libraryRefreshPending = false;
            if (effectivePreferredPath is not null &&
                string.Equals(
                    _pendingLibraryPreferredPath,
                    effectivePreferredPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                _pendingLibraryPreferredPath = null;
            }

            if (IsReplaySessionState(_latestState) &&
                snapshot.Clips.Any(clip => clip.ThumbnailPath is null))
            {
                // Paint the cached snapshot first, then fill only the bounded
                // recent strip while ClipForge remains foreground. Extraction
                // is serialized, single-threaded and Idle priority, and this
                // refresh token is cancelled as soon as the game regains focus
                // or capture enters a critical transition.
                var hydratedClips = await _clipLibraryService.HydrateThumbnailsAsync(
                    _settings.SaveDirectory,
                    snapshot.Clips,
                    maximumMissingThumbnails: requestedCount,
                    refreshCancellation.Token);

                refreshCancellation.Token.ThrowIfCancellationRequested();
                if (_captureCriticalPresentationActive || !IsVisible || !IsActive)
                {
                    _libraryRefreshPending = true;
                    return;
                }

                RecentClipsItemsControl.ItemsSource = hydratedClips;
                if (_currentClip is { } currentClip &&
                    hydratedClips.FirstOrDefault(clip => clip.FullPath.Equals(
                        currentClip.FullPath,
                        StringComparison.OrdinalIgnoreCase)) is { } hydratedCurrentClip)
                {
                    _currentClip = hydratedCurrentClip;
                    ClipPlayerPosterImage.DataContext = hydratedCurrentClip;
                }
            }
        }
        catch (OperationCanceledException) when (refreshCancellation.IsCancellationRequested)
        {
            // A newer gallery request superseded this work, or the app is closing.
        }
        catch (Exception exception)
        {
            if (IsVisible &&
                IsActive &&
                !_captureCriticalPresentationActive &&
                _playerSourceReleasedForBackground &&
                _currentClip is { } releasedClip)
            {
                // A failed foreground refresh must not leave enabled playback
                // controls pointing at a released MediaElement source.
                SelectClip(releasedClip, autoplay: false);
            }

            ShowError($"The clip gallery could not be refreshed. {exception.Message}");
        }
        finally
        {
            if (gateEntered)
            {
                _libraryRefreshGate.Release();
            }

            lock (_libraryRefreshCancellationGate)
            {
                if (ReferenceEquals(_activeLibraryRefreshCancellation, refreshCancellation))
                {
                    _activeLibraryRefreshCancellation = null;
                }
            }

            refreshCancellation.Dispose();
        }
    }

    private void SelectClip(ClipLibraryItem clip, bool autoplay)
    {
        if (!ClipLibraryService.IsCurrentClipSafe(_settings.SaveDirectory, clip))
        {
            ClearPlayer();
            ShowError("The selected clip changed or is no longer a safe local ClipForge recording. Refresh the gallery and try again.");
            return;
        }

        ReplaceClipPlayerElement();
        ClipPlayerPosterImage.DataContext = clip;

        if (_captureCriticalPresentationActive)
        {
            _currentClip = clip;
            _playerSourceReleasedForBackground = true;
            _playWhenOpened = false;
            SetPlayerPlaying(false);
            LatestClipNameText.Text = $"{clip.FileName} · {clip.RecordedAtUtc.ToLocalTime():dd MMM yyyy, HH:mm}";
            PlayerEmptyState.Visibility = Visibility.Visible;
            PlayerSurfacePlayButton.Visibility = Visibility.Collapsed;
            OpenCurrentClipButton.IsEnabled = true;
            TrimCurrentClipButton.IsEnabled = false;
            SetPlayerControlsEnabled(false);
            SetSeekUi(TimeSpan.Zero, clip.Duration ?? TimeSpan.Zero);
            PlayerTimeText.Text = clip.Duration is { } deferredDuration
                ? $"0:00 / {FormatDuration(deferredDuration)}"
                : "0:00 / --:--";
            return;
        }

        if (IsReplaySessionState(_latestState) && !autoplay)
        {
            // A gallery refresh during replay updates presentation state without
            // opening a decoder. The user's Play/clip click is the explicit
            // opt-in that creates the one foreground media graph.
            _currentClip = clip;
            _playerSourceReleasedForBackground = true;
            _playWhenOpened = false;
            SetPlayerPlaying(false);
            LatestClipNameText.Text = $"{clip.FileName} · {clip.RecordedAtUtc.ToLocalTime():dd MMM yyyy, HH:mm}";
            PlayerEmptyState.Visibility = Visibility.Collapsed;
            OpenCurrentClipButton.IsEnabled = true;
            TrimCurrentClipButton.IsEnabled = true;
            SetPlayerControlsEnabled(false);
            PlayerSurfacePlayButton.IsEnabled = true;
            PlayerSurfacePlayButton.Visibility = Visibility.Visible;
            SetSeekUi(TimeSpan.Zero, clip.Duration ?? TimeSpan.Zero);
            PlayerTimeText.Text = clip.Duration is { } replayDuration
                ? $"0:00 / {FormatDuration(replayDuration)}"
                : "0:00 / --:--";
            return;
        }

        _currentClip = clip;
        _playerSourceReleasedForBackground = false;
        _playWhenOpened = autoplay;
        SetPlayerPlaying(false);
        ClipPlayer.Source = new Uri(clip.FullPath, UriKind.Absolute);
        ApplyPlayerVolume(IsReplaySessionState(_latestState) && !_replayPlaybackAudioOptIn
            ? 0
            : null);
        LatestClipNameText.Text = $"{clip.FileName} · {clip.RecordedAtUtc.ToLocalTime():dd MMM yyyy, HH:mm}";
        PlayerEmptyState.Visibility = Visibility.Collapsed;
        OpenCurrentClipButton.IsEnabled = true;
        TrimCurrentClipButton.IsEnabled = true;
        SetPlayerControlsEnabled(true);
        PlayerSurfacePlayButton.Visibility = Visibility.Visible;
        SetSeekUi(TimeSpan.Zero, clip.Duration ?? TimeSpan.Zero);
        PlayerTimeText.Text = clip.Duration is { } duration
            ? $"0:00 / {FormatDuration(duration)}"
            : "0:00 / --:--";
        if (autoplay)
        {
            ClipPlayer.Play();
            SetPlayerPlaying(true);
        }
    }

    private void ClearPlayer()
    {
        ReplaceClipPlayerElement();
        ClipPlayerPosterImage.DataContext = null;
        _currentClip = null;
        _playerSourceReleasedForBackground = false;
        _playWhenOpened = false;
        SetPlayerPlaying(false);
        LatestClipNameText.Text = "Your newest saved clip will appear here.";
        PlayerEmptyState.Visibility = Visibility.Visible;
        PlayerSurfacePlayButton.Visibility = Visibility.Collapsed;
        OpenCurrentClipButton.IsEnabled = false;
        TrimCurrentClipButton.IsEnabled = false;
        SetPlayerControlsEnabled(false);
        SetSeekUi(TimeSpan.Zero, TimeSpan.Zero);
        PlayerTimeText.Text = "0:00 / 0:00";
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentClip is null)
        {
            return;
        }

        if (ClipPlayer.Source is null)
        {
            if (_captureCriticalPresentationActive)
            {
                return;
            }

            SelectClip(_currentClip, autoplay: true);
            return;
        }

        if (_isPlayerPlaying)
        {
            ClipPlayer.Pause();
            SetPlayerPlaying(false);
        }
        else
        {
            var duration = GetPlayerDuration();
            if (duration > TimeSpan.Zero && ClipPlayer.Position >= duration - TimeSpan.FromMilliseconds(250))
            {
                ClipPlayer.Position = TimeSpan.Zero;
            }

            ClipPlayer.Play();
            SetPlayerPlaying(true);
        }
    }

    private void ClipPlayer_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        PlayPauseButton_Click(sender, e);
        e.Handled = true;
    }

    private void BackTenSecondsButton_Click(object sender, RoutedEventArgs e) =>
        SeekPlayerTo(ClipPlayer.Position - TimeSpan.FromSeconds(10));

    private void ForwardTenSecondsButton_Click(object sender, RoutedEventArgs e) =>
        SeekPlayerTo(ClipPlayer.Position + TimeSpan.FromSeconds(10));

    private void MutePlayerButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentClip is null)
        {
            return;
        }

        if (_isPlayerMuted || PlayerVolumeSlider.Value <= 0)
        {
            _isPlayerMuted = false;
            if (IsReplaySessionState(_latestState))
            {
                _replayPlaybackAudioOptIn = true;
            }

            PlayerVolumeSlider.Value = Math.Max(5, _playerVolumeBeforeMute * 100);
        }
        else
        {
            _playerVolumeBeforeMute = Math.Clamp(PlayerVolumeSlider.Value / 100, 0.05, 1);
            _isPlayerMuted = true;
            _replayPlaybackAudioOptIn = false;
            PlayerVolumeSlider.Value = 0;
        }

        ApplyPlayerVolume();
    }

    private void RestartClipButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentClip is null)
        {
            return;
        }

        ClipPlayer.Position = TimeSpan.Zero;
        ClipPlayer.Play();
        SetPlayerPlaying(true);
    }

    private void ClipPlayer_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, ClipPlayer))
        {
            return;
        }

        if (!CanHandlePlayerMediaEvent())
        {
            SuppressLatePlayerMediaEvent();
            return;
        }

        PlayerEmptyState.Visibility = Visibility.Collapsed;
        SetPlayerControlsEnabled(true);
        var shouldAutoplay = _playWhenOpened;
        _playWhenOpened = false;
        if (shouldAutoplay)
        {
            ClipPlayer.Play();
            SetPlayerPlaying(true);
        }

        UpdatePlayerTime();
    }

    private void ClipPlayer_MediaEnded(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, ClipPlayer))
        {
            return;
        }

        if (!CanHandlePlayerMediaEvent())
        {
            SuppressLatePlayerMediaEvent();
            return;
        }

        ClipPlayer.Pause();
        ClipPlayer.Position = TimeSpan.Zero;
        SetPlayerPlaying(false);
        UpdatePlayerTime();
    }

    private void ClipPlayer_MediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, ClipPlayer))
        {
            return;
        }

        if (!CanHandlePlayerMediaEvent())
        {
            SuppressLatePlayerMediaEvent();
            return;
        }

        _playWhenOpened = false;
        SetPlayerPlaying(false);
        SetPlayerControlsEnabled(false);
        PlayerSurfacePlayButton.Visibility = Visibility.Collapsed;
        ShowError($"This clip could not be played inside ClipForge. {e.ErrorException.Message}");
    }

    private bool CanHandlePlayerMediaEvent() => ShouldHandlePlayerMediaEvent(
        _captureCriticalPresentationActive,
        _isClosing,
        IsVisible,
        IsActive,
        _currentClip is not null,
        ClipPlayer.Source is not null);

    internal static bool ShouldHandlePlayerMediaEvent(
        bool captureCritical,
        bool isClosing,
        bool isVisible,
        bool isActive,
        bool hasCurrentClip,
        bool hasSource) =>
        !captureCritical &&
        !isClosing &&
        isVisible &&
        isActive &&
        hasCurrentClip &&
        hasSource;

    private void SuppressLatePlayerMediaEvent()
    {
        _playWhenOpened = false;
        ClipPlayer.Volume = 0;
        ReplaceClipPlayerElement();

        _playerSourceReleasedForBackground = _currentClip is not null;
        SetPlayerPlaying(false);
        SetPlayerControlsEnabled(false);
        PlayerSurfacePlayButton.Visibility = Visibility.Collapsed;
    }

    private void SetPlayerPlaying(bool isPlaying)
    {
        _isPlayerPlaying = isPlaying &&
                           !_captureCriticalPresentationActive &&
                           ClipPlayer.Source is not null;
        PlayPauseButton.Content = _isPlayerPlaying ? "⏸" : "▶";
        PlayPauseButton.ToolTip = _isPlayerPlaying ? "Pause clip" : "Play clip";
        PlayPauseButton.SetValue(
            System.Windows.Automation.AutomationProperties.NameProperty,
            _isPlayerPlaying ? "Pause selected clip" : "Play selected clip");
        PlayerSurfacePlayButton.Visibility = !_isPlayerPlaying &&
                                             !_captureCriticalPresentationActive &&
                                             _currentClip is not null &&
                                             ClipPlayer.Source is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (_isPlayerPlaying && IsActive && IsVisible)
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
        if (_currentClip is null)
        {
            return;
        }

        var total = GetPlayerDuration();
        var position = total > TimeSpan.Zero && ClipPlayer.Position > total
            ? total
            : ClipPlayer.Position;
        PlayerTimeText.Text = $"{FormatDuration(position)} / {(total > TimeSpan.Zero ? FormatDuration(total) : "--:--")}";
        if (!_isPlayerSeeking)
        {
            SetSeekUi(position, total);
        }
    }

    private void SetPlayerControlsEnabled(bool isEnabled)
    {
        PlayPauseButton.IsEnabled = isEnabled;
        RestartClipButton.IsEnabled = isEnabled;
        BackTenSecondsButton.IsEnabled = isEnabled;
        ForwardTenSecondsButton.IsEnabled = isEnabled;
        MutePlayerButton.IsEnabled = isEnabled;
        PlayerVolumeSlider.IsEnabled = isEnabled;
        PlayerSurfacePlayButton.IsEnabled = isEnabled;
        PlayerSeekSlider.IsEnabled = isEnabled && GetPlayerDuration() > TimeSpan.Zero;
    }

    private TimeSpan GetPlayerDuration() => ClipPlayer.NaturalDuration.HasTimeSpan
        ? ClipPlayer.NaturalDuration.TimeSpan
        : _currentClip?.Duration ?? TimeSpan.Zero;

    private void SetSeekUi(TimeSpan position, TimeSpan total)
    {
        _isUpdatingPlayerControls = true;
        try
        {
            var maximumSeconds = total > TimeSpan.Zero ? total.TotalSeconds : 1;
            PlayerSeekSlider.Maximum = maximumSeconds;
            PlayerSeekSlider.Value = Math.Clamp(position.TotalSeconds, 0, maximumSeconds);
            PlayerSeekSlider.IsEnabled = _currentClip is not null &&
                                         ClipPlayer.Source is not null &&
                                         !_captureCriticalPresentationActive &&
                                         total > TimeSpan.Zero;
        }
        finally
        {
            _isUpdatingPlayerControls = false;
        }
    }

    private void SeekPlayerTo(TimeSpan requestedPosition, bool updateSlider = true)
    {
        if (_currentClip is null)
        {
            return;
        }

        var total = GetPlayerDuration();
        if (total <= TimeSpan.Zero)
        {
            return;
        }

        var target = TimeSpan.FromSeconds(Math.Clamp(requestedPosition.TotalSeconds, 0, total.TotalSeconds));
        ClipPlayer.Position = target;
        PlayerTimeText.Text = $"{FormatDuration(target)} / {FormatDuration(total)}";
        if (updateSlider)
        {
            SetSeekUi(target, total);
        }
    }

    private void PlayerSeekSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!PlayerSeekSlider.IsEnabled || _currentClip is null)
        {
            return;
        }

        _isPlayerSeeking = true;
        _resumePlayerAfterSeek = _isPlayerPlaying;
        if (_resumePlayerAfterSeek)
        {
            ClipPlayer.Pause();
            _playerTimer.Stop();
        }

        if (PlayerSeekSlider.ActualWidth > 0)
        {
            var pointer = e.GetPosition(PlayerSeekSlider);
            PlayerSeekSlider.Value = Math.Clamp(
                pointer.X / PlayerSeekSlider.ActualWidth * PlayerSeekSlider.Maximum,
                PlayerSeekSlider.Minimum,
                PlayerSeekSlider.Maximum);
        }
    }

    private void PlayerSeekSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) =>
        FinishPlayerSeek();

    private void FinishPlayerSeek()
    {
        if (!_isPlayerSeeking)
        {
            return;
        }

        SeekPlayerTo(TimeSpan.FromSeconds(PlayerSeekSlider.Value));
        _isPlayerSeeking = false;
        if (_resumePlayerAfterSeek)
        {
            ClipPlayer.Play();
            SetPlayerPlaying(true);
        }
        else
        {
            SetPlayerPlaying(false);
            UpdatePlayerTime();
        }

        _resumePlayerAfterSeek = false;
    }

    private void PlayerSeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingPlayerControls || _currentClip is null)
        {
            return;
        }

        if (_isPlayerSeeking || PlayerSeekSlider.IsKeyboardFocusWithin)
        {
            SeekPlayerTo(TimeSpan.FromSeconds(e.NewValue), updateSlider: false);
        }
    }

    private void PlayerSeekSlider_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space)
        {
            return;
        }

        PlayPauseButton_Click(sender, e);
        e.Handled = true;
    }

    private void PlayerVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isInitializing &&
            IsReplaySessionState(_latestState) &&
            e.NewValue > 0 &&
            e.NewValue != e.OldValue)
        {
            _replayPlaybackAudioOptIn = true;
        }

        ApplyPlayerVolume();
    }

    private void ApplyPlayerVolume(double? requestedVolume = null)
    {
        if (ClipPlayer is null || MutePlayerButton is null)
        {
            return;
        }

        var volume = Math.Clamp(requestedVolume ?? PlayerVolumeSlider.Value / 100, 0, 1);
        ClipPlayer.Volume = volume;
        _isPlayerMuted = volume <= 0.001;
        if (!_isPlayerMuted)
        {
            _playerVolumeBeforeMute = volume;
        }

        MutePlayerButton.Content = _isPlayerMuted ? "🔇" : "🔊";
        MutePlayerButton.ToolTip = _isPlayerMuted ? "Unmute clip audio" : "Mute clip audio";
        MutePlayerButton.SetValue(
            System.Windows.Automation.AutomationProperties.NameProperty,
            _isPlayerMuted ? "Unmute clip audio" : "Mute clip audio");
    }

    private void MainWindow_Activated(object? sender, EventArgs e)
    {
        if (_isClosing)
        {
            return;
        }

        UpdateControlsForState(_latestState);
        UpdateStorageText();

        if (_captureCriticalPresentationActive)
        {
            return;
        }

        var libraryRefreshRequired = _libraryRefreshPending || _pendingLibraryPreferredPath is not null;
        if (!libraryRefreshRequired &&
            _playerSourceReleasedForBackground &&
            _currentClip is { } releasedClip)
        {
            SelectClip(releasedClip, autoplay: false);
        }

        if (libraryRefreshRequired)
        {
            _ = RefreshClipLibraryAsync(_pendingLibraryPreferredPath);
        }

        if (!_isPlayerPlaying)
        {
            return;
        }

        UpdatePlayerTime();
        _playerTimer.Start();
    }

    private void MainWindow_Deactivated(object? sender, EventArgs e)
    {
        ReleasePlayerForBackground();
        CancelActiveLibraryRefreshForBackground();
    }

    private void CancelActiveLibraryRefreshForBackground()
    {
        lock (_libraryRefreshCancellationGate)
        {
            if (_activeLibraryRefreshCancellation is null)
            {
                return;
            }

            _libraryRefreshPending = true;
            _activeLibraryRefreshCancellation.Cancel();
        }
    }

    private void PausePlayerForBackgroundWork()
    {
        // Cancel an autoplay that is still waiting for MediaOpened as well as
        // active playback/seeking. This prevents hidden or background playback
        // from consuming capture resources or feeding audio into desktop capture.
        _playWhenOpened = false;
        _isPlayerSeeking = false;
        _resumePlayerAfterSeek = false;
        if (_isPlayerPlaying)
        {
            ClipPlayer.Pause();
        }

        SetPlayerPlaying(false);
    }

    private void ReleasePlayerForBackground()
    {
        PausePlayerForBackgroundWork();
        if (_currentClip is null || ClipPlayer.Source is null)
        {
            return;
        }

        ReplaceClipPlayerElement();
        _playerSourceReleasedForBackground = true;
    }

    private void ReplaceClipPlayerElement()
    {
        var previousPlayer = ClipPlayer;
        previousPlayer.MouseLeftButtonUp -= ClipPlayer_MouseLeftButtonUp;
        previousPlayer.MediaOpened -= ClipPlayer_MediaOpened;
        previousPlayer.MediaEnded -= ClipPlayer_MediaEnded;
        previousPlayer.MediaFailed -= ClipPlayer_MediaFailed;
        previousPlayer.Volume = 0;
        previousPlayer.Stop();
        previousPlayer.Close();
        previousPlayer.Source = null;

        if (_isClosing)
        {
            return;
        }

        // Each clip owns a fresh WPF media graph. Late events from a closed
        // source remain attached to the detached element and cannot mutate the
        // next clip's controls or autoplay state.
        var replacement = new MediaElement
        {
            LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Manual,
            Stretch = System.Windows.Media.Stretch.Uniform,
            ScrubbingEnabled = true,
            Volume = 0.8,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        replacement.MouseLeftButtonUp += ClipPlayer_MouseLeftButtonUp;
        replacement.MediaOpened += ClipPlayer_MediaOpened;
        replacement.MediaEnded += ClipPlayer_MediaEnded;
        replacement.MediaFailed += ClipPlayer_MediaFailed;
        System.Windows.Automation.AutomationProperties.SetName(
            replacement,
            "Clip preview player");
        System.Windows.Automation.AutomationProperties.SetHelpText(
            replacement,
            "Click to play or pause the selected clip");

        var index = ClipPlayerHost.Children.IndexOf(previousPlayer);
        if (index >= 0)
        {
            ClipPlayerHost.Children.RemoveAt(index);
            ClipPlayerHost.Children.Insert(index, replacement);
        }
        else
        {
            ClipPlayerHost.Children.Insert(0, replacement);
        }

        ClipPlayer = replacement;
    }

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{(int)duration.TotalMinutes}:{duration.Seconds:00}";

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!_exitRequested && !_isClosing)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        if (_isClosing)
        {
            e.Cancel = true;
            return;
        }

        e.Cancel = true;
        _isClosing = true;
        _replayStartRequested = false;
        IsEnabled = false;
        StatusText.Text = "Closing ClipForge…";
        _lifetimeCancellation.Cancel();
        _displayModeChangeCancellation?.Cancel();
        CancelSavedToast();
        _libraryWindow?.Close();
        _libraryWindow = null;

        try
        {
            SyncSettingsFromControls();
            await _settingsService.SaveAsync(_settings);

            await _captureCommandGate.WaitAsync();
            try
            {
                await _replayBufferService.StopAsync();
            }
            finally
            {
                _captureCommandGate.Release();
            }

            await _replayBufferService.DisposeAsync();
        }
        catch
        {
            // Shutdown should continue even when capture cleanup cannot complete normally.
        }
        finally
        {
            _replayBufferService.StateChanged -= ReplayBufferService_StateChanged;
            _replayBufferService.CaptureRecoveryRequested -=
                ReplayBufferService_CaptureRecoveryRequested;
            _appUpdateService.StateChanged -= AppUpdateService_StateChanged;
            _hotkeyService.SaveClipPressed -= HotkeyService_SaveClipPressed;
            _hotkeyService.ToggleOverlayPressed -= HotkeyService_ToggleOverlayPressed;
            _hotkeyService.RegistrationFailed -= HotkeyService_RegistrationFailed;
            _trayIconService.ShowRequested -= TrayIconService_ShowRequested;
            _trayIconService.SaveClipRequested -= TrayIconService_SaveClipRequested;
            _trayIconService.ExitRequested -= TrayIconService_ExitRequested;
            System.Windows.Application.Current.SessionEnding -= Application_SessionEnding;
            SystemEvents.DisplaySettingsChanged -= SystemEvents_DisplaySettingsChanged;
            PreviewKeyDown -= MainWindow_PreviewKeyDown;
            Activated -= MainWindow_Activated;
            Deactivated -= MainWindow_Deactivated;
            _playerTimer.Stop();
            _playerTimer.Tick -= PlayerTimer_Tick;
            ReplaceClipPlayerElement();
            if (_overlayWindow is not null)
            {
                _overlayWindow.Close();
            }

            _trayIconService.Dispose();
            _nativeWindowThemeService.Dispose();
            _clipSavedSoundService.Dispose();
            _appUpdateService.Dispose();
            _hotkeyService.Dispose();
            _settingsService.Dispose();
            _lifetimeCancellation.Dispose();
            _captureCommandGate.Dispose();
            Closing -= MainWindow_Closing;
            Close();
        }
    }
}
