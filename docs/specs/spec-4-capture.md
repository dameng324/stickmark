# Spec 4 — Capture Machine (Global Fast Capture · Tray Drop · Image Paste)

- Target version: v2.2.2 → **v2.3.0 suggested**
- Status: Draft specification (prior to implementation)
- Commit based on related files: `1a9dd0d` (main)

---

## 1. Goal / Non-goal

### Goal
1. **Global Quick Capture** — In any app, open a small input window with a single global shortcut, and immediately save a new note with Enter. The default operation is “save only” (does not bring up the note window) and feeds back as a tray toast after saving. Automatic prefilling of clipboard text is an opt-in option.
2. **Paste image** — When you paste a clipboard image in the markdown note editor, it is saved as a PNG file and `![](attachments/xxx.png)` is inserted at the caret position. The image is actually visible in preview (split/full) and HTML/PDF export.
3. Prove at the design stage that the above function does not break the existing **security invariants** (preview script OFF, remote network policy invariance, WYSIWYG virtual host whitelist).

### Non-target (excluding scope — including rationale)
- **Tray icon drop**: Technically impossible → After providing detailed evidence and alternatives in §3.4, **v1 scope exclusion** is recommended.
- **Image pasting inside WYSIWYG (CodeMirror 6)**: Requires rebuilding the `tools/mdeditor` bundle + extending the WebMessage protocol, so the benefit is low compared to the risk. WYSIWYG is a source-oriented editor, so images are displayed as `![](…)` text anyway, so v1 supports pasting only in the source editor (RichTextBox). (Subsequent candidate, §3.3.6)
- **Attach image of RichText (format) note**: RichTextXaml is a format that is demoted to PlainText when saved in the vault (`VaultMarkdown.cs:21-23`), so attachment references cannot be reliably round-tripped. Maintain existing behavior (pasting formatting).
- **Insert image paste in HTML format notes**: Supports preview display (rewriting src), but excludes v1 for automatic `<img>` insertion when pasting (Markdown only).
- **Backup (JSON)·Vault export ZIP includes attachments**: Document known limitations and address them in a subsequent release (§3.3.7).

---

## 2. UX definition

### 2.1 Global Fast Capture

#### Entry
- New global shortcut **default `Ctrl+Shift+Space`** (can be changed in settings, uses same capture UI as existing 2 shortcuts).
  - In case of conflict, the existing `HotkeyService.TryRegister` operation is followed (log and skip, `HotkeyService.cs:58-62`).
- Added "Quick Capture" item to tray context menu (accessible without shortcut key).

#### Capture window appearance
- `QuickCaptureWindow` — Size approx. **420×130**, no border (`WindowStyle=None`), rounded corners, always on top, no taskbar (`ShowInTaskbar=False`), **aligned to the center** of the top third of the active monitor.
- Configuration: Multiline `TextBox` (3-5 lines, `AcceptsReturn=True`) + bottom hint label ("Enter Save · Shift+Enter Line Break · Esc Cancel").
- No use of WebView2 — lightweight pure WPF window that pops up instantly.

#### Key Action
| key | Action |
|---|---|
| `Enter` | Save and close the window. By default, the note window is not displayed (can be changed through settings) |
| `Shift+Enter` | line break |
| `Ctrl+Enter` | Save + immediately opens the note window to continue editing |
| `Esc` / lose focus | Close without saving (discard contents — do not restore when window reopens) |

- Enter with empty content (only spaces) → Close without saving anything.
- The capture window is **Single Instance**: When it is already floating, pressing the shortcut key again activates the existing window.

#### Feedback after saving
- Tray toast (balloon notification): Title "Stickmark", body `Note saved — "<first 30 characters of derived title>"`. H.NotifyIcon Uses `TaskbarIcon.ShowNotification`, silently ignores on failure (log only).
- Saved notes are immediately reflected in the management list/search like other notes.

#### Clipboard prefill (opt-in)
- Set `Prefill with clipboard text` (default OFF).
- If ON, when the window opens, if it is `Clipboard.ContainsText()`, the text will be prefilled with **all selection** — it will be replaced if you type it right away, and it will be saved as is if you just press Enter.
- Prefill upper limit of 100 KB (excess trimmed) — Prevent UI freezes due to abnormally large clipboard.
- Clipboard contents are not logged in any path (same as existing Serilog policy).

#### Type of saved note
- `Format = Markdown` (matches vault mode `.md` round trip, utilizes automatic extraction of `#tag`).
- `Title`/`Tags`/`PlainText` is derived from `TextExtraction` in the same way as `WindowManager.CreateAndShowNew(template)` (reusing `WindowManager.cs:83-96` pattern).
- Position/Size/Color: Same as the existing cascade rule (`120 + n*24`, 280×280, color rotation).

### 2.2 Paste image

#### Triggers and Inserts
- When pressing Ctrl+V in the source editor (RichTextBox) of **Markdown Note**, if there is an image on the clipboard (when there is no text):
  1. Save the image as PNG in the attachments folder,
  2. Insert `![](attachments/<file name>.png)` text at the caret position,
  3. If split preview is open, shows immediately with traditional debounce re-render (350ms).
- If the clipboard has **both text and an image**, text takes precedence as before (maintaining the current `OnEditorPasting` branch).
- (P2, optional) If you drop an image **file** on the note window, copy it as an attachment and insert the same — Since the existing `Window_OnPreviewDrop`(`NoteWindow.xaml.cs:674`) only opens supported text files, an image extension branch has been added.

#### Storage location (by mode)
| mode | Physics Folder | References written in notes |
|---|---|---|
| bolt | `<VaultPath>\attachments\` | `attachments/<filename>.png` (relative — Obsidian compatible) |
| LiteDB | `%LOCALAPPDATA%\Stickmark\attachments\` | `attachments/<file name>.png` (same notation — the renderer interprets it as a mode-specific physical folder) |

- Both modes use the **same relative notation**. Reasons for not recording the absolute path: (1) broken when moving/backing up the vault, (2) no need to rewrite when switching modes (LiteDB↔vault migration), (3) removing the local path exposure surface when rewriting the HTML.
- Vault mode safety check: `VaultStore.Load` only scans `*.md` as `SearchOption.TopDirectoryOnly` (`VaultStore.cs:42`), so the `attachments/` subfolder has no effect on note loading. ✅

#### File name rules
- `paste-YYYYMMDD-HHmmss.png` (local time), `-2`, `-3` for collisions within the same second… suffix.
- The `" (2)"` pattern of `ExportNaming.UniqueFileName` is **not used** — Spaces and parentheses are encoded as `%20`, etc. when rendering in Markdig, which creates a risk of rewriting and compatibility, so place a small helper that uses a suffix without spaces in `AttachmentService`. (Because it is not a name derived from the title, normalization of `SafeFileName` is not necessary.)

#### Deletion policy (orphaned files)
- **Move/Restore Recycle Bin**: Attached files are not touched (they must remain alive when restored).
- When **permanently purge**:
  - **LiteDB mode**: Extracts `attachments/<name>` references from the content of the note being deleted, and deletes **only files not referenced by all other notes (active + trash). Best-effort (failure is logarithmic only).
  - **Vault mode**: **Do not delete.** The vault is a user-owned folder, so external tools such as Obsidian can refer to the same attachments, and it is consistent with the existing VaultStore philosophy of "not deleting user files in chunks" (`VaultStore.cs:18-19` comment). This asymmetry is specified in README/CHANGELOG.
- Reference extraction is done using file name-based regular expressions (`attachments/([\w.\-]+\.(png|jpg|jpeg|gif|webp))`, ignoring case) and operates in both Markdown/HTML grammar.

#### Display (Preview·Export·WYSIWYG)
- **Note Preview**: Render actual images (virtual host mapping, §3.3.3).
- **HTML/PDF Export**: Keep self-complete document with data: URI inline (§3.3.5).
- **WYSIWYG**: Images are displayed as `![](attachments/….png)` source text (CM6 is a source-oriented editor — does not render). No display change = no security change.

---

## 3. Technical design

### 3.1 New components

| file | Content |
|---|---|
| `Stickmark/Services/IAttachmentService.cs` + `AttachmentService.cs` | Interpretation of physical attachments folder (based on StorageMode/VaultPath in settings), clipboard image → return relative reference after saving as PNG, cleanup of orphans when purging. DI singleton registration (`App.xaml.cs` service component). |
| `Stickmark/Views/QuickCaptureWindow.xaml` + `.xaml.cs` | Capture window in §2.1. Code-behind only handles key processing and clipboard prefill, and storage is delegated to the new `IWindowManager` method. |

`AttachmentService` core signature (draft):

```csharp
public interface IAttachmentService
{
    /// Physical attachments folder based on current save mode (don't create it if it doesn't exist — only creates it when saving).
    string GetAttachmentsDirectory();
    /// Saves the clipboard image as PNG and returns a relative reference to "attachments/<name>.png". If there is no image, null.
    Task<string?> SaveClipboardImageAsync();
    /// Cleans up orphaned attachments of notes that are permanently deleted (LiteDB mode only, best effort).
    Task CleanupOrphansAsync(IReadOnlyList<Note> purged, IReadOnlyList<Note> remaining);
}
```

- Clipboard reading order: **① `"PNG"` format stream of `Clipboard.GetDataObject()`** (alpha preserved, provided by most browsers and capture tools) → **② `Clipboard.GetImage()` + `PngBitmapEncoder`** fallback. Avoid the chronic issue of WPF, where alpha is damaged when passing through DIB, with ①.
- `Directory.CreateDirectory(attachmentsDir)` just before saving.

### 3.2 Global Fast Capture — Change File

| file | change |
|---|---|
| `Stickmark/Models/AppSettings.cs` | Added 3 properties: `QuickCaptureHotkey` (default `"Ctrl+Shift+Space"`), `QuickCapturePrefillClipboard` (default `false`), and `QuickCaptureOpensNote` (default `false`). |
| `Stickmark/Services/IHotkeyService.cs` / `HotkeyService.cs` | Added third `Action quickCaptureHandler` to `Configure`, third gesture argument to `Apply`, constant `QuickCaptureId = "Stickmark.QuickCapture"`, and Remove to `Unregister`. Reuse existing `TryRegister` (fallback/conflict handling) (`HotkeyService.cs:46-67`). |
| `Stickmark/App.xaml.cs` | Passing third handler/gesture to bootstrap wiring (`App.xaml.cs:164-168`). The handler calls `IWindowManager.ShowQuickCapture()`. |
| `Stickmark/Services/IWindowManager.cs` / `WindowManager.cs` | `void ShowQuickCapture()` — Single instance management (existing `_settingsWindow` pattern). `Task<Note> CaptureNoteAsync(string text, bool showWindow)` — Note creation (§2.1 storage type) → `UpsertAsync` → `BuildWindow(note)` is **always performed**, but only `Show()` is omitted if `showWindow` is false (`IsHidden` remains false — displayed normally at next run, and immediately activated with `ShowAll`/tray toggle Consistent with existing rule `WindowManager.cs:45-49` in `RestoreAllAsync`). |
| `Stickmark/Services/ITrayService.cs` / `TrayService.cs` | Added a "quick capture" item to the context menu (existing `MenuItem` helper), `ShowToast(string title, string message)` — wraps `_icon?.ShowNotification(...)`, logs and ignores exceptions. |
| `Stickmark/ViewModels/SettingsViewModel.cs` | `QuickCaptureHotkey`·`QuickCapturePrefillClipboard`·`QuickCaptureOpensNote` binding properties + updated with `hotkeys.Apply(...)` 3-argument call when saving. |
| `Stickmark/Views/SettingsWindow.xaml` / `.xaml.cs` | Third hotkey capture box (added to the `ReferenceEquals(sender, …)` branch of the existing `HotkeyBox_PreviewKeyDown`, `SettingsWindow.xaml.cs:43-44` pattern) + 2 checkboxes. |
| `Stickmark/Resources/Strings.cs` / `Strings.resx` / `Strings.resx` | `Settings_QuickCaptureHotkey`, `Settings_QuickCapturePrefill`, `Settings_QuickCaptureOpensNote`, `Tray_QuickCapture`, `Capture_Hint`, `Capture_SavedToast`, etc. en pairs. |

Need to check (first step in implementation): Verify that `HotkeyGesture.TryParse` parses `"Space"` key name (`Utils/HotkeyGesture.cs`). WPF `Key.Space` will pass if the enum name is `Space` and it is based on `Enum.TryParse`. If that fails, replace the default with `Ctrl+Shift+J`.

Security/Stability Impact:
- The global hook is the same as the existing NHotkey path (no new libraries). Registration failures are already handled harmlessly.
- Clipboard prefill is read-only, opt-in, and log prohibited. No external transmission path.
- Capture window does not create WebView2, so no increase in attack surface.

### 3.3 Paste image — change files and security analysis

#### 3.3.1 Entry point: `NoteWindow.OnEditorPasting` (`Views/NoteWindow.xaml.cs:151-168`)

In the current markup mode, pasting without text (including images) is blocked with `e.CancelCommand()`. Expand this to:

```csharp
private void OnEditorPasting(object sender, DataObjectPastingEventArgs e)
{
    if (!IsMarkup) return;                       // Format mode maintains existing behavior

    if (e.DataObject.GetDataPresent(DataFormats.UnicodeText))
    {
        // ... Existing plain text forcing logic as is...
        return;
    }

    e.CancelCommand();                            // Always cancel the default paste
    if (_viewModel.Format == NoteContentFormat.Markdown)
        _ = InsertClipboardImageAsync();          // If markdown, save attachment + insert reference
}
```

- `InsertClipboardImageAsync`: `IAttachmentService.SaveClipboardImageAsync()` → On success, inserts `![](attachments/…)` into `Editor.CaretPosition` (`InsertTextInRun` + move caret), restarts preview debounce timer. Failures (no image on clipboard/save error) are silently ignored (same feeling as current blocking behavior).
- `NoteWindow` already uses the callback injection pattern (`WindowManager.cs:294`), so `IAttachmentService` also passes `WindowManager` as a delegate instead of constructor injection, or passes a service instance in the window creation part (adapted to the existing style).
- HTML mode does not support v1 (non-target) — Image pasting continues to be blocked.

#### 3.3.2 The essence of the display problem (Why isn't it just visible?)

- Preview is based on `NavigateToString` (`NoteWindow.xaml.cs:580`) → Document origin is `about:blank` and `attachments/x.png` relative path **cannot be interpreted**.
- Preview CSP is `default-src 'none'; img-src data: https: http:` (`HtmlRenderer.cs:38-40`) → `file:` Scheme images are unsuitable at the scheme level before CSP.
- The WYSIWYG virtual host (`stickmark.editor`) maps only the editor asset folder (`MarkdownWysiwyg.cs:58-59`), and the editor.html CSP only maps `img-src data:` — but WYSIWYG does not render images, so **no change required**.

#### 3.3.3 Proposed: Mapping a virtual host dedicated to attachments in the preview WebView

Add 1 line to `NoteWindow.EnsureWebViewAsync` (`Views/NoteWindow.xaml.cs:593-635`):

```csharp
core.SetVirtualHostNameToFolderMapping(
    "stickmark.attachments", attachmentsDir, CoreWebView2HostResourceAccessKind.DenyCors);
```

`HtmlRenderer.Render` exactly matches the `src="attachments/` prefix in HTML **after** Markdig conversion and rewrites it as `src="https://stickmark.attachments/` (the same substitution applies to user-created `<img>` in HTML format notes). The signature extends to the expression `Render(source, format, theme, bool mapAttachments = false)` so that it does not break existing calls and tests.

Rewrite rules (double defense):
- Rewrite only relative srcs that **start** with `attachments/`. `../`, absolute paths (`C:\`, `/`, `file:`), and remote URLs are not touched → Attempts to escape the folder are already neutralized at the rewriting stage.
- WebView2's folder mapping itself blocks access outside the mapping folder by normalizing the path (secondary defense).

**Comparison of alternatives (preview):**

| plan | Advantages | Disadvantages | Judgment |
|---|---|---|---|
| **Virtual Host Mapping (Adoption)** | CSP `img-src https:` is already allowed → CSP no change. NavigateToString 2MB limit is irrelevant. No file re-reading, no burden on 350ms debounce re-render. Already proven mechanism in WYSIWYG | 1 line of WebView configuration + src rewriting logic required | ✅ |
| data: URI inline | No need for mapping or rewriting | **NavigateToString has a string limit of approximately 2MB** — 1-2 screenshots will exceed this and the entire preview will fail to be silent. Read file per render + base64 encoding (inefficient for live preview) | ❌ (Preview) |
| Switch Preview to Temporary Files+Virtual Host Page | Relative path natural analysis | NavigateToString-based existing security review completely invalidates, scope of change exaggerates | ❌ |

**Security impact analysis (SECURITY-REVIEW invariant contrast):**

| invariant | Judgment | Evidence |
|---|---|---|
| Preview script OFF | **Maintain** | `IsScriptEnabled=false`(`NoteWindow.xaml.cs:606`) No change. Mapping only changes resource interpretation and does not grant script execution capabilities. Note Even if HTML references a file in the attachments folder as `<script src>`, there is a double blocking of CSP `default-src 'none'` (absence of script-src) + engine-level script deactivation. |
| Network blocking/remote policy invariant | **Maintain** | A virtual host maps a local folder — not an actual network request. The CSP string is also unchanged (already passed with the existing `img-src https:`). The remote image allowable range is the same as the current one. |
| WYSIWYG whitelist | **Maintain** | `stickmark.editor` NavigationStarting filter (`MarkdownWysiwyg.cs:63-67`) No change. Do not add new mappings to WYSIWYG WebView. |
| Newly opened surface | **Limited** | A malicious note can mark arbitrary files in a folder as `<img>` with `https://stickmark.attachments/<guess name>`, but only image files saved by this feature exist in the folder, and there is no external leak channel because the script is turned off (local single-user app — accepted, documented). Access to the fetch/XHR name `DenyCors` is blocked anyway, and there is no script itself. |

Side-processing: The preview `NavigationStarting` handler (`NoteWindow.xaml.cs:615-622`) causes the user to access `https://… ` When a link is clicked, it opens in an external browser, but since the `stickmark.attachments` host is meaningless in the browser, a branch line is added to **cancel but not open**.

#### 3.3.4 Physical folder interpretation

`AttachmentService.GetAttachmentsDirectory()`:
- StorageMode == "vault" && VaultPath valid → `Path.Combine(VaultPath, "attachments")`
- Others → `Path.Combine(LOCALAPPDATA, "Stickmark", "attachments")`

`EnsureWebViewAsync` maps once when the window is first rendered, so there are no conflicts with mode switches during the session (requires restart — `AppSettings.cs:15` comment).

#### 3.3.5 Export (HTML/PDF): data: URI inline

`HtmlRenderer.RenderDocument` (`Utils/HtmlRenderer.cs:64-120`) is for sharing and printing, so the CSP is only `img-src data:` (intentional — blocking tracking pixels, `HtmlRenderer.cs:68-70` comment). Here we **read the file** and inline** it with a data: URI:
- `RenderDocument(notes, title, string? attachmentsDir = null)` — If a directory is given, replace `src="attachments/…"` with base64 after rendering the body of each note.
- Upper limit: 5MB per image, 20MB total documents. Excess/absent files retain the original text (broken image icon) — the export itself does not fail.
- No change to CSP (already allows `img-src data:`) → Security properties of the exported document remain unchanged, self-complete (displayed even if there are no attachments on the recipient's PC). PDF export also uses the same HTML, so it is resolved automatically.
- Passing the `IAttachmentService` folder to the caller `BackupService.ExportAsHtmlAsync/ExportAsPdfAsync`.

#### 3.3.6 (Subsequent) Pasting WYSIWYG images

Recorded as a v2 candidate only: In the CM6 `paste` event, the image data is passed to the host as a base64 WebMessage(`{type:"pasteImage", data:…}`), and C# saves it and inserts a reference with the `insertText` callback. Fix `tools/mdeditor`(esbuild, `package.json` exists — check if rebuild is possible) + enable maintaining editor.html CSP. However, a separate security review is required because a binary payload is added to the WebMessage channel.

#### 3.3.7 Orphan organization/backup integration

- `VaultRepository.PurgeAsync`/`PurgeTrashedOlderThanAsync`(`Services/VaultRepository.cs:101-113`) and the LiteDB counterpart's **caller side** (NotesList's permanent deletion flow, automatic 30-day cleanup point) passes the list of notes to be purged to `IAttachmentService.CleanupOrphansAsync`. Keep the storage layer unaware of attachments (separation of concerns — only notes for storage, services for attachments).
- LiteDB mode-specific deletion (§2.2 deletion policy). Reference count before deletion is calculated with `GetAllAsync + GetTrashedAsync` snapshot.
- Backup gap (documented): JSON backup·vault ZIP export does not include attachments → CHANGELOG “Known limitation” + follow-up issue. Vault mode is naturally included in the user workflow of copying folders.

### 3.4 Tray Drop — Technical review results: **No, scope not recommended**

**Rationale:**
1. The Windows notification area (tray) is the `Shell_TrayWnd → TrayNotifyWnd → SysPager → ToolbarWindow32` HWND layer owned by explorer.exe. Because OLE drop reception (`RegisterDragDrop`) can be registered **only to the process that owns the corresponding HWND**, third-party apps cannot receive drops on their tray icons.
2. The callback messages that the `Shell_NotifyIcon` API delivers to the app are only click/mouse movement series (NIN_*, WM_LBUTTONUP, etc.) — there are no messages that convey drag source data.
3. `TaskbarIcon` of H.NotifyIcon.Wpf 2.1.4 (currently used, `Stickmark.csproj:44`) also wraps the same API, so there are no drop-related events/properties (scope of use of `TrayService.cs`: Left/DoubleClick command, ContextMenu, ForceCreate — `TrayService.cs:42-58`).

**Compare alternatives:**

| alternative | evaluation |
|---|---|
| A. “Drop Pocket” mini window — 64×64 always-top translucent target in the corner of the screen, turned on via Settings/Tray menu, receiving text/files with `AllowDrop` → Create note | Technically simple (reuses existing `Window_OnPreviewDrop` pattern). However, there are maintenance costs (monitor configuration changes, DPI, blocking other apps) and discoverability issues of the new UI that is always on.
| B. Screen edge hot zone | Requires global mouse hook — unsuitable due to security, performance, and anti-virus false positive risks |
| C. Create a new note when dropping text into an existing note window | It is only a partial replacement (impossible if no notes are displayed) |

**Recommendation: Excludes v1 scopes.** Quick Capture's clipboard prefill (Copy → Shortcut → Enter) covers most of the "frictionless collection" needs, and Drop Pocket (Alternative A) is being considered as a standalone feature in a subsequent release after confirming user demand.

### 3.5 Single File Deployment Impact

No new runtime assets — QuickCaptureWindow is compiled XAML (BAML), attachments is a runtime generated folder. No changes to WYSIWYG embed asset rules (embed+extract). Verification is performed based on **publish output** as is customary.

---

## 4. Work decomposition + risk classification table

| # | work | file | Risk | Evidence |
|---|---|---|---|---|
| 1 | `AttachmentService` — Folder interpretation·Clipboard PNG storage·File name rules | New 2 files, App.xaml.cs DI | **High Risk (Opus Direct)** | Lots of edges on file system writes + clipboard format negotiation (PNG/DIB fallback) |
| 2 | Attach `AttachmentService.CleanupOrphansAsync` + purge flow | AttachmentService, purge caller | **High Risk (Opus Direct)** | **User file deletion logic** — Reference count error = data loss. Includes asymmetric policy for each mode |
| 3 | extend `OnEditorPasting` + insert caret | NoteWindow.xaml.cs | **High Risk (Opus Direct)** | Editor core path. RichTextBox caret/focus/undo stack subtleties, preventing reversion to existing plaintext forcing logic |
| 4 | Preview virtual host mapping + `HtmlRenderer` src rewrite + NavigationStarting branch | NoteWindow.xaml.cs, HtmlRenderer.cs | **High Risk (Opus Direct)** | Security invariant contact point (CSP·Script OFF·Network Policy). Rewrite rules are your first line of defense |
| 5 | `RenderDocument` data: inline (with upper size limit) | HtmlRenderer.cs, BackupService.cs | Low Risk (Sonnet Delegation) | Render string manipulation + file reading, even if it fails, it only results in quality issues with the exported document. Easy to cover with test |
| 6 | `IHotkeyService`/`HotkeyService` 3rd shortcut + App wiring | IHotkeyService.cs, HotkeyService.cs, App.xaml.cs | Low Risk (Sonnet Delegation) | Mechanical extension of two existing patterns, reuse of existing conflict handling logic. `"Space"` parsing check precedence |
| 7 | `QuickCaptureWindow` UI + key handling + prefill | New XAML/cs | Low Risk (Sonnet Delegation) | Independent new window — no conflicts with existing code. Implement the approved UX (§2.1) as is. Confirm prohibition of clipboard logging in reviews |
| 8 | `WindowManager.ShowQuickCapture/CaptureNoteAsync` | IWindowManager.cs, WindowManager.cs | **High Risk (Opus Direct)** | Window lifecycle (`_windows` list/hidden windows) consistency — RestoreAll/ToggleAllVisible regression risk |
| 9 | Settings UI (3rd hotkey box + 2 checkboxes) + SettingsViewModel | SettingsWindow.xaml/.cs, SettingsViewModel.cs | Low Risk (Sonnet Delegation) | Duplicate existing pattern (add `ReferenceEquals` branch) |
| 10 | Tray menu item + `ShowToast` | ITrayService.cs, TrayService.cs | Low Risk (Sonnet Delegation) | Off-the-shelf MenuItem helper + wrapping ShowNotification, failure harmless |
| 11 | Strings en/en Add resource | Strings.cs, Strings.resx, Strings.resx | Low Risk (Sonnet Delegation) | Pure Resources |
| 12 | Writing Tests (§5 Unit Tests Complete) | Stickmark.Tests/Tests.cs | Low Risk (Sonnet Delegation) | Test code — However, the test case list #2·#4 is specified by Opus |
| 13 | CHANGELOG/README/docs update + version upgrade | document, csproj, iss | Low Risk (Sonnet Delegation) | Document/Version Synchronization Conventional Tasks |
| 14 | (Select P2) Drop insert image file | NoteWindow.xaml.cs | Hold | Window_OnPreviewDrop extension — #3·#4 decided after stabilization |

Recommended order: **#6→#8→#7→#9~11** (capture axis) and **#1→#3→#4→#2→#5** (image axis) can be parallel. #12·#13 after completing each axis.

---

## 5. Test/verification plan

### 5.1 Unit tests (Stickmark.Tests — added to current 107)

| target | case |
|---|---|
| Attachment file name rules | `paste-\d{8}-\d{6}(-\d+)?\.png` format, `-2` suffix for same second conflict, no spaces/parentheses |
| HtmlRenderer src rewrite | `![](attachments/a.png)` → `https://stickmark.attachments/a.png`; `../attachments/`·Absolute path·`file:`·Remote URL is **immutable**; Rewrite `<img src="attachments/…">` in HTML formatted notes; Completely immutable (no regression to existing tests) when `mapAttachments=false` (default) |
| RenderDocument inline | Existing file → `data:image/png;base64,… `insert; Absent file → maintain original text; Exceeding 5MB → Beauty Line; CSP meta string immutability |
| Orphan Cleanup | Reference extraction regular expression (Markdown/HTML mix); When two notes refer to the same file, one side is preserved by purging; Delete LiteDB mode only (vault mode no-op) |
| HotkeyGesture | `"Ctrl+Shift+Space"` Parsing/Format Round Trip |
| VaultMarkdown | Lossless round trip in the body, including image references |
| AppSettings | New 3-field default value/serialization round trip (backward compatibility with existing configuration file — default value if field is absent) |

### 5.2 Manual verification (publish build basis — single file distribution convention)

1. **Capture flow**: Shortcut key in any app focus state → Show window → Enter Save → Confirm toast → Note exists in the entire list. `Ctrl+Enter` opens the note window immediately. Not saved when Esc/focus is lost. Activates existing window when re-entering shortcut key (no duplicate window).
2. **Prefill**: Setting ON + Copy text → Prefill all selections in the window. Blank window when there are only images on the clipboard.
3. **Paste image (LiteDB)**: Copy screen with capture tool → Ctrl+V in markdown note → Create PNG in `%LOCALAPPDATA%\Stickmark\attachments\` + Insert reference + Display image in split preview. Simultaneous text + image clipboard → Text priority (existing behavior).
4. **Paste image (vault)**: Confirm the same after switching to vault mode + Create `<vault>\attachments\` + **Open the vault with Obsidian and check image display** (relative path compatibility) + Attachments folder does not affect note list loading.
5. **Export**: Export notes containing images to HTML/PDF → Display images even when opened in a different location without an attachments folder (self-complete).
6. **Delete Policy**: Note Recycle Bin → File Preservation → Restore → Mark as Normal → Permanently Delete (LiteDB) → Delete only unreferenced files, preserve files shared with other notes. Vault mode permanent deletion → file preservation.
7. **Security regression**: Enter `<script>alert(1)</script>` + `<img src="https://stickmark.attachments/../../settings.json">` in the note → Confirm that the script is not executed and the image is not displayed. Remote image behavior in preview is the same as before.
8. **107 existing tests + new tests all green**, WYSIWYG edit/preview/export smoke.

---

## 6. Expected size and release proposal

| Item | estimate |
|---|---|
| New File | 4 (IAtachmentService/AttachmentService, QuickCaptureWindow.xaml/.cs) |
| edit file | About 12 (NoteWindow, HtmlRenderer, HotkeyService±I, WindowManager±I, TrayService±I, App, AppSettings, SettingsWindow±VM, 3 types of Strings, BackupService) |
| Net increase code | Approximately 700 to 1,000 lines (excluding tests) |
| New Test | 15-20 |
| Implementation effort | Opus axis (5 high-risk cases) 1.5 days + Sonnet axis (8 low-risk cases) Parallel 1 day + Verification 0.5 days |

**Release version: v2.3.0** (Minor — 2 new features added, no data schema changes, settings backward compatibility). Tray drop records scope exclusions with justification in CHANGELOG and leaves WYSIWYG image paste/backup attachments/drop pockets as subsequent candidates.
