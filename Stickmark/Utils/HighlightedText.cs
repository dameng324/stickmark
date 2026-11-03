using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Stickmark.ViewModels;

namespace Stickmark.Utils;

/// Attached property that fills the Inlines of TextBlock with the HighlightSegment collection.
/// Only segments with IsMatch=true are treated in bold. Used in search results cards.
public static class HighlightedText
{
    public static readonly DependencyProperty SegmentsProperty =
        DependencyProperty.RegisterAttached(
            "Segments",
            typeof(IReadOnlyList<HighlightSegment>),
            typeof(HighlightedText),
            new PropertyMetadata(null, OnSegmentsChanged));

    public static IReadOnlyList<HighlightSegment>? GetSegments(DependencyObject d) =>
        (IReadOnlyList<HighlightSegment>?)d.GetValue(SegmentsProperty);

    public static void SetSegments(DependencyObject d, IReadOnlyList<HighlightSegment>? value) =>
        d.SetValue(SegmentsProperty, value);

    private static void OnSegmentsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;
        tb.Inlines.Clear();
        if (e.NewValue is not IReadOnlyList<HighlightSegment> segs || segs.Count == 0) return;

        foreach (var seg in segs)
        {
            var run = new Run(seg.Text);
            if (seg.IsMatch)
            {
                run.FontWeight = FontWeights.Bold;
            }
            tb.Inlines.Add(run);
        }
    }
}
