using System;
using System.Threading.Tasks;
using Stickmark.Models;
using Stickmark.Views;

namespace Stickmark.Services;

public interface IWindowManager
{
    Task RestoreAllAsync();
    Task ReloadAsync();
    NoteWindow CreateAndShowNew();

    /// Creates and displays a new note pre-filled with the built-in template.
    NoteWindow CreateAndShowNew(NoteTemplate template);

    /// Opens an external .md/text file (or moves an already opened linked window to the front) and renders it.
    /// Note edits are saved in the original file, and changes outside the file are reflected as notes.
    Task<NoteWindow?> OpenFileAsync(string path);

    void ShowAll();
    void HideAll();
    void ToggleAllVisible();
    void OpenNotesList();
    void OpenSettings();
    bool FocusNoteByTitle(string title);
    bool FocusNoteById(Guid id);

    /// Reflects the external change diff calculated by the vault watch in detail in the open window (only called from the UI thread).
    Task ApplyVaultDiffAsync(VaultDiff diff);

    /// If there is an open window, returns the latest content (including those not yet saved by debounce) of that window's VM.
    bool TryGetLiveNoteContent(Guid id, out string content, out Models.NoteContentFormat format);

    /// Inject new content into the editor/VM of the open window (causing a debounce save). false if there is no window.
    Task<bool> TryUpdateLiveNoteContentAsync(Guid id, string newContent);

    /// Opens the quick switcher (Ctrl+P) pop-up.
    void OpenQuickSwitcher();

    /// Opens the note list window as the 'To Do' tab.
    void OpenTodoView();
}
