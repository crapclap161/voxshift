using System.Collections.Generic;

namespace VoxShift.ViewModels;

/// <summary>UI dialogs needed by view models (implemented with WPF in <c>Services/DialogService</c>).</summary>
public interface IDialogService
{
    /// <summary>Shows an "open file" dialog for audio files. Returns an empty list when cancelled.</summary>
    IReadOnlyList<string> PickAudioFiles(string title, bool multiple);

    /// <summary>Asks for a short text (e.g. a new name). Returns null when cancelled.</summary>
    string? Prompt(string title, string label, string initialText);

    bool Confirm(string title, string message);

    /// <summary>Opens Windows Explorer with the file selected.</summary>
    void ShowInExplorer(string path);
}
