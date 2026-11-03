using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LiteDB;
using Stickmark.Models;

namespace Stickmark.Services;

public sealed class NoteRepository : INoteRepository, IDisposable
{
    private const string CollectionName = "notes";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly LiteDatabase _db;
    private bool _disposed;

    public NoteRepository(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        // If schema migration is necessary, snapshot the DB before opening (so it can be reverted even if something goes wrong).
        SchemaMigrator.BackupBeforeMigration(dbPath);
        _db = new LiteDatabase($"Filename={dbPath};Connection=shared");
        // Migrate DB schema to the latest version (based on 2.0 data model evolution).
        SchemaMigrator.Migrate(_db);
        var col = _db.GetCollection<Note>(CollectionName);
        col.EnsureIndex(x => x.ModifiedAt);
        col.EnsureIndex(x => x.Title);
        col.EnsureIndex(x => x.IsDeleted);
    }

    public async Task<IReadOnlyList<Note>> GetAllAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var col = _db.GetCollection<Note>(CollectionName);
            return col.Find(n => !n.IsDeleted)
                .OrderByDescending(n => n.ModifiedAt)
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<Note>> GetTrashedAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var col = _db.GetCollection<Note>(CollectionName);
            return col.Find(n => n.IsDeleted)
                .OrderByDescending(n => n.DeletedAt ?? n.ModifiedAt)
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Note?> GetByIdAsync(Guid id)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var note = _db.GetCollection<Note>(CollectionName).FindById(id);
            // Block trash bin items from being visible from the outside. Explicit recovery is handled by RestoreAsync.
            return note is null || note.IsDeleted ? null : note;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Note?> FindByTitleAsync(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var col = _db.GetCollection<Note>(CollectionName);
            return col.Find(n => !n.IsDeleted)
                .FirstOrDefault(n => string.Equals(n.Title, title.Trim(), StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Note?> FindByLinkedPathAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var target = path.Trim();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var col = _db.GetCollection<Note>(CollectionName);
            // Path comparison is performed on the client side ignoring case (Windows file system convention).
            return col.Find(n => !n.IsDeleted && n.LinkedFilePath != null)
                .FirstOrDefault(n => string.Equals(n.LinkedFilePath, target, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpsertAsync(Note note)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            note.ModifiedAt = DateTime.UtcNow;
            _db.GetCollection<Note>(CollectionName).Upsert(note);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveContentAsync(Note note)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var col = _db.GetCollection<Note>(CollectionName);
            // Recycle bin state (IsDeleted/DeletedAt) transitions are owned only by DeleteAsync/RestoreAsync/PurgeAsync.
            // If saving content touches that state, the note window that was open immediately after deletion is closed and the old memory
            // Flush the copy (IsDeleted=false) to revive the note just deleted.
            var existing = col.FindById(note.Id);
            if (existing is null)
            {
                // The note has already been permanently deleted (delete from list → PurgeAsync empty note, or permanently delete from trash).
                // A debounce save that came in late while the window was closing creates a stale copy (IsDeleted=false).
                // If you reinsert with Upsert, the note you just deleted will be restored to the active list. Content storage is
                // Since it is for 'note update' only, missing notes are not recreated and are ignored.
                return;
            }
            // Preserves the state of the recycle bin stored in the DB as is, preventing the stale value of the in-memory copy from reversing deletion.
            note.IsDeleted = existing.IsDeleted;
            note.DeletedAt = existing.DeletedAt;
            note.ModifiedAt = DateTime.UtcNow;
            col.Update(note);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(Guid id)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var col = _db.GetCollection<Note>(CollectionName);
            var note = col.FindById(id);
            if (note is null) return;
            note.IsDeleted = true;
            note.DeletedAt = DateTime.UtcNow;
            col.Update(note);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RestoreAsync(Guid id)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var col = _db.GetCollection<Note>(CollectionName);
            var note = col.FindById(id);
            if (note is null) return;
            note.IsDeleted = false;
            note.DeletedAt = null;
            note.ModifiedAt = DateTime.UtcNow;
            col.Update(note);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task PurgeAsync(Guid id)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _db.GetCollection<Note>(CollectionName).Delete(id);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> PurgeTrashedOlderThanAsync(DateTime cutoff)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var col = _db.GetCollection<Note>(CollectionName);
            var stale = col.Find(n => n.IsDeleted && n.DeletedAt != null && n.DeletedAt < cutoff)
                .Select(n => n.Id)
                .ToList();
            foreach (var id in stale) col.Delete(id);
            return stale.Count;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _db.Dispose();
        _gate.Dispose();
    }
}
