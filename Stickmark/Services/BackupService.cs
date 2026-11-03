using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;
using Stickmark.Models;
using Stickmark.Resources;
using Stickmark.Utils;

namespace Stickmark.Services;

public sealed class BackupService : IBackupService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly INoteRepository _repository;
    private readonly IWindowManager _windowManager;
    private readonly ILogger<BackupService> _logger;

    public BackupService(INoteRepository repository, IWindowManager windowManager, ILogger<BackupService> logger)
    {
        _repository = repository;
        _windowManager = windowManager;
        _logger = logger;
    }

    public async Task ExportInteractiveAsync()
    {
        var dlg = new SaveFileDialog
        {
            Filter = Strings.Backup_JsonFilter,
            FileName = $"stickmark-{DateTime.Now:yyyyMMdd-HHmmss}.json",
            Title = Strings.Backup_ExportDialogTitle,
        };
        if (DialogOwner.Show(dlg) != true) return;

        var notes = await _repository.GetAllAsync().ConfigureAwait(true);
        var payload = new BackupPayload(1, DateTime.UtcNow, notes);
        await using var stream = File.Create(dlg.FileName);
        await JsonSerializer.SerializeAsync(stream, payload, JsonOptions).ConfigureAwait(true);
        _logger.LogInformation("Exported {Count} notes to {Path}", notes.Count, dlg.FileName);
    }

    public async Task ExportNotesAsTextAsync()
    {
        var dlg = new SaveFileDialog
        {
            Filter = Strings.Backup_TextExportFilter,
            FileName = $"stickmark-notes-{DateTime.Now:yyyyMMdd-HHmmss}.md",
            Title = Strings.Backup_TextExportDialogTitle,
        };
        if (DialogOwner.Show(dlg) != true) return;

        var notes = await _repository.GetAllAsync().ConfigureAwait(true);
        var sb = new StringBuilder();
        sb.AppendLine("# Stickmark notes");
        sb.AppendLine($"_Exported {DateTime.Now:yyyy-MM-dd HH:mm} · {notes.Count} note(s)_");
        sb.AppendLine();

        foreach (var note in notes)
        {
            var title = string.IsNullOrWhiteSpace(note.Title) ? "(untitled)" : note.Title;
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine($"## {title}");
            sb.AppendLine();

            var body = (note.PlainText ?? string.Empty).Replace("\r\n", "\n").TrimEnd();
            // PlainText's first line is the derived title — drop it so the title isn't printed twice.
            var lines = body.Split('\n');
            if (lines.Length > 0 && lines[0].Trim() == title)
            {
                body = string.Join("\n", lines.Skip(1)).Trim('\n');
            }
            sb.AppendLine(body.Length == 0 ? "_(empty)_" : body);
            sb.AppendLine();

            if (note.Tags is { Count: > 0 })
            {
                sb.AppendLine("Tags: " + string.Join(" ", note.Tags.Select(t => "#" + t)));
                sb.AppendLine();
            }
            sb.AppendLine($"<sub>Modified {note.ModifiedAt.ToLocalTime():yyyy-MM-dd HH:mm}</sub>");
            sb.AppendLine();
        }

        await File.WriteAllTextAsync(dlg.FileName, sb.ToString()).ConfigureAwait(true);
        _logger.LogInformation("Exported {Count} notes as text to {Path}", notes.Count, dlg.FileName);
    }

    public async Task ExportNotesAsync(IReadOnlyList<Guid> noteIds, ExportFormat format)
    {
        if (noteIds is null || noteIds.Count == 0) return;

        var all = await _repository.GetAllAsync().ConfigureAwait(true);
        var byId = all.ToDictionary(n => n.Id);
        // Maintain the selection order, and quietly skip any ids deleted in the meantime.
        var notes = noteIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
        if (notes.Count == 0)
        {
            MessageBox.Show(Strings.Backup_NoNotesToExport, "Stickmark",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        switch (format)
        {
            case ExportFormat.MarkdownFiles: await ExportAsMarkdownFilesAsync(notes).ConfigureAwait(true); break;
            case ExportFormat.Html: await ExportAsHtmlAsync(notes).ConfigureAwait(true); break;
            case ExportFormat.Pdf: await ExportAsPdfAsync(notes).ConfigureAwait(true); break;
        }
    }

    private async Task ExportAsMarkdownFilesAsync(IReadOnlyList<Note> notes)
    {
        var dlg = new OpenFolderDialog { Title = Strings.Backup_ChooseExportFolderTitle };
        if (DialogOwner.Show(dlg) != true) return;
        var folder = dlg.FolderName;

        // Reserve file names that already exist in the folder in advance — to avoid overwriting existing files with the same name.
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var existing in Directory.EnumerateFiles(folder))
            {
                taken.Add(Path.GetFileName(existing).ToLowerInvariant());
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not enumerate target folder before export: {Folder}", folder);
        }

        var count = 0;
        var failures = new List<string>();
        foreach (var note in notes)
        {
            var baseName = ExportNaming.SafeFileName(note.Title);
            var fileName = ExportNaming.UniqueFileName(baseName, ".md", taken);
            try
            {
                await File.WriteAllTextAsync(Path.Combine(folder, fileName), BuildMarkdown(note)).ConfigureAwait(true);
                count++;
            }
            catch (Exception ex)
            {
                // Even if one note fails, the rest continue to be saved, and the failure list is reported at the end.
                _logger.LogError(ex, "Failed to write note {Id} to {File}", note.Id, fileName);
                failures.Add(fileName);
            }
        }

        _logger.LogInformation("Exported {Count}/{Total} notes as .md files to {Folder}", count, notes.Count, folder);
        if (failures.Count == 0)
        {
            MessageBox.Show(string.Format(Strings.Backup_MarkdownFilesExportedFormat, count, folder), "Stickmark",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            var list = string.Join(", ", failures.Take(5)) + (failures.Count > 5 ? " …" : "");
            MessageBox.Show(
                string.Format(Strings.Backup_MarkdownFilesPartialFailureFormat, count, failures.Count, list),
                "Stickmark", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task ExportAsHtmlAsync(IReadOnlyList<Note> notes)
    {
        var dlg = new SaveFileDialog
        {
            Filter = Strings.Export_HtmlFilter,
            FileName = $"stickmark-notes-{DateTime.Now:yyyyMMdd-HHmmss}.html",
            Title = Strings.Export_HtmlDialogTitle,
        };
        if (DialogOwner.Show(dlg) != true) return;

        var html = HtmlRenderer.RenderDocument(notes, Strings.Export_DocumentTitle);
        try
        {
            await File.WriteAllTextAsync(dlg.FileName, html).ConfigureAwait(true);
            _logger.LogInformation("Exported {Count} notes as HTML to {Path}", notes.Count, dlg.FileName);
            MessageBox.Show(string.Format(Strings.Backup_HtmlExportedFormat, notes.Count, dlg.FileName), "Stickmark",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HTML export failed");
            MessageBox.Show(string.Format(Strings.Export_HtmlFailedFormat, ex.Message), "Stickmark",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task ExportAsPdfAsync(IReadOnlyList<Note> notes)
    {
        var dlg = new SaveFileDialog
        {
            Filter = Strings.Export_PdfFilter,
            FileName = $"stickmark-notes-{DateTime.Now:yyyyMMdd-HHmmss}.pdf",
            Title = Strings.Export_PdfDialogTitle,
        };
        if (DialogOwner.Show(dlg) != true) return;

        var html = HtmlRenderer.RenderDocument(notes, Strings.Export_DocumentTitle);
        try
        {
            await PdfRenderer.RenderAsync(html, dlg.FileName).ConfigureAwait(true);
            _logger.LogInformation("Exported {Count} notes as PDF to {Path}", notes.Count, dlg.FileName);
            MessageBox.Show(string.Format(Strings.Backup_PdfExportedFormat, notes.Count, dlg.FileName), "Stickmark",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PDF export failed");
            MessageBox.Show(
                string.Format(Strings.Export_PdfFailedFormat, ex.Message),
                "Stickmark", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public async Task ExportSingleNoteAsync(Note note, ExportFormat format)
    {
        var safe = ExportNaming.SafeFileName(note.Title);
        var docTitle = string.IsNullOrWhiteSpace(note.Title) ? Strings.Export_DocumentTitle : note.Title;

        switch (format)
        {
            case ExportFormat.Html:
            {
                var dlg = new SaveFileDialog
                {
                    Filter = Strings.Export_HtmlFilter,
                    FileName = safe + ".html",
                    Title = Strings.Export_HtmlDialogTitle,
                };
                if (DialogOwner.Show(dlg) != true) return;

                try
                {
                    await File.WriteAllTextAsync(dlg.FileName, HtmlRenderer.RenderDocument(new[] { note }, docTitle))
                        .ConfigureAwait(true);
                    _logger.LogInformation("Exported note {Id} as HTML to {Path}", note.Id, dlg.FileName);
                    MessageBox.Show(string.Format(Strings.Export_HtmlSavedFormat, dlg.FileName), "Stickmark",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Single-note HTML export failed");
                    MessageBox.Show(string.Format(Strings.Export_HtmlFailedFormat, ex.Message), "Stickmark",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                break;
            }
            case ExportFormat.Pdf:
            {
                var dlg = new SaveFileDialog
                {
                    Filter = Strings.Export_PdfFilter,
                    FileName = safe + ".pdf",
                    Title = Strings.Export_PdfDialogTitle,
                };
                if (DialogOwner.Show(dlg) != true) return;

                try
                {
                    await PdfRenderer.RenderAsync(HtmlRenderer.RenderDocument(new[] { note }, docTitle), dlg.FileName)
                        .ConfigureAwait(true);
                    _logger.LogInformation("Exported note {Id} as PDF to {Path}", note.Id, dlg.FileName);
                    MessageBox.Show(string.Format(Strings.Export_PdfSavedFormat, dlg.FileName), "Stickmark",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Single-note PDF export failed");
                    MessageBox.Show(
                        string.Format(Strings.Export_PdfFailedFormat, ex.Message),
                        "Stickmark", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                break;
            }
            case ExportFormat.MarkdownFiles:
            {
                var dlg = new SaveFileDialog
                {
                    Filter = Strings.Export_MarkdownFilter,
                    FileName = safe + ".md",
                    Title = Strings.Export_MarkdownDialogTitle,
                };
                if (DialogOwner.Show(dlg) != true) return;

                try
                {
                    await File.WriteAllTextAsync(dlg.FileName, BuildMarkdown(note)).ConfigureAwait(true);
                    _logger.LogInformation("Exported note {Id} as Markdown to {Path}", note.Id, dlg.FileName);
                    MessageBox.Show(string.Format(Strings.Export_MarkdownSavedFormat, dlg.FileName), "Stickmark",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Single-note Markdown export failed");
                    MessageBox.Show(string.Format(Strings.Export_MarkdownFailedFormat, ex.Message), "Stickmark",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                break;
            }
        }
    }

    public async Task PrintNoteAsync(Note note)
    {
        var docTitle = string.IsNullOrWhiteSpace(note.Title) ? Strings.Export_DocumentTitle : note.Title;
        var html = HtmlRenderer.RenderDocument(new[] { note }, docTitle);
        var tmp = Path.Combine(Path.GetTempPath(), $"stickmark-print-{Guid.NewGuid():N}.html");
        await File.WriteAllTextAsync(tmp, html).ConfigureAwait(true);

        var udf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Stickmark", "WebView2-export");
        Directory.CreateDirectory(udf);

        var web = new WebView2 { CreationProperties = new CoreWebView2CreationProperties { UserDataFolder = udf } };
        var host = new Window
        {
            Title = Strings.Print_WindowTitle,
            Width = 860,
            Height = 1000,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = web,
        };
        host.Closed += (_, _) => { try { web.Dispose(); } catch { } try { File.Delete(tmp); } catch { } };
        host.Show();
        try
        {
            await web.EnsureCoreWebView2Async().ConfigureAwait(true);
            var s = web.CoreWebView2.Settings;
            s.IsScriptEnabled = false; s.AreHostObjectsAllowed = false; s.IsWebMessageEnabled = false;
            var nav = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            web.NavigationCompleted += (_, e) => nav.TrySetResult(e.IsSuccess);
            web.CoreWebView2.Navigate(new Uri(tmp).AbsoluteUri);
            await nav.Task.ConfigureAwait(true);
            web.CoreWebView2.ShowPrintUI(CoreWebView2PrintDialogKind.Browser);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Print failed");
            MessageBox.Show(string.Format(Strings.Print_PrepareFailedFormat, ex.Message), "Stickmark",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            host.Close();
        }
    }

    /// Create one note with YAML front matter + .md text of the body.
    private static string BuildMarkdown(Note note)
    {
        var sb = new StringBuilder();
        var title = string.IsNullOrWhiteSpace(note.Title) ? Strings.NoteList_Untitled : note.Title;

        sb.Append("---\n");
        sb.Append("title: ").Append(YamlScalar(title)).Append('\n');
        if (note.Tags is { Count: > 0 })
        {
            sb.Append("tags: [").Append(string.Join(", ", note.Tags.Select(YamlScalar))).Append("]\n");
        }
        sb.Append("color: ").Append(note.Color).Append('\n');
        sb.Append("created: ").Append(note.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")).Append('\n');
        sb.Append("modified: ").Append(note.ModifiedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")).Append('\n');
        sb.Append("---\n\n");

        var body = note.Format switch
        {
            // Markdown/HTML notes remain in the original source (HTML is also valid within .md).
            NoteContentFormat.Markdown or NoteContentFormat.Html => note.Content ?? string.Empty,
            // PlainText/RichTextXaml uses a human-readable PlainText projection (does not include the original XAML).
            _ => note.PlainText ?? string.Empty,
        };
        sb.Append(body.Replace("\r\n", "\n").TrimEnd()).Append('\n');
        return sb.ToString();
    }

    /// If there are special characters in the YAML value, make them safe by wrapping them in double quotes.
    private static string YamlScalar(string value)
    {
        value = value.Replace("\r", " ").Replace("\n", " ");
        if (value.Length == 0) return "\"\"";

        var needsQuote = value[0] == ' ' || value[^1] == ' ' ||
            value.IndexOfAny(YamlSpecials) >= 0;
        if (!needsQuote) return value;
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    private static readonly char[] YamlSpecials =
        { ':', '#', '"', '\'', '[', ']', '{', '}', ',', '&', '*', '!', '|', '>', '%', '@', '`' };

    public async Task ImportInteractiveAsync()
    {
        var dlg = new OpenFileDialog
        {
            Filter = Strings.Backup_JsonImportFilter,
            Title = Strings.Backup_ImportDialogTitle,
        };
        if (DialogOwner.Show(dlg) != true) return;

        BackupPayload? payload;
        try
        {
            await using var stream = File.OpenRead(dlg.FileName);
            payload = await JsonSerializer.DeserializeAsync<BackupPayload>(stream).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Import parse failure");
            MessageBox.Show(string.Format(Strings.Backup_ReadFailedFormat, ex.Message), "Stickmark",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (payload?.Notes is null || payload.Notes.Count == 0)
        {
            MessageBox.Show(Strings.Backup_NoNotesInFile, "Stickmark",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            string.Format(Strings.Backup_ImportConfirmFormat, payload.Notes.Count),
            "Stickmark",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        foreach (var note in payload.Notes)
        {
            await _repository.UpsertAsync(SanitizeImportedNote(note)).ConfigureAwait(true);
        }
        await _windowManager.ReloadAsync().ConfigureAwait(true);
        _logger.LogInformation("Imported {Count} notes from {Path}", payload.Notes.Count, dlg.FileName);
    }

    public async Task ExportVaultAsync()
    {
        var dlg = new OpenFolderDialog { Title = Strings.Vault_ChooseExportFolder };
        if (DialogOwner.Show(dlg) != true) return;
        var folder = dlg.FolderName;

        var notes = await _repository.GetAllAsync().ConfigureAwait(true);

        // Reserve file names that already exist in the folder in advance — to avoid overwriting existing files with the same name.
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var existing in Directory.EnumerateFiles(folder))
            {
                taken.Add(Path.GetFileName(existing).ToLowerInvariant());
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not enumerate target folder before vault export: {Folder}", folder);
        }

        var count = 0;
        var failures = new List<string>();
        foreach (var note in notes)
        {
            var baseName = ExportNaming.SafeFileName(note.Title);
            var fileName = ExportNaming.UniqueFileName(baseName, ".md", taken);
            try
            {
                await File.WriteAllTextAsync(Path.Combine(folder, fileName), VaultMarkdown.ToMarkdown(note))
                    .ConfigureAwait(true);
                count++;
            }
            catch (Exception ex)
            {
                // Even if one note fails, the rest continue to be saved, and the failure list is reported at the end.
                _logger.LogError(ex, "Failed to write vault note {Id} to {File}", note.Id, fileName);
                failures.Add(fileName);
            }
        }

        _logger.LogInformation("Exported {Count}/{Total} notes to vault {Folder}", count, notes.Count, folder);
        if (failures.Count == 0)
        {
            MessageBox.Show(string.Format(Strings.Vault_ExportedFormat, count, folder), "Stickmark",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            var list = string.Join(", ", failures.Take(5)) + (failures.Count > 5 ? " …" : "");
            MessageBox.Show(
                string.Format(Strings.Backup_MarkdownFilesPartialFailureFormat, count, failures.Count, list),
                "Stickmark", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public async Task ImportVaultAsync()
    {
        var dlg = new OpenFolderDialog { Title = Strings.Vault_ChooseImportFolder };
        if (DialogOwner.Show(dlg) != true) return;
        var folder = dlg.FolderName;

        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(folder, "*.md", SearchOption.TopDirectoryOnly).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not enumerate vault folder: {Folder}", folder);
            return;
        }

        if (files.Count == 0)
        {
            MessageBox.Show(Strings.Vault_NoMarkdownFiles, "Stickmark",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            string.Format(Strings.Vault_ImportConfirmFormat, files.Count),
            "Stickmark",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        var count = 0;
        var failures = new List<string>();
        foreach (var file in files)
        {
            try
            {
                var text = await File.ReadAllTextAsync(file).ConfigureAwait(true);
                var note = SanitizeImportedNote(VaultMarkdown.FromMarkdown(text, Path.GetFileNameWithoutExtension(file)));
                await _repository.UpsertAsync(note).ConfigureAwait(true);
                count++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to import vault file {File}", file);
                failures.Add(Path.GetFileName(file));
            }
        }

        await _windowManager.ReloadAsync().ConfigureAwait(true);
        _logger.LogInformation("Imported {Count}/{Total} notes from vault {Folder}", count, files.Count, folder);

        if (failures.Count == 0)
        {
            MessageBox.Show(string.Format(Strings.Vault_ImportedFormat, count), "Stickmark",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            var list = string.Join(", ", failures.Take(5)) + (failures.Count > 5 ? " …" : "");
            MessageBox.Show(
                string.Format(Strings.Backup_MarkdownFilesPartialFailureFormat, count, failures.Count, list),
                "Stickmark", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// Disarms dangerous fields in notes from untrusted backup files (security).
    /// - RichTextXaml is an unbounded XAML parser (TextRange.Load) when the window is opened.
    /// This is demoted to pure text because it runs a gadget and can execute arbitrary code.
    /// - LinkedFilePath provides a path to overwrite arbitrary files by quietly writing notes in the path when editing.
    ///, so remove it (the imported note becomes a regular note — if necessary, open the file again to link it).
    internal static Note SanitizeImportedNote(Note note)
    {
        note.LinkedFilePath = null;
        note.LinkedFileSyncedUtc = null;
        if (note.Format == NoteContentFormat.RichTextXaml)
        {
            note.Content = note.PlainText ?? string.Empty;
            note.Format = NoteContentFormat.PlainText;
        }
        return note;
    }

    private sealed record BackupPayload(int Version, DateTime ExportedAt, System.Collections.Generic.IReadOnlyList<Note> Notes);
}
