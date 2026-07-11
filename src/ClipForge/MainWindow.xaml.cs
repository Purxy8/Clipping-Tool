using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using ClipForge.Models;
using ClipForge.Services;
using Microsoft.Win32;
using Brush = System.Windows.Media.Brush;

namespace ClipForge;

public partial class MainWindow : Window
{
    private static readonly int[] FrameRateOptions = [30, 60];

    private readonly SettingsService _settingsService = new();
    private readonly DeviceDiscoveryService _deviceDiscoveryService = new();
    private readonly FfmpegSetupService _ffmpegSetupService = new();
    private readonly AppUpdateService _appUpdateService = new();
    private readonly ReplayBufferService _replayBufferService;
    private readonly GlobalHotkeyService _hotkeyService = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly SemaphoreSlim _captureCommandGate = new(1, 1);

    private AppSettings _settings = new();
    private ReplayStateSnapshot _latestState = new(
        ReplayState.Stopped,
        TimeSpan.Zero,
        TimeSpan.FromMinutes(2),
        0);
    private bool _isInitializing = true;
    private bool _isClosing;
    private string? _lastSavedPath;

    public MainWindow()
    {
        _replayBufferService = new ReplayBufferService(_ffmpegSetupService);
        _replayBufferService.StateChanged += ReplayBufferService_StateChanged;
        _appUpdateService.StateChanged += AppUpdateService_StateChanged;

        InitializeComponent();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;

        try
        {
            _settings = await _settingsService.LoadAsync(_lifetimeCancellation.Token);
            PopulateControls();
            EnsureSaveDirectory();
            RefreshEngineState();
            UpdateStorageText();
            InitializeUpdateControls();

            _hotkeyService.Pressed += HotkeyService_Pressed;
            try
            {
                _hotkeyService.Register(this);
            }
            catch (Exception exception)
            {
                ShowError($"The global shortcut could not be registered. {exception.Message}");
                HotkeyText.Text = "Unavailable";
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

    private void ReplayBufferService_StateChanged(object? sender, ReplayStateSnapshot snapshot)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
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
        if (_isInitializing)
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
        _ = Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }

    private void ShowLastSaved(string path)
    {
        _lastSavedPath = path;
        LastSavedText.Text = $"{Path.GetFileName(path)} · {Path.GetDirectoryName(path)}";
        LastSavedPanel.Visibility = Visibility.Visible;
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
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
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

    private void HotkeyService_Pressed(object? sender, EventArgs e)
    {
        if (_replayBufferService.IsRunning)
        {
            _ = SaveClipAsync();
        }
        else
        {
            ShowError("Instant Replay is off. Start it before using Ctrl + Shift + F10.");
        }
    }

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{(int)duration.TotalMinutes}:{duration.Seconds:00}";

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_isClosing)
        {
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
            _hotkeyService.Pressed -= HotkeyService_Pressed;
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
