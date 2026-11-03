namespace Stickmark.Models;

public sealed class AppSettings
{
    public bool AutoStartWithWindows { get; set; }
    public bool GlobalHotkeysEnabled { get; set; } = true;
    public string NewNoteHotkey { get; set; } = "Ctrl+Shift+N";
    public string NotesListHotkey { get; set; } = "Ctrl+Shift+L";
    public bool NotesHiddenAtExit { get; set; }
    public bool AutoCheckForUpdates { get; set; } = true;

    /// UI language selector. English is the only shipped UI language; "system" is kept for future localization.
    public string Language { get; set; } = "system";

    /// Storage: "litedb" (default, built-in DB) or "vault" (.md set of folders). Changes take effect after restart.
    public string StorageMode { get; set; } = "litedb";

    /// Absolute path to the folder where notes are saved when in vault mode.
    public string? VaultPath { get; set; }

    /// Whether to hide completed items in the To Do tab. Persistent to keep the toggle state across reruns.
    public bool TodoHideCompleted { get; set; }
}
