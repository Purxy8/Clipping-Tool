using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ClipForge.Models;
using ClipForge.Services;
using Microsoft.Win32;
using Button = System.Windows.Controls.Button;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using Forms = System.Windows.Forms;

namespace ClipForge;

public partial class MainWindow : Window
{
    private static readonly int[] FrameRateOptions = [30, 60];

    private readonly SettingsService _settingsService = new();
    private readonly DeviceDiscoveryService _deviceDiscoveryService = new();
    private readonly FfmpegSetupService _ffmpegSetupService = new();
    private readonly AppUpdateService _appUpdateService = new();
    private readonly ReplayBufferService _replayBufferService;
    private readonly ClipLibraryService _clipLibraryService;
    private readonly GlobalHotkeyService _hotkeyService = new();
    private readonly TrayIconService _trayIconService;
    private readonly DispatcherTimer _playerTimer;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly SemaphoreSlim _captureCommandGate = new(1, 1);
    private readonly SemaphoreSlim _libraryRefreshGate = new(1, 1);

    private AppSettings _settings = new();
    private ReplayStateSnapshot _latestState = new(
        ReplayState.Stopped,
        TimeSpan.Zero,
        TimeSpan.FromMinutes(2),
        0);
    private bool _isInitializing = true;
    private bool _isClosing;
    private bool _exitRequested;
    private bool _backgroundHintShown;
    private bool _isPlayerPlaying;
    private bool _playWhenOpened;
    private bool _isPlayerSeeking;
    private bool _resumePlayerAfterSeek;
    private bool _isUpdatingPlayerControls;
    private bool _isPlayerMuted;
    private double _playerVolumeBeforeMute = 0.8;
    private string? _pendingLibraryPreferredPath;
    private string? _lastSavedPath;
    private ClipLibraryItem? _currentClip;
    private OverlayWindow? _overlayWindow;
    private GlobalHotkeyAction? _capturingHotkeyAction;

    public MainWindow()
    {
        _replayBufferService = new ReplayBufferService(_ffmpegSetupService);
        _clipLibraryService = new ClipLibraryService(_ffmpegSetupService);
        _replayBufferService.StateChanged += ReplayBufferService_StateChanged;
        _appUpdateService.StateChanged += AppUpdateService_StateChanged;

        InitializeComponent();
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
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;

        try
        {
            _settings = await _settingsService.LoadAsync(_lifetimeCancellation.Token);
            _settings.SaveClipHotkey ??= HotkeyGesture.DefaultSaveClip;
            _settings.ToggleOverlayHotkey ??= HotkeyGesture.DefaultToggleOverlay;
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
            _ = RunAutomaticUpdateCheckAsync();
            _ = RefreshClipLibraryAsync();
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
        HotkeyText.Text = _settings.SaveClipHotkey.DisplayText;
        OverlayHotkeyText.Text = _settings.ToggleOverlayHotkey.DisplayText;
        ApplyBackgroundColor(_settings.BackgroundColor);

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
                await _replayBufferService.StopAsync();
            }
            else
            {
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
            UpdateControlsForState(_latestState);
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

    private async void DisplayComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        await CaptureConfigurationChangedAsync(restartRequired: true);

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

        if (restartRequired && _replayBufferService.IsRunning)
        {
            await RunCaptureCommandAsync(async () =>
            {
                await _replayBufferService.StopAsync();
                await StartReplayCoreAsync();
            });
        }
    }

    private async Task RunCaptureCommandAsync(Func<Task> command)
    {
        await _captureCommandGate.WaitAsync();
        BufferToggleButton.IsEnabled = false;

        try
        {
            HideError();
            await command();
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // Application shutdown.
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
        finally
        {
            _captureCommandGate.Release();
            UpdateControlsForState(_latestState);
        }
    }

    private async Task StartReplayCoreAsync()
    {
        if (_ffmpegSetupService.FindExecutable() is null)
        {
            InstallEnginePanel.Visibility = Visibility.Visible;
            throw new InvalidOperationException("Install the capture engine before starting Instant Replay.");
        }

        // Embedded playback is presentation work and can feed its own audio back
        // into desktop capture. Always leave it paused before capture starts/restarts.
        PausePlayerForBackgroundWork();

        SyncSettingsFromControls();
        EnsureSaveDirectory();
        await PersistSettingsAsync();
        await _replayBufferService.StartAsync(
            BuildCaptureConfiguration(),
            _lifetimeCancellation.Token);
    }

    private CaptureConfiguration BuildCaptureConfiguration()
    {
        var display = DisplayComboBox.SelectedItem as DisplayOption
            ?? throw new InvalidOperationException("No display is available to capture.");
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
        _settings.CheckForUpdatesAutomatically = AutoUpdateCheckBox.IsChecked == true;
        _settings.BackgroundColor = NormalizeBackgroundColor(_settings.BackgroundColor);
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

    private async void BackgroundSwatchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || _isClosing || sender is not Button { Tag: string color })
        {
            return;
        }

        ApplyBackgroundColor(color);
        await PersistSettingsAsync();
    }

    private async void CustomBackgroundButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || _isClosing)
        {
            return;
        }

        var current = ParseBackgroundColor(NormalizeBackgroundColor(_settings.BackgroundColor));
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

        ApplyBackgroundColor($"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}");
        await PersistSettingsAsync();
    }

    private async void ResetBackgroundButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || _isClosing)
        {
            return;
        }

        ApplyBackgroundColor(AppSettings.DefaultBackgroundColor);
        await PersistSettingsAsync();
    }

    private void ApplyBackgroundColor(string? requestedColor)
    {
        var normalized = NormalizeBackgroundColor(requestedColor);
        var color = ParseBackgroundColor(normalized);
        var backgroundBrush = new SolidColorBrush(color);
        backgroundBrush.Freeze();

        Resources["UserBackgroundBrush"] = backgroundBrush;
        _settings.BackgroundColor = normalized;
        BackgroundColorValueText.Text = normalized;

        foreach (var button in BackgroundSwatchesPanel.Children.OfType<Button>())
        {
            var isSelected = string.Equals(button.Tag as string, normalized, StringComparison.OrdinalIgnoreCase);
            button.BorderBrush = Brush(isSelected ? "AccentHoverBrush" : "BorderStrongBrush");
            button.BorderThickness = new Thickness(isSelected ? 2 : 1);
            button.SetValue(
                System.Windows.Automation.AutomationProperties.ItemStatusProperty,
                isSelected ? "Selected" : string.Empty);
        }
    }

    private static string NormalizeBackgroundColor(string? requestedColor) =>
        AppSettings.NormalizeBackgroundColor(requestedColor);

    private static Color ParseBackgroundColor(string normalizedColor) => Color.FromRgb(
        byte.Parse(normalizedColor.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        byte.Parse(normalizedColor.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        byte.Parse(normalizedColor.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));

    private sealed class ColorDialogOwner(nint handle) : Forms.IWin32Window
    {
        public nint Handle { get; } = handle;
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

        _latestState = snapshot;
        UpdateControlsForState(snapshot);

        if (!string.IsNullOrWhiteSpace(snapshot.LastSavedPath))
        {
            ShowLastSaved(snapshot.LastSavedPath);
        }

        if (snapshot.State == ReplayState.Faulted && !string.IsNullOrWhiteSpace(snapshot.Message))
        {
            ShowError(snapshot.Message);
        }
    }

    private void UpdateControlsForState(ReplayStateSnapshot snapshot)
    {
        if (_isInitializing || _isClosing)
        {
            return;
        }

        var engineReady = _ffmpegSetupService.FindExecutable() is not null;
        var isBusy = snapshot.State is ReplayState.Starting or ReplayState.Stopping;
        var isSaving = snapshot.State == ReplayState.Saving;
        var isRunning = _replayBufferService.IsRunning;

        (StatusText.Text, StatusDot.Fill) = snapshot.State switch
        {
            ReplayState.Starting => ("Starting replay…", Brush("AccentBrush")),
            ReplayState.Buffering => ("Building buffer", Brush("WarningBrush")),
            ReplayState.Ready => ("Replay ready", Brush("SuccessBrush")),
            ReplayState.Saving => ("Saving clip…", Brush("AccentBrush")),
            ReplayState.Faulted => ("Replay error", Brush("ErrorBrush")),
            ReplayState.Stopping => ("Stopping replay…", Brush("TextMutedBrush")),
            _ => ("Replay off", Brush("TextMutedBrush"))
        };

        BufferToggleButton.Content = isRunning ? "Stop replay" : "Start replay";
        BufferToggleButton.IsEnabled = engineReady && !isBusy && !isSaving && !_isClosing;
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

        SaveClipButton.IsEnabled = isRunning &&
                                   !isBusy &&
                                   !isSaving &&
                                   snapshot.AvailableDuration >= TimeSpan.FromSeconds(1) &&
                                   !_isClosing;
        if (_replayBufferService.ActiveEncoderDescription is { Length: > 0 } encoderDescription)
        {
            EncoderStatusText.Text = $"Performance mode: {encoderDescription}";
            EncoderStatusText.Foreground = Brush(
                encoderDescription.Contains("software", StringComparison.OrdinalIgnoreCase)
                    ? "WarningBrush"
                    : "SuccessBrush");
        }

        _trayIconService.UpdateStatus(StatusText.Text, SaveClipButton.IsEnabled);
        _overlayWindow?.UpdateState(
            snapshot,
            isRunning,
            _settings.SaveClipHotkey.DisplayText);
        UpdateStorageText();
    }

    private Brush Brush(string resourceKey) => (Brush)FindResource(resourceKey);

    private void UpdatePrimaryActionText()
    {
        SaveClipButton.Content = ReplayLengthComboBox.SelectedItem is ReplayLengthOption option
            ? $"Save last {option.Label}"
            : "Save last clip";
    }

    private void RefreshEngineState()
    {
        var engineReady = _ffmpegSetupService.FindExecutable() is not null;
        InstallEnginePanel.Visibility = engineReady ? Visibility.Collapsed : Visibility.Visible;
        BufferToggleButton.IsEnabled = engineReady && !_isClosing;
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
            var freeText = !string.IsNullOrWhiteSpace(root)
                ? $" · {StorageEstimator.FormatBytes(new DriveInfo(root).AvailableFreeSpace)} free"
                : string.Empty;

            StorageText.Text = $"~{StorageEstimator.FormatBytes(estimate)} replay buffer{freeText}";
            StorageText.Foreground = !string.IsNullOrWhiteSpace(root) &&
                                     new DriveInfo(root).AvailableFreeSpace < estimate * 2
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

    private void ShowLastSaved(string path)
    {
        var changed = !string.Equals(_lastSavedPath, path, StringComparison.OrdinalIgnoreCase);
        _lastSavedPath = path;
        LastSavedText.Text = $"{Path.GetFileName(path)} · saved";
        LastSavedPanel.Visibility = Visibility.Visible;
        if (changed)
        {
            if (IsVisible && IsActive)
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

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorBanner.Visibility = Visibility.Visible;
    }

    private void HideError() => ErrorBanner.Visibility = Visibility.Collapsed;

    private void InitializeUpdateControls()
    {
        VersionText.Text = $"v{ReleaseInfo.Version}";
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
            _overlayWindow?.UpdateState(_latestState, _replayBufferService.IsRunning, saveGesture.DisplayText);
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

        PausePlayerForBackgroundWork();

        Hide();
        if (!_backgroundHintShown)
        {
            _backgroundHintShown = true;
            _trayIconService.ShowBackgroundHint();
        }
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

    private async Task RefreshClipLibraryAsync(string? preferredPath = null)
    {
        if (_isClosing)
        {
            return;
        }

        var gateEntered = false;
        try
        {
            await _libraryRefreshGate.WaitAsync(_lifetimeCancellation.Token);
            gateEntered = true;
            var snapshot = await _clipLibraryService.LoadAsync(
                _settings.SaveDirectory,
                count: 5,
                includeThumbnails: true,
                _lifetimeCancellation.Token);

            RecentClipsItemsControl.ItemsSource = snapshot.GalleryClips;
            var selected = preferredPath is { Length: > 0 }
                ? snapshot.Clips.FirstOrDefault(clip =>
                    clip.FullPath.Equals(preferredPath, StringComparison.OrdinalIgnoreCase))
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
                     preferredPath is not null)
            {
                // Automatic save refreshes select the new recording without decoding
                // and playing it over the still-running capture session.
                SelectClip(selected, autoplay: false);
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // Application shutdown.
        }
        catch (Exception exception)
        {
            ShowError($"The clip gallery could not be refreshed. {exception.Message}");
        }
        finally
        {
            if (gateEntered)
            {
                _libraryRefreshGate.Release();
            }
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

        ClipPlayer.Stop();
        _currentClip = clip;
        _playWhenOpened = autoplay;
        SetPlayerPlaying(false);
        ClipPlayer.Source = new Uri(clip.FullPath, UriKind.Absolute);
        LatestClipNameText.Text = $"{clip.FileName} · {clip.RecordedAtUtc.ToLocalTime():dd MMM yyyy, HH:mm}";
        PlayerEmptyState.Visibility = Visibility.Collapsed;
        OpenCurrentClipButton.IsEnabled = true;
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
        ClipPlayer.Stop();
        ClipPlayer.Source = null;
        _currentClip = null;
        _playWhenOpened = false;
        SetPlayerPlaying(false);
        LatestClipNameText.Text = "Your newest saved clip will appear here.";
        PlayerEmptyState.Visibility = Visibility.Visible;
        PlayerSurfacePlayButton.Visibility = Visibility.Collapsed;
        OpenCurrentClipButton.IsEnabled = false;
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
            PlayerVolumeSlider.Value = Math.Max(5, _playerVolumeBeforeMute * 100);
        }
        else
        {
            _playerVolumeBeforeMute = Math.Clamp(PlayerVolumeSlider.Value / 100, 0.05, 1);
            _isPlayerMuted = true;
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
        ClipPlayer.Pause();
        ClipPlayer.Position = TimeSpan.Zero;
        SetPlayerPlaying(false);
        UpdatePlayerTime();
    }

    private void ClipPlayer_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        _playWhenOpened = false;
        SetPlayerPlaying(false);
        SetPlayerControlsEnabled(false);
        PlayerSurfacePlayButton.Visibility = Visibility.Collapsed;
        ShowError($"This clip could not be played inside ClipForge. {e.ErrorException.Message}");
    }

    private void SetPlayerPlaying(bool isPlaying)
    {
        _isPlayerPlaying = isPlaying;
        PlayPauseButton.Content = isPlaying ? "⏸" : "▶";
        PlayPauseButton.ToolTip = isPlaying ? "Pause clip" : "Play clip";
        PlayPauseButton.SetValue(
            System.Windows.Automation.AutomationProperties.NameProperty,
            isPlaying ? "Pause selected clip" : "Play selected clip");
        PlayerSurfacePlayButton.Visibility = !isPlaying && _currentClip is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (isPlaying && IsActive && IsVisible)
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
            PlayerSeekSlider.IsEnabled = _currentClip is not null && total > TimeSpan.Zero;
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

    private void PlayerVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        ApplyPlayerVolume();

    private void ApplyPlayerVolume()
    {
        if (ClipPlayer is null || MutePlayerButton is null)
        {
            return;
        }

        var volume = Math.Clamp(PlayerVolumeSlider.Value / 100, 0, 1);
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

        if (_pendingLibraryPreferredPath is { } pendingPath)
        {
            _pendingLibraryPreferredPath = null;
            _ = RefreshClipLibraryAsync(pendingPath);
        }

        if (!_isPlayerPlaying)
        {
            return;
        }

        UpdatePlayerTime();
        _playerTimer.Start();
    }

    private void MainWindow_Deactivated(object? sender, EventArgs e) =>
        PausePlayerForBackgroundWork();

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
        IsEnabled = false;
        StatusText.Text = "Closing ClipForge…";
        _lifetimeCancellation.Cancel();

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
            _appUpdateService.StateChanged -= AppUpdateService_StateChanged;
            _hotkeyService.SaveClipPressed -= HotkeyService_SaveClipPressed;
            _hotkeyService.ToggleOverlayPressed -= HotkeyService_ToggleOverlayPressed;
            _hotkeyService.RegistrationFailed -= HotkeyService_RegistrationFailed;
            _trayIconService.ShowRequested -= TrayIconService_ShowRequested;
            _trayIconService.SaveClipRequested -= TrayIconService_SaveClipRequested;
            _trayIconService.ExitRequested -= TrayIconService_ExitRequested;
            System.Windows.Application.Current.SessionEnding -= Application_SessionEnding;
            PreviewKeyDown -= MainWindow_PreviewKeyDown;
            Activated -= MainWindow_Activated;
            Deactivated -= MainWindow_Deactivated;
            _playerTimer.Stop();
            _playerTimer.Tick -= PlayerTimer_Tick;
            ClipPlayer.Stop();
            if (_overlayWindow is not null)
            {
                _overlayWindow.Close();
            }

            _trayIconService.Dispose();
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
