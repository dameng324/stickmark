using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Stickmark.Models;
using Stickmark.Services;
using Stickmark.Utils;

namespace Stickmark.ViewModels;

public sealed partial class NoteViewModel : ObservableObject, IDisposable
{
    public static readonly IReadOnlyList<double> FontSizeCatalog =
        new double[] { 10, 11, 12, 13, 14, 16, 18, 20, 24, 28, 32, 40 };

    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);

    private readonly INoteRepository _repository;
    private readonly ILogger<NoteViewModel> _logger;
    private readonly DebounceAction _saver;
    private readonly Func<NoteViewModel, Task> _onDeleteRequested;
    private bool _hydrating = true;

    /// The original text that was last synchronized with the file. It is used to detect external changes, determine conflicts, and prevent unnecessary resaving.
    private string _lastSyncedText;

    /// If true, edits to this note will no longer be recorded in the linked file (when deleted or disconnected).
    private bool _fileSyncSuspended;

    public Guid Id => Note.Id;
    public Note Note { get; }

    public IReadOnlyList<NoteTheme> Palette => NotePalette.All;

    [ObservableProperty]
    private string _content;

    [ObservableProperty]
    private NoteContentFormat _format;

    [ObservableProperty]
    private string _plainText;

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private double _x;

    [ObservableProperty]
    private double _y;

    [ObservableProperty]
    private double _width;

    [ObservableProperty]
    private double _height;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Theme))]
    [NotifyPropertyChangedFor(nameof(BackgroundBrush))]
    [NotifyPropertyChangedFor(nameof(HeaderBrush))]
    [NotifyPropertyChangedFor(nameof(BorderBrush))]
    [NotifyPropertyChangedFor(nameof(ForegroundBrush))]
    [NotifyPropertyChangedFor(nameof(SubtleForegroundBrush))]
    private NoteColor _color;

    [ObservableProperty]
    private double _opacity;

    [ObservableProperty]
    private bool _isAlwaysOnTop;

    [ObservableProperty]
    private bool _isPreviewMode;

    [ObservableProperty]
    private double _editorFontSize = 14;

    public NoteTheme Theme => NotePalette.For(Color);
    public Brush BackgroundBrush => Frozen(Theme.Background);
    public Brush HeaderBrush => Frozen(Theme.Header);
    public Brush BorderBrush => Frozen(Theme.Border);
    public Brush ForegroundBrush => Frozen(Theme.Foreground);
    public Brush SubtleForegroundBrush => Frozen(Theme.SubtleForeground);

    public NoteViewModel(
        Note note,
        INoteRepository repository,
        ILogger<NoteViewModel> logger,
        Func<NoteViewModel, Task> onDeleteRequested)
    {
        Note = note;
        _repository = repository;
        _logger = logger;
        _onDeleteRequested = onDeleteRequested;

        _content = note.Content;
        _format = note.Format;
        _plainText = note.PlainText;
        _title = note.Title;
        _x = note.X;
        _y = note.Y;
        _width = note.Width;
        _height = note.Height;
        _color = note.Color;
        _opacity = ClampOpacity(note.Opacity);
        _isAlwaysOnTop = note.IsAlwaysOnTop;
        _lastSyncedText = note.Content;

        _saver = new DebounceAction(SaveDelay, _ => SaveAsync());
        _hydrating = false;
    }

    /// Is the note linked to an external file?
    public bool IsLinkedFile => !string.IsNullOrEmpty(Note.LinkedFilePath);

    /// Absolute path to the linked file (null if not present).
    public string? LinkedFilePath => Note.LinkedFilePath;

    /// The original text that was last synchronized with the file (to determine local edits in case of external changes).
    public string LastSyncedText => _lastSyncedText;

    /// Displays the content read back from the file (or just written into the file) as the 'synchronization standard'.
    public void MarkSynced(string text) => _lastSyncedText = text;

    /// Stops so that subsequent saves do not touch the linked file (called when deleting/unlinking).
    /// Prevents accidents where the original file is overwritten with empty content.
    public void SuspendFileSync() => _fileSyncSuspended = true;

    partial void OnContentChanged(string value) => Schedule();
    partial void OnFormatChanged(NoteContentFormat value) => Schedule();
    partial void OnXChanged(double value) => Schedule();
    partial void OnYChanged(double value) => Schedule();
    partial void OnWidthChanged(double value) => Schedule();
    partial void OnHeightChanged(double value) => Schedule();
    partial void OnColorChanged(NoteColor value) => Schedule();
    partial void OnOpacityChanged(double value) => Schedule();
    partial void OnIsAlwaysOnTopChanged(bool value) => Schedule();

    [RelayCommand]
    private void SetColor(NoteColor color) => Color = color;

    [RelayCommand]
    private void TogglePin() => IsAlwaysOnTop = !IsAlwaysOnTop;

    [RelayCommand]
    private void TogglePreview() => IsPreviewMode = !IsPreviewMode;

    [RelayCommand]
    private Task DeleteAsync() => _onDeleteRequested(this);

    public void UpdateContent(string content, NoteContentFormat format)
    {
        if (_hydrating) return;
        Content = content;
        Format = format;
        var plain = TextExtraction.ToPlainText(content, format);
        PlainText = plain;
        // The title of the linked note is fixed to the file name — making it easier to find out what file it is in the list.
        Title = IsLinkedFile
            ? System.IO.Path.GetFileName(Note.LinkedFilePath!)
            : TextExtraction.DeriveTitle(plain);
    }

    /// Hydrate external changes reflected by the vault watch without a save schedule —
    /// Block Schedule/UpdateContent re-entrancy so that reflection does not cause another file write (echo);
    /// Align the Note field together so that future location saves, etc. do not rewrite the old content.
    public void HydrateExternal(string content, NoteContentFormat format)
    {
        _hydrating = true;
        try
        {
            Content = content;
            Format = format;
            var plain = TextExtraction.ToPlainText(content, format);
            PlainText = plain;
            Title = IsLinkedFile
                ? System.IO.Path.GetFileName(Note.LinkedFilePath!)
                : TextExtraction.DeriveTitle(plain);

            Note.Content = content;
            Note.Format = format;
            Note.PlainText = plain;
            Note.Title = Title;
            Note.Tags = TextExtraction.ExtractTags(plain);

            _lastSyncedText = content;   // This content is synchronized with the file
        }
        finally { _hydrating = false; }
    }

    private void Schedule()
    {
        if (_hydrating) return;
        _saver.Trigger();
    }

    private async Task SaveAsync()
    {
        try
        {
            Note.Content = Content;
            Note.Format = Format;
            Note.PlainText = PlainText;
            Note.Title = Title;
            Note.Tags = TextExtraction.ExtractTags(PlainText);
            Note.X = X;
            Note.Y = Y;
            Note.Width = Width;
            Note.Height = Height;
            Note.Color = Color;
            Note.Opacity = Opacity;
            Note.IsAlwaysOnTop = IsAlwaysOnTop;
            // SaveContentAsync preserves the trash can status (IsDeleted/DeletedAt) as a DB value.
            // Do not use UpsertAsync so that the window of the deleted note is closed and not revived even if flushed.
            await _repository.SaveContentAsync(Note).ConfigureAwait(false);

            await SyncToLinkedFileAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save note {NoteId}", Note.Id);
        }
    }

    /// If it is a linked note, the edits are recorded in the original file. Use only when the content has actually changed.
    /// Prevents saving such as location and color changes from touching the file mtime. After deletion/disconnection
    /// (SuspendFileSync) The file is never touched, preventing accidents where the original is covered with empty content.
    private async Task SyncToLinkedFileAsync()
    {
        if (!IsLinkedFile || _fileSyncSuspended) return;
        // Rich text (XAML) should never be written to text files — the linked note was accidentally set to 'T' (rich) mode.
        // Protects the original .md from being covered by XAML even if changed. Only the text source format is reflected in the file.
        if (Format == NoteContentFormat.RichTextXaml) return;
        if (string.Equals(Content, _lastSyncedText, StringComparison.Ordinal)) return;

        try
        {
            var writeUtc = await LinkedFile.WriteAsync(Note.LinkedFilePath!, Content).ConfigureAwait(false);
            _lastSyncedText = Content;
            Note.LinkedFileSyncedUtc = writeUtc;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write linked file {Path}", Note.LinkedFilePath);
        }
    }

    public Task FlushAsync() => _saver.FlushAsync();

    public bool IsEmpty => string.IsNullOrWhiteSpace(PlainText);

    public void Dispose() => _saver.Dispose();

    private static double ClampOpacity(double value)
    {
        if (double.IsNaN(value) || value <= 0) return 1.0;
        if (value < 0.5) return 0.5;
        if (value > 1.0) return 1.0;
        return value;
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
