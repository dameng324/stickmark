using System;
using System.IO;
using System.Linq;
using Serilog;

namespace Stickmark.Services;

/// Safe one-time transfer when first switching to vault mode. If the vault folder is empty (=no .md) and the existing LiteDB is
/// If there are notes, copy the LiteDB notes (active + trash) to the vault.
/// Never overwrite a vault that already contains notes (data safety).
public static class VaultBootstrap
{
    public static void EnsureSeeded(string vaultFolder, string dbPath)
    {
        try
        {
            if (Directory.Exists(vaultFolder) &&
                Directory.EnumerateFiles(vaultFolder, "*.md", SearchOption.TopDirectoryOnly).Any())
            {
                return; // Vault that already has a note — don't touch it.
            }
            if (!File.Exists(dbPath)) return;

            using var db = new NoteRepository(dbPath);
            // Make the vault folder match the active list — only move notes visible in the list.
            // Excluding trash notes and empty placeholder notes (not listed in the vault folder)
            // Prevents the problem of creating a lot of .md files). Because the original notes.db is preserved as is,
            // The recycle bin can be viewed again by returning to LiteDB mode.
            var toSeed = db.GetAllAsync().GetAwaiter().GetResult()
                .Where(n => !string.IsNullOrWhiteSpace(n.PlainText))
                .ToList();
            if (toSeed.Count == 0) return;

            new VaultStore(vaultFolder).Save(toSeed);
            Log.Information("Seeded vault {Folder} from {Count} active notes", vaultFolder, toSeed.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Vault seed failed for {Folder}", vaultFolder);
        }
    }
}
