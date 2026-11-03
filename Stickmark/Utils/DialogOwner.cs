using System.Linq;
using System.Windows;
using Microsoft.Win32;

namespace Stickmark.Utils;

/// If you launch a file/folder dialog without an owner from the tray context menu (especially in .NET 10)
/// <see cref="OpenFolderDialog"/>) There is a problem of "appearing and immediately disappearing" attached to the closing menu pop-up.
/// Always attach a live owner window to ensure stable display — if no window is visible (all notes are
/// Hidden pure tray state) Create a temporary transparent owner window in the center of the screen and use it as the owner.
public static class DialogOwner
{
    public static bool? Show(CommonDialog dialog)
    {
        var owner = Application.Current?.Windows.OfType<Window>()
            .FirstOrDefault(w => w.IsVisible && w.IsLoaded);
        if (owner is not null) return dialog.ShowDialog(owner);

        // When there are no visible windows: Set up a 1x1 transparent window in the center of the screen as the owner (in the user's eyes)
        // Although it is not visible, there is an actual HWND to which the dialog box will be attached, so it does not disappear and appears in the center).
        var wa = SystemParameters.WorkArea;
        var transient = new Window
        {
            Width = 1,
            Height = 1,
            Left = wa.Left + wa.Width / 2,
            Top = wa.Top + wa.Height / 2,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = null,
            Opacity = 0,
            ShowActivated = false,
        };
        transient.Show();
        try { return dialog.ShowDialog(transient); }
        finally { transient.Close(); }
    }
}
