using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stickmark.Models;
using Stickmark.Resources;
using Stickmark.Services;
using Stickmark.Utils;

namespace Stickmark.ViewModels;

/// VM in quick switcher (Ctrl+P) popup. Because a new one is created each time it is opened (WindowManager.OpenQuickSwitcher)
/// It is sufficient to hold the snapshot in instance state and there is no need for separate cache invalidation.
public sealed partial class QuickSwitcherViewModel : ObservableObject
{
    private const int MaxResults = 20;

    private readonly INoteRepository _repository;
    private readonly IWindowManager _windowManager;
    private List<Note> _snapshot = new();

    public ObservableCollection<QuickSwitcherItem> Results { get; } = new();

    /// Request to close the pop-up after opening the selected note with Enter/click. Code-behind subscribes and calls Close().
    public event Action? CloseRequested;

    [ObservableProperty]
    private string _queryText = string.Empty;

    [ObservableProperty]
    private int _selectedIndex = -1;

    public QuickSwitcherViewModel(INoteRepository repository, IWindowManager windowManager)
    {
        _repository = repository;
        _windowManager = windowManager;
    }

    public async Task LoadAsync()
    {
        var all = await _repository.GetAllAsync().ConfigureAwait(true);
        _snapshot = all.ToList();
        RebuildResults();
    }

    partial void OnQueryTextChanged(string value) => RebuildResults();

    public void MoveSelection(int delta)
    {
        if (Results.Count == 0)
        {
            SelectedIndex = -1;
            return;
        }
        var next = (SelectedIndex + delta) % Results.Count;
        if (next < 0) next += Results.Count;
        SelectedIndex = next;
    }

    [RelayCommand]
    private void OpenSelected()
    {
        if (SelectedIndex < 0 || SelectedIndex >= Results.Count) return;
        OpenNote(Results[SelectedIndex]);
    }

    [RelayCommand]
    private void OpenNote(QuickSwitcherItem? item)
    {
        if (item is null) return;
        _windowManager.FocusNoteById(item.NoteId);
        CloseRequested?.Invoke();
    }

    private void RebuildResults()
    {
        var query = QueryText ?? string.Empty;

        var matches = new List<(Note Note, string Title, string TagsLine, FuzzyMatcher.FuzzyResult Match)>();
        foreach (var note in _snapshot)
        {
            var title = string.IsNullOrWhiteSpace(note.Title) ? Strings.NoteList_Untitled : note.Title;
            var tagsLine = note.Tags.Count == 0 ? string.Empty : "#" + string.Join(" #", note.Tags);
            var target = tagsLine.Length == 0 ? title : title + " " + tagsLine;

            var match = FuzzyMatcher.Match(target, query, title.Length);
            if (!match.Matched) continue;

            matches.Add((note, title, tagsLine, match));
        }

        IEnumerable<(Note Note, string Title, string TagsLine, FuzzyMatcher.FuzzyResult Match)> ordered =
            string.IsNullOrWhiteSpace(query)
                ? matches.OrderByDescending(m => m.Note.ModifiedAt)
                : matches.OrderByDescending(m => m.Match.Score).ThenByDescending(m => m.Note.ModifiedAt);

        Results.Clear();
        foreach (var (note, title, tagsLine, match) in ordered.Take(MaxResults))
        {
            var theme = NotePalette.For(note.Color);
            Results.Add(new QuickSwitcherItem(
                NoteId: note.Id,
                Title: title,
                TagsLine: tagsLine,
                AccentBrush: Frozen(theme.Header),
                TitleSegments: BuildSegments(title, 0, match.Positions),
                TagSegments: BuildSegments(tagsLine, title.Length + 1, match.Positions)));
        }

        SelectedIndex = Results.Count > 0 ? 0 : -1;
    }

    /// match.Positions is an index based on the composite string "Title + ' ' + Tagline", so to cut to each field
    /// Subtract the offset from where the field starts from the composite string and change it to a local index.
    private static IReadOnlyList<HighlightSegment> BuildSegments(string text, int offset, IReadOnlyList<int> positions)
    {
        if (string.IsNullOrEmpty(text)) return new[] { new HighlightSegment(text ?? string.Empty, false) };

        var matchSet = new HashSet<int>();
        foreach (var pos in positions)
        {
            var local = pos - offset;
            if (local >= 0 && local < text.Length) matchSet.Add(local);
        }
        if (matchSet.Count == 0) return new[] { new HighlightSegment(text, false) };

        var segments = new List<HighlightSegment>();
        var start = 0;
        var currentIsMatch = matchSet.Contains(0);
        for (var i = 1; i <= text.Length; i++)
        {
            var isMatch = i < text.Length && matchSet.Contains(i);
            if (i == text.Length || isMatch != currentIsMatch)
            {
                segments.Add(new HighlightSegment(text.Substring(start, i - start), currentIsMatch));
                start = i;
                currentIsMatch = isMatch;
            }
        }
        return segments;
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

public sealed record QuickSwitcherItem(
    Guid NoteId,
    string Title,
    string TagsLine,
    Brush AccentBrush,
    IReadOnlyList<HighlightSegment> TitleSegments,
    IReadOnlyList<HighlightSegment> TagSegments);
