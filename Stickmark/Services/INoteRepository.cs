using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Stickmark.Models;

namespace Stickmark.Services;

public interface INoteRepository
{
    /// Returns notes that are not in the trash (IsDeleted == false) in descending order of ModifiedAt.
    Task<IReadOnlyList<Note>> GetAllAsync();

    /// Returns only Recycle Bin items in DeletedAt descending order.
    Task<IReadOnlyList<Note>> GetTrashedAsync();

    Task<Note?> GetByIdAsync(Guid id);
    Task<Note?> FindByTitleAsync(string title);

    /// Find notes linked to the given absolute path (not in the trash can). When opening the same .md file again
    /// Lookup to reuse existing notes without creating duplicate notes. Path comparison ignores case.
    Task<Note?> FindByLinkedPathAsync(string path);

    /// Insert/replace the entire note document. Even the trash can status (IsDeleted/DeletedAt) is reflected as a parameter.
    /// Only for paths where “the imported value is the correct answer,” such as backup import · creating a new note.
    Task UpsertAsync(Note note);

    /// Only editable fields such as content, format, and location are saved. Recycle bin status (IsDeleted/DeletedAt) is
    /// Preserve values already saved in the DB — Prevent old memory copies of open note windows from reversing deletion.
    /// Notes that have already been permanently deleted (not in the DB) are ignored rather than reinserted — belated
    /// Prevents flush from resurrecting deleted notes. Converting and reinserting the recycle bin is not covered.
    /// That role is only handled by UpsertAsync/DeleteAsync/RestoreAsync/PurgeAsync.
    Task SaveContentAsync(Note note);

    /// Soft delete — IsDeleted=true, DeletedAt=now.
    Task DeleteAsync(Guid id);

    /// Restore normal notes from the Recycle Bin.
    Task RestoreAsync(Guid id);

    /// Completely remove from DB (empty trash/clean after 30 days).
    Task PurgeAsync(Guid id);

    /// Permanently deletes all Recycle Bin items where DeletedAt < cutoff. Returns the deleted count.
    Task<int> PurgeTrashedOlderThanAsync(DateTime cutoff);
}
