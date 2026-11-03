using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Stickmark.Models;
using Stickmark.Resources;
using Stickmark.Utils;
using Stickmark.ViewModels;
using Stickmark.Views;

namespace Stickmark.Services;

public sealed class WindowManager : IWindowManager
{
    private readonly INoteRepository _repository;
    private readonly IServiceProvider _services;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<WindowManager> _logger;
    private readonly List<NoteWindow> _windows = new();
    private NotesListWindow? _notesListWindow;
    private SettingsWindow? _settingsWindow;

    public WindowManager(INoteRepository repository, IServiceProvider services, ILoggerFactory loggerFactory)
    {
        _repository = repository;
        _services = services;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<WindowManager>();
    }

    public async Task RestoreAllAsync()
    {
        var notes = await _repository.GetAllAsync().ConfigureAwait(true);

        if (notes.Count == 0)
        {
            CreateAndShowNew();
            return;
        }

        foreach (var note in notes)
        {
            var window = BuildWindow(note);
            if (!note.IsHidden) window.Show();
        }
    }

    public async Task ReloadAsync()
    {
        // Close every existing live window, then re-build from the database. Used after import/restore.
        foreach (var window in _windows.ToList()) window.RequestClose();
        _windows.Clear();
        await RestoreAllAsync().ConfigureAwait(true);
    }

    public NoteWindow CreateAndShowNew()
    {
        var note = new Note
        {
            X = 120 + _windows.Count * 24,
            Y = 120 + _windows.Count * 24,
            Width = 280,
            Height = 280,
            Color = (NoteColor)(_windows.Count % NotePalette.All.Count),
        };
        _ = _repository.UpsertAsync(note);
        var window = BuildWindow(note);
        window.Show();
        window.Activate();
        return window;
    }

    public NoteWindow CreateAndShowNew(NoteTemplate template)
    {
        // Clean up any empty notes that were automatically created on startup or haven't been touched yet — next to the template notes
        // So that there is not one more empty note left behind.
        DiscardEmptyPlaceholders();

        var content = template.Content.Replace("{{date}}", DateTime.Now.ToString("yyyy-MM-dd"));
        var plainText = TextExtraction.ToPlainText(content, template.Format);
        var note = new Note
        {
            X = 120 + _windows.Count * 24,
            Y = 120 + _windows.Count * 24,
            Width = 280,
            Height = 280,
            Color = template.Color,
            Content = content,
            Format = template.Format,
            PlainText = plainText,
            Title = TextExtraction.DeriveTitle(plainText),
            Tags = TextExtraction.ExtractTags(plainText),
        };
        _ = _repository.UpsertAsync(note);
        var window = BuildWindow(note);
        window.Show();
        window.Activate();
        return window;
    }

    public void ShowAll()
    {
        foreach (var w in _windows)
        {
            if (!w.IsVisible) w.Show();
        }
    }

    public void HideAll()
    {
        foreach (var w in _windows)
        {
            if (w.IsVisible) w.Hide();
        }
    }

    public void ToggleAllVisible()
    {
        if (_windows.Count == 0)
        {
            CreateAndShowNew();
            return;
        }
        if (_windows.Any(w => w.IsVisible)) HideAll(); else ShowAll();
    }

    public void OpenNotesList()
    {
        if (_notesListWindow is null)
        {
            var vm = _services.GetRequiredService<NotesListViewModel>();
            _notesListWindow = new NotesListWindow(vm);
        }
        _ = _notesListWindow.ShowAndReloadAsync();
    }

    public void OpenSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }
        var vm = _services.GetRequiredService<SettingsViewModel>();
        _settingsWindow = new SettingsWindow(vm);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    public async Task<NoteWindow?> OpenFileAsync(string path)
    {
        var full = LinkedFile.Normalize(path);
        if (string.IsNullOrEmpty(full)) return null;

        if (!File.Exists(full))
        {
            MessageBox.Show(string.Format(Strings.Note_FileNotFoundFormat, full), "Stickmark",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        // If a linked window is already open, move that window forward.
        var live = _windows.FirstOrDefault(w =>
            string.Equals(w.ViewModel.LinkedFilePath, full, StringComparison.OrdinalIgnoreCase));
        if (live is not null)
        {
            ShowAndActivate(live);
            return live;
        }

        string content;
        try
        {
            (content, _) = await LinkedFile.ReadAsync(full).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open linked file {Path}", full);
            MessageBox.Show(string.Format(Strings.Note_OpenFileError, ex.Message), "Stickmark",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        var format = LinkedFile.FormatFor(full);
        var fileName = Path.GetFileName(full);

        // If a note linked to the same file already exists in the DB, reuse it (since the file is the original, the contents are updated with the file).
        var note = await _repository.FindByLinkedPathAsync(full).ConfigureAwait(true);
        if (note is null)
        {
            note = new Note
            {
                X = 140 + _windows.Count * 24,
                Y = 140 + _windows.Count * 24,
                Width = 380,
                Height = 460,
                Color = NoteColor.Blue,
                LinkedFilePath = full,
            };
        }

        note.LinkedFilePath = full;
        note.Content = content;
        note.Format = format;
        note.PlainText = TextExtraction.ToPlainText(content, format);
        note.Title = fileName;
        note.Tags = TextExtraction.ExtractTags(note.PlainText);
        await _repository.UpsertAsync(note).ConfigureAwait(true);

        // The moment you open a file, clean up any empty placeholder notes (so that no more empty notes remain).
        DiscardEmptyPlaceholders();

        var window = BuildWindow(note);
        window.OpenInPreview = true;   // When opening .md, it is first displayed as a rendered screen.
        window.Show();
        window.Activate();
        return window;
    }

    public bool FocusNoteByTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;
        var live = _windows.FirstOrDefault(w =>
            string.Equals(w.ViewModel.Title, title.Trim(), StringComparison.OrdinalIgnoreCase));
        if (live is not null)
        {
            ShowAndActivate(live);
            return true;
        }

        var note = _repository.FindByTitleAsync(title).GetAwaiter().GetResult();
        if (note is null) return false;
        var window = BuildWindow(note);
        window.Show();
        window.Activate();
        return true;
    }

    public bool FocusNoteById(Guid id)
    {
        var live = _windows.FirstOrDefault(w => w.ViewModel.Id == id);
        if (live is not null)
        {
            ShowAndActivate(live);
            return true;
        }

        var note = _repository.GetByIdAsync(id).GetAwaiter().GetResult();
        if (note is null) return false;
        var window = BuildWindow(note);
        window.Show();
        window.Activate();
        return true;
    }

    /// The external change diff calculated by the vault monitor is reflected in detail in the open window (spec-5 §3.7).
    /// UI thread only — The watcher passes it to the Dispatcher and calls it. Unlike ReloadAsync (discard previous window)
    /// Since only the relevant note window is updated/created/organized, irrelevant windows do not blink.
    public async Task ApplyVaultDiffAsync(VaultDiff diff)
    {
        // Change: silently replace unsaved edits if not present, prompt to file first/keep my edits if present (or editing WYSIWYG).
        foreach (var (fresh, oldContent) in diff.Changed)
        {
            var win = _windows.FirstOrDefault(w => w.ViewModel.Id == fresh.Id);
            if (win is null) continue;   // If there is no window, just updating the cache is sufficient (up to date on next view)

            var dirty = win.IsWysiwygActive
                || !VaultContentEquals(win.ViewModel.Content, oldContent);
            if (dirty)
            {
                var choice = MessageBox.Show(win,
                    Strings.Note_FileChangedPrompt, Strings.Note_FileChangedTitle,
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
                if (choice != MessageBoxResult.Yes) continue;   // keep my edits → next save will overwrite the file
            }
            await win.ApplyExternalContentAsync(fresh.Content, fresh.Format).ConfigureAwait(true);
        }

        // Added: Only creates a window and does not show it (§2.1 — Prevent surprise pop-ups and window bombs). One notification was sent by a monitor.
        foreach (var note in diff.Added)
        {
            if (note.IsDeleted) continue;
            if (_windows.Any(w => w.ViewModel.Id == note.Id)) continue;
            BuildWindow(note);
        }

        // Delete: Quietly close if there are no unsaved edits (file is original). If there is a “Save it again?” prompt.
        foreach (var (id, oldContent) in diff.Removed)
        {
            var win = _windows.FirstOrDefault(w => w.ViewModel.Id == id);
            if (win is null) continue;
            var vm = win.ViewModel;

            var dirty = win.IsWysiwygActive
                || !VaultContentEquals(vm.Content, oldContent);
            if (dirty)
            {
                var choice = MessageBox.Show(win,
                    Strings.Vault_FileDeletedPrompt, Strings.Note_FileChangedTitle,
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
                if (choice == MessageBoxResult.Yes)
                {
                    // Maintain: SaveContentAsync ignores ids that are not in the cache, so re-register with Upsert —
                    // A new file is created and the note is returned to the vault.
                    vm.Note.Content = vm.Content;
                    vm.Note.Format = vm.Format;
                    vm.Note.PlainText = vm.PlainText;
                    vm.Note.Title = vm.Title;
                    vm.Note.Tags = TextExtraction.ExtractTags(vm.PlainText);
                    await _repository.UpsertAsync(vm.Note).ConfigureAwait(true);
                    continue;
                }
            }

            vm.SuspendFileSync();   // Closed so that flush does not touch the linked file
            win.RequestClose();
            _windows.Remove(win);
            vm.Dispose();
        }

        if (_notesListWindow?.IsVisible == true)
        {
            _ = _notesListWindow.ShowAndReloadAsync();
        }
    }

    public bool TryGetLiveNoteContent(Guid id, out string content, out NoteContentFormat format)
    {
        var win = _windows.FirstOrDefault(w => w.ViewModel.Id == id);
        if (win is null)
        {
            content = string.Empty;
            format = default;
            return false;
        }
        // During the debounce window (≤500ms), the DB is outdated — the live VM is up to date.
        content = win.ViewModel.Content;
        format = win.ViewModel.Format;
        return true;
    }

    public async Task<bool> TryUpdateLiveNoteContentAsync(Guid id, string newContent)
    {
        var win = _windows.FirstOrDefault(w => w.ViewModel.Id == id);
        if (win is null) return false;
        await win.ApplyLocalEditAsync(newContent).ConfigureAwait(true);
        return true;
    }

    public void OpenQuickSwitcher()
    {
        // Unlike the list window, it is created anew every time — the pop-up is lightweight, and the hidden cache only creates stale snapshot and focus problems.
        var vm = _services.GetRequiredService<QuickSwitcherViewModel>();
        var window = new QuickSwitcherWindow(vm);
        window.Show();
        window.Activate();
    }

    public void OpenTodoView()
    {
        if (_notesListWindow is null)
        {
            var vm = _services.GetRequiredService<NotesListViewModel>();
            _notesListWindow = new NotesListWindow(vm);
        }
        _ = _notesListWindow.ShowTodoTabAsync();
    }

    /// Comparison of content for unsaved edit judgment — Differences in end space and CRLF are not considered edits.
    /// (Vault serialization is the normalization part anyway, so it only creates a ghost prompt).
    private static bool VaultContentEquals(string? a, string? b) =>
        string.Equals(
            (a ?? string.Empty).Replace("\r\n", "\n").TrimEnd(),
            (b ?? string.Empty).Replace("\r\n", "\n").TrimEnd(),
            StringComparison.Ordinal);

    /// Clean up note windows without content (non-linked). If you start when there are no notes, it will automatically appear.
    /// Prevents empty placeholder notes from being left next to the user when the actual note is opened by opening a template/file.
    /// When closed, <see cref="OnWindowDismissed"/> purges/removes/disposes empty notes.
    private void DiscardEmptyPlaceholders()
    {
        foreach (var w in _windows.ToList())
        {
            if (w.ViewModel.IsLinkedFile || !w.ViewModel.IsEmpty) continue;
            w.RequestClose();
        }
    }

    private static void ShowAndActivate(NoteWindow window)
    {
        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private NoteWindow BuildWindow(Note note)
    {
        var corrected = MonitorHelper.EnsureOnScreen(note.X, note.Y, note.Width, note.Height);
        note.X = corrected.X;
        note.Y = corrected.Y;
        note.Width = corrected.Width;
        note.Height = corrected.Height;

        var vm = new NoteViewModel(
            note,
            _repository,
            _loggerFactory.CreateLogger<NoteViewModel>(),
            DeleteAsync);
        var window = new NoteWindow(vm, OnWindowDismissed, CreateAndShowNew, OpenNotesList, FocusNoteByTitle,
            path => OpenFileAsync(path));
        _windows.Add(window);
        return window;
    }

    private async Task DeleteAsync(NoteViewModel vm)
    {
        try
        {
            await _repository.DeleteAsync(vm.Id).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete note {NoteId}", vm.Id);
            return;
        }

        var window = _windows.Find(w => ReferenceEquals(w.ViewModel, vm));
        if (window is not null)
        {
            // When deleting a linked note, empty content is written to the original file to prevent accidents where the file becomes empty.
            // Only the notes (DB items) go to the trash and the original .md file remains.
            vm.SuspendFileSync();
            vm.UpdateContent(string.Empty, NoteContentFormat.PlainText);
            window.RequestClose();
        }

        if (_notesListWindow?.IsVisible == true)
        {
            _ = _notesListWindow.ShowAndReloadAsync();
        }
    }

    private async void OnWindowDismissed(NoteWindow window)
    {
        var vm = window.ViewModel;

        try
        {
            // Permanently delete empty notes immediately rather than sending them to the trash — leaving no trace.
            if (vm.IsEmpty && !window.IsVisible)
            {
                await _repository.PurgeAsync(vm.Id).ConfigureAwait(true);
                _windows.Remove(window);
                vm.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cleanup failed for note {NoteId}", vm.Id);
        }
    }
}
