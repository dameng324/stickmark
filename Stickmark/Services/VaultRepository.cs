using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stickmark.Models;

namespace Stickmark.Services;

/// External change diff consumed by the vault watcher. Removed includes previous content to determine if it has not been saved.
public sealed record VaultDiff(
    IReadOnlyList<Note> Added,
    IReadOnlyList<(Guid Id, string OldContent)> Removed,
    IReadOnlyList<(Note Fresh, string OldContent)> Changed)
{
    public static readonly VaultDiff Empty = new(
        Array.Empty<Note>(), Array.Empty<(Guid, string)>(), Array.Empty<(Note, string)>());

    public bool IsEmpty => Added.Count == 0 && Removed.Count == 0 && Changed.Count == 0;
}

/// Implementation of <see cref="INoteRepository"/> that uses a vault folder (.md set) as a repository (2.0 live vault mode).
/// Notes are maintained as an in-memory cache, and only one note is written to the folder for each change (<see cref="VaultStore.SaveOne"/>).
/// The default storage is still LiteDB, and vault mode is opted in in settings (no existing data at risk).
public sealed class VaultRepository : INoteRepository, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly VaultStore _store;
    private readonly Dictionary<Guid, Note> _notes = new();
    private bool _disposed;

    public VaultRepository(string folder)
    {
        _store = new VaultStore(folder);
        foreach (var n in _store.Load()) _notes[n.Id] = n;
    }

    /// Reread the cache to reflect changes outside the folder (called from the file watcher).
    public async Task ReloadAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _notes.Clear();
            foreach (var n in _store.Load()) _notes[n.Id] = n;
        }
        finally { _gate.Release(); }
    }

    /// Watcher's echo judgment — true if the file was written by us recently (3 seconds).
    public bool IsRecentSelfWrite(string fileName) => _store.IsRecentSelfWrite(fileName);

    /// Rereads the folder, replaces the cache, and returns the difference from the previous cache (for vault watchers).
    /// Since snapshot, replacement, and diff calculations occur atomically in the critical section such as the storage path (_gate),
    /// There are no lost or ghost diffs. ModifiedAt is noise that is updated every time we write, so it is not used in judgment.
    public async Task<VaultDiff> ReloadWithDiffAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var old = _notes.Values.ToDictionary(
                n => n.Id, n => (n.Content, n.Title, n.Format, n.PlainText));

            _notes.Clear();
            foreach (var n in _store.Load()) _notes[n.Id] = n;

            var added = new List<Note>();
            var changed = new List<(Note Fresh, string OldContent)>();
            foreach (var n in _notes.Values)
            {
                if (!old.TryGetValue(n.Id, out var o)) { added.Add(n); continue; }
                // Compare file canonical forms (the form actually used by vault serialization) to avoid ghost changes:
                // Rich (XAML) notes are demoted to PlainText in the file, and spaces at the end of the text are trimmed.
                var (oc, of) = CanonOf(o.Content, o.Format, o.PlainText);
                var (nc, nf) = CanonOf(n.Content, n.Format, n.PlainText);
                if (!string.Equals(oc, nc, StringComparison.Ordinal)
                    || !string.Equals(o.Title, n.Title, StringComparison.Ordinal)
                    || of != nf)
                {
                    changed.Add((n, o.Content));   // OldContent for UI judgment is passed as the original
                }
            }
            var removed = old
                .Where(kv => !_notes.ContainsKey(kv.Key))
                .Select(kv => (kv.Key, kv.Value.Content))
                .ToList();

            return new VaultDiff(added, removed, changed);
        }
        finally { _gate.Release(); }
    }

    /// Vault file normal form: Rich (XAML) is demoted to PlainText and spaces and CRLF at the end of the body are normalized.
    /// (same as <see cref="Utils.VaultMarkdown"/> round trip rule). diff should compare these types
    /// Reloading immediately after a self-save does not create a ghost change.
    private static (string Content, NoteContentFormat Format) CanonOf(
        string? content, NoteContentFormat format, string? plainText)
    {
        var (c, f) = format == NoteContentFormat.RichTextXaml
            ? (plainText ?? string.Empty, NoteContentFormat.PlainText)
            : (content ?? string.Empty, format);
        return (c.Replace("\r\n", "\n").TrimEnd(), f);
    }

    public Task<IReadOnlyList<Note>> GetAllAsync() => Read(() =>
        (IReadOnlyList<Note>)_notes.Values.Where(n => !n.IsDeleted)
            .OrderByDescending(n => n.ModifiedAt).ToList());

    public Task<IReadOnlyList<Note>> GetTrashedAsync() => Read(() =>
        (IReadOnlyList<Note>)_notes.Values.Where(n => n.IsDeleted)
            .OrderByDescending(n => n.DeletedAt ?? n.ModifiedAt).ToList());

    public Task<Note?> GetByIdAsync(Guid id) => Read(() =>
        _notes.TryGetValue(id, out var n) && !n.IsDeleted ? n : null);

    public Task<Note?> FindByTitleAsync(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return Task.FromResult<Note?>(null);
        var t = title.Trim();
        return Read(() => _notes.Values.FirstOrDefault(n =>
            !n.IsDeleted && string.Equals(n.Title, t, StringComparison.OrdinalIgnoreCase)));
    }

    public Task<Note?> FindByLinkedPathAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return Task.FromResult<Note?>(null);
        var target = path.Trim();
        return Read(() => _notes.Values.FirstOrDefault(n =>
            !n.IsDeleted && n.LinkedFilePath != null &&
            string.Equals(n.LinkedFilePath, target, StringComparison.OrdinalIgnoreCase)));
    }

    public Task UpsertAsync(Note note) => Write(() =>
    {
        note.ModifiedAt = DateTime.UtcNow;
        _notes[note.Id] = note;
        _store.SaveOne(note);
    });

    public Task SaveContentAsync(Note note) => Write(() =>
    {
        // Content storage only — Lost notes will not be restored, and the Recycle Bin status will preserve cache values.
        if (!_notes.TryGetValue(note.Id, out var existing)) return;
        note.IsDeleted = existing.IsDeleted;
        note.DeletedAt = existing.DeletedAt;
        note.ModifiedAt = DateTime.UtcNow;
        _notes[note.Id] = note;
        _store.SaveOne(note);
    });

    public Task DeleteAsync(Guid id) => Write(() =>
    {
        if (!_notes.TryGetValue(id, out var n)) return;
        n.IsDeleted = true;
        n.DeletedAt = DateTime.UtcNow;
        _store.SaveOne(n);
    });

    public Task RestoreAsync(Guid id) => Write(() =>
    {
        if (!_notes.TryGetValue(id, out var n)) return;
        n.IsDeleted = false;
        n.DeletedAt = null;
        n.ModifiedAt = DateTime.UtcNow;
        _store.SaveOne(n);
    });

    public Task PurgeAsync(Guid id) => Write(() =>
    {
        if (_notes.Remove(id)) _store.DeleteFile(id);
    });

    public Task<int> PurgeTrashedOlderThanAsync(DateTime cutoff) => Write(() =>
    {
        var stale = _notes.Values
            .Where(n => n.IsDeleted && n.DeletedAt != null && n.DeletedAt < cutoff)
            .Select(n => n.Id).ToList();
        foreach (var id in stale) { _notes.Remove(id); _store.DeleteFile(id); }
        return stale.Count;
    });

    private async Task<T> Read<T>(Func<T> body)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { return body(); } finally { _gate.Release(); }
    }

    private async Task Write(Action body)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { body(); } finally { _gate.Release(); }
    }

    private async Task<T> Write<T>(Func<T> body)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { return body(); } finally { _gate.Release(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
    }
}
