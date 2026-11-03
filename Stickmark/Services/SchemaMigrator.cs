using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LiteDB;
using Serilog;

namespace Stickmark.Services;

/// Manage the DB schema version and sequentially migrate to the latest version expected by the app.
/// The version is stored in LiteDB's built-in <see cref="LiteDatabase.UserVersion"/>.
///
/// When the Note model/storage method changes going to 2.0 (vault/synchronization), add migration here
/// If you add +1 to <see cref="CurrentVersion"/>, the existing user DB will be automatically updated to the latest version.
public static class SchemaMigrator
{
    /// The current DB schema version that the app expects. +1 for every schema change and adding an entry to Migrations below.
    public const int CurrentVersion = 1;

    /// (From, Apply): Conversion from From version to From+1 version. Must be filled in without order/omission.
    private static readonly IReadOnlyList<(int From, Action<LiteDatabase> Apply)> Migrations =
        Array.Empty<(int, Action<LiteDatabase>)>();

    public static void Migrate(LiteDatabase db) => Migrate(db, CurrentVersion, Migrations);

    /// Only when actual schema migration is necessary, snapshot the file before opening the DB.
    /// If the DB version is already up to date (=general user), there is no burden as nothing is done.
    /// Even if migration goes wrong, it can be reverted to <c>notes.db.v{version}.bak</c>.
    public static void BackupBeforeMigration(string dbPath) => BackupBeforeMigration(dbPath, CurrentVersion);

    internal static void BackupBeforeMigration(string dbPath, int currentVersion)
    {
        try
        {
            if (!File.Exists(dbPath)) return;

            int version;
            using (var probe = new LiteDatabase($"Filename={dbPath};Connection=shared"))
                version = probe.UserVersion;
            if (version < 1) version = 1;            // 0 = version not indicated DB = baseline 1
            if (version >= currentVersion) return;   // Nothing to upload → No need for snapshots

            var backup = $"{dbPath}.v{version}.bak";
            if (File.Exists(backup)) return;       // This version snapshot already exists
            File.Copy(dbPath, backup);
            Log.Information("Backed up DB before schema migration (v{Version}) -> {Backup}", version, backup);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Pre-migration DB backup skipped");
        }
    }

    /// Testable core. From==Starts from the current version of the DB and applies it step by step up to target.
    internal static void Migrate(
        LiteDatabase db, int targetVersion,
        IReadOnlyList<(int From, Action<LiteDatabase> Apply)> migrations)
    {
        var version = db.UserVersion;

        // UserVersion 0 = DB for which schema version is not yet indicated (new or 1.x). The current Note form is
        // Since it is v1, it is considered baseline(1) without migration.
        if (version < 1) version = 1;

        // DB is newer than app (downgrade) — leave untouched.
        if (version > targetVersion)
        {
            Log.Warning("DB schema v{Version} is newer than app v{Target}; leaving as-is", version, targetVersion);
            return;
        }

        while (version < targetVersion)
        {
            var migration = migrations.FirstOrDefault(m => m.From == version);
            if (migration.Apply is null)
                throw new InvalidOperationException($"No migration registered from schema v{version}.");

            Log.Information("Migrating DB schema v{From} -> v{To}", version, version + 1);
            migration.Apply(db);
            version++;
        }

        if (db.UserVersion != version) db.UserVersion = version;
    }
}
