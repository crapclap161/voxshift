using VoxShift.Core.Lifecycle;

namespace VoxShift.Core.Config;

/// <summary>What clicking the X button of the main window does.</summary>
public enum CloseButtonAction
{
    /// <summary>Show the "What would you like to do with VoxShift?" dialog (default).</summary>
    Ask,

    /// <summary>Hide VoxShift into the system tray without asking.</summary>
    MinimizeToTray,

    /// <summary>Quit VoxShift without asking.</summary>
    Quit,
}

public static class CloseButtonActionExtensions
{
    /// <summary>The choice a saved preference makes on its own, or null when the user has to be asked.</summary>
    public static CloseChoice? ToChoice(this CloseButtonAction action) => action switch
    {
        CloseButtonAction.MinimizeToTray => CloseChoice.MinimizeToTray,
        CloseButtonAction.Quit => CloseChoice.Quit,
        _ => null,
    };

    /// <summary>The preference to store for a remembered dialog choice; Cancel is never remembered (null).</summary>
    public static CloseButtonAction? FromRememberedChoice(CloseChoice choice) => choice switch
    {
        CloseChoice.MinimizeToTray => CloseButtonAction.MinimizeToTray,
        CloseChoice.Quit => CloseButtonAction.Quit,
        _ => null,
    };
}
