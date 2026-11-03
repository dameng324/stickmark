using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Stickmark.Models;
using Stickmark.Resources;
using Stickmark.Services;
using Stickmark.Utils;

namespace Stickmark.ViewModels;

/// Tab of the note list window. Todos is an integrated to-do view (spec-2) that collects the checkboxes of all active notes.
public enum NoteListViewMode { Active, Trash, Todos }

public sealed partial class NotesListViewModel : ObservableObject
{
    private readonly INoteRepository _repository;
    private readonly IWindowManager _windowManager;
    private readonly IBackupService _backupService;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<NotesListViewModel> _logger;
    private readonly List<Note> _activeRaw = new();
    private readonly List<Note> _trashedRaw = new();
    private readonly ObservableCollection<NoteSummary> _items = new();
    private readonly ObservableCollection<TagFilter> _tags = new();

    /// Save the selection status for export as a note id — The selection is maintained even if the list is reorganized by search/filter.
    private readonly HashSet<Guid> _selectedIds = new();

    public ObservableCollection<NoteSummary> Items => _items;
    public IReadOnlyList<TagFilter> Tags => _tags;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private int _selectedCount;

    public bool HasSelection => SelectedCount > 0;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string? _activeTag;

    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private int _trashedCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTrash))]
    [NotifyPropertyChangedFor(nameof(IsActiveView))]
    [NotifyPropertyChangedFor(nameof(IsTrashView))]
    [NotifyPropertyChangedFor(nameof(IsTodoView))]
    [NotifyPropertyChangedFor(nameof(EmptyMessage))]
    private NoteListViewMode _viewMode;

    public bool IsActiveView => ViewMode == NoteListViewMode.Active;

    /// For compatibility with existing XAML bindings — Trash is exposed as bool (Active↔Trash toggles when set).
    public bool ShowTrash
    {
        get => ViewMode == NoteListViewMode.Trash;
        set => ViewMode = value ? NoteListViewMode.Trash : NoteListViewMode.Active;
    }

    public bool IsTrashView => ViewMode == NoteListViewMode.Trash;
    public bool IsTodoView => ViewMode == NoteListViewMode.Todos;

    public string EmptyMessage => ViewMode switch
    {
        NoteListViewMode.Trash => Strings.NoteList_EmptyTrash,
        NoteListViewMode.Todos => Strings.Todo_EmptyMessage,
        _ => string.IsNullOrWhiteSpace(SearchText) && string.IsNullOrEmpty(ActiveTag)
            ? Strings.NoteList_EmptyNoNotes
            : Strings.NoteList_EmptyNoMatch,
    };

    public NotesListViewModel(
        INoteRepository repository, IWindowManager windowManager, IBackupService backupService,
        ISettingsService settingsService, ILogger<NotesListViewModel> logger)
    {
        _repository = repository;
        _windowManager = windowManager;
        _backupService = backupService;
        _settingsService = settingsService;
        _logger = logger;
        _hideCompletedTodos = settingsService.Current.TodoHideCompleted;
    }

    partial void OnSearchTextChanged(string value)
    {
        RebuildItems();
        if (IsTodoView) RebuildTodos();
        OnPropertyChanged(nameof(EmptyMessage));
    }

    partial void OnActiveTagChanged(string? value)
    {
        RebuildItems();
        OnPropertyChanged(nameof(EmptyMessage));
    }

    partial void OnViewModeChanged(NoteListViewMode value)
    {
        // Reset tag filter in trash/todo mode (active tab only).
        if (value != NoteListViewMode.Active) ActiveTag = null;
        RebuildItems();
        if (value == NoteListViewMode.Todos) RebuildTodos();
        OnPropertyChanged(nameof(EmptyMessage));
    }

    public async Task ReloadAsync()
    {
        try
        {
            var active = await _repository.GetAllAsync().ConfigureAwait(true);
            var trashed = await _repository.GetTrashedAsync().ConfigureAwait(true);

            _activeRaw.Clear();
            _activeRaw.AddRange(active);
            _trashedRaw.Clear();
            _trashedRaw.AddRange(trashed);

            // Clean up the selection of notes that have been deleted and are no longer active — match the count and export target.
            _selectedIds.IntersectWith(_activeRaw.Select(n => n.Id));

            // Tag count only applies to active notes.
            var tagCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var note in active)
            {
                foreach (var tag in note.Tags)
                {
                    tagCounts[tag] = tagCounts.TryGetValue(tag, out var c) ? c + 1 : 1;
                }
            }
            _tags.Clear();
            foreach (var (tag, count) in tagCounts.OrderByDescending(p => p.Value).ThenBy(p => p.Key))
            {
                _tags.Add(new TagFilter(tag, count));
            }

            TotalCount = active.Count;
            TrashedCount = trashed.Count;
            RebuildItems();
            RebuildTodos();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load notes for list view");
        }
    }

    // ── Integrated to-do view (spec-2) ─────────────────────────────────────────────────

    private readonly ObservableCollection<TodoGroup> _todoGroups = new();
    public ObservableCollection<TodoGroup> TodoGroups => _todoGroups;

    [ObservableProperty]
    private int _openTodoCount;   // Tab Badge: Total number of incompletes (regardless of filter)

    [ObservableProperty]
    private bool _hideCompletedTodos;

    partial void OnHideCompletedTodosChanged(bool value)
    {
        _settingsService.Current.TodoHideCompleted = value;
        _ = _settingsService.SaveAsync();
        RebuildTodos();
    }

    /// Checkbox collection target: Only PlainText/Markdown among active notes (source replacement is not possible for rich and HTML).
    private static bool IsTodoSource(Note n) =>
        n.Format is NoteContentFormat.PlainText or NoteContentFormat.Markdown;

    private void RebuildTodos()
    {
        var query = (SearchText ?? string.Empty).Trim();
        _todoGroups.Clear();
        var openTotal = 0;

        foreach (var note in _activeRaw.Where(IsTodoSource))
        {
            var tasks = TodoExtraction.ExtractTasks(note.Content);
            if (tasks.Count == 0) continue;
            openTotal += tasks.Count(t => !t.IsChecked);

            var theme = NotePalette.For(note.Color);
            var items = new ObservableCollection<TodoItemViewModel>();
            foreach (var t in tasks)
            {
                if (HideCompletedTodos && t.IsChecked) continue;
                if (query.Length > 0 && !t.Text.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                items.Add(new TodoItemViewModel
                {
                    NoteId = note.Id,
                    LineIndex = t.LineIndex,
                    RawLine = t.RawLine,
                    Text = t.Text,
                    IsChecked = t.IsChecked,
                });
            }
            if (items.Count == 0) continue;

            var title = string.IsNullOrWhiteSpace(note.Title) ? Strings.NoteList_Untitled : note.Title;
            var group = new TodoGroup(note.Id, title, Frozen(theme.Header), Frozen(theme.Foreground), items);
            group.RefreshCount(tasks.Count(t => t.IsChecked), tasks.Count);
            _todoGroups.Add(group);
        }

        OpenTodoCount = openTotal;
        OnPropertyChanged(nameof(EmptyMessage));
    }

    [RelayCommand]
    private void OpenTodoNote(Guid noteId) => _windowManager.FocusNoteById(noteId);

    /// Check/uncheck to-do — Live (open window) / Closed (direct to DB) / Linked file 3 paths (spec-2 §3.3).
    /// The CheckBox's TwoWay binding is executed after flipping IsChecked first, so item.IsChecked is the target state.
    [RelayCommand]
    private async Task ToggleTodoAsync(TodoItemViewModel? item)
    {
        if (item is null) return;
        var desired = item.IsChecked;
        try
        {
            // 1) Secure the latest source — If there is an open window, the live VM, including the debounce and unsaved content, is the original.
            if (!_windowManager.TryGetLiveNoteContent(item.NoteId, out var source, out _))
            {
                var fresh = await _repository.GetByIdAsync(item.NoteId).ConfigureAwait(true);
                if (fresh is null) { await ReloadAsync().ConfigureAwait(true); return; }
                source = fresh.Content;
            }

            // 2) Toggle offset 1 character. If it is stale (edited in the meantime), revert and recollect the entire thing.
            var newContent = TodoExtraction.ToggleLine(source, item.LineIndex, item.RawLine, desired);
            if (newContent is null)
            {
                item.IsChecked = !desired;
                await ReloadAsync().ConfigureAwait(true);
                return;
            }
            if (string.Equals(newContent, source, StringComparison.Ordinal)) return;   // Already in target state

            // 3) Persistence — Live path (window's editor + VM injection → debounce save) first.
            if (!await _windowManager.TryUpdateLiveNoteContentAsync(item.NoteId, newContent).ConfigureAwait(true))
            {
                // Closed route: Direct to DB. If it is a linked note, the original file is also recorded (if not, it will be overwritten the next time you view it).
                var note = await _repository.GetByIdAsync(item.NoteId).ConfigureAwait(true);
                if (note is null) { await ReloadAsync().ConfigureAwait(true); return; }
                note.Content = newContent;
                note.PlainText = TextExtraction.ToPlainText(newContent, note.Format);
                if (string.IsNullOrEmpty(note.LinkedFilePath))
                {
                    note.Title = TextExtraction.DeriveTitle(note.PlainText);
                }
                note.Tags = TextExtraction.ExtractTags(note.PlainText);
                if (note.LinkedFilePath is { Length: > 0 } linkedPath)
                {
                    note.LinkedFileSyncedUtc = await LinkedFile.WriteAsync(linkedPath, newContent).ConfigureAwait(true);
                }
                await _repository.SaveContentAsync(note).ConfigureAwait(true);
            }

            // 4) Local update of local state only (without full recollection) — Cache(_activeRaw)·item·group count.
            var cached = _activeRaw.FirstOrDefault(n => n.Id == item.NoteId);
            if (cached is not null)
            {
                cached.Content = newContent;
                cached.PlainText = TextExtraction.ToPlainText(newContent, cached.Format);
            }
            item.RawLine = TodoExtraction.ToggleLine(item.RawLine, 0, item.RawLine, desired) ?? item.RawLine;

            var group = _todoGroups.FirstOrDefault(g => g.NoteId == item.NoteId);
            if (group is not null)
            {
                var done = group.DoneCount + (desired ? 1 : -1);
                group.RefreshCount(done, group.TotalCount);
                if (HideCompletedTodos && desired) group.Items.Remove(item);
            }
            OpenTodoCount += desired ? -1 : 1;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Todo toggle failed for note {NoteId}", item.NoteId);
            item.IsChecked = !desired;   //Revert check in case of failure
        }
    }

    private void RebuildItems()
    {
        var source = ShowTrash ? _trashedRaw : _activeRaw;
        var tokens = SearchMatcher.Tokenize(SearchText ?? string.Empty);

        var results = new List<NoteSummary>();
        foreach (var note in source)
        {
            // tag filter (active mode only)
            if (!ShowTrash && !string.IsNullOrEmpty(ActiveTag))
            {
                if (!note.Tags.Any(t => string.Equals(t, ActiveTag, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
            }

            var title = string.IsNullOrWhiteSpace(note.Title) ? Strings.NoteList_Untitled : note.Title;
            var excerpt = TextExtraction.ExcerptOf(note.PlainText);
            var tagsLine = note.Tags.Count == 0 ? string.Empty : "#" + string.Join("  #", note.Tags);

            var match = SearchMatcher.Score(title, tagsLine, note.PlainText, tokens);
            if (!match.Matched) continue;

            var theme = NotePalette.For(note.Color);
            var summary = new NoteSummary(
                Id: note.Id,
                Title: title,
                Excerpt: excerpt,
                PlainText: note.PlainText,
                TagsLine: tagsLine,
                Color: note.Color,
                AccentBrush: Frozen(theme.Header),
                ForegroundBrush: Frozen(theme.Foreground),
                ModifiedAt: note.ModifiedAt,
                IsTrashed: note.IsDeleted,
                DeletedAt: note.DeletedAt,
                TitleSegments: SearchMatcher.Highlight(title, tokens),
                ExcerptSegments: SearchMatcher.Highlight(excerpt, tokens),
                TagSegments: SearchMatcher.Highlight(tagsLine, tokens),
                Score: match.Score);
            // Restoring the selection state before subscribing — so that the settings here do not cause handler side effects.
            summary.IsSelected = _selectedIds.Contains(note.Id);
            summary.PropertyChanged += OnSummarySelectionChanged;
            results.Add(summary);
        }

        IEnumerable<NoteSummary> ordered = tokens.Count == 0
            ? results.OrderByDescending(s => ShowTrash ? (s.DeletedAt ?? s.ModifiedAt) : s.ModifiedAt)
            : results.OrderByDescending(s => s.Score).ThenByDescending(s => s.ModifiedAt);

        _items.Clear();
        foreach (var item in ordered)
        {
            _items.Add(item);
        }
        UpdateSelectedCount();
    }

    private void OnSummarySelectionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(NoteSummary.IsSelected) || sender is not NoteSummary summary) return;
        if (summary.IsSelected) _selectedIds.Add(summary.Id);
        else _selectedIds.Remove(summary.Id);
        UpdateSelectedCount();
    }

    /// Total number of notes selected (even if hidden by search). Always matches the export target.
    private void UpdateSelectedCount() => SelectedCount = _selectedIds.Count;

    /// List of ids of currently selected active notes (including those hidden by search).
    private IReadOnlyList<Guid> SelectedActiveIds() =>
        _activeRaw.Where(n => _selectedIds.Contains(n.Id)).Select(n => n.Id).ToList();

    [RelayCommand]
    private void Open(NoteSummary? summary)
    {
        if (summary is null || summary.IsTrashed) return;
        _windowManager.FocusNoteById(summary.Id);
    }

    [RelayCommand]
    private async Task DeleteAsync(NoteSummary? summary)
    {
        if (summary is null) return;
        await _repository.DeleteAsync(summary.Id).ConfigureAwait(true);
        await ReloadAsync().ConfigureAwait(true);
        await _windowManager.ReloadAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RestoreAsync(NoteSummary? summary)
    {
        if (summary is null) return;
        await _repository.RestoreAsync(summary.Id).ConfigureAwait(true);
        await ReloadAsync().ConfigureAwait(true);
        await _windowManager.ReloadAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task PurgeAsync(NoteSummary? summary)
    {
        if (summary is null) return;
        await _repository.PurgeAsync(summary.Id).ConfigureAwait(true);
        await ReloadAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task EmptyTrashAsync()
    {
        var ids = _trashedRaw.Select(n => n.Id).ToList();
        foreach (var id in ids)
        {
            await _repository.PurgeAsync(id).ConfigureAwait(true);
        }
        await ReloadAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void NewNote() => _windowManager.CreateAndShowNew();

    // ── Select & Export ──────────────────────────────────────────────────────────

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var item in _items) item.IsSelected = true;
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var item in _items) item.IsSelected = false;
        _selectedIds.Clear();   // Clear all hidden selections through search.
        UpdateSelectedCount();
    }

    [RelayCommand]
    private Task ExportMarkdownFiles() => ExportAsync(ExportFormat.MarkdownFiles);

    [RelayCommand]
    private Task ExportHtml() => ExportAsync(ExportFormat.Html);

    [RelayCommand]
    private Task ExportPdf() => ExportAsync(ExportFormat.Pdf);

    private async Task ExportAsync(ExportFormat format)
    {
        try
        {
            var ids = ExportTargetIds();
            if (ids.Count == 0) return;
            await _backupService.ExportNotesAsync(ids, format).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Export failed ({Format})", format);
        }
    }

    /// If there are selected notes, those notes (including those hidden by search, in list order),
    /// If not, it targets all currently visible (filtered) images.
    private IReadOnlyList<Guid> ExportTargetIds()
    {
        if (_selectedIds.Count > 0)
        {
            return _activeRaw.Where(n => _selectedIds.Contains(n.Id)).Select(n => n.Id).ToList();
        }
        return _items.Select(i => i.Id).ToList();
    }

    // ── Batch operations ───────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task BulkDeleteAsync()
    {
        try
        {
            var ids = SelectedActiveIds();
            if (ids.Count == 0) return;

            var result = MessageBox.Show(
                string.Format(Strings.Bulk_DeleteConfirmFormat, ids.Count),
                "Stickmark",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);
            if (result != MessageBoxResult.OK) return;

            foreach (var id in ids)
            {
                await _repository.DeleteAsync(id).ConfigureAwait(true);
            }

            _selectedIds.Clear();
            await ReloadAsync().ConfigureAwait(true);
            await _windowManager.ReloadAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Bulk delete failed");
        }
    }

    [RelayCommand]
    private async Task BulkSetColorAsync(NoteColor color)
    {
        try
        {
            var ids = SelectedActiveIds();
            if (ids.Count == 0) return;

            foreach (var id in ids)
            {
                var note = _activeRaw.FirstOrDefault(n => n.Id == id);
                if (note is null) continue;
                note.Color = color;
                await _repository.SaveContentAsync(note).ConfigureAwait(true);
            }

            await ReloadAsync().ConfigureAwait(true);
            await _windowManager.ReloadAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Bulk color change failed");
        }
    }

    [RelayCommand]
    private void ClearTag() => ActiveTag = null;

    [RelayCommand]
    private void SelectTag(string? tag) => ActiveTag = tag;

    [RelayCommand]
    private void ToggleTrashView() => ShowTrash = !ShowTrash;

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

public sealed record TagFilter(string Tag, int Count);

/// An item in the integrated to-do view — Points to a specific line in the original note.
public sealed partial class TodoItemViewModel : ObservableObject
{
    public required Guid NoteId { get; init; }
    public required string Text { get; init; }
    public int LineIndex { get; set; }
    public string RawLine { get; set; } = string.Empty;   // For comparison of stale when toggled (updated on success)

    [ObservableProperty]
    private bool _isChecked;
}

/// Group by note in integrated to-do view (header = note color accent + title + completion count).
public sealed partial class TodoGroup : ObservableObject
{
    public TodoGroup(Guid noteId, string title, Brush accentBrush, Brush foregroundBrush,
        ObservableCollection<TodoItemViewModel> items)
    {
        NoteId = noteId;
        Title = title;
        AccentBrush = accentBrush;
        ForegroundBrush = foregroundBrush;
        Items = items;
    }

    public Guid NoteId { get; }
    public string Title { get; }
    public Brush AccentBrush { get; }
    public Brush ForegroundBrush { get; }
    public ObservableCollection<TodoItemViewModel> Items { get; }

    public int DoneCount { get; private set; }
    public int TotalCount { get; private set; }

    [ObservableProperty]
    private string _countText = string.Empty;

    public void RefreshCount(int done, int total)
    {
        DoneCount = done;
        TotalCount = total;
        CountText = string.Format(Strings.Todo_GroupCountFormat, done, total);
    }
}
