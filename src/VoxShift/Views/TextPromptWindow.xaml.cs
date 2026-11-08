using System.Windows;
using System.Windows.Interop;
using VoxShift.Services;

namespace VoxShift.Views;

/// <summary>Small themed text input dialog (rename sound / soundboard).</summary>
public partial class TextPromptWindow : Window
{
    public TextPromptWindow(string title, string label, string initialText)
    {
        InitializeComponent();
        Title = title;
        LabelText.Text = label;
        Input.Text = initialText;
        SourceInitialized += (_, _) => WindowStyling.ApplyDarkChrome(new WindowInteropHelper(this).Handle, 0x14, 0x16, 0x1B);
        Loaded += (_, _) =>
        {
            Input.Focus();
            Input.SelectAll();
        };
    }

    public string Value => Input.Text.Trim();

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (Value.Length == 0)
        {
            Input.Focus();
            return;
        }

        DialogResult = true;
    }
}
