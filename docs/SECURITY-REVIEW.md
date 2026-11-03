# Stickmark Security Review (2026-07-04)

Project-wide security review. 2 parallel reviews + manual review of automatic update path + XAML RCE verification test
Confirmed. The vulnerabilities below were hardened in v1.6.1. The app runs asInvoker (normal integrity) and runs on all OS
Writes are limited to HKCU/%LocalAppData%.

## Findings

| Severity | Item | status |
|---|---|---|
| 🔴 CRITICAL | Import malicious backup (.json) → remote code execution (RCE). `BackupService.ImportInteractiveAsync` deserializes the attacker JSON to `Note`(Content·Format) → `NoteWindow.LoadEditorContent` loads `RichTextXaml` into `TextRange.Load(DataFormats.Xaml)`. This path is an unconstrained XAML parser in WPF, so the `ObjectDataProvider` gadget is executed (actual: check marker file creation). | **Fixed (v1.6.1)** — Demote RichTextXaml→PlainText on import (`SanitizeImportedNote`), apply dangerous marker guard (`ContainsDangerousXaml`) to two additional XAML sinks (`NoteWindow.LoadEditorContent`, `TextExtraction.ToPlainText`). |
| 🟠 HIGH | Unauthorized overwriting of arbitrary files with imported `LinkedFilePath`. When an imported note contains an arbitrary path and is edited, the note body is quietly written to that path (`SyncToLinkedFileAsync`). | **FIXED** — Remove `LinkedFilePath`/`LinkedFileSyncedUtc` on import. |
| 🟡 MEDIUM | WebView2 `NewWindowRequested` scheme not verified → When clicking the `file:`/UNC/protocol link in the note markup, it is executed locally with `ShellExecute`. | **FIXED** — Only allow http/https. |
| 🟡 MEDIUM | Export/Preview CSP allows remote (http/https) images → Automatic remote request (tracking/SSRF/plain text) when manipulated notes are previewed, PDF created, and shared `.html` viewed. | **Partially fixed** — Export document (`RenderDocument`: HTML/PDF/Print) restricts img/media/font-src to `data:`. In-app live preview (`Render`) maintains remote images (viewable on user device — documented as residual risk). |
| 🟡 MEDIUM | Named pipe (`Stickmark.OpenFile.v1`) input bypasses `IsSupported` whitelist + unlimited number (same session process opens random files/windows bombs). The default DACL already blocks other users and low-integrity users. | **FIXED** — Validate `File.Exists`+`IsSupported` as the command line for piped input + limit to maximum 20. |
| 🟡 MEDIUM | Automatic update: downloaded installer hash is not Authenticode-signed; `release.Tag` (GitHub value) is validated, and the helper uses an unpredictable temporary path plus `%TEMP%` staging. | **Partially fixed** — Tag regular expression validation (`IsSafeReleaseTag`), download URL https+GitHub host restriction (`IsTrustedDownloadUrl`), unpredictable GUID staging folder + no tag in filename. **Remaining/Recommended: Post SHA-256 in release and then collate or add Authenticode code signature verification** (currently unsigned CI artifacts). |
| 🟢 LOW | Importing trusted attacker IDs → overwriting existing notes with the same GUID. | **Unmodified (accepted)** — Id reissue breaks normal backup restore (restore creates duplicates). Accepted with low effectiveness, requiring the attacker to know the specific GUID. |
| 🟢 LOW | Print/PDF temporary html (note full text) `%TEMP%` residual, log file path (not content), auto-open UNC NTLM leak, unlimited import size. | **Documentation (recommended hardening)**. |

## Safe confirmed (avoid false positives)

- Automatic startup/file connection registry quotation marks processing normal (HKCU, no administrator required, no injection)
- Guard to preserve linked files when deleted
- Linked save is only Markdown
- Data `%LocalAppData%` user ACL
- Preview WebView2 keeps a strict CSP (`default-src 'none'`); only a host-injected double-click listener runs, and note-provided scripts are still blocked
- No System.Text.Json polymorphic gadgets

## Recommended follow-up

1. Update exe signature/hash verification (add SHA-256 posting to CI)
2. Opt-in for live preview remote images
3. Immediately clean temporary files/clean at startup
4. Import size/number upper limit

## WYSIWYG Editor WebView Review (2026-07-06, v2.1.0+)

In v2.1.0, for Markdown WYSIWYG editing, scripts/web messages were enabled in the edit-only WebView2. In v3.0.0 the rendered preview also enables the WebView2 script engine, but only to run a host-injected double-click listener; note markup remains constrained by CSP `default-src 'none'`. We reviewed the isolation below and confirmed that there was no code execution or leakage route.

- **Block all networks** — Page CSP `default-src 'none'; script-src 'self'; connect-src 'none'` (inline/eval script, no network requests), virtual host mapping is `DenyCors` (local assets only).
- **Block off-page exit** — `NavigationStarting` cancels URLs other than `https://stickmark.editor/`, blocks `NewWindowRequested`. Links in notes are source text, not clickable anchors in the editor.
- **Content is data-only** — C#→JS only has `setMarkdown(<JSON string>)`, and `JsonSerializer.Serialize` makes JS strings safe by escaping `<`·`>`·`U+2028/2029`, etc. The content only goes into CodeMirror's document text (DOM text node) and is not parsed/injected into HTML → XSS/code execution is not possible with crafted notes.
- **No host exposure** — `AreHostObjectsAllowed=false`, DevTools/Default context menu/Autocomplete disabled. JS→C# simply parses the `{type,md}` string message into `JsonDocument`.
- **Asset Integrity** — Editor bundles are embedded in the assembly, extracted and mapped to `%LocalAppData%` at process startup, and overwritten on each run. A local attacker with LocalAppData write permission can already replace the exe itself, so it is not a new threat surface, and damage is limited by blocking CSP/host.

**Conclusion:** No new threat surfaces. The preview listener receives no note data and only posts a fixed `stickmark:edit` message. Note-provided scripts remain blocked by CSP, and WebView2 host objects are disabled.
