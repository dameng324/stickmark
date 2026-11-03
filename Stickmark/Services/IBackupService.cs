using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Stickmark.Models;

namespace Stickmark.Services;

public interface IBackupService
{
    Task ExportInteractiveAsync();
    Task ImportInteractiveAsync();

    /// Exports active notes to a human-readable Markdown/text file (not a restorable backup).
    Task ExportNotesAsTextAsync();

    /// Exports the specified notes in the selected format (.md file per note / single HTML / PDF).
    /// The noteIds order is maintained, and only active (not trash) notes are targeted.
    Task ExportNotesAsync(IReadOnlyList<Guid> noteIds, ExportFormat format);

    /// Save a single note (current window) in the selected format. Markdown is 1 file (SaveFileDialog).
    Task ExportSingleNoteAsync(Note note, ExportFormat format);

    /// Print a single note to the system print UI (visible preview window, not offscreen).
    Task PrintNoteAsync(Note note);

    /// Saves all active notes as .md (possible for round trip, including id) per note in a folder (vault export).
    Task ExportVaultAsync();

    /// Import the .md files in the folder into notes. The same ID is updated (round trip). Handmade .mds are also accepted.
    Task ImportVaultAsync();
}
