using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using LiteDB;
using Stickmark.Models;
using Stickmark.Resources;
using Stickmark.Services;
using Stickmark.Utils;

namespace Stickmark.Tests;

[Collection("LiteDb")] // There is a test that opens the actual shared mode DB file, so it can be serialized (to prevent flaking)
public class SchemaMigratorTests
{
    private static LiteDatabase NewDb() => new(new MemoryStream());
    private static readonly (int, Action<LiteDatabase>)[] None = Array.Empty<(int, Action<LiteDatabase>)>();

    [Fact]
    public void FreshDb_StampedToBaseline_AndIdempotent()
    {
        using var db = NewDb();
        Assert.Equal(0, db.UserVersion);            // before display
        SchemaMigrator.Migrate(db, 1, None);
        Assert.Equal(1, db.UserVersion);            // baseline
        SchemaMigrator.Migrate(db, 1, None);        // run again
        Assert.Equal(1, db.UserVersion);            // unchanged (idempotent)
    }

    [Fact]
    public void RunsMigrationsInOrder()
    {
        using var db = NewDb();
        var applied = new List<int>();
        var migrations = new (int, Action<LiteDatabase>)[]
        {
            (1, _ => applied.Add(1)),
            (2, _ => applied.Add(2)),
        };
        SchemaMigrator.Migrate(db, 3, migrations);
        Assert.Equal(new[] { 1, 2 }, applied);      // in order v1→v2→v3
        Assert.Equal(3, db.UserVersion);
    }

    [Fact]
    public void ThrowsOnMissingMigration()
    {
        using var db = NewDb();
        var migrations = new (int, Action<LiteDatabase>)[] { (1, _ => { }) }; // 2→3 None
        Assert.Throws<InvalidOperationException>(() => SchemaMigrator.Migrate(db, 3, migrations));
    }

    [Fact]
    public void NewerDb_LeftUntouched()
    {
        using var db = NewDb();
        db.UserVersion = 5;                          // newer than the app (downgrade scenario)
        SchemaMigrator.Migrate(db, 1, None);
        Assert.Equal(5, db.UserVersion);             // left untouched
    }

    [Fact]
    public void BackupBeforeMigration_SnapshotsWhenMigrationPending()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sp-mig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "notes.db");
        try
        {
            using (var db = new LiteDatabase($"Filename={dbPath};Connection=shared")) db.UserVersion = 1;
            SchemaMigrator.BackupBeforeMigration(dbPath, currentVersion: 2);   // app expects v2 → snapshot
            Assert.True(File.Exists(dbPath + ".v1.bak"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void BackupBeforeMigration_NoBackupWhenUpToDate()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sp-mig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "notes.db");
        try
        {
            using (var db = new LiteDatabase($"Filename={dbPath};Connection=shared")) db.UserVersion = 1;
            SchemaMigrator.BackupBeforeMigration(dbPath, currentVersion: 1);   // Already up to date → No backup
            Assert.False(File.Exists(dbPath + ".v1.bak"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}

public class SecurityHardeningTests
{
    [Fact]
    public void Import_RichTextXaml_DowngradedToPlainText_KillsXamlSink()
    {
        var id = Guid.NewGuid();
        var note = new Note
        {
            Id = id,
            Format = NoteContentFormat.RichTextXaml,
            Content = "<Section><ObjectDataProvider MethodName=\"Start\"/></Section>",
            PlainText = "Hello",
            LinkedFilePath = @"C:\Users\x\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\evil.cmd",
        };

        var r = BackupService.SanitizeImportedNote(note);

        Assert.Equal(NoteContentFormat.PlainText, r.Format);   // does not reach the XAML loader
        Assert.Equal("Hello", r.Content);                        // original content is not XAML
        Assert.DoesNotContain("ObjectDataProvider", r.Content);
        Assert.Null(r.LinkedFilePath);                          // Remove random file write path
        Assert.Equal(id, r.Id);                                 // Id preserved (for normal restore)
    }

    [Fact]
    public void Import_NonXaml_KeepsContent_StripsLinkedPath()
    {
        var r = BackupService.SanitizeImportedNote(new Note
        {
            Format = NoteContentFormat.Markdown,
            Content = "# hi",
            LinkedFilePath = @"C:\x\a.md",
        });
        Assert.Equal(NoteContentFormat.Markdown, r.Format);
        Assert.Equal("# hi", r.Content);
        Assert.Null(r.LinkedFilePath);
    }

    [Fact]
    public void ExportDocument_Csp_BlocksRemoteResources()
    {
        var note = new Note { Title = "t", Format = NoteContentFormat.PlainText, PlainText = "x", Content = "x" };
        var html = HtmlRenderer.RenderDocument(new[] { note }, "doc");
        Assert.Contains("default-src 'none'; img-src data:; media-src data:; ", html);
        Assert.DoesNotContain("img-src data: https", html);   // Do not allow remote images
    }

    [Theory]
    [InlineData("v1.6.1", true)]
    [InlineData("1.6.1", true)]
    [InlineData("v1.6.1.0", true)]
    [InlineData("v2", true)]
    [InlineData("x\" & calc & \"", false)] // Attempt cmd injection
    [InlineData("1.6.0; whoami", false)]
    [InlineData("", false)]
    public void Update_TagValidation(string tag, bool expected) =>
        Assert.Equal(expected, UpdateService.IsSafeReleaseTag(tag));

    [Fact]
    public void DangerousXaml_GadgetDetected_AndKeptFromParser()
    {
        var payload =
            "<Section xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
            "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" " +
            "xmlns:d=\"clr-namespace:System.Diagnostics;assembly=System.Diagnostics.Process\">" +
            "<Section.Resources><ObjectDataProvider x:Key=\"p\" ObjectType=\"{x:Type d:Process}\" MethodName=\"Start\"/></Section.Resources></Section>";

        Assert.True(TextExtraction.ContainsDangerousXaml(payload));
        // Since the guard blocks it in front of the parser, it is not executed and only the tag removal text is returned.
        var text = TextExtraction.ToPlainText(payload, NoteContentFormat.RichTextXaml);
        Assert.DoesNotContain("<ObjectDataProvider", text);
    }

    [Fact]
    public void LegitFlowXaml_NotFlagged()
    {
        var legit =
            "<Section xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">" +
            "<Paragraph><Run>hello</Run> <Bold>world</Bold></Paragraph></Section>";
        Assert.False(TextExtraction.ContainsDangerousXaml(legit));   // no false positive
    }

    [Theory]
    [InlineData("https://github.com/dameng324/stickmark/releases/download/v3.0.0/Stickmark-v3.0.0-win-x64-setup.exe", true)]
    [InlineData("https://objects.githubusercontent.com/abc", true)]
    [InlineData("http://github.com/x", false)] // Plain text
    [InlineData("https://evil.example/Stickmark.exe", false)]
    [InlineData("file:///C:/evil.exe", false)]
    [InlineData(null, false)]
    public void Update_UrlValidation(string? url, bool expected) =>
        Assert.Equal(expected, UpdateService.IsTrustedDownloadUrl(url));
}

public class UpdateIntegrityTests
{
    private const string HelloSha256 = "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824";

    [Fact]
    public void VerifyChecksum_MatchesCorrectHash_RejectsOthers()
    {
        var data = System.Text.Encoding.UTF8.GetBytes("hello");
        Assert.True(UpdateService.VerifyChecksum(data, HelloSha256 + "  Stickmark-setup.exe"));
        Assert.True(UpdateService.VerifyChecksum(data, HelloSha256.ToUpperInvariant() + " x")); // case-insensitive
        Assert.False(UpdateService.VerifyChecksum(data, new string('0', 64) + " x"));            // mismatch
        Assert.False(UpdateService.VerifyChecksum(data, "not-a-hash"));                           // no hex
        Assert.False(UpdateService.VerifyChecksum(data, null));                                   // missing → rejected
    }

    [Fact]
    public void ExtractSha256Hex_ParsesTokenOrNull()
    {
        Assert.Equal(HelloSha256, UpdateService.ExtractSha256Hex(HelloSha256 + "  file.exe"));
        Assert.Null(UpdateService.ExtractSha256Hex("nope"));
        Assert.Null(UpdateService.ExtractSha256Hex(""));
    }

    [Fact]
    public void ParseRelease_PicksInstallerAndChecksumAssets()
    {
        var json = @"{""tag_name"":""v1.7.0"",""assets"":[
            {""name"":""Stickmark-v1.7.0-win-x64-setup.exe"",""browser_download_url"":""https://github.com/a/b/releases/download/v1.7.0/Stickmark-v1.7.0-win-x64-setup.exe""},
            {""name"":""Stickmark-v1.7.0-win-x64-setup.exe.sha256"",""browser_download_url"":""https://github.com/a/b/releases/download/v1.7.0/Stickmark-v1.7.0-win-x64-setup.exe.sha256""}]}";
        var r = UpdateService.ParseRelease(json);
        Assert.NotNull(r);
        Assert.EndsWith("win-x64-setup.exe", r!.DownloadUrl);
        Assert.EndsWith(".exe.sha256", r.ChecksumUrl);
    }
}

public class VaultMarkdownTests
{
    [Fact]
    public void RoundTrip_Markdown_PreservesEverything()
    {
        var n = new Note
        {
            Id = Guid.NewGuid(),
            Title = "Meeting Notes",
            Content = "# Meeting\n\n- Agenda 1\n- Agenda 2",
            Format = NoteContentFormat.Markdown,
            Color = NoteColor.Blue,
            Tags = new() { "Work", "Meeting" },
            CreatedAt = new DateTime(2026, 7, 1, 9, 30, 0, DateTimeKind.Utc),
            ModifiedAt = new DateTime(2026, 7, 5, 14, 0, 0, DateTimeKind.Utc),
        };
        var r = VaultMarkdown.FromMarkdown(VaultMarkdown.ToMarkdown(n));
        Assert.Equal(n.Id, r.Id);
        Assert.Equal(NoteContentFormat.Markdown, r.Format);
        Assert.Equal(NoteColor.Blue, r.Color);
        Assert.Equal(n.Content, r.Content.TrimEnd());
        Assert.Equal(new[] { "Work", "Meeting" }, r.Tags);
        Assert.Equal(n.CreatedAt, r.CreatedAt);
        Assert.Equal(n.ModifiedAt, r.ModifiedAt);
        Assert.Equal("Meeting Notes", r.Title);
    }

    [Fact]
    public void RichTextXaml_DowngradedToReadableText()
    {
        var n = new Note
        {
            Format = NoteContentFormat.RichTextXaml,
            Content = "<Section><Run>x</Run></Section>",
            PlainText = "hello world",
            Color = NoteColor.Pink,
        };
        var md = VaultMarkdown.ToMarkdown(n);
        Assert.Contains("format: PlainText", md);
        Assert.Contains("hello world", md);
        Assert.DoesNotContain("<Section>", md);        // XAML does not leak to the vault
        var r = VaultMarkdown.FromMarkdown(md);
        Assert.Equal(NoteContentFormat.PlainText, r.Format);
        Assert.Equal("hello world", r.Content.Trim());
    }

    [Fact]
    public void PlainMarkdown_NoFrontMatter_ParsedTolerantly()
    {
        var r = VaultMarkdown.FromMarkdown("# Hello\n\nSome #idea here", "notes.md");
        Assert.NotEqual(Guid.Empty, r.Id);              //Give new id
        Assert.Equal(NoteContentFormat.Markdown, r.Format);
        Assert.Contains("Hello", r.Content);
        Assert.Contains("idea", r.Tags);                // Extract tags from the body
    }

    [Fact]
    public void BadValues_FallBackSafely()
    {
        var md = "---\nid: not-a-guid\ntitle: X\nformat: Bogus\ncolor: Rainbow\n---\n\nbody";
        var r = VaultMarkdown.FromMarkdown(md);
        Assert.NotEqual(Guid.Empty, r.Id);              // Bad id → New Guid
        Assert.Equal(NoteContentFormat.Markdown, r.Format); // Bad format → default
        Assert.Equal(NoteColor.Yellow, r.Color);        // Bad color → Default
        Assert.Equal("body", r.Content.Trim());
    }
}

public class VaultStoreTests
{
    private static string TempFolder() => Path.Combine(Path.GetTempPath(), "sp-vault-" + Guid.NewGuid().ToString("N"));
    private static void TryDelete(string dir) { try { Directory.Delete(dir, true); } catch { } }

    [Fact]
    public void SaveThenLoad_RoundTripsContentStateAndTrash()
    {
        var folder = TempFolder();
        try
        {
            var active = new Note
            {
                Id = Guid.NewGuid(), Title = "Meeting", Content = "# Meeting\n\n- x",
                Format = NoteContentFormat.Markdown, Color = NoteColor.Blue, Tags = new() { "Work" },
                X = 120, Y = 240, Width = 360, Height = 300, Opacity = 0.8, IsAlwaysOnTop = true, ZoomFactor = 1.35,
                CreatedAt = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
                ModifiedAt = new DateTime(2026, 7, 5, 0, 0, 0, DateTimeKind.Utc),
            };
            var trashed = new Note
            {
                Id = Guid.NewGuid(), Title = "Old Note", Content = "old", Format = NoteContentFormat.PlainText,
                IsDeleted = true, DeletedAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            };
            new VaultStore(folder).Save(new[] { active, trashed });

            var loaded = new VaultStore(folder).Load();   // new instance = restart simulation
            var a = loaded.Single(n => n.Id == active.Id);
            var t = loaded.Single(n => n.Id == trashed.Id);

            Assert.Equal(active.Content, a.Content.TrimEnd());
            Assert.Equal(NoteColor.Blue, a.Color);
            Assert.Equal(new[] { "Work" }, a.Tags);
            Assert.Equal(120.0, a.X);
            Assert.Equal(360.0, a.Width);
            Assert.Equal(0.8, a.Opacity, 6);
            Assert.True(a.IsAlwaysOnTop);
            Assert.Equal(1.35, a.ZoomFactor, 6);
            Assert.False(a.IsDeleted);
            Assert.True(t.IsDeleted);                       // round trip to trash can state
            Assert.Equal(trashed.DeletedAt, t.DeletedAt);
        }
        finally { TryDelete(folder); }
    }

    [Fact]
    public void Save_ReusesFileNameForSameId()
    {
        var folder = TempFolder();
        try
        {
            var store = new VaultStore(folder);
            var note = new Note { Id = Guid.NewGuid(), Title = "First", Content = "a", Format = NoteContentFormat.Markdown };
            store.Save(new[] { note });
            var firstFile = Directory.EnumerateFiles(folder, "*.md").Single();

            note.Title = "Renamed"; note.Content = "b";     // Even if the title changes
            store.Save(new[] { note });
            var files = Directory.EnumerateFiles(folder, "*.md").ToList();

            Assert.Single(files);                            // do not create new file
            Assert.Equal(firstFile, files[0]);               // same file
        }
        finally { TryDelete(folder); }
    }

    [Fact]
    public void DeleteFile_RemovesOnlyThatNote()
    {
        var folder = TempFolder();
        try
        {
            var store = new VaultStore(folder);
            var a = new Note { Id = Guid.NewGuid(), Title = "keep", Content = "a", Format = NoteContentFormat.Markdown };
            var b = new Note { Id = Guid.NewGuid(), Title = "drop", Content = "b", Format = NoteContentFormat.Markdown };
            store.Save(new[] { a, b });

            store.DeleteFile(b.Id);                          //delete only file b
            var remaining = new VaultStore(folder).Load();
            var r = Assert.Single(remaining);
            Assert.Equal(a.Id, r.Id);
        }
        finally { TryDelete(folder); }
    }

    [Fact]
    public void Load_ReadsHandAuthoredMarkdown_WithDefaults()
    {
        var folder = TempFolder();
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "hand.md"), "# Hand\n\nno front matter #idea");
            var r = Assert.Single(new VaultStore(folder).Load());

            Assert.NotEqual(Guid.Empty, r.Id);   // new id
            Assert.Equal(280.0, r.Width);        // Note default size
            Assert.Contains("idea", r.Tags);     // Extract body tags
        }
        finally { TryDelete(folder); }
    }
}

public class VaultRepositoryTests
{
    private static string TempFolder() => Path.Combine(Path.GetTempPath(), "sp-vaultrepo-" + Guid.NewGuid().ToString("N"));
    private static void TryDelete(string dir) { try { Directory.Delete(dir, true); } catch { } }
    private static Note NewNote(string title, string body = "body") => new()
    {
        Id = Guid.NewGuid(), Title = title, Content = body, PlainText = body, Format = NoteContentFormat.Markdown,
    };

    [Fact]
    public async Task Upsert_PersistsAcrossReopen()
    {
        var folder = TempFolder();
        try
        {
            var repo = new VaultRepository(folder);
            var a = NewNote("A"); var b = NewNote("B");
            await repo.UpsertAsync(a); await repo.UpsertAsync(b);
            Assert.Equal(2, (await repo.GetAllAsync()).Count);
            repo.Dispose();

            var repo2 = new VaultRepository(folder);   // Simulate restart — load from folder
            var all = await repo2.GetAllAsync();
            Assert.Equal(2, all.Count);
            Assert.Contains(all, n => n.Id == a.Id && n.Title == "A");
            repo2.Dispose();
        }
        finally { TryDelete(folder); }
    }

    [Fact]
    public async Task Delete_Restore_TrashSemantics()
    {
        var folder = TempFolder();
        try
        {
            var repo = new VaultRepository(folder);
            var a = NewNote("A"); await repo.UpsertAsync(a);
            await repo.DeleteAsync(a.Id);
            Assert.Empty(await repo.GetAllAsync());
            Assert.Single(await repo.GetTrashedAsync());
            Assert.Null(await repo.GetByIdAsync(a.Id));    // Recycle bin is excluded from GetById
            await repo.RestoreAsync(a.Id);
            Assert.Single(await repo.GetAllAsync());
            Assert.Empty(await repo.GetTrashedAsync());
            repo.Dispose();
        }
        finally { TryDelete(folder); }
    }

    [Fact]
    public async Task Purge_RemovesNoteAndFile_AcrossReopen()
    {
        var folder = TempFolder();
        try
        {
            var repo = new VaultRepository(folder);
            var a = NewNote("Keep"); var b = NewNote("Drop");
            await repo.UpsertAsync(a); await repo.UpsertAsync(b);
            await repo.DeleteAsync(b.Id);
            await repo.PurgeAsync(b.Id);
            Assert.Empty(await repo.GetTrashedAsync());
            Assert.Single(await repo.GetAllAsync());
            repo.Dispose();

            var repo2 = new VaultRepository(folder);
            Assert.Single(await repo2.GetAllAsync());
            Assert.Empty(await repo2.GetTrashedAsync());
            repo2.Dispose();
        }
        finally { TryDelete(folder); }
    }

    [Fact]
    public async Task SaveContent_PreservesTrash_AndDoesNotResurrect()
    {
        var folder = TempFolder();
        try
        {
            var repo = new VaultRepository(folder);
            var a = NewNote("A"); await repo.UpsertAsync(a);
            await repo.DeleteAsync(a.Id);                  // to trash can

            var stale = NewNote("A"); stale.Id = a.Id; stale.IsDeleted = false; // old memory copy
            await repo.SaveContentAsync(stale);
            Assert.Empty(await repo.GetAllAsync());        // not resurrected
            Assert.Single(await repo.GetTrashedAsync());

            await repo.PurgeAsync(a.Id);
            await repo.SaveContentAsync(NewNote("ghost")); // Notes not in cache → ignore
            Assert.DoesNotContain(await repo.GetAllAsync(), n => n.Title == "ghost");
            repo.Dispose();
        }
        finally { TryDelete(folder); }
    }

    [Fact]
    public async Task PurgeTrashedOlderThan_RemovesOldTrash()
    {
        var folder = TempFolder();
        try
        {
            var repo = new VaultRepository(folder);
            var a = NewNote("A"); await repo.UpsertAsync(a);
            await repo.DeleteAsync(a.Id);
            var count = await repo.PurgeTrashedOlderThanAsync(DateTime.UtcNow.AddMinutes(1));
            Assert.Equal(1, count);
            Assert.Empty(await repo.GetTrashedAsync());
            repo.Dispose();
        }
        finally { TryDelete(folder); }
    }
}

// Tests that open LiteDB (Connection=shared) files rarely have lock contention when executed in parallel.
// Grouped into the same collection and executed sequentially (removing flaky).
[Collection("LiteDb")]
public class VaultBootstrapTests
{
    [Fact]
    public async Task EnsureSeeded_CopiesLiteDbNotes_OnlyIntoEmptyVault()
    {
        var root = Path.Combine(Path.GetTempPath(), "sp-boot-" + Guid.NewGuid().ToString("N"));
        var dbPath = Path.Combine(root, "notes.db");
        var vault = Path.Combine(root, "vault");
        try
        {
            Directory.CreateDirectory(root);
            using (var db = new NoteRepository(dbPath))
            {
                await db.UpsertAsync(new Note { Title = "A", Content = "a", PlainText = "a", Format = NoteContentFormat.Markdown });
                await db.UpsertAsync(new Note { Title = "B", Content = "b", PlainText = "b", Format = NoteContentFormat.Markdown });
            }

            VaultBootstrap.EnsureSeeded(vault, dbPath);
            Assert.Equal(2, new VaultStore(vault).Load().Count);   // LiteDB → Vault transfer

            VaultBootstrap.EnsureSeeded(vault, dbPath);            // Even if called again
            Assert.Equal(2, new VaultStore(vault).Load().Count);   // Do not touch non-empty bolts
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task EnsureSeeded_SkipsTrashedAndEmptyNotes()
    {
        var root = Path.Combine(Path.GetTempPath(), "sp-boot-" + Guid.NewGuid().ToString("N"));
        var dbPath = Path.Combine(root, "notes.db");
        var vault = Path.Combine(root, "vault");
        try
        {
            Directory.CreateDirectory(root);
            using (var db = new NoteRepository(dbPath))
            {
                await db.UpsertAsync(new Note { Title = "Keep", Content = "real", PlainText = "real", Format = NoteContentFormat.Markdown });
                // Empty (contentless) placeholder note — visible in the list but not moved to the vault.
                await db.UpsertAsync(new Note { Title = "Blank", Content = "", PlainText = "   ", Format = NoteContentFormat.Markdown });
                // Recycle Bin Note — Not moved to vault as it is not in the active list.
                var trashed = new Note { Title = "Gone", Content = "was here", PlainText = "was here", Format = NoteContentFormat.Markdown };
                await db.UpsertAsync(trashed);
                await db.DeleteAsync(trashed.Id);
            }

            VaultBootstrap.EnsureSeeded(vault, dbPath);

            var seeded = new VaultStore(vault).Load();
            Assert.Single(seeded);                 // Only transfer notes that are not active or empty
            Assert.Equal("Keep", seeded[0].Title);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}

public class TodoExtractionTests
{
    // ── Extraction ────────────────────────────────────────────────────────────────

    [Fact]
    public void Extract_RecognizesBulletsIndentAndCase()
    {
        var src = "- [ ] one\n* [x] two\n  + [X] three\n- not a task\ntext [ ] middle\n";
        var tasks = TodoExtraction.ExtractTasks(src);

        Assert.Equal(3, tasks.Count);
        Assert.Equal((0, false, "one"), (tasks[0].LineIndex, tasks[0].IsChecked, tasks[0].Text));
        Assert.Equal((1, true, "two"), (tasks[1].LineIndex, tasks[1].IsChecked, tasks[1].Text));
        Assert.Equal((2, true, "three"), (tasks[2].LineIndex, tasks[2].IsChecked, tasks[2].Text));
    }

    [Fact]
    public void Extract_EmptyTextAndEmptySource()
    {
        Assert.Empty(TodoExtraction.ExtractTasks(""));
        Assert.Empty(TodoExtraction.ExtractTasks(null));
        var only = Assert.Single(TodoExtraction.ExtractTasks("- [ ] "));
        Assert.Equal(string.Empty, only.Text);           // Checkbox without text is also an item
    }

    // ── Toggle: Byte-by-byte preservation is key ──────────────────────────────────────────

    [Theory]
    [InlineData("- [ ] a\n- [ ] b\n")]                    // LF
    [InlineData("- [ ] a\r\n- [ ] b\r\n")]                // CRLF
    [InlineData("- [ ] a\r\n- [ ] b\n- [ ] c")] // Mix + no trailing newline
    public void Toggle_RoundTrip_IsByteIdentical(string src)
    {
        var tasks = TodoExtraction.ExtractTasks(src);
        var toggled = TodoExtraction.ToggleLine(src, tasks[1].LineIndex, tasks[1].RawLine, check: true);
        Assert.NotNull(toggled);

        // Exactly 1 letter different
        Assert.Equal(src.Length, toggled!.Length);
        Assert.Equal(1, src.Zip(toggled).Count(p => p.First != p.Second));

        // When reverted, the original byte unit
        var backTasks = TodoExtraction.ExtractTasks(toggled);
        var back = TodoExtraction.ToggleLine(toggled, backTasks[1].LineIndex, backTasks[1].RawLine, check: false);
        Assert.Equal(src, back);
    }

    [Fact]
    public void Toggle_UppercaseX_UnchecksToSpace()
    {
        var result = TodoExtraction.ToggleLine("- [X] done", 0, "- [X] done", check: false);
        Assert.Equal("- [ ] done", result);
    }

    [Fact]
    public void Toggle_StaleLine_ReturnsNull()
    {
        var src = "- [ ] a\n- [ ] b";
        Assert.Null(TodoExtraction.ToggleLine(src, 1, "- [ ] EDITED", check: true));   // line changes
        Assert.Null(TodoExtraction.ToggleLine(src, 9, "- [ ] b", check: true));        // out of range
        Assert.Null(TodoExtraction.ToggleLine(src, 0, "plain text", check: true));     // Not a checkbox line
    }

    [Fact]
    public void Toggle_DuplicateLines_TogglesExactIndexOnly()
    {
        var src = "- [ ] same\n- [ ] same\n- [ ] same";
        var result = TodoExtraction.ToggleLine(src, 1, "- [ ] same", check: true);
        Assert.Equal("- [ ] same\n- [x] same\n- [ ] same", result);
    }

    [Fact]
    public void Toggle_AlreadyDesiredState_ReturnsSourceUnchanged()
    {
        var src = "- [x] done";
        Assert.Equal(src, TodoExtraction.ToggleLine(src, 0, "- [x] done", check: true));
    }

    [Fact]
    public void Toggle_PreservesIndentAndSurroundingText()
    {
        var src = "# Title\n\n - [ ] Indented item \nBody";
        var result = TodoExtraction.ToggleLine(src, 2, " - [ ] Indented item ", check: true);
        Assert.Equal("# Title\n\n - [x] Indented item \nBody", result);
    }
}

public class VaultIdDuplicateTests
{
    private static string NewDir() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "sp-dup-" + Guid.NewGuid().ToString("N"))).FullName;

    [Fact]
    public void Load_DuplicateId_IndexPointedFileIsCanonical_FilesUntouched()
    {
        var dir = NewDir();
        try
        {
            // Make the original copy SaveOne so that the index points to "Memo.md", then copy the conflicting copy with the same id.
            var note = new Note { Title = "Memo", Content = "Original", PlainText = "Original", Format = NoteContentFormat.Markdown };
            new VaultStore(dir).SaveOne(note);
            var canonicalPath = Path.Combine(dir, "memo.md");
            var conflictPath = Path.Combine(dir, "Memo (conflict copy).md");
            File.Copy(canonicalPath, conflictPath);
            var beforeCanonical = File.ReadAllText(canonicalPath);
            var beforeConflict = File.ReadAllText(conflictPath);

            var store = new VaultStore(dir);
            var loaded = store.Load();

            Assert.Equal(2, loaded.Count);                              // Both appear to be notes
            Assert.Single(loaded, n => n.Id == note.Id);                // There is only one original id
            var canonical = loaded.First(n => n.Id == note.Id);
            var displaced = loaded.First(n => n.Id != note.Id);
            Assert.NotEqual(Guid.Empty, displaced.Id);

            // Whether authentic edits are recorded in the authentic file — the key to blocking data accident paths.
            canonical.Content = "Modified";
            store.SaveOne(canonical);
            Assert.Contains("Modified", File.ReadAllText(canonicalPath));
            Assert.Equal(beforeConflict, File.ReadAllText(conflictPath));   // copy is immutable (read-only pass)

            // Load itself does not rewrite the file.
            Assert.True(beforeCanonical.Length > 0);

            // The id of the pushed note must be stable even after repeated reloads —
            // Otherwise the watch diff will oscillate with ghost additions/deletes throughout the copy's existence.
            var again = new VaultStore(dir).Load();
            Assert.Equal(displaced.Id, again.First(n => n.Id != note.Id).Id);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Load_DuplicateId_NoIndexMatch_ConflictPatternIsDisplaced()
    {
        var dir = NewDir();
        try
        {
            var note = new Note { Title = "b", Content = "x", PlainText = "x", Format = NoteContentFormat.Markdown };
            var md = VaultMarkdown.ToMarkdown(note);
            File.WriteAllText(Path.Combine(dir, "b.md"), md);
            File.WriteAllText(Path.Combine(dir, "b (conflicting copy).md"), md);
            // No index → The conflict pattern file must be on the side that is pushed out.

            var store = new VaultStore(dir);
            var loaded = store.Load();

            Assert.Equal(2, loaded.Count);
            var canonical = loaded.First(n => n.Id == note.Id);
            canonical.Content = "canon";
            store.SaveOne(canonical);
            Assert.Contains("canon", File.ReadAllText(Path.Combine(dir, "b.md")));               // Original = b.md
            Assert.DoesNotContain("canon", File.ReadAllText(Path.Combine(dir, "b (conflicting copy).md")));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Load_FrontmatterlessFile_GetsStableIdAcrossReloads()
    {
        var dir = NewDir();
        try
        {
            // .md without front matter, handcrafted by Obsidian etc.
            File.WriteAllText(Path.Combine(dir, "loose.md"), "# Add manually\nContent");

            var first = new VaultStore(dir).Load().Single();
            var second = new VaultStore(dir).Load().Single();

            Assert.Equal(first.Id, second.Id);          // ID stable between reloads/restarts
            Assert.NotEqual(Guid.Empty, first.Id);
            Assert.Equal("# Add manually\nContent", File.ReadAllText(Path.Combine(dir, "loose.md")));  // file immutable
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void SelfWriteLedger_TracksWritesWithinWindow()
    {
        var dir = NewDir();
        try
        {
            var store = new VaultStore(dir);
            var note = new Note { Title = "l", Content = "x", PlainText = "x", Format = NoteContentFormat.Markdown };
            store.SaveOne(note);

            Assert.True(store.IsRecentSelfWrite("l.md"));                                    // just wrote
            Assert.True(store.IsRecentSelfWrite("L.MD", DateTime.UtcNow));                   // ignore case
            Assert.False(store.IsRecentSelfWrite("l.md", DateTime.UtcNow.AddSeconds(4)));    // outside the time window
            Assert.False(store.IsRecentSelfWrite("other.md"));

            store.DeleteFile(note.Id);
            Assert.True(store.IsRecentSelfWrite("l.md"));                                    // Deletion is also self-written
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}

public class VaultDiffTests
{
    private static string NewDir() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "sp-diff-" + Guid.NewGuid().ToString("N"))).FullName;

    private static Note NewNote(string title, string content) => new()
    {
        Title = title, Content = content, PlainText = content, Format = NoteContentFormat.Markdown,
    };

    [Fact]
    public async Task ReloadWithDiff_DetectsAddChangeRemove_AndOldContent()
    {
        var dir = NewDir();
        try
        {
            using var repo = new VaultRepository(dir);
            var keep = NewNote("keep", "same");
            var edit = NewNote("edit", "before");
            var gone = NewNote("gone", "bye");
            await repo.UpsertAsync(keep);
            await repo.UpsertAsync(edit);
            await repo.UpsertAsync(gone);

            // No change → 3 sets empty (Echo backstop protocol: Reload immediately after SaveOne is no-op)
            var none = await repo.ReloadWithDiffAsync();
            Assert.True(none.IsEmpty);

            // Simulate external changes: edit / delete / add
            var editPath = Path.Combine(dir, "edit.md");
            File.WriteAllText(editPath, File.ReadAllText(editPath).Replace("before", "after"));
            File.Delete(Path.Combine(dir, "gone.md"));
            File.WriteAllText(Path.Combine(dir, "new.md"), "# New note\nAdd from external");

            var diff = await repo.ReloadWithDiffAsync();

            Assert.Single(diff.Changed);
            Assert.Equal(edit.Id, diff.Changed[0].Fresh.Id);
            Assert.Contains("after", diff.Changed[0].Fresh.Content);
            Assert.Contains("before", diff.Changed[0].OldContent);      // Previous content is correct

            Assert.Single(diff.Removed);
            Assert.Equal(gone.Id, diff.Removed[0].Id);
            Assert.Equal("bye", diff.Removed[0].OldContent);

            Assert.Single(diff.Added);
            Assert.Contains("Add from external", diff.Added[0].Content);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task ReloadWithDiff_ConcurrentWithSaves_NoLossNoException()
    {
        var dir = NewDir();
        try
        {
            using var repo = new VaultRepository(dir);
            var note = NewNote("c", "v0");
            await repo.UpsertAsync(note);

            // Even when diff reloading and saving occur (_gate serialization), there should be no exceptions or losses.
            for (var i = 1; i <= 30; i++)
            {
                note.Content = $"v{i}";
                note.PlainText = note.Content;
                await Task.WhenAll(
                    repo.ReloadWithDiffAsync(),
                    repo.SaveContentAsync(note));
            }

            var all = await repo.GetAllAsync();
            var survived = Assert.Single(all);
            Assert.Equal(note.Id, survived.Id);
            Assert.StartsWith("v", survived.Content);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}

public class VaultWatcherServiceTests
{
    private static string NewDir() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "sp-watch-" + Guid.NewGuid().ToString("N"))).FullName;

    private static VaultWatcherService NewWatcher(
        string folder, Func<string, bool> echo, Func<Task<VaultDiff>> reload,
        Action<string, string>? notify = null)
    {
        var w = new VaultWatcherService(
            echo, reload, _ => Task.CompletedTask, notify ?? ((_, _) => { }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<VaultWatcherService>.Instance);
        w.SetFolderForTest(folder);
        return w;
    }

    [Fact]
    public async Task AllEchoBatch_SkipsReload()
    {
        var dir = NewDir();
        try
        {
            var reloads = 0;
            using var w = NewWatcher(dir, _ => true,
                () => { Interlocked.Increment(ref reloads); return Task.FromResult(VaultDiff.Empty); });

            await w.ProcessBatchForTestAsync(new[] { Path.Combine(dir, "a.md"), Path.Combine(dir, "b.md") });
            Assert.Equal(0, reloads);   // echo all → do not reload
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task MixedBatch_ReloadsOnce()
    {
        var dir = NewDir();
        try
        {
            var reloads = 0;
            using var w = NewWatcher(dir, f => f == "mine.md",
                () => { Interlocked.Increment(ref reloads); return Task.FromResult(VaultDiff.Empty); });

            await w.ProcessBatchForTestAsync(new[] { Path.Combine(dir, "mine.md"), Path.Combine(dir, "theirs.md") });
            Assert.Equal(1, reloads);   // If even one external change occurs, reload once
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task ConcurrentCycles_AreSerialized()
    {
        var dir = NewDir();
        try
        {
            var inFlight = 0; var maxInFlight = 0; var reloads = 0;
            using var w = NewWatcher(dir, _ => false, async () =>
            {
                var now = Interlocked.Increment(ref inFlight);
                InterlockedMax(ref maxInFlight, now);
                await Task.Delay(50);
                Interlocked.Decrement(ref inFlight);
                Interlocked.Increment(ref reloads);
                return VaultDiff.Empty;
            });

            await Task.WhenAll(
                w.ProcessBatchForTestAsync(new[] { Path.Combine(dir, "x.md") }),
                w.ProcessBatchForTestAsync(new[] { Path.Combine(dir, "y.md") }));

            Assert.Equal(1, maxInFlight);   // 1 cycle simultaneously (serialized)
            Assert.True(reloads >= 1);      // second batch can be absorbed into first cycle (path accumulation)
        }
        finally { try { Directory.Delete(dir, true); } catch { } }

        static void InterlockedMax(ref int target, int value)
        {
            int snapshot;
            while (value > (snapshot = Volatile.Read(ref target)))
            {
                if (Interlocked.CompareExchange(ref target, value, snapshot) == snapshot) break;
            }
        }
    }

    [Fact]
    public async Task ConflictCopyInBatch_NotifiesOncePerSession()
    {
        var dir = NewDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "memo.md"), "x");
            File.WriteAllText(Path.Combine(dir, "Memo (conflict copy).md"), "x");

            var notifications = 0;
            using var w = NewWatcher(dir, _ => false, () => Task.FromResult(VaultDiff.Empty),
                (_, _) => notifications++);

            var conflictPath = Path.Combine(dir, "Memo (conflict copy).md");
            await w.ProcessBatchForTestAsync(new[] { conflictPath });
            await w.ProcessBatchForTestAsync(new[] { conflictPath });   //Same file re-event

            Assert.Equal(1, notifications);   // Only once per session
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}

public class EmbeddedAssetTests
{
    // WYSIWYG editor assets must be physically embedded in the assembly (in single file deployments)
    // Prevent a regression that would fail with "Path not found" due to missing folder next to the exe).
    [Theory]
    [InlineData(".editor.html")]
    [InlineData(".mdeditor.bundle.js")]
    public void MdEditorAsset_IsEmbeddedAndNonEmpty(string suffix)
    {
        var asm = typeof(Stickmark.Utils.MarkdownWysiwyg).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(name);

        using var s = asm.GetManifestResourceStream(name!);
        Assert.NotNull(s);
        Assert.True(s!.Length > 0);
    }
}

public class LocalizationTests
{
    [Fact]
    public void Strings_ResolvePerCulture()
    {
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("en");
            Assert.Equal("Settings", Strings.Settings_Title);      // neutral (English) fallback
            Assert.Equal("Save", Strings.Common_Save);

            // Non-English cultures fall back to the neutral English resources.
            CultureInfo.CurrentUICulture = new CultureInfo("ko");
            Assert.Equal("Settings", Strings.Settings_Title);

            CultureInfo.CurrentUICulture = new CultureInfo("fr");
            Assert.Equal("Settings", Strings.Settings_Title);
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }
}

public class HotkeyGestureTests
{
    [Theory]
    [InlineData("Ctrl+Shift+N", Key.N, ModifierKeys.Control | ModifierKeys.Shift)]
    [InlineData("ctrl+l", Key.L, ModifierKeys.Control)]
    [InlineData("Ctrl+Alt+1", Key.D1, ModifierKeys.Control | ModifierKeys.Alt)]
    public void TryParse_ValidGestures(string gesture, Key expectedKey, ModifierKeys expectedMods)
    {
        Assert.True(HotkeyGesture.TryParse(gesture, out var key, out var mods));
        Assert.Equal(expectedKey, key);
        Assert.Equal(expectedMods, mods);
    }

    [Theory]
    [InlineData("N")]          // no modifier
    [InlineData("")]           // empty
    [InlineData("Ctrl+")]      // no key
    [InlineData("Ctrl+Bogus")] // unknown key
    public void TryParse_Rejects(string gesture)
    {
        Assert.False(HotkeyGesture.TryParse(gesture, out _, out _));
    }

    [Fact]
    public void Format_RoundTrips()
    {
        var text = HotkeyGesture.Format(Key.N, ModifierKeys.Control | ModifierKeys.Shift);
        Assert.Equal("Ctrl+Shift+N", text);
        Assert.True(HotkeyGesture.TryParse(text, out var key, out var mods));
        Assert.Equal(Key.N, key);
        Assert.Equal(ModifierKeys.Control | ModifierKeys.Shift, mods);
    }
}

public class TextExtractionTests
{
    [Fact]
    public void ExtractTags_DedupesAndKeepsOrder()
    {
        var tags = TextExtraction.ExtractTags("todo #work stuff #idea more #work");
        Assert.Equal(new[] { "work", "idea" }, tags);
    }

    [Fact]
    public void ExtractTags_NoneWhenAbsent()
    {
        Assert.Empty(TextExtraction.ExtractTags("plain text, email a#b not a tag"));
    }

    [Fact]
    public void FindLinks_FindsWikiTitle()
    {
        var links = TextExtraction.FindLinks("see [[My Note]] here").ToList();
        Assert.Single(links);
        Assert.Equal("My Note", links[0].Title);
    }

    [Fact]
    public void FindUrls_StripsTrailingPunctuation()
    {
        var urls = TextExtraction.FindUrls("visit https://example.com/path, now").ToList();
        Assert.Single(urls);
        Assert.Equal("https://example.com/path", urls[0].Url);
    }

    [Fact]
    public void DeriveTitle_UsesFirstNonEmptyLine()
    {
        Assert.Equal("Hello world", TextExtraction.DeriveTitle("\n\n  Hello world \nmore"));
    }

    [Fact]
    public void ExcerptOf_TruncatesLongText()
    {
        var excerpt = TextExtraction.ExcerptOf(new string('a', 300));
        Assert.EndsWith("…", excerpt);
        Assert.True(excerpt.Length <= 161);
    }

    [Fact]
    public void ToPlainText_StripsMarkdown()
    {
        var plain = TextExtraction.ToPlainText("# Heading\n\nsome **bold** text", NoteContentFormat.Markdown);
        Assert.Contains("Heading", plain);
        Assert.Contains("bold", plain);
        Assert.DoesNotContain("#", plain);
        Assert.DoesNotContain("**", plain);
    }

    [Fact]
    public void ToPlainText_StripsHtmlTags()
    {
        var plain = TextExtraction.ToPlainText("<p>hi <b>there</b></p>", NoteContentFormat.Html);
        Assert.Equal("hi there", plain.Trim());
    }

    [Fact]
    public void ToPlainText_PlainPassthrough()
    {
        Assert.Equal("just text", TextExtraction.ToPlainText("just text", NoteContentFormat.PlainText));
    }
}

public class HtmlRendererTests
{
    private static NoteTheme Theme => NotePalette.For(NoteColor.Yellow);

    [Fact]
    public void Render_Markdown_ProducesHtmlAndCsp()
    {
        var html = HtmlRenderer.Render("# Hi\n\ntext", NoteContentFormat.Markdown, Theme);
        Assert.Contains("<h1", html);
        Assert.Contains("Hi", html);
        Assert.Contains("Content-Security-Policy", html);
        Assert.DoesNotContain("script-src", html); // scripts not allowed
    }

    [Fact]
    public void Render_Html_PassesThrough()
    {
        var html = HtmlRenderer.Render("<b>x</b>", NoteContentFormat.Html, Theme);
        Assert.Contains("<b>x</b>", html);
    }

    [Fact]
    public void Render_PlainText_IsEscaped()
    {
        var html = HtmlRenderer.Render("a < b & c", NoteContentFormat.PlainText, Theme);
        Assert.Contains("a &lt; b &amp; c", html);
    }

    // ── Code block syntax highlighting (spec-1 §5-2) ──────────────────────────────────────

    [Fact]
    public void Render_FencedCSharp_IsHighlightedInlineStyles()
    {
        var html = HtmlRenderer.Render("```csharp\nvar x = \"s\";\n```", NoteContentFormat.Markdown, Theme);
        Assert.Contains("style=", html);            // inline style highlight span
        Assert.Contains("var", html);               // Preserve original code text
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Render_FencedHtmlWithScript_DoesNotEmitExecutableScript()
    {
        // The code body is always escaped, even in the highlight path — no executable <script> tags should appear.
        var html = HtmlRenderer.Render("```html\n<script>alert(1)</script>\n```", NoteContentFormat.Markdown, Theme);
        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("alert(1)", html);          // Must remain as text (escaped form)
    }

    [Fact]
    public void Render_UnsupportedLanguage_FallsBackToEscapedPre()
    {
        // For unsupported languages, fallback to the same <pre><code> escape as before.
        var html = HtmlRenderer.Render("```zig\nconst a = <b>;\n```", NoteContentFormat.Markdown, Theme);
        Assert.Contains("<pre><code", html);
        Assert.Contains("&lt;b&gt;", html);
    }

    [Fact]
    public void Render_InlineCode_NotHighlighted()
    {
        var html = HtmlRenderer.Render("say `var x` here", NoteContentFormat.Markdown, Theme);
        Assert.Contains("<code>var x</code>", html);   // Inline code remains in the existing style
    }

    [Fact]
    public void RenderDocument_HighlightsFencedCode_AndKeepsStrictCsp()
    {
        var note = new Note
        {
            Title = "t", Format = NoteContentFormat.Markdown,
            Content = "```js\nconst n = 1;\n```", PlainText = "const n = 1;",
        };
        var html = HtmlRenderer.RenderDocument(new[] { note }, "doc");
        Assert.Contains("style=", html);                                     // Apply highlighting to export as well
        Assert.Contains("default-src 'none'; img-src data:; media-src data:; ", html); // Strictly CSP immutable
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Render_JsonAlias_MapsToHighlighter()
    {
        var html = HtmlRenderer.Render("```json\n{ \"k\": 1 }\n```", NoteContentFormat.Markdown, Theme);
        Assert.Contains("style=", html);            // highlight with json → js approximate mapping
    }
}

public class UpdateServiceVersionTests
{
    [Fact]
    public void Compare_NewerIsGreater()
    {
        Assert.True(UpdateService.Compare(new Version(1, 3, 0), new Version(1, 2, 9)) > 0);
        Assert.True(UpdateService.Compare(new Version(2, 0, 0), new Version(1, 9, 9)) > 0);
    }

    [Fact]
    public void Compare_IgnoresRevision()
    {
        Assert.Equal(0, UpdateService.Compare(new Version(1, 3, 0, 0), new Version(1, 3, 0)));
    }

    [Theory]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("1.4.0", 1, 4, 0)]
    public void ParseVersion_HandlesTag(string tag, int maj, int min, int build)
    {
        var v = UpdateService.ParseVersion(tag);
        Assert.NotNull(v);
        Assert.Equal(maj, v!.Major);
        Assert.Equal(min, v.Minor);
        Assert.Equal(build, v.Build);
    }

    [Fact]
    public void ParseVersion_NullOnGarbage() => Assert.Null(UpdateService.ParseVersion("nope"));

    [Fact]
    public void ParseRelease_ReadsTagAndAsset()
    {
        const string json = """
        {
          "tag_name": "v1.4.0",
          "assets": [
            { "name": "notes.txt", "browser_download_url": "https://x/notes.txt" },
            { "name": "Stickmark-v1.4.0-win-x64-setup.exe", "browser_download_url": "https://x/app.exe" }
          ]
        }
        """;
        var info = UpdateService.ParseRelease(json);
        Assert.NotNull(info);
        Assert.Equal("v1.4.0", info!.Tag);
        Assert.Equal(new Version(1, 4, 0), info.Version);
        Assert.Equal("https://x/app.exe", info.DownloadUrl);
    }
}

[Collection("LiteDb")]
public class NoteRepositoryTests
{
    private static string TempDbPath() =>
        Path.Combine(Path.GetTempPath(), "stickmark-test-" + Guid.NewGuid().ToString("N") + ".db");

    private static async Task WithRepo(Func<NoteRepository, Task> body)
    {
        var path = TempDbPath();
        try
        {
            using (var repo = new NoteRepository(path))
            {
                await body(repo);
            }
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    private static Note NewNote(string content = "hello") => new()
    {
        Content = content,
        PlainText = content,
        Title = content,
    };

    [Fact]
    public Task Upsert_ThenGetAll_ReturnsNote() => WithRepo(async repo =>
    {
        var note = NewNote();
        await repo.UpsertAsync(note);
        var all = await repo.GetAllAsync();
        Assert.Single(all);
        Assert.Equal(note.Id, all[0].Id);
    });

    [Fact]
    public Task Delete_MovesToTrash() => WithRepo(async repo =>
    {
        var note = NewNote();
        await repo.UpsertAsync(note);
        await repo.DeleteAsync(note.Id);

        Assert.Empty(await repo.GetAllAsync());
        Assert.Null(await repo.GetByIdAsync(note.Id));
        var trashed = await repo.GetTrashedAsync();
        Assert.Single(trashed);
        Assert.True(trashed[0].IsDeleted);
    });

    [Fact]
    public Task Restore_BringsNoteBack() => WithRepo(async repo =>
    {
        var note = NewNote();
        await repo.UpsertAsync(note);
        await repo.DeleteAsync(note.Id);
        await repo.RestoreAsync(note.Id);

        Assert.Single(await repo.GetAllAsync());
        Assert.Empty(await repo.GetTrashedAsync());
    });

    [Fact]
    public Task Purge_RemovesEntirely() => WithRepo(async repo =>
    {
        var note = NewNote();
        await repo.UpsertAsync(note);
        await repo.DeleteAsync(note.Id);
        await repo.PurgeAsync(note.Id);

        Assert.Empty(await repo.GetAllAsync());
        Assert.Empty(await repo.GetTrashedAsync());
    });

    [Fact]
    public Task SaveContent_DoesNotResurrectDeletedNote() => WithRepo(async repo =>
    {
        var note = NewNote("original");
        await repo.UpsertAsync(note);
        await repo.DeleteAsync(note.Id);

        // A stale open-window copy flushes with IsDeleted=false — must not undelete.
        var stale = NewNote("edited later");
        stale.Id = note.Id;
        stale.IsDeleted = false;
        await repo.SaveContentAsync(stale);

        Assert.Null(await repo.GetByIdAsync(note.Id));      // still hidden
        var trashed = await repo.GetTrashedAsync();
        Assert.Single(trashed);
        Assert.True(trashed[0].IsDeleted);
    });

    [Fact]
    public Task PurgeTrashedOlderThan_RemovesOldOnly() => WithRepo(async repo =>
    {
        var oldNote = NewNote("old");
        await repo.UpsertAsync(oldNote);
        await repo.DeleteAsync(oldNote.Id);

        var recent = NewNote("recent");
        await repo.UpsertAsync(recent);
        await repo.DeleteAsync(recent.Id);

        // Cutoff in the future removes everything trashed before now.
        var purged = await repo.PurgeTrashedOlderThanAsync(DateTime.UtcNow.AddDays(1));
        Assert.Equal(2, purged);
        Assert.Empty(await repo.GetTrashedAsync());
    });

    [Fact]
    public Task FindByLinkedPath_MatchesCaseInsensitiveAndSkipsTrash() => WithRepo(async repo =>
    {
        var note = NewNote("linked");
        note.LinkedFilePath = @"C:\Docs\Report.md";
        await repo.UpsertAsync(note);

        // Case-ignoring matching.
        var found = await repo.FindByLinkedPathAsync(@"c:\docs\report.md");
        Assert.NotNull(found);
        Assert.Equal(note.Id, found!.Id);

        // Query without link path is null.
        Assert.Null(await repo.FindByLinkedPathAsync(@"C:\Docs\Other.md"));

        // If it goes to the trash, it will no longer match (the original file is not touched by the code).
        await repo.DeleteAsync(note.Id);
        Assert.Null(await repo.FindByLinkedPathAsync(@"C:\Docs\Report.md"));
    });
}

public class LinkedFileTests
{
    [Theory]
    [InlineData("a.md")]
    [InlineData("a.MARKDOWN")]
    [InlineData("a.txt")]
    [InlineData("a.log")]
    [InlineData("a.unknownext")]
    public void FormatFor_AlwaysMarkdown_SoRawTextRoundTrips(string name)
    {
        // Linked text files are always treated as Markdown sources to prevent XAML contamination.
        Assert.Equal(NoteContentFormat.Markdown, LinkedFile.FormatFor(name));
    }

    [Theory]
    [InlineData("notes.md", true)]
    [InlineData("readme.txt", true)]
    [InlineData("app.exe", false)]
    [InlineData("photo.png", false)]
    [InlineData("archive.zip", false)]
    public void IsSupported_FiltersBinaries(string name, bool expected)
    {
        Assert.Equal(expected, LinkedFile.IsSupported(name));
    }

    [Fact]
    public async Task WriteThenRead_RoundTripsWithoutBom()
    {
        var path = Path.Combine(Path.GetTempPath(), "stickmark-linked-" + Guid.NewGuid().ToString("N") + ".md");
        try
        {
            const string text = "# Title\n\nBody with **bold** 🎯";
            var writeUtc = await LinkedFile.WriteAsync(path, text);

            // BOM must not be attached (UTF-8, no BOM).
            var bytes = await File.ReadAllBytesAsync(path);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);

            var (content, readUtc) = await LinkedFile.ReadAsync(path);
            Assert.Equal(text, content);
            Assert.Equal(writeUtc, readUtc);
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task Read_DetectsBomEncodedFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "stickmark-bom-" + Guid.NewGuid().ToString("N") + ".md");
        try
        {
            const string text = "héllo hello";
            await File.WriteAllTextAsync(path, text, new System.Text.UTF8Encoding(true)); // with BOM
            var (content, _) = await LinkedFile.ReadAsync(path);
            Assert.Equal(text, content); // The BOM must be removed and only the contents remain.
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }
}

public class ExportNamingTests
{
    [Theory]
    [InlineData("Shopping list", "Shopping list")]
    [InlineData("a/b:c*d?", "a_b_c_d_")] // Prohibited characters are _
    [InlineData("///", "___")] // Even if all characters are prohibited, they are valid if replaced with _
    [InlineData(" spaced ", "spaced")] // Remove leading and trailing spaces
    [InlineData("trailing...", "trailing")] // remove end point
    public void SafeFileName_Sanitizes(string title, string expected)
    {
        Assert.Equal(expected, ExportNaming.SafeFileName(title));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")] // Blank only → fallback if empty
    [InlineData("...")] // Only dots → Empty when trimmed fallback
    public void SafeFileName_FallsBackWhenEmpty(string title)
    {
        Assert.Equal("note", ExportNaming.SafeFileName(title));
    }

    [Fact]
    public void SafeFileName_CapsLength()
    {
        var name = ExportNaming.SafeFileName(new string('x', 500));
        Assert.True(name.Length <= 80);
    }

    [Theory]
    [InlineData("CON", "_CON")]
    [InlineData("nul", "_nul")]
    [InlineData("COM1", "_COM1")]
    [InlineData("Contract", "Contract")] // If it is just a prefix of a reserved word, it is as is.
    public void SafeFileName_EscapesReservedDeviceNames(string title, string expected)
    {
        Assert.Equal(expected, ExportNaming.SafeFileName(title));
    }

    [Fact]
    public void UniqueFileName_DisambiguatesCollisions()
    {
        var taken = new HashSet<string>();
        Assert.Equal("note.md", ExportNaming.UniqueFileName("note", ".md", taken));
        Assert.Equal("note (2).md", ExportNaming.UniqueFileName("note", ".md", taken));
        Assert.Equal("note (3).md", ExportNaming.UniqueFileName("note", ".md", taken));
        Assert.Equal("other.md", ExportNaming.UniqueFileName("other", "md", taken)); // No need for a dot in front
    }
}

public class HtmlRendererDocumentTests
{
    private static Note PlainNote(string title, string plain) => new()
    {
        Title = title,
        PlainText = plain,
        Format = NoteContentFormat.PlainText,
        Content = plain,
    };

    [Fact]
    public void RenderDocument_IncludesTitlesAndDocHeading()
    {
        var notes = new[] { PlainNote("First", "one"), PlainNote("Second", "two") };
        var html = HtmlRenderer.RenderDocument(notes, "My Notes");

        Assert.Contains("My Notes", html);
        Assert.Contains(">First<", html);
        Assert.Contains(">Second<", html);
        // One article per note.
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(html, "<article").Count);
    }

    [Fact]
    public void RenderDocument_EscapesPlainTextBody()
    {
        var notes = new[] { PlainNote("XSS", "<script>alert(1)</script>") };
        var html = HtmlRenderer.RenderDocument(notes, "doc");

        // PlainText body must be escaped so that no executable tags remain.
        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void RenderDocument_RendersMarkdownBody()
    {
        var note = new Note
        {
            Title = "MD",
            Format = NoteContentFormat.Markdown,
            Content = "# Heading\n\n- item",
            PlainText = "Heading\nitem",
        };
        var html = HtmlRenderer.RenderDocument(new[] { note }, "doc");

        Assert.Contains("<h1", html);   //Render headings with Markdig
        Assert.Contains("<li", html);   // list render
    }
}

public class ConflictCopyDetectorTests
{
    private static readonly Func<string, bool> AlwaysTrue = _ => true;
    private static readonly Func<string, bool> AlwaysFalse = _ => false;

    // ── Dropbox (EN) ──────────────────────────────────────────────────────
    [Theory]
    [InlineData("notes (DESKTOP's conflicted copy 2026-07-01).md")] // Includes date
    [InlineData("notes (DESKTOP's conflicted copy 2026-07-01 (2)).md")] // date + counter
    [InlineData("notes (Bob's conflicted copy).md")] // No date
    [InlineData("NOTES (BOB'S CONFLICTED COPY).MD")] // Ignore case
    public void DropboxEnglishPattern_IsConflictCopy(string fileName)
    {
        Assert.True(ConflictCopyDetector.IsConflictCopy(fileName, AlwaysFalse));
    }

    // ── Dropbox (KO) ──────────────────────────────────────────────────────
    [Theory]
    [InlineData("Note (conflicting copy 2026-07-01).md")]
    [InlineData("Name (Conflict Copy).md")]
    public void DropboxKoreanPattern_IsConflictCopy(string fileName)
    {
        Assert.True(ConflictCopyDetector.IsConflictCopy(fileName, AlwaysFalse));
    }

    // ── Syncthing ─────────────────────────────────────────────────────────
    [Theory]
    [InlineData("plan.sync-conflict-20260701-093012-ABCDEF.md")] // Include device indication
    [InlineData("plan.sync-conflict-20260701-093012.md")] // No device indication
    public void SyncthingPattern_IsConflictCopy(string fileName)
    {
        Assert.True(ConflictCopyDetector.IsConflictCopy(fileName, AlwaysFalse));
    }

    // ── OneDrive: Machine name matching + original existence double condition ────────────────────────
    [Fact]
    public void OneDrivePattern_MachineNameMatches_AndOriginalExists_IsConflictCopy()
    {
        var fileName = $"todo-{Environment.MachineName}.md";
        Assert.True(ConflictCopyDetector.IsConflictCopy(fileName, name => name == "todo.md"));
    }

    [Fact]
    public void OneDrivePattern_NumberedSuffix_AndOriginalExists_IsConflictCopy()
    {
        var fileName = $"todo-{Environment.MachineName}-2.md";
        Assert.True(ConflictCopyDetector.IsConflictCopy(fileName, name => name == "todo.md"));
    }

    [Fact]
    public void OneDrivePattern_MachineNameMatches_ButOriginalMissing_IsNotConflictCopy()
    {
        var fileName = $"todo-{Environment.MachineName}.md";
        Assert.False(ConflictCopyDetector.IsConflictCopy(fileName, AlwaysFalse));
    }

    [Fact]
    public void OneDrivePattern_MachineNameMismatch_IsNotConflictCopy()
    {
        var fileName = $"todo-{Environment.MachineName}X.md";   // another machine name
        Assert.False(ConflictCopyDetector.IsConflictCopy(fileName, AlwaysTrue));  // Doesn't matter if the original exists
    }

    // ── No false positives ─────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("week-plan-2026.md")] // Plain hyphenated file name
    [InlineData("my-conflicted-thoughts.md")] // Contains the word "conflicted" but no parentheses pattern
    public void NormalHyphenatedFileNames_AreNotConflictCopies(string fileName)
    {
        Assert.False(ConflictCopyDetector.IsConflictCopy(fileName, AlwaysTrue));
    }
}

public class ExternalAppLocatorTests
{
    [Fact]
    public void FindVsCode_PrefersLocalAppDataCandidate()
    {
        var expected = Path.Combine(@"C:\Users\test\AppData\Local", "Programs", "Microsoft VS Code", "Code.exe");
        string? Env(string name) => name switch
        {
            "LOCALAPPDATA" => @"C:\Users\test\AppData\Local",
            "ProgramFiles" => @"C:\Program Files",
            "PATH" => @"C:\some\dir",
            _ => null,
        };
        Assert.Equal(expected, ExternalAppLocator.FindVsCode(p => p == expected, Env));
    }

    [Fact]
    public void FindVsCode_FallsBackToProgramFiles_WhenLocalAppDataCandidateMissing()
    {
        var expected = Path.Combine(@"C:\Program Files", "Microsoft VS Code", "Code.exe");
        string? Env(string name) => name switch
        {
            "LOCALAPPDATA" => @"C:\Users\test\AppData\Local",
            "ProgramFiles" => @"C:\Program Files",
            "PATH" => null,
            _ => null,
        };
        Assert.Equal(expected, ExternalAppLocator.FindVsCode(p => p == expected, Env));
    }

    [Fact]
    public void FindVsCode_FallsBackToPathCodeCmd_WhenExeCandidatesMissing()
    {
        var dir = @"C:\tools\vscode-bin";
        var expected = Path.Combine(dir, "code.cmd");
        string? Env(string name) => name switch
        {
            "LOCALAPPDATA" => @"C:\Users\test\AppData\Local",
            "ProgramFiles" => @"C:\Program Files",
            "PATH" => $@"C:\other;{dir};C:\another",
            _ => null,
        };
        Assert.Equal(expected, ExternalAppLocator.FindVsCode(p => p == expected, Env));
    }

    [Fact]
    public void FindVsCode_SkipsEmptyPathEntries()
    {
        var dir = @"C:\tools\vscode-bin";
        var expected = Path.Combine(dir, "code.cmd");
        string? Env(string name) => name switch
        {
            "LOCALAPPDATA" => null,
            "ProgramFiles" => null,
            "PATH" => $@";;{dir};",
            _ => null,
        };
        Assert.Equal(expected, ExternalAppLocator.FindVsCode(p => p == expected, Env));
    }

    [Fact]
    public void FindVsCode_ReturnsNull_WhenNoCandidateExists()
    {
        string? Env(string name) => name switch
        {
            "LOCALAPPDATA" => @"C:\Users\test\AppData\Local",
            "ProgramFiles" => @"C:\Program Files",
            "PATH" => @"C:\other;C:\another",
            _ => null,
        };
        Assert.Null(ExternalAppLocator.FindVsCode(_ => false, Env));
    }
}

public class FuzzyMatcherTests
{
    [Fact]
    public void Match_SubsequenceInOrder_Succeeds()
    {
        // "rls" is a subsequence of "release" (maintaining the order of r,l,s among r-e-l-e-a-s-e).
        var result = FuzzyMatcher.Match("release notes", "rls");
        Assert.True(result.Matched);
    }

    [Fact]
    public void Match_OutOfOrderChars_Fails()
    {
        // "slr" cannot be found in "release" by following the order (there is no l after s).
        var result = FuzzyMatcher.Match("release", "slr");
        Assert.False(result.Matched);
    }

    [Fact]
    public void Match_IsCaseInsensitive()
    {
        var result = FuzzyMatcher.Match("Release Notes", "RELEASE");
        Assert.True(result.Matched);
    }

    [Fact]
    public void Match_Title_MatchesAsSubsequence()
    {
        var result = FuzzyMatcher.Match("Meeting minutes organized", "minutes");
        Assert.True(result.Matched);
    }

    [Fact]
    public void Match_ConsecutiveRun_ScoresHigherThanScatteredMatch()
    {
        var consecutive = FuzzyMatcher.Match("abcdef", "abc");
        var scattered = FuzzyMatcher.Match("axbxcx", "abc");
        Assert.True(consecutive.Matched);
        Assert.True(scattered.Matched);
        Assert.True(consecutive.Score > scattered.Score);
    }

    [Fact]
    public void Match_WordStartMatch_ScoresHigherThanMidWordMatch()
    {
        var wordStart = FuzzyMatcher.Match("release notes", "notes");   // "notes" starts the word after a space
        var midWord = FuzzyMatcher.Match("xnotes", "notes");            // Same consecutive match, but in the middle of a word
        Assert.True(wordStart.Score > midWord.Score);
    }

    [Fact]
    public void Match_TitleRegion_ScoresHigherThanTagRegion()
    {
        const string target = "release notes";
        var inTitle = FuzzyMatcher.Match(target, "notes", titleLength: target.Length);   // Treat the whole as a title
        var inTag = FuzzyMatcher.Match(target, "notes", titleLength: 0);                 // Treat everything as a tag
        Assert.True(inTitle.Score > inTag.Score);
    }

    [Fact]
    public void Match_EarlierFirstMatch_ScoresHigherThanLaterMatch_AllElseEqual()
    {
        // Since both are consecutive matches and not word starts (run/word-start bonuses are the same), only the first match position bonus is different.
        var earlier = FuzzyMatcher.Match("xabc", "abc");
        var later = FuzzyMatcher.Match("xxxxxabc", "abc");
        Assert.True(earlier.Score > later.Score);
    }

    [Fact]
    public void Match_MultipleTokens_RequireAllToMatch()
    {
        var bothMatch = FuzzyMatcher.Match("release notes final", "rel fin");
        var oneMissing = FuzzyMatcher.Match("release notes final", "rel xyz");
        Assert.True(bothMatch.Matched);
        Assert.False(oneMissing.Matched);
    }

    [Fact]
    public void Match_EmptyQuery_MatchesEverythingWithZeroScore()
    {
        var result = FuzzyMatcher.Match("anything at all", "");
        Assert.True(result.Matched);
        Assert.Equal(0, result.Score);
        Assert.Empty(result.Positions);
    }

    [Fact]
    public void Match_NoMatch_ReturnsEmptyPositionsAndZeroScore()
    {
        var result = FuzzyMatcher.Match("hello", "xyz");
        Assert.False(result.Matched);
        Assert.Equal(0, result.Score);
        Assert.Empty(result.Positions);
    }

    [Fact]
    public void Match_Positions_AreValidSortedIndicesIntoTarget()
    {
        var target = "hello world";
        var result = FuzzyMatcher.Match(target, "how");
        Assert.True(result.Matched);
        Assert.NotEmpty(result.Positions);
        foreach (var pos in result.Positions)
        {
            Assert.InRange(pos, 0, target.Length - 1);
        }
        Assert.Equal(result.Positions.OrderBy(p => p).Distinct(), result.Positions);
    }
}
