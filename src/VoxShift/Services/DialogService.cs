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
    private static Window? Owner => Application.Current?.MainWindow is { IsVisible: true } w ? w : null;

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
        var window = new TextPromptWindow(title, label, initialText) { Owner = Owner };
        return window.ShowDialog() == true ? window.Value : null;
    }

    public bool Confirm(string title, string message) =>
        MessageBox.Show(Owner!, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.OK;

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
