using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using VoxShift.Audio.Soundboard;
using VoxShift.Core.Audio;
using VoxShift.Core.Config;
using VoxShift.Core.Logging;
using VoxShift.Core.Soundboard;

namespace VoxShift.ViewModels;

/// <summary>A soundboard collection in the selector.</summary>
public sealed record SoundboardItem(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// Soundboard section of the main window: collections, sound buttons, import (file picker and drag & drop),
/// playback state, volume and the collapsed/expanded state. All edits go through <see cref="SoundboardLibrary"/>
/// and are persisted immediately via the settings store. Files are referenced in place, never copied.
/// </summary>
public sealed class SoundboardViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly SoundboardLibrary _library;
    private readonly SoundboardPlayer _player;
    private readonly IDialogService _dialogs;
    private readonly Action _save;
    private readonly ILog _log;
    private readonly List<VoiceProgress> _progress = new();
    private SoundboardItem? _selectedBoard;
    private bool _isExpanded;
    private int _volume;
    private bool _isDragOver;
    private string _dropHint = string.Empty;
    private string? _message;
    private bool _messageIsError;
    private bool _anyPlaying;
    private int _playingCount;
    private bool _isEditMode;
    private int _gridColumns = 3;
    private bool _outputAvailable = true;
    private bool _monitorAvailable;
    private bool _monitorEnabled;
    private int _monitorVolume;
    private string _monitorStatus = string.Empty;
    private bool _monitorStatusIsWarning;
    private readonly Action<bool> _setMonitorEnabled;
    private readonly Action<int> _setMonitorVolume;
    private bool _suppressSelection;
    private int _missingCheckCountdown;

    public SoundboardViewModel(
        AppSettings settings,
        SoundboardPlayer player,
        IDialogService dialogs,
        Action save,
        ILog log,
        Action<bool>? setMonitorEnabled = null,
        Action<int>? setMonitorVolume = null)
    {
        _setMonitorEnabled = setMonitorEnabled ?? (_ => { });
        _setMonitorVolume = setMonitorVolume ?? (_ => { });
        _monitorEnabled = settings.SoundboardMonitorEnabled;
        _monitorVolume = settings.SoundboardMonitorVolumePercent;
        _setMonitorEnabled(_monitorEnabled);
        _setMonitorVolume(_monitorVolume);
        _settings = settings;
        _library = new SoundboardLibrary(settings);
        _player = player;
        _dialogs = dialogs;
        _save = save;
        _log = log;
        _isExpanded = settings.SoundboardDockExpanded;
        _volume = settings.SoundboardVolumePercent;
        _player.Mixer.Volume = VolumeMath.PercentToGain(_volume);

        ToggleExpandedCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
        ExpandCommand = new RelayCommand(() => IsExpanded = true);
        CollapseCommand = new RelayCommand(() => IsExpanded = false);
        ToggleEditModeCommand = new RelayCommand(() => IsEditMode = !IsEditMode);
        AddFilesCommand = new RelayCommand(AddFiles);
        NewBoardCommand = new RelayCommand(NewBoard);
        RenameBoardCommand = new RelayCommand(RenameBoard, () => _selectedBoard is not null);
        DeleteBoardCommand = new RelayCommand(DeleteBoard, () => _selectedBoard is not null);
        StopAllCommand = new RelayCommand(StopAll);
        DismissMessageCommand = new RelayCommand(() => Message = null);

        Reload();
    }

    public ObservableCollection<SoundboardItem> Boards { get; } = new();

    public ObservableCollection<SoundButtonViewModel> Sounds { get; } = new();

    public ICommand ToggleExpandedCommand { get; }

    public ICommand ExpandCommand { get; }

    public ICommand CollapseCommand { get; }

    /// <summary>Edit mode: clicking a sound opens its actions (rename, relink, remove) instead of playing it.</summary>
    public ICommand ToggleEditModeCommand { get; }

    public ICommand AddFilesCommand { get; }

    public ICommand NewBoardCommand { get; }

    public ICommand RenameBoardCommand { get; }

    public ICommand DeleteBoardCommand { get; }

    public ICommand StopAllCommand { get; }

    public ICommand DismissMessageCommand { get; }

    public static string SupportedFormatsText => SoundboardLibrary.SupportedFormatsText;

    public SoundboardItem? SelectedBoard
    {
        get => _selectedBoard;
        set
        {
            if (!Set(ref _selectedBoard, value) || _suppressSelection || value is null) return;
            _library.Select(value.Id);
            _save();
            LoadSounds();
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!Set(ref _isExpanded, value)) return;
            _settings.SoundboardDockExpanded = value;
            _settings.SoundboardExpanded = value;
            _save();
        }
    }

    /// <summary>Soundboard volume in percent (0–200), independent of the microphone/application volume.</summary>
    public int Volume
    {
        get => _volume;
        set
        {
            value = VolumeMath.ClampPercent(value);
            if (!Set(ref _volume, value)) return;
            _player.Mixer.Volume = VolumeMath.PercentToGain(value);
            _settings.SoundboardVolumePercent = value;
            _save();
        }
    }

    /// <summary>Hear soundboard sounds yourself (played on your Windows default playback device).</summary>
    public bool MonitorEnabled
    {
        get => _monitorEnabled;
        set
        {
            if (!Set(ref _monitorEnabled, value)) return;
            _settings.SoundboardMonitorEnabled = value;
            _setMonitorEnabled(value);
            _save();
        }
    }

    /// <summary>Volume in your own headphones (0–200 %), independent of the volume VRChat hears.</summary>
    public int MonitorVolume
    {
        get => _monitorVolume;
        set
        {
            value = VolumeMath.ClampPercent(value);
            if (!Set(ref _monitorVolume, value)) return;
            _settings.SoundboardMonitorVolumePercent = value;
            _setMonitorVolume(value);
            _save();
        }
    }

    /// <summary>Set from the engine status: whether the headphone monitor output is running.</summary>
    public bool MonitorAvailable
    {
        get => _monitorAvailable;
        set => Set(ref _monitorAvailable, value);
    }

    /// <summary>e.g. "You hear sounds on "Headphones (Arctis 7)"."</summary>
    public string MonitorStatus
    {
        get => _monitorStatus;
        set => Set(ref _monitorStatus, value);
    }

    public bool MonitorStatusIsWarning
    {
        get => _monitorStatusIsWarning;
        set => Set(ref _monitorStatusIsWarning, value);
    }

    /// <summary>True while files are dragged over the soundbar (drop-target highlight).</summary>
    public bool IsDragOver
    {
        get => _isDragOver;
        set => Set(ref _isDragOver, value);
    }

    public string DropHint
    {
        get => _dropHint;
        set => Set(ref _dropHint, value);
    }

    /// <summary>Inline feedback (import summary, missing files, playback problems).</summary>
    public string? Message
    {
        get => _message;
        private set => Set(ref _message, value);
    }

    public bool MessageIsError
    {
        get => _messageIsError;
        private set => Set(ref _messageIsError, value);
    }

    public bool AnyPlaying
    {
        get => _anyPlaying;
        private set => Set(ref _anyPlaying, value);
    }

    /// <summary>Number of different sounds playing right now.</summary>
    public int PlayingCount
    {
        get => _playingCount;
        private set => Set(ref _playingCount, value);
    }

    /// <summary>
    /// Edit mode (no right-click needed, e.g. with VR controllers): clicking a sound shows its actions menu.
    /// </summary>
    public bool IsEditMode
    {
        get => _isEditMode;
        set => Set(ref _isEditMode, value);
    }

    /// <summary>Columns of the large sound grid in VR Controller Mode (set by the window from its width).</summary>
    public int GridColumns
    {
        get => _gridColumns;
        set => Set(ref _gridColumns, Math.Clamp(value, 1, 8));
    }

    public bool HasBoards => Boards.Count > 0;

    public bool IsEmpty => Sounds.Count == 0;

    /// <summary>Header summary, e.g. "6 sounds · Memes".</summary>
    public string Summary
    {
        get
        {
            if (_selectedBoard is null) return "Drop audio files here to get started";
            string count = Sounds.Count == 1 ? "1 sound" : $"{Sounds.Count} sounds";
            return $"{count} · {_selectedBoard.Name}";
        }
    }

    /// <summary>Set by the main view model from the engine status (virtual cable running or not).</summary>
    public bool OutputAvailable
    {
        get => _outputAvailable;
        set => Set(ref _outputAvailable, value);
    }

    /// <summary>True if any path is a supported audio file or a folder (used while dragging).</summary>
    public static int CountImportable(IEnumerable<string> paths) =>
        paths.Count(p => SoundboardLibrary.IsSupportedFile(p) || Directory.Exists(p));

    /// <summary>Adds files (drag & drop or picker). Creates the first soundboard automatically if needed.</summary>
    public void ImportFiles(IEnumerable<string> paths)
    {
        var list = paths.ToList();
        if (list.Count == 0) return;
        ImportResult result;
        try
        {
            result = _library.Import(list);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowMessage("Could not read the dropped files: " + ex.Message, error: true);
            return;
        }

        _save();
        Reload();
        if (result.Added.Count > 0)
        {
            IsExpanded = true;
            _player.Preload(result.Added.Select(a => a.FilePath));
        }

        string prefix = result.CreatedCollection ? $"Created \"{result.Collection?.Name}\". " : string.Empty;
        ShowMessage(prefix + result.Summary, error: result.Added.Count == 0 || result.HasProblems);
        _log.Info($"Soundboard import: {result.Added.Count} added, {result.Duplicates.Count} duplicates, {result.Unsupported.Count} unsupported, {result.Missing.Count} missing.");
    }

    /// <summary>Called ~30 times per second by the UI timer: playing state, progress and missing-file checks.</summary>
    public void UpdatePlayback()
    {
        _player.Mixer.CopyProgress(_progress);
        bool any = false;
        int count = 0;
        foreach (var sound in Sounds)
        {
            double progress = -1;
            foreach (var p in _progress)
            {
                if (p.SoundId == sound.Id && p.Progress > progress) progress = p.Progress;
            }

            sound.IsPlaying = progress >= 0;
            sound.Progress = Math.Max(0, progress);
            any |= sound.IsPlaying;
            if (sound.IsPlaying) count++;
        }

        AnyPlaying = any;
        PlayingCount = count;

        if (--_missingCheckCountdown <= 0)
        {
            _missingCheckCountdown = 90; // ~3 s
            foreach (var sound in Sounds) sound.IsMissing = !File.Exists(sound.FilePath);
        }
    }

    internal async Task PlayAsync(SoundButtonViewModel sound)
    {
        if (sound.IsPlaying)
        {
            _player.Stop(sound.Id);
            return;
        }

        await StartAsync(sound);
    }

    internal async Task StartAsync(SoundButtonViewModel sound)
    {
        if (!File.Exists(sound.FilePath))
        {
            sound.IsMissing = true;
            ShowMessage($"\"{sound.Name}\" can't be found at its original location. Use Edit (or right-click it) and choose Relink file…", error: true);
            return;
        }

        sound.IsMissing = false;
        if (!OutputAvailable && !MonitorAvailable)
        {
            ShowMessage("No output is running: the virtual microphone (VRChat) isn't available and monitoring is off or has no device.", error: true);
            return;
        }

        sound.IsLoading = true;
        try
        {
            var result = await _player.PlayAsync(sound.Id, sound.FilePath);
            switch (result.Status)
            {
                case PlayStatus.Missing:
                    sound.IsMissing = true;
                    ShowMessage($"\"{sound.Name}\" can't be found. Use Edit (or right-click it) and choose Relink file…", error: true);
                    break;
                case PlayStatus.Failed:
                    ShowMessage(result.Message ?? "Playback failed.", error: true);
                    break;
                default:
                    if (!OutputAvailable)
                    {
                        ShowMessage("Playing only in your headphones: VRChat can't hear it because the virtual microphone isn't running.", error: true);
                    }
                    else if (MessageIsError)
                    {
                        Message = null;
                    }

                    break;
            }
        }
        finally
        {
            sound.IsLoading = false;
        }
    }

    internal void Stop(SoundButtonViewModel sound) => _player.Stop(sound.Id);

    internal void Rename(SoundButtonViewModel sound)
    {
        if (_selectedBoard is null) return;
        string? name = _dialogs.Prompt("Rename sound", "Name shown on the button:", sound.Name);
        if (name is null || !_library.RenameSound(_selectedBoard.Id, sound.Id, name)) return;
        _save();
        sound.Name = _library.FindSound(_selectedBoard.Id, sound.Id)?.Name ?? name;
    }

    internal void Relink(SoundButtonViewModel sound)
    {
        if (_selectedBoard is null) return;
        var picked = _dialogs.PickAudioFiles($"Locate \"{sound.Name}\"", multiple: false);
        if (picked.Count == 0) return;
        string oldPath = sound.FilePath;
        var result = _library.Relink(_selectedBoard.Id, sound.Id, picked[0]);
        switch (result)
        {
            case RelinkResult.Ok:
                _save();
                _player.Invalidate(oldPath);
                sound.FilePath = _library.FindSound(_selectedBoard.Id, sound.Id)!.FilePath;
                sound.IsMissing = false;
                _player.Preload([sound.FilePath]);
                ShowMessage($"\"{sound.Name}\" now plays {Path.GetFileName(sound.FilePath)}.", error: false);
                break;
            case RelinkResult.Duplicate:
                ShowMessage("That file is already on this soundboard.", error: true);
                break;
            case RelinkResult.Unsupported:
                ShowMessage("Unsupported format. Supported: " + SupportedFormatsText + ".", error: true);
                break;
            default:
                ShowMessage("The selected file could not be found.", error: true);
                break;
        }
    }

    internal void Remove(SoundButtonViewModel sound)
    {
        if (_selectedBoard is null) return;
        _player.Stop(sound.Id);
        if (!_library.RemoveSound(_selectedBoard.Id, sound.Id)) return;
        _save();
        Sounds.Remove(sound);
        RaiseListChanged();
        ShowMessage($"Removed \"{sound.Name}\" from the soundboard. The file itself was not touched.", error: false);
    }

    internal void ShowInExplorer(SoundButtonViewModel sound) => _dialogs.ShowInExplorer(sound.FilePath);

    private void AddFiles()
    {
        var files = _dialogs.PickAudioFiles("Add sounds", multiple: true);
        if (files.Count > 0) ImportFiles(files);
    }

    private void NewBoard()
    {
        string? name = _dialogs.Prompt("New soundboard", "Name:", NextBoardName());
        if (name is null) return;
        _library.CreateCollection(name);
        _save();
        Reload();
        IsExpanded = true;
    }

    private void RenameBoard()
    {
        if (_selectedBoard is null) return;
        string? name = _dialogs.Prompt("Rename soundboard", "Name:", _selectedBoard.Name);
        if (name is null || !_library.RenameCollection(_selectedBoard.Id, name)) return;
        _save();
        Reload();
    }

    private void DeleteBoard()
    {
        if (_selectedBoard is null) return;
        int count = Sounds.Count;
        string detail = count == 0 ? "It is empty." : $"Its {count} button(s) will be removed. Your audio files stay where they are.";
        if (!_dialogs.Confirm("Delete soundboard", $"Delete \"{_selectedBoard.Name}\"? {detail}")) return;
        foreach (var s in Sounds) _player.Stop(s.Id);
        _library.DeleteCollection(_selectedBoard.Id);
        _save();
        Reload();
    }

    private void StopAll() => _player.StopAll();

    private string NextBoardName()
    {
        string name = SoundboardLibrary.DefaultCollectionName;
        for (int i = 2; Boards.Any(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)); i++) name = $"{SoundboardLibrary.DefaultCollectionName} {i}";
        return name;
    }

    private void ShowMessage(string text, bool error)
    {
        MessageIsError = error;
        Message = text;
    }

    /// <summary>Rebuilds the collection list and the buttons of the selected collection.</summary>
    private void Reload()
    {
        _suppressSelection = true;
        try
        {
            Boards.Clear();
            foreach (var board in _library.Collections) Boards.Add(new SoundboardItem(board.Id, board.Name));
            var selected = _library.Selected;
            SelectedBoard = selected is null ? null : Boards.FirstOrDefault(b => b.Id == selected.Id);
        }
        finally
        {
            _suppressSelection = false;
        }

        LoadSounds();
        OnPropertyChanged(nameof(HasBoards));
    }

    private void LoadSounds()
    {
        Sounds.Clear();
        var board = _selectedBoard is null ? null : _library.Find(_selectedBoard.Id);
        if (board is not null)
        {
            foreach (var entry in board.Sounds)
            {
                Sounds.Add(new SoundButtonViewModel(this, entry.Id, entry.Name, entry.FilePath) { IsMissing = !_library.FileExists(entry) });
            }

            _player.Preload(board.Sounds.Select(s => s.FilePath));
        }

        RaiseListChanged();
    }

    private void RaiseListChanged()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Summary));
    }

    /// <summary>Documentation screenshots only: sample collections and a deterministic visual state.</summary>
    internal void ApplyPreview(IReadOnlyList<SoundboardItem> boards, IReadOnlyList<(string Name, double Progress, bool Missing)> sounds, bool expanded, bool dragOver, string dropHint)
    {
        _suppressSelection = true;
        Boards.Clear();
        foreach (var b in boards) Boards.Add(b);
        SelectedBoard = Boards.FirstOrDefault();
        _suppressSelection = false;
        Sounds.Clear();
        foreach (var (name, progress, missing) in sounds)
        {
            Sounds.Add(new SoundButtonViewModel(this, Guid.NewGuid().ToString("N"), name, @"C:\Sounds\" + name + ".mp3")
            {
                IsPlaying = progress >= 0,
                Progress = Math.Max(0, progress),
                IsMissing = missing,
            });
        }

        _isExpanded = expanded;
        OnPropertyChanged(nameof(IsExpanded));
        AnyPlaying = sounds.Any(s => s.Progress >= 0);
        PlayingCount = sounds.Count(s => s.Progress >= 0);
        IsDragOver = dragOver;
        DropHint = dropHint;
        MonitorAvailable = true;
        MonitorStatus = "You hear sounds on \"Headphones (Valve Index)\".";
        MonitorStatusIsWarning = false;
        Message = null;
        OnPropertyChanged(nameof(HasBoards));
        RaiseListChanged();
    }
}

/// <summary>One sound button.</summary>
public sealed class SoundButtonViewModel : ObservableObject
{
    private string _name;
    private string _filePath;
    private bool _isPlaying;
    private double _progress;
    private bool _isMissing;
    private bool _isLoading;

    public SoundButtonViewModel(SoundboardViewModel owner, string id, string name, string filePath)
    {
        Id = id;
        _name = name;
        _filePath = filePath;
        PlayCommand = new RelayCommand(async () => await owner.PlayAsync(this));
        PlayAgainCommand = new RelayCommand(async () => await owner.StartAsync(this));
        StopCommand = new RelayCommand(() => owner.Stop(this));
        RenameCommand = new RelayCommand(() => owner.Rename(this));
        RelinkCommand = new RelayCommand(() => owner.Relink(this));
        RemoveCommand = new RelayCommand(() => owner.Remove(this));
        ShowInExplorerCommand = new RelayCommand(() => owner.ShowInExplorer(this), () => !IsMissing);
    }

    public string Id { get; }

    public string Name
    {
        get => _name;
        set
        {
            if (Set(ref _name, value)) OnPropertyChanged(nameof(ToolTip));
        }
    }

    public string FilePath
    {
        get => _filePath;
        set
        {
            if (Set(ref _filePath, value)) OnPropertyChanged(nameof(ToolTip));
        }
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        set => Set(ref _isPlaying, value);
    }

    /// <summary>0..1 playback position of the furthest-along voice of this sound.</summary>
    public double Progress
    {
        get => _progress;
        set => Set(ref _progress, value);
    }

    public bool IsMissing
    {
        get => _isMissing;
        set
        {
            if (Set(ref _isMissing, value)) OnPropertyChanged(nameof(ToolTip));
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        set => Set(ref _isLoading, value);
    }

    public string ToolTip => IsMissing
        ? $"File not found: {FilePath}\nRight-click → Relink file…"
        : $"{FilePath}\nClick to play · click again to stop · Edit or right-click for more";

    public ICommand PlayCommand { get; }

    public ICommand PlayAgainCommand { get; }

    public ICommand StopCommand { get; }

    public ICommand RenameCommand { get; }

    public ICommand RelinkCommand { get; }

    public ICommand RemoveCommand { get; }

    public ICommand ShowInExplorerCommand { get; }
}
