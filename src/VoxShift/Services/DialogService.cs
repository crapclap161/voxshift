using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using VoxShift.Core.Soundboard;
using VoxShift.ViewModels;
using VoxShift.Views;

namespace VoxShift.Services;

/// <summary>WPF implementation of <see cref="IDialogService"/>.</summary>
public sealed class DialogService : IDialogService
{
    /// <summary>
    /// The window a dialog belongs to: the VoxShift window the user is working in (the full interface, or the VR
    /// Controller window while the main window is hidden). Null when no window is on screen, e.g. in the tray.
    /// </summary>
    private static Window? Owner
    {
        get
        {
            var app = Application.Current;
            if (app is null) return null;
            Window? fallback = null;
            foreach (Window window in app.Windows)
            {
                // The dock is an owned tool window; dialogs opened from it belong to its owner (the main window).
                if (!window.IsVisible || window is SoundboardDockWindow) continue;
                if (window.IsActive) return window;
                fallback ??= window;
            }

            return app.MainWindow is { IsVisible: true } main ? main : fallback;
        }
    }

    public IReadOnlyList<string> PickAudioFiles(string title, bool multiple)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = SoundboardLibrary.FileDialogFilter,
            Multiselect = multiple,
            CheckFileExists = true,
        };
        return dialog.ShowDialog(Owner) == true ? dialog.FileNames : Array.Empty<string>();
    }

    public string? Prompt(string title, string label, string initialText)
    {
        var window = new TextPromptWindow(title, label, initialText);
        if (Owner is { } owner) window.Owner = owner;
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return window.ShowDialog() == true ? window.Value : null;
    }

    public bool Confirm(string title, string message)
    {
        // MessageBox.Show(Window, …) throws for a null owner, so the owner-less overload is used when nothing is on screen.
        var result = Owner is { } owner
            ? MessageBox.Show(owner, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel)
            : MessageBox.Show(message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        return result == MessageBoxResult.OK;
    }

    public void ShowInExplorer(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception)
        {
            // Opening Explorer is a convenience only.
        }
    }
}
