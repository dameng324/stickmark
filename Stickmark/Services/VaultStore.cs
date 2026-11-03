using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Stickmark.Models;
using Stickmark.Utils;

namespace Stickmark.Services;

/// Storage engine for vault folders ↔ note sets (active+trash) (based on 2.0 Live Vault mode).
///
/// The contents of each note are human-readable `.md` (<see cref="VaultMarkdown"/>), with Stickmark's unique mark/state
/// (Window position, size, transparency, always on top, hidden, linked path, trash can) is a sidecar in the folder.
/// Put it in <c>.stickmark-index.json</c> — so that `.md` can be used neatly in Obsidian, etc.
///
/// The instance holds an id → file name map to keep the file name stable (the same file even if the title changes).
/// Do not delete files in bulk — delete only one specific note with <see cref="DeleteFile"/>
/// (to avoid accidentally deleting other `.md` files that the user may have added).
public sealed class VaultStore
{
    private const string IndexFileName = ".stickmark-index.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// The echo judgment time window of the self-writing ledger — FSW events that come within this are considered to have been written by us.
    private const int SelfWriteWindowSeconds = 3;

    private readonly string _folder;
    private readonly Dictionary<Guid, string> _files = new(); // id -> file name (based on folder)

    /// Self-writing ledger (file name → recording time UTC). For echo suppression in vault watchers — just an IO-saving optimization;
    /// Even if the decision is wrong, diff integrity prevents malfunction (spec-5 §3.4).
    private readonly ConcurrentDictionary<string, DateTime> _selfWrites = new(StringComparer.OrdinalIgnoreCase);

    public VaultStore(string folder) => _folder = folder;

    private void RecordSelfWrite(string fileName) => _selfWrites[fileName] = DateTime.UtcNow;

    /// True if it is a recent (3 seconds) self-written file. Items that have passed the time window are lazily cleaned.
    public bool IsRecentSelfWrite(string fileName) => IsRecentSelfWrite(fileName, DateTime.UtcNow);

    internal bool IsRecentSelfWrite(string fileName, DateTime nowUtc)
    {
        if (_selfWrites.TryGetValue(fileName, out var at))
        {
            if ((nowUtc - at).TotalSeconds <= SelfWriteWindowSeconds) return true;
            _selfWrites.TryRemove(fileName, out _);
        }
        return false;
    }

    private sealed record NoteState(
        string File, double X, double Y, double Width, double Height,
        double Opacity, bool IsAlwaysOnTop, bool IsHidden, string? LinkedFilePath,
        bool IsDeleted, DateTime? DeletedAt);

    public IReadOnlyList<Note> Load()
    {
        _files.Clear();
        var result = new List<Note>();
        if (!Directory.Exists(_folder)) return result;

        var index = ReadIndex();
        foreach (var file in Directory.EnumerateFiles(_folder, "*.md", SearchOption.TopDirectoryOnly))
        {
            Note note;
            bool hadId;
            try { note = VaultMarkdown.FromMarkdown(File.ReadAllText(file), Path.GetFileNameWithoutExtension(file), out hadId); }
            catch { continue; }

            var name = Path.GetFileName(file);

            // Manually added .md without front matter id — Provides a stable id based on the file name.
            // If the ID is random, it appears as a different note every time you reload, and the monitoring diff vibrates (notification spam and hidden window accumulation).
            // Sidecar state (location, etc.) is also lost on each restart. This id is persistent in the file the moment you edit it.
            if (!hadId) note.Id = DisplacedId(Guid.Empty, name);

            // ID duplication (typical of conflicting copies) — the non-authentic one obscures the original from the cache, and subsequent saves
            // Prevent data accidents from being recorded in conflict copy files (spec-5 §3.6). Read-only pass —
            // Do not rewrite the file here. The page that was pushed out becomes a separate note with a new guide and is removed from the list.
            // Can be checked and organized by the user (the moment the note is edited, the new id is persistent in the file).
            if (_files.TryGetValue(note.Id, out var existingName))
            {
                var indexFile = index.TryGetValue(note.Id.ToString(), out var idxState) ? idxState.File : null;
                bool newIsCanonical;
                if (indexFile is not null && string.Equals(name, indexFile, StringComparison.OrdinalIgnoreCase))
                    newIsCanonical = true;                                   // index points to new file
                else if (indexFile is not null && string.Equals(existingName, indexFile, StringComparison.OrdinalIgnoreCase))
                    newIsCanonical = false;                                  // index points to existing file
                else
                {
                    // Cannot be determined by index → The page that is not a conflict copy pattern is considered the original. If it's still ambiguous, read it first.
                    bool Sibling(string f) => File.Exists(Path.Combine(_folder, f));
                    var existingIsConflict = ConflictCopyDetector.IsConflictCopy(existingName, Sibling);
                    var newIsConflict = ConflictCopyDetector.IsConflictCopy(name, Sibling);
                    newIsCanonical = existingIsConflict && !newIsConflict;
                }

                if (newIsCanonical)
                {
                    // The side read first is pushed out — keep a separate note with a deterministic derived id (same id for each reload →
                    // Watch diff does not oscillate with ghost adds/deletes). The file is not rewritten.
                    var displaced = result.First(n => n.Id == note.Id);
                    displaced.Id = DisplacedId(note.Id, existingName);
                    _files[displaced.Id] = existingName;
                    _files[note.Id] = name;
                    if (index.TryGetValue(note.Id.ToString(), out var s2)) Apply(note, s2);
                    result.Add(note);
                }
                else
                {
                    // The newly read page is pushed out.
                    note.Id = DisplacedId(note.Id, name);
                    _files[note.Id] = name;
                    result.Add(note);   // Sidecar status is authentic copy — not applicable
                }
                continue;
            }

            _files[note.Id] = name; // Remember the actual file name → Reuse when saving later
            if (index.TryGetValue(note.Id.ToString(), out var s)) Apply(note, s);
            result.Add(note);
        }
        return result;
    }

    /// Save the entire note (for migration/first recording).
    public void Save(IReadOnlyList<Note> notes)
    {
        Directory.CreateDirectory(_folder);
        var index = ReadIndex();
        foreach (var note in notes)
        {
            var name = EnsureFileName(note);
            File.WriteAllText(Path.Combine(_folder, name), VaultMarkdown.ToMarkdown(note));
            RecordSelfWrite(name);
            index[note.Id.ToString()] = StateOf(note, name);
        }
        WriteIndex(index);
    }

    /// Saves only one note (used when changing — updates only 1 file + index).
    public void SaveOne(Note note)
    {
        Directory.CreateDirectory(_folder);
        var name = EnsureFileName(note);
        File.WriteAllText(Path.Combine(_folder, name), VaultMarkdown.ToMarkdown(note));
        RecordSelfWrite(name);
        var index = ReadIndex();
        index[note.Id.ToString()] = StateOf(note, name);
        WriteIndex(index);
    }

    /// Deletes only the .md (and index entry) of a single note managed by this repository. For permanent purge.
    public void DeleteFile(Guid id)
    {
        if (_files.TryGetValue(id, out var name))
        {
            try { File.Delete(Path.Combine(_folder, name)); } catch { /* Already missing or locked — ignore */ }
            RecordSelfWrite(name);
            _files.Remove(id);
        }
        var index = ReadIndex();
        if (index.Remove(id.ToString())) WriteIndex(index);
    }

    private string EnsureFileName(Note note)
    {
        if (_files.TryGetValue(note.Id, out var existing)) return existing;

        var taken = new HashSet<string>(_files.Values, StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.EnumerateFiles(_folder, "*.md", SearchOption.TopDirectoryOnly))
            taken.Add(Path.GetFileName(f).ToLowerInvariant());

        var name = ExportNaming.UniqueFileName(ExportNaming.SafeFileName(note.Title), ".md", taken);
        _files[note.Id] = name;
        return name;
    }

    /// In-memory id of the pushed (id duplicate) file — derived deterministically from (original id, file name)
    /// Even if reloading is repeated, the same id appears. When the user edits the note, this id is persisted to the file.
    private static Guid DisplacedId(Guid originalId, string fileName)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(originalId.ToString("N") + "|" + fileName.ToLowerInvariant()));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static NoteState StateOf(Note note, string name) => new(
        name, note.X, note.Y, note.Width, note.Height, note.Opacity,
        note.IsAlwaysOnTop, note.IsHidden, note.LinkedFilePath, note.IsDeleted, note.DeletedAt);

    private static void Apply(Note note, NoteState s)
    {
        note.X = s.X; note.Y = s.Y; note.Width = s.Width; note.Height = s.Height;
        note.Opacity = s.Opacity; note.IsAlwaysOnTop = s.IsAlwaysOnTop;
        note.IsHidden = s.IsHidden; note.LinkedFilePath = s.LinkedFilePath;
        note.IsDeleted = s.IsDeleted; note.DeletedAt = s.DeletedAt;
    }

    private Dictionary<string, NoteState> ReadIndex()
    {
        var path = Path.Combine(_folder, IndexFileName);
        if (!File.Exists(path)) return new();
        try { return JsonSerializer.Deserialize<Dictionary<string, NoteState>>(File.ReadAllText(path)) ?? new(); }
        catch { return new(); } // Ignore damaged indexes — read the .md content as is.
    }

    private void WriteIndex(Dictionary<string, NoteState> index) =>
        File.WriteAllText(Path.Combine(_folder, IndexFileName), JsonSerializer.Serialize(index, JsonOptions));
}
