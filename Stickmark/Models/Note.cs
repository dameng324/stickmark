using System;
using System.Collections.Generic;

namespace Stickmark.Models;

public sealed class Note
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Content { get; set; } = string.Empty;
    public NoteContentFormat Format { get; set; } = NoteContentFormat.Markdown;

    /// Plain-text projection of Content. Cached so the notes-list and search don't have to re-parse XAML.
    public string PlainText { get; set; } = string.Empty;

    /// Auto-derived from the first non-empty line of PlainText. Used by `[[Title]]` link resolution.
    public string Title { get; set; } = string.Empty;

    public List<string> Tags { get; set; } = new();

    public NoteColor Color { get; set; } = NoteColor.Yellow;

    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 280;
    public double Height { get; set; } = 280;

    /// WebView zoom ratio for preview and WYSIWYG editing. 1.0 = 100%.
    public double ZoomFactor { get; set; } = 1.0;

    public double Opacity { get; set; } = 1.0;
    public bool IsAlwaysOnTop { get; set; }
    public bool IsHidden { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;

    /// If the note is bi-directionally linked to an external .md (or text) file, the absolute path to that file. If null, it is a general DB note.
    /// If this value is present, note edits are saved to the original file, and changes outside the file are reflected as notes.
    /// Even if you delete/move a note to the trash, this file itself is never deleted (just disconnected).
    public string? LinkedFilePath { get; set; }

    /// LastWriteTimeUtc of the file when it was last synchronized with the file. To assist in external change/conflict judgment.
    public DateTime? LinkedFileSyncedUtc { get; set; }

    /// Recycle bin marker. If true, it is excluded from general search, search, and window restoration.
    /// Only visible in the Recycle Bin tab of NotesListWindow.
    public bool IsDeleted { get; set; }

    /// The time it was moved to the trash can. Automatic permanent deletion after 30 days.
    public DateTime? DeletedAt { get; set; }
}
