using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows.Media;
using Stickmark.Models;

namespace Stickmark.ViewModels;

/// Search highlight unit. Segments with IsMatch=true are drawn in bold.
public sealed record HighlightSegment(string Text, bool IsMatch);

/// Display model for one list card. Since it contains a variable IsSelected that is toggled for each card,
/// It is a class (reference equality), not a record (value equality) — If it is value equality, IsSelected is
/// Hash-based lookup of WPF Selector may be broken when mixed with GetHashCode.
public sealed class NoteSummary(
    Guid Id,
    string Title,
    string Excerpt,
    string PlainText,
    string TagsLine,
    NoteColor Color,
    Brush AccentBrush,
    Brush ForegroundBrush,
    DateTime ModifiedAt,
    bool IsTrashed,
    DateTime? DeletedAt,
    IReadOnlyList<HighlightSegment> TitleSegments,
    IReadOnlyList<HighlightSegment> ExcerptSegments,
    IReadOnlyList<HighlightSegment> TagSegments,
    double Score) : INotifyPropertyChanged
{
    public Guid Id { get; } = Id;
    public string Title { get; } = Title;
    public string Excerpt { get; } = Excerpt;
    public string PlainText { get; } = PlainText;
    public string TagsLine { get; } = TagsLine;
    public NoteColor Color { get; } = Color;
    public Brush AccentBrush { get; } = AccentBrush;
    public Brush ForegroundBrush { get; } = ForegroundBrush;
    public DateTime ModifiedAt { get; } = ModifiedAt;
    public bool IsTrashed { get; } = IsTrashed;
    public DateTime? DeletedAt { get; } = DeletedAt;
    public IReadOnlyList<HighlightSegment> TitleSegments { get; } = TitleSegments;
    public IReadOnlyList<HighlightSegment> ExcerptSegments { get; } = ExcerptSegments;
    public IReadOnlyList<HighlightSegment> TagSegments { get; } = TagSegments;
    public double Score { get; } = Score;

    private bool _isSelected;

    /// Selected with a checkbox from the list. Used to select the export destination.
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, IsSelectedChangedArgs);
        }
    }

    private static readonly PropertyChangedEventArgs IsSelectedChangedArgs = new(nameof(IsSelected));

    public event PropertyChangedEventHandler? PropertyChanged;
}
