using System.Collections.ObjectModel;
using System.Windows;
using AutoShazam.Models;
using AutoShazam.Services.Audio;
using AutoShazam.Services.Lyrics;
using AutoShazam.Services.Recognition;
using AutoShazam.Services.Settings;
using AutoShazam.Services.Update;
using AutoShazam.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Velopack;

namespace AutoShazam.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly RecognitionCoordinator _coordinator;
    private readonly AudioDeviceService _deviceService = new();
    private readonly AppUpdateService _updateService;
    private readonly SettingsService _settingsService;
    private readonly LyricsService _lyricsService = new();
    private LyricsWindow? _lyricsWindow;
    private int _lyricsRequestVersion;

    /// <summary>The loaded (and, on selection change, mutated) persisted settings object; saved
    /// immediately on every change - see the On*Changed partial methods below.</summary>
    public AppSettings Settings { get; }

    /// <summary>True for a genuine Velopack install (the shipped release build), false for a local/dev run.</summary>
    public bool IsInstalled => _updateService.IsInstalled;

    public ObservableCollection<AudioDeviceOption> Microphones { get; } = new();

    [ObservableProperty]
    private AudioDeviceOption? selectedMicrophone;

    // Always starts false - Auto Shazam must default to off on every application start.
    [ObservableProperty]
    private bool isAutoShazamEnabled;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string statusText = "Ready.";

    [ObservableProperty]
    private string? resultTitle;

    [ObservableProperty]
    private string? resultArtist;

    [ObservableProperty]
    private string? coverArtUrl;

    [ObservableProperty]
    private bool hasResult;

    [ObservableProperty]
    private string? lyricsText;

    [ObservableProperty]
    private bool hasLyrics;

    [ObservableProperty]
    private bool isMicrophoneActive;

    [ObservableProperty]
    private bool isQueryingShazam;

    [ObservableProperty]
    private double currentDbFs = AudioLevelConstants.SilenceThresholdDbFs;

    [ObservableProperty]
    private bool isSoundDetected;

    [ObservableProperty]
    private string soundLevelTooltip = "No sound detected";

    private DateTime? _silenceStartUtc;

    // The raw dBFS reading flickers back and forth across the threshold for genuinely quiet
    // sounds, which made the tooltip bounce between "Sound detected"/"No sound detected" on every
    // sample. Only commit a state change once the raw reading has held steady for this long.
    private bool _rawSoundDetected;
    private DateTime _rawSoundStateChangedUtc = DateTime.MinValue;

    [ObservableProperty]
    private double extendedSilenceTimeoutSeconds;

    [ObservableProperty]
    private double silenceThresholdDbFs;

    [ObservableProperty]
    private double soundStateDebounceMs;

    [ObservableProperty]
    private bool automaticallyCheckForUpdates;

    public MainViewModel(AppSettings settings, AppUpdateService updateService, SettingsService settingsService, string appDataRoot)
    {
        Settings = settings;
        _updateService = updateService;
        _settingsService = settingsService;
        _coordinator = new RecognitionCoordinator(appDataRoot);

        foreach (var device in _deviceService.GetCaptureDevices())
        {
            Microphones.Add(device);
        }

        selectedMicrophone = Microphones.FirstOrDefault(d => d.Id == settings.SelectedMicrophoneDeviceId)
            ?? Microphones.FirstOrDefault();

        if (selectedMicrophone is not null)
        {
            _coordinator.SetDevice(selectedMicrophone.Id);
        }

        extendedSilenceTimeoutSeconds = settings.ExtendedSilenceTimeoutSeconds;
        _coordinator.SetExtendedSilenceTimeout(TimeSpan.FromSeconds(Math.Max(1, extendedSilenceTimeoutSeconds)));

        silenceThresholdDbFs = settings.SilenceThresholdDbFs;
        _coordinator.SetSilenceThreshold(silenceThresholdDbFs);
        currentDbFs = silenceThresholdDbFs; // keep the level icon's idle state consistent with the configured threshold

        soundStateDebounceMs = settings.SoundStateDebounceMs;

        automaticallyCheckForUpdates = settings.AutomaticallyCheckForUpdates;

        _coordinator.RecognitionStarted += (_, _) => RunOnUi(() =>
        {
            IsBusy = true;
            StatusText = "Listening...";

            // The last identified track stays on screen - it's only ever replaced by a new
            // match (RecognitionSucceeded below), never cleared just because listening started.
        });

        _coordinator.RecognitionSucceeded += (_, result) => RunOnUi(() =>
        {
            IsBusy = false;
            ResultTitle = result.Title;
            ResultArtist = result.Artist;
            CoverArtUrl = result.CoverArtUrl;
            HasResult = true;
            StatusText = string.Empty; // artist/title are already shown prominently above

            _ = LoadLyricsAsync(result.Artist, result.Title);
        });

        _coordinator.RecognitionNoMatch += (_, _) => RunOnUi(() =>
        {
            IsBusy = false;
            StatusText = "No match found.";
        });

        _coordinator.RecognitionFailed += (_, error) => RunOnUi(() =>
        {
            IsBusy = false;
            StatusText = $"Recognition failed: {error}";
        });

        // No status message here - this fires alongside AutoDisabledBySilence/AutoStoppedUnexpectedly,
        // which already set a more useful message; this just clears the busy flag those don't touch.
        _coordinator.RecognitionCancelled += (_, _) => RunOnUi(() => IsBusy = false);

        _coordinator.MicrophoneActiveChanged += (_, active) => RunOnUi(() =>
        {
            IsMicrophoneActive = active;
            if (!active)
            {
                CurrentDbFs = SilenceThresholdDbFs; // nothing to meter - show uncoloured
                _silenceStartUtc = null;
                SoundLevelTooltip = "No sound detected";
            }
        });

        _coordinator.LevelChanged += (_, dbFs) => RunOnUi(() => CurrentDbFs = dbFs);
        _coordinator.ShazamQueryActiveChanged += (_, active) => RunOnUi(() => IsQueryingShazam = active);

        _coordinator.AutoDisabledBySilence += (_, _) => RunOnUi(() =>
        {
            IsAutoShazamEnabled = false; // triggers OnIsAutoShazamEnabledChanged, which stops capture
            StatusText = $"Auto Shazam turned off after {FormatTimeout(ExtendedSilenceTimeoutSeconds)} of silence.";
        });

        _coordinator.AutoStoppedUnexpectedly += (_, error) => RunOnUi(() =>
        {
            IsAutoShazamEnabled = false;
            StatusText = $"Auto Shazam stopped: {error}";
        });
    }

    partial void OnSelectedMicrophoneChanged(AudioDeviceOption? value)
    {
        Settings.SelectedMicrophoneDeviceId = value?.Id;
        _settingsService.Save(Settings);
        _coordinator.SetDevice(value?.Id);
    }

    partial void OnIsAutoShazamEnabledChanged(bool value)
    {
        if (value)
        {
            try
            {
                _coordinator.SetAutoEnabled(true);
                StatusText = "Auto Shazam is on — listening for track changes.";
            }
            catch (Exception ex)
            {
                // Revert without recursing back through this same handler.
                isAutoShazamEnabled = false;
                OnPropertyChanged(nameof(IsAutoShazamEnabled));
                StatusText = $"Couldn't start Auto Shazam: {ex.Message}";
            }
        }
        else
        {
            _coordinator.SetAutoEnabled(false);
            StatusText = "Auto Shazam is off.";
        }

        ShazamCommand.NotifyCanExecuteChanged(); // the manual button is disabled while auto mode is on
    }

    partial void OnIsBusyChanged(bool value)
    {
        ShazamCommand.NotifyCanExecuteChanged();
    }

    partial void OnCurrentDbFsChanged(double value)
    {
        bool raw = value > SilenceThresholdDbFs;
        var now = DateTime.UtcNow;
        if (raw != _rawSoundDetected)
        {
            _rawSoundDetected = raw;
            _rawSoundStateChangedUtc = now;
        }

        bool settled = now - _rawSoundStateChangedUtc >= TimeSpan.FromMilliseconds(Math.Max(0, SoundStateDebounceMs));
        if (settled)
        {
            IsSoundDetected = _rawSoundDetected;
        }

        if (IsSoundDetected)
        {
            _silenceStartUtc = null;
            SoundLevelTooltip = "Sound detected";
            return;
        }

        _silenceStartUtc ??= DateTime.UtcNow;
        int elapsed = (int)(DateTime.UtcNow - _silenceStartUtc.Value).TotalSeconds;
        SoundLevelTooltip = elapsed > 0 ? $"No sound detected ({elapsed}s)" : "No sound detected";
    }

    partial void OnExtendedSilenceTimeoutSecondsChanged(double value)
    {
        Settings.ExtendedSilenceTimeoutSeconds = value;
        _settingsService.Save(Settings);
        _coordinator.SetExtendedSilenceTimeout(TimeSpan.FromSeconds(Math.Max(1, value)));
    }

    partial void OnSilenceThresholdDbFsChanged(double value)
    {
        Settings.SilenceThresholdDbFs = value;
        _settingsService.Save(Settings);
        _coordinator.SetSilenceThreshold(value);

        // Re-evaluate against the new threshold immediately rather than waiting for the next
        // audio callback, so the level icon/tooltip reflect the change right away.
        OnCurrentDbFsChanged(CurrentDbFs);
    }

    partial void OnSoundStateDebounceMsChanged(double value)
    {
        Settings.SoundStateDebounceMs = value;
        _settingsService.Save(Settings);
    }

    partial void OnAutomaticallyCheckForUpdatesChanged(bool value)
    {
        Settings.AutomaticallyCheckForUpdates = value;
        _settingsService.Save(Settings);
    }

    [RelayCommand(CanExecute = nameof(CanTriggerManual))]
    private Task ShazamAsync() => _coordinator.TriggerManualAsync();

    private bool CanTriggerManual() => !IsBusy && !IsAutoShazamEnabled;

    private static string FormatTimeout(double seconds)
        => seconds == 1 ? "1 second" : $"{seconds:0.#} seconds";

    // Guards against a slower lookup for an earlier match overwriting a newer one that resolved
    // first (or resolved not-found) - only the most recent request's result is ever applied.
    private async Task LoadLyricsAsync(string artist, string title)
    {
        int version = ++_lyricsRequestVersion;
        string? lyrics = await _lyricsService.GetLyricsAsync(artist, title);
        if (version != _lyricsRequestVersion)
        {
            return;
        }

        RunOnUi(() =>
        {
            LyricsText = lyrics;
            HasLyrics = lyrics is not null;
        });
    }

    [RelayCommand]
    private void ShowLyrics()
    {
        if (_lyricsWindow is not null)
        {
            _lyricsWindow.Activate();
            return;
        }

        _lyricsWindow = new LyricsWindow { Owner = Application.Current.MainWindow, DataContext = this };
        _lyricsWindow.Closed += (_, _) => _lyricsWindow = null;
        _lyricsWindow.Show();
    }

    /// <summary>Silent unless an update is actually found - used for the automatic startup check.</summary>
    public async Task CheckForUpdatesOnStartupAsync()
    {
        if (!AutomaticallyCheckForUpdates)
        {
            return;
        }

        var update = await _updateService.CheckForUpdatesAsync();
        if (update is not null)
        {
            PromptToInstallUpdate(update);
        }
    }

    /// <summary>Always reports something - used for the "Check for Update" menu item.</summary>
    public async Task CheckForUpdatesManuallyAsync()
    {
        if (!_updateService.IsInstalled)
        {
            RunOnUi(() => MessageDialog.ShowInfo(
                Application.Current.MainWindow,
                "Check for Update",
                "Update checks are only available in an installed release build, not this local/dev build."));
            return;
        }

        var update = await _updateService.CheckForUpdatesAsync();
        if (update is null)
        {
            RunOnUi(() => MessageDialog.ShowInfo(
                Application.Current.MainWindow,
                "Check for Update",
                $"You're up to date (version {ReleaseInfo.GetVersion()})."));
            return;
        }

        PromptToInstallUpdate(update);
    }

    private void PromptToInstallUpdate(UpdateInfo update)
    {
        RunOnUi(() =>
        {
            bool confirmed = MessageDialog.ShowConfirm(
                Application.Current.MainWindow,
                "Update available",
                $"AutoShazam {update.TargetFullRelease.Version} is available. Download and install now?");

            if (confirmed)
            {
                _ = DownloadAndApplyUpdateAsync(update);
            }
        });
    }

    private async Task DownloadAndApplyUpdateAsync(UpdateInfo update)
    {
        try
        {
            RunOnUi(() => StatusText = "Downloading update...");
            await _updateService.DownloadAndApplyAsync(
                update,
                percent => RunOnUi(() => StatusText = $"Downloading update... {percent}%"));
        }
        catch (Exception ex)
        {
            RunOnUi(() => StatusText = $"Update failed: {ex.Message}");
        }
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            // BeginInvoke (not Invoke): events like ExtendedSilenceDetected are raised from the
            // microphone's own capture callback thread, and the handler here can end up calling
            // back into MicrophoneCaptureService.StopContinuous(), which needs that same capture
            // thread to exit. A blocking Invoke would deadlock (UI thread waiting on the capture
            // thread to stop; capture thread waiting inside Invoke for the UI thread to finish).
            dispatcher.BeginInvoke(action);
        }
    }

    public void Dispose()
    {
        _coordinator.Dispose();
        _lyricsService.Dispose();
    }
}
