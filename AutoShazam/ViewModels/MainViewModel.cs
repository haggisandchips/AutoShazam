using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
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
    private readonly DispatcherTimer _lyricsSyncTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private IReadOnlyList<LyricLine>? _syncedLines;
    private double _matchOffsetSeconds;
    private DateTime _matchRecordingStartedUtc;

    /// <summary>How far ahead of a line's real timestamp the mini preview panel starts scrolling
    /// it into the middle slot - matched to the panel's own scroll-animation duration in
    /// MainWindow.xaml.cs, so the animation finishes right as the line becomes current.</summary>
    private static readonly TimeSpan LyricsPreviewLead = TimeSpan.FromMilliseconds(250);

    /// <summary>How far ahead of the very first line's timestamp the preview panel scrolls it into
    /// the bottom slot - longer than <see cref="LyricsPreviewLead"/> since otherwise the first line
    /// would sit there, looking "active", for however long the track's intro runs before anything
    /// is actually sung; before this, the panel stays fully blank.</summary>
    private static readonly TimeSpan LyricsPreviewFirstLineLead = TimeSpan.FromMilliseconds(500);

    /// <summary>The loaded (and, on selection change, mutated) persisted settings object; saved
    /// immediately on every change - see the On*Changed partial methods below.</summary>
    public AppSettings Settings { get; }

    /// <summary>True for a genuine Velopack install (the shipped release build), false for a local/dev run.</summary>
    public bool IsInstalled => _updateService.IsInstalled;

    /// <summary>Every known microphone, for the Settings "offered" checklist and the right-click
    /// picker on the microphone icon.</summary>
    public ObservableCollection<AudioDeviceItem> Microphones { get; } = new();

    /// <summary>Every known speaker/render device, for the Settings "offered" checklist and the
    /// right-click picker on the speaker icon.</summary>
    public ObservableCollection<AudioDeviceItem> Speakers { get; } = new();

    [ObservableProperty]
    private AudioDeviceItem? selectedMicrophoneDevice;

    [ObservableProperty]
    private AudioDeviceItem? selectedSpeakerDevice;

    [ObservableProperty]
    private AudioSourceKind activeSource;

    public bool IsMicrophoneSelected => ActiveSource == AudioSourceKind.Microphone;

    public bool IsSpeakerSelected => ActiveSource == AudioSourceKind.Speaker;

    // Always starts false - Auto Shazam must default to off on every application start.
    [ObservableProperty]
    private bool isAutoShazamEnabled;

    [ObservableProperty]
    private bool isBusy;

    // Blank by default: Auto Shazam starts off and nothing is in flight, so there's nothing to say.
    [ObservableProperty]
    private string statusText = string.Empty;

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

    public ObservableCollection<LyricLineViewModel> SyncedLyricLines { get; } = new();

    [ObservableProperty]
    private bool hasSyncedLyrics;

    [ObservableProperty]
    private int currentLyricLineIndex = -1;

    /// <summary>Index of the line that should sit in the middle of the main window's mini lyrics
    /// preview panel - unlike <see cref="CurrentLyricLineIndex"/> (which flips exactly on time and
    /// drives highlighting), this advances <see cref="LyricsPreviewLead"/> early so the panel's
    /// scroll animation has time to settle by the moment a line is genuinely current. -2 is the
    /// "nothing due yet" reset state (panel fully blank) - -1 means the first line has scrolled
    /// into the bottom slot but isn't current yet.</summary>
    [ObservableProperty]
    private int previewLyricLineIndex = -2;

    /// <summary>True when only plain-text lyrics are available - drives the fallback (non-synced)
    /// lyrics view, since <see cref="HasLyrics"/> alone is also true for synced results.</summary>
    public bool ShowPlainLyrics => HasLyrics && !HasSyncedLyrics;

    [ObservableProperty]
    private bool isQueryingShazam;

    [ObservableProperty]
    private double currentDbFs = AudioLevelConstants.GlowFloorDbFs;

    [ObservableProperty]
    private string soundLevelTooltip = "Input level";

    [ObservableProperty]
    private bool automaticallyCheckForUpdates;

    public MainViewModel(AppSettings settings, AppUpdateService updateService, SettingsService settingsService, string appDataRoot)
    {
        Settings = settings;
        _updateService = updateService;
        _settingsService = settingsService;
        _coordinator = new RecognitionCoordinator(appDataRoot);

        var microphoneOptions = _deviceService.GetCaptureDevices();
        var speakerOptions = _deviceService.GetRenderDevices();

        // Seed "offered" with every device found on first run (or whenever nothing has been
        // curated yet) rather than leaving the set empty. GetOfferedDevices/CreateDeviceItem
        // treat an empty set as "offer everything" for display purposes, but that convention
        // breaks the moment the user unchecks a single device: removing an id that was never
        // actually in the set is a no-op, so the uncheck silently fails to persist. Seeding here
        // makes every subsequent check/uncheck a normal, unambiguous set membership change.
        if (Settings.OfferedDeviceIds.Count == 0)
        {
            foreach (var option in microphoneOptions.Concat(speakerOptions))
            {
                Settings.OfferedDeviceIds.Add(option.Id);
            }

            _settingsService.Save(Settings);
        }

        foreach (var device in microphoneOptions)
        {
            Microphones.Add(CreateDeviceItem(device));
        }

        foreach (var device in speakerOptions)
        {
            Speakers.Add(CreateDeviceItem(device));
        }

        selectedMicrophoneDevice = Microphones.FirstOrDefault(d => d.Id == settings.SelectedMicrophoneDeviceId)
            ?? Microphones.FirstOrDefault();
        selectedSpeakerDevice = Speakers.FirstOrDefault(d => d.Id == settings.SelectedSpeakerDeviceId)
            ?? Speakers.FirstOrDefault();

        // The two lines above bypass the generated property setters (direct field assignment, so
        // OnSelected*DeviceChanged never runs) - if that left a freshly-picked default that isn't
        // what's on disk yet (typically: nothing was ever explicitly selected before), persist it
        // now rather than leaving Settings pointing at a device that's only an implicit fallback.
        if (Settings.SelectedMicrophoneDeviceId != selectedMicrophoneDevice?.Id
            || Settings.SelectedSpeakerDeviceId != selectedSpeakerDevice?.Id)
        {
            Settings.SelectedMicrophoneDeviceId = selectedMicrophoneDevice?.Id;
            Settings.SelectedSpeakerDeviceId = selectedSpeakerDevice?.Id;
            _settingsService.Save(Settings);
        }

        activeSource = settings.ActiveAudioSource;

        automaticallyCheckForUpdates = settings.AutomaticallyCheckForUpdates;

        _lyricsSyncTimer.Tick += (_, _) => UpdateCurrentLyricLine();

        _coordinator.RecognitionStarted += (_, _) => RunOnUi(() =>
        {
            IsBusy = true;
            StatusText = "Listening...";

            // The last identified track stays on screen - it's only ever replaced by a new
            // match (RecognitionSucceeded below), never cleared just because a check started.
        });

        _coordinator.RecognitionSucceeded += (_, result) => RunOnUi(() =>
        {
            IsBusy = false;

            // Recognition runs periodically and re-confirms whatever's still playing - reloading
            // lyrics on every one of those would flash-rebuild the list for no reason, so that
            // part stays gated on it genuinely being a different track.
            bool isNewTrack = !string.Equals(ResultArtist, result.Artist, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(ResultTitle, result.Title, StringComparison.OrdinalIgnoreCase);

            ResultTitle = result.Title;
            ResultArtist = result.Artist;
            CoverArtUrl = result.CoverArtUrl;
            HasResult = true;
            StatusText = string.Empty; // artist/title are already shown prominently above

            // Timing, though, is resynced on every match, same track or not: a re-confirmation
            // still carries a real, current playback position from Shazam, and trusting it - not
            // just extrapolating from whenever the track was first matched - is what lets the
            // lyrics notice the track being rewound/seeked instead of silently playing on from the
            // original position. A genuine no-match is ignored entirely (nothing to resync to);
            // repeated no-matches (e.g. to eventually decide the track has stopped) are a
            // separate concern for later.
            _matchOffsetSeconds = result.MatchOffsetSeconds;
            _matchRecordingStartedUtc = result.RecordingStartedUtc;

            if (isNewTrack)
            {
                _ = LoadLyricsAsync(result.Artist, result.Title);
            }
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

        _coordinator.RecognitionCancelled += (_, _) => RunOnUi(() =>
        {
            IsBusy = false;
            UpdateRestingStatusText();
        });

        _coordinator.SourceActiveChanged += (_, active) => RunOnUi(() =>
        {
            if (!active)
            {
                CurrentDbFs = AudioLevelConstants.GlowFloorDbFs; // nothing to meter - show uncoloured
            }
        });

        _coordinator.LevelChanged += (_, dbFs) => RunOnUi(() => CurrentDbFs = dbFs);

        _coordinator.ShazamQueryActiveChanged += (_, active) => RunOnUi(() =>
        {
            IsQueryingShazam = active;
            if (active)
            {
                StatusText = "Shazaming";
            }
        });

        // Fires every second while Auto Shazam is on and nothing is in flight - settles the
        // status line on "Idle" rather than leaving the previous outcome message shown until
        // the next check, potentially a long wait away.
        _coordinator.Idle += (_, _) => RunOnUi(UpdateRestingStatusText);

        _coordinator.AutoStoppedUnexpectedly += (_, error) => RunOnUi(() =>
        {
            IsAutoShazamEnabled = false;
            StatusText = $"Auto Shazam stopped: {error}";
        });

        _coordinator.SetSource(ActiveSource, CurrentDeviceId());
    }

    private AudioDeviceItem CreateDeviceItem(AudioDeviceOption option)
    {
        var item = new AudioDeviceItem(option, Settings.OfferedDeviceIds.Contains(option.Id));
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(AudioDeviceItem.IsOffered))
            {
                return;
            }

            if (item.IsOffered)
            {
                Settings.OfferedDeviceIds.Add(item.Id);
            }
            else
            {
                Settings.OfferedDeviceIds.Remove(item.Id);
            }

            _settingsService.Save(Settings);
        };
        return item;
    }

    private string? CurrentDeviceId()
        => ActiveSource == AudioSourceKind.Microphone ? SelectedMicrophoneDevice?.Id : SelectedSpeakerDevice?.Id;

    /// <summary>Devices to show in the right-click picker for <paramref name="kind"/> - only the
    /// ones marked "offered" in Settings, or every device of that kind if none have been curated.</summary>
    public IReadOnlyList<AudioDeviceItem> GetOfferedDevices(AudioSourceKind kind)
    {
        var source = kind == AudioSourceKind.Microphone ? Microphones : Speakers;
        var offered = source.Where(d => d.IsOffered).ToList();
        return offered.Count > 0 ? offered : source.ToList();
    }

    [RelayCommand]
    private void SelectMicrophone() => ActiveSource = AudioSourceKind.Microphone;

    [RelayCommand]
    private void SelectSpeaker() => ActiveSource = AudioSourceKind.Speaker;

    /// <summary>Called from the right-click picker - picking a device also switches to its source,
    /// since choosing one implies you want to use it now.</summary>
    public void PickMicrophoneDevice(AudioDeviceItem device)
    {
        SelectedMicrophoneDevice = device;
        ActiveSource = AudioSourceKind.Microphone;
    }

    public void PickSpeakerDevice(AudioDeviceItem device)
    {
        SelectedSpeakerDevice = device;
        ActiveSource = AudioSourceKind.Speaker;
    }

    partial void OnSelectedMicrophoneDeviceChanged(AudioDeviceItem? value)
    {
        Settings.SelectedMicrophoneDeviceId = value?.Id;
        _settingsService.Save(Settings);
        if (ActiveSource == AudioSourceKind.Microphone)
        {
            _coordinator.SetSource(AudioSourceKind.Microphone, value?.Id);
        }
    }

    partial void OnSelectedSpeakerDeviceChanged(AudioDeviceItem? value)
    {
        Settings.SelectedSpeakerDeviceId = value?.Id;
        _settingsService.Save(Settings);
        if (ActiveSource == AudioSourceKind.Speaker)
        {
            _coordinator.SetSource(AudioSourceKind.Speaker, value?.Id);
        }
    }

    partial void OnActiveSourceChanged(AudioSourceKind value)
    {
        Settings.ActiveAudioSource = value;
        _settingsService.Save(Settings);
        _coordinator.SetSource(value, CurrentDeviceId());
        OnPropertyChanged(nameof(IsMicrophoneSelected));
        OnPropertyChanged(nameof(IsSpeakerSelected));
    }

    partial void OnIsAutoShazamEnabledChanged(bool value)
    {
        if (value)
        {
            try
            {
                _coordinator.SetAutoEnabled(true);
            }
            catch (Exception ex)
            {
                // Revert without recursing back through this same handler.
                isAutoShazamEnabled = false;
                OnPropertyChanged(nameof(IsAutoShazamEnabled));
                StatusText = $"Couldn't start Auto Shazam: {ex.Message}";
                ShazamCommand.NotifyCanExecuteChanged();
                return;
            }
        }
        else
        {
            _coordinator.SetAutoEnabled(false);
        }

        // The initial check (when turning on) may already have set a more specific status
        // ("Listening...") synchronously above - this only fills in when nothing more specific
        // is already showing.
        UpdateRestingStatusText();
        ShazamCommand.NotifyCanExecuteChanged(); // the manual button is disabled while auto mode is on
    }

    partial void OnIsBusyChanged(bool value)
    {
        ShazamCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The status line's fallback text for whenever nothing more specific (Listening.../
    /// Shazaming, or a just-finished outcome message) applies: "Idle" while Auto Shazam is on and
    /// waiting for its next check, blank otherwise.</summary>
    private void UpdateRestingStatusText()
    {
        if (IsBusy)
        {
            return;
        }

        StatusText = IsAutoShazamEnabled ? "Idle" : string.Empty;
    }

    [RelayCommand(CanExecute = nameof(CanTriggerManual))]
    private Task ShazamAsync() => _coordinator.TriggerManualAsync();

    private bool CanTriggerManual() => !IsBusy && !IsAutoShazamEnabled;

    [RelayCommand]
    private void ClearResult()
    {
        HasResult = false;
        ResultTitle = null;
        ResultArtist = null;
        CoverArtUrl = null;

        _syncedLines = null;
        SyncedLyricLines.Clear();
        CurrentLyricLineIndex = -1;
        PreviewLyricLineIndex = -2;
        HasSyncedLyrics = false;
        LyricsText = null;
        HasLyrics = false;
        _lyricsSyncTimer.Stop();
        _lyricsRequestVersion++; // invalidate any in-flight lyrics fetch for the track being cleared

        _lyricsWindow?.Close(); // its Closed handler clears _lyricsWindow and stops the sync timer
    }

    partial void OnHasLyricsChanged(bool value) => OnPropertyChanged(nameof(ShowPlainLyrics));

    partial void OnHasSyncedLyricsChanged(bool value) => OnPropertyChanged(nameof(ShowPlainLyrics));

    partial void OnCurrentDbFsChanged(double value) => SoundLevelTooltip = $"{value:F0} dBFS";

    partial void OnAutomaticallyCheckForUpdatesChanged(bool value)
    {
        Settings.AutomaticallyCheckForUpdates = value;
        _settingsService.Save(Settings);
    }

    // Guards against a slower lookup for an earlier match overwriting a newer one that resolved
    // first (or resolved not-found) - only the most recent request's result is ever applied.
    private async Task LoadLyricsAsync(string artist, string title)
    {
        int version = ++_lyricsRequestVersion;
        LyricsResult? result = await _lyricsService.GetLyricsAsync(artist, title);
        if (version != _lyricsRequestVersion)
        {
            return;
        }

        RunOnUi(() =>
        {
            _syncedLines = result?.SyncedLines;

            SyncedLyricLines.Clear();
            CurrentLyricLineIndex = -1;
            PreviewLyricLineIndex = -2;
            if (_syncedLines is not null)
            {
                foreach (var line in _syncedLines)
                {
                    SyncedLyricLines.Add(new LyricLineViewModel(line.Text));
                }
            }

            HasSyncedLyrics = _syncedLines is { Count: > 0 };
            LyricsText = result?.PlainText;
            HasLyrics = HasSyncedLyrics || result?.PlainText is not null;

            // Keeps running for as long as there's something to sync, regardless of whether the
            // separate Lyrics window is open - the main window's mini preview panel needs live
            // updates too.
            if (HasSyncedLyrics)
            {
                _lyricsSyncTimer.Start();
            }
            else
            {
                _lyricsSyncTimer.Stop();
            }
        });
    }

    private void UpdateCurrentLyricLine()
    {
        if (_syncedLines is not { Count: > 0 })
        {
            return;
        }

        var elapsed = TimeSpan.FromSeconds(_matchOffsetSeconds) + (DateTime.UtcNow - _matchRecordingStartedUtc);

        int index = FindLyricLineIndex(elapsed);
        if (index != CurrentLyricLineIndex)
        {
            if (CurrentLyricLineIndex >= 0 && CurrentLyricLineIndex < SyncedLyricLines.Count)
            {
                SyncedLyricLines[CurrentLyricLineIndex].IsCurrent = false;
            }

            if (index >= 0 && index < SyncedLyricLines.Count)
            {
                SyncedLyricLines[index].IsCurrent = true;
            }

            CurrentLyricLineIndex = index;
        }

        // Scroll the mini preview panel into position ahead of time - see LyricsPreviewLead. The
        // very first line gets a longer lead (and stays off entirely before that), so it doesn't
        // just sit there for however long the track's intro runs.
        int preview = FindLyricLineIndex(elapsed + LyricsPreviewLead);
        if (preview < 0 && elapsed + LyricsPreviewFirstLineLead < _syncedLines[0].Timestamp)
        {
            preview = -2;
        }

        PreviewLyricLineIndex = preview;
    }

    private int FindLyricLineIndex(TimeSpan elapsed)
    {
        int index = -1;
        for (int i = 0; i < _syncedLines!.Count; i++)
        {
            if (_syncedLines[i].Timestamp > elapsed)
            {
                break;
            }

            index = i;
        }

        return index;
    }

    [RelayCommand]
    private void ShowLyrics()
    {
        if (_lyricsWindow is not null)
        {
            _lyricsWindow.Activate();
            return;
        }

        // Not tied to the sync timer's start/stop - that now runs for as long as HasSyncedLyrics
        // is true, independent of this window, since the main window's mini preview panel needs
        // it too.
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
            // BeginInvoke (not Invoke): some coordinator events are raised from the capture
            // callback thread, and the handler here can end up calling back into
            // AudioCaptureService, which needs that same capture thread to exit. A blocking
            // Invoke would deadlock (UI thread waiting on the capture thread to stop; capture
            // thread waiting inside Invoke for the UI thread to finish).
            dispatcher.BeginInvoke(action);
        }
    }

    public void Dispose()
    {
        _lyricsSyncTimer.Stop();
        _coordinator.Dispose();
        _lyricsService.Dispose();
    }
}
