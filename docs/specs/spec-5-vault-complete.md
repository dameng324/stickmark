# Spec 5 — Vault completion (real-time folder monitoring, conflict copy notification, opening with external app)

- Target version: v2.3.0 (currently v2.2.2)
- Status: Waiting for specification confirmation (before implementation)
- Commit related code: `1a9dd0d` (main)

v2.0 Live Vault mode (Note = `.md` in folder + sidecar `.stickmark-index.json`)
External changes are reflected **only upon restart** (assuming single author, documented on wiki).
This specification defines the implementation details of a combination of three functions that bridge that gap.

---

## 1. Goal / Non-goal

### Goal

| # | Features | Summary |
|---|------|------|
| F1 | Real-time folder monitoring | External changes to the vault folder's `.md` (cloud synchronization, Obsidian/VS Code editing) are automatically reflected during execution. Changes (echoes) made by the user are ignored. If there are unsaved edits in the open window, prompt before overwriting them. |
| F2 | Conflict copy information | OneDrive/Dropbox/Syncthing conflict file pattern detection → Notification in tray (does not automatically merge/delete). It also blocks the existing risk of conflicting copies obscuring the original note from the cache (duplicate id). |
| F3 | Open Vault with External App | Added "Open Folder (Explorer)" / "Open in Obsidian" / "Open in VS Code" to the tray vault menu. Hide uninstalled app items. |

### Non-targets (things not done in this release)

- **3-way merge / automatic conflict resolution** — External changes vs. local unsaved edits only go to the “File first / Keep my edits” alternative prompt. Conflict copy files are only notified and are not automatically merged or deleted.
- **`.stickmark-index.json` external editing monitoring** — Sidecar is a Stickmark-only file. Since it cannot be modified by external tools and SaveOne always rewrites it (echo noise source), it is excluded from monitoring.
- **Vault path change hot swap** — Storage switching/path changes are applied after restart as before (maintaining the `AppSettings.StorageMode` annotation policy).
- **Monitoring in LiteDB mode** — The monitoring service runs only in vault mode.
- **Polling fallback when FileSystemWatcher fails** — If FSW fails on a network drive, etc., only a log is left and the function is quietly turned off (watching is an additional feature — the same as the existing attitude of `NoteWindow.StartFileWatcherIfLinked`).
- **LinkedFilePath monitoring reorganization** — Window-level monitoring (`NoteWindow.xaml.cs:694-794`) is left as is. Vault monitoring is a separate central service.

---

## 2. UX definition

### 2.1 Reflect external changes (F1)

| Situation | Action |
|------|------|
| **Edit** `.md` externally, no unsaved edits in that note window | **Quietly Reflecting** (Obsidian style). Instant replacement of editor text, title, and preview. No separate pop-ups/badges — Sticky notes are always on the screen, so changing content itself is feedback. Serilog 1 line of information log. |
| Edited externally, has **unsaved edits** in the note window | Make that window the owner of a Yes/No prompt (reusing existing resources): `Strings.Note_FileChangedPrompt` / `Strings.Note_FileChangedTitle`, default button **No** (keep my edits). Yes → Replace with file contents, No → Keep my edits (next debounce save will overwrite the file again = revert to single author). Same wording as `NoteWindow.ReloadLinkedFileAsync`(NoteWindow.xaml.cs:725-761)·same default value. |
| **Add** (1-3) `.md` from external | Create a new note window, but do not **Show** (prevent surprise pop-ups). 1 tray balloon notification: "N new notes added to vault". Update the list if the note list window is open. |
| **Add bulk** from external `.md` (more than 4 in one debounce batch — cloud initial sync, etc.) | Same as above, but only the notification text is summarized. Prevent window bombs (same effect as `MaxFilesToOpen` in App.xaml.cs:197). |
| **delete** `.md` from external, no unsaved edits in window | Quietly close the note window and remove it from the cache/window list (not the trash — the file is the original). |
| Delete externally, window has **unsaved edits** | Prompt: "The file in this note has been deleted from the vault. Would you like to resave the edits you are making to a new file?" — **Yes (default)**: Keep, next save will recreate the file. No: Close the window and remove. |
| **Rename** externally (same as frontmatter id) | Recognition as the same note based on ID — Maintain window, update only file name mapping. If the title has changed from frontmatter, the title is reflected. |
| Reflection occurs while the notes list window (`NotesListWindow`) is open | Recalling `ShowAndReloadAsync()` in the same pattern as `WindowManager.DeleteAsync`(WindowManager.cs:322-325). |

Only one prompt per note window at a time (`_reloadPromptOpen` pattern, NoteWindow.xaml.cs:51). Subsequent changes that arrive while the prompt is floating are processed in the next debounce cycle.

### 2.2 Conflicting copy information (F2)

- **Notification type**: Tray balloon notification (H.NotifyIcon `TaskbarIcon.ShowNotification`, best-effort — only log on failure).
  - Title: `Vault Conflict Copy Found` (`Strings.Vault_ConflictTitle`)
  - Body: `"{file name}" — It appears to be a cloud sync conflict. Please check the contents and organize them.` (`Strings.Vault_ConflictBodyFormat`)
  - If there are multiple cases, group them into one case and summarize “{N} conflicting copies found: {first file name} and others.”
- **Notification timing**: (a) first scan immediately after app startup, (b) when a new conflicting copy appears in the watcher Created/Renamed event.
- **Prohibit duplicate notifications within a session**: The set of notified file names (ignoring case) is remembered during the session.
- **Handling of the conflicting copy file itself**: It will be loaded as a regular `.md` note (since the user has to view the content), but **the id of the original note will not be intercepted** (§3.6). No automatic delete/merge.

### 2.3 Open Vault with External App (F3)

Extend Tray Vault Menu (`TrayService.BuildVaultMenu`, TrayService.cs:148-162) — Show items below only when in vault mode (`StorageMode == "vault"` && `VaultPath` in effect):

| Menu | Display conditions | Action |
|------|-----------|------|
| Open Folder (Explorer) | When in bolt mode, always | `explorer.exe "<VaultPath>"` |
| Open in Obsidian | Show only when the `obsidian://` URL protocol is registered in the registry (not installed → **hidden**) | `obsidian://open?path=<EscapeDataString(VaultPath)>` |
| Open in VS Code | Code Displayed when executable file is found or `vscode://` protocol is registered (not installed → **hidden**) | Execute Code.exe directly `"<code>" "<VaultPath>"` First, if not, `vscode://file/<path>` |

- Policy is **hidden** (not inactive) — neater than having uninstalled app menus remain gray and messy. Installation detection is reevaluated every time in the menu `Opened` event (pattern already used for `_autoStartMenu` update, TrayService.cs:115-121), so installation/uninstallation is reflected without app restart.
- In LiteDB mode, only the existing “Export/Import to Vault” is visible (maintained as is).

---

## 3. Technical design

### 3.1 New component overview

```
Services/IVaultWatcher.cs // Interface (Start/Dispose + entry point for testing)
Services/VaultWatcherService.cs // FSW + debounce + echo suppression + apply diff orchestration
Utils/ConflictCopyDetector.cs // Pure function: File name → Conflict copy status (static)
Utils/ExternalAppLocator.cs // Pure (registry/file presence) detection: Obsidian/VS Code
```

Modified files: `App.xaml.cs`(DI·Lifetime), `WindowManager.cs`(+`IWindowManager`) — diff application API,
`NoteWindow.xaml.cs` — External update apply method, `VaultRepository.cs` — Snapshot/diff support,
`VaultStore.cs` — id duplication policy + self-writing ledger, `TrayService.cs`(+`ITrayService`) — menu/notification,
`Resources/Strings.resx`·`Strings.resx` — New string.

### 3.2 Surveillance service life cycle

- **Register**: `services.AddSingleton<IVaultWatcher, VaultWatcherService>()` in `App.xaml.cs` DI block (App.xaml.cs:92-123).
- **Startup**: After `RestoreAllAsync()`·`TrayService.Initialize()` in `OnStartup` (around App.xaml.cs:146-148):

  ```csharp
  if (_host.Services.GetRequiredService<INoteRepository>() is VaultRepository)
      _host.Services.GetRequiredService<IVaultWatcher>().Start(settings.VaultPath!);
  ```

  It does not start if it is not in vault mode or if `VaultRepository` initialization has fallen back to LiteDB (App.xaml.cs:105-110).
- **One reload cycle immediately after startup**: `Start()` executes one reload cycle immediately **after** FSW activation — retrieves external changes caught between the time of the constructor `Load()` (app startup) and the start of the watch (lace sealing). This first cycle also doubles as the “first scan of conflict copies immediately after startup” in §2.2.
- **Exit**: `GetService<IVaultWatcher>()?.Dispose()` before tray in the cleanup order of `OnExit` (App.xaml.cs:216-225). Dispose releases the FSW event → stops the debouncer → returns without waiting for the reload to complete while in progress (safe because the reload is serialized with the save with `_gate`).

### 3.3 Event flow and debounce

```
FSW (*.md, excluding subfolders) ──Created/Changed/Deleted/Renamed──▶ Event queue (thread pool)
        │ NotifyFilter = LastWrite | FileName | Size (same as NoteWindow.xaml.cs:705)
        ▼
  Route Accumulation(ConcurrentDictionary<string, byte>) + DebounceAction(700ms) Trigger
        ▼ (Once after it becomes quiet)
  ① Echo judgment: All paths in the batch match the self-writing ledger → Skip all
  ② Conflict copy inspection: ConflictCopyDetector match during deployment path → Tray notification (duplicate suppression)
  ③ Reload snapshot diff: VaultRepository.ReloadWithDiffAsync()
        ▼
  ④ Apply UI: Dispatcher.InvokeAsync(() => WindowManager.ApplyVaultDiffAsync(diff))
```

- **Debounce 700ms**, reuse `Utils/DebounceAction` (existing, DebounceAction.cs:8). Since cloud synchronization pours out multiple events (write + properties) per file, paths are accumulated as a set and reloaded only once per batch.
- FSW `Error` event (buffer overflow): Handle as “full reload + skip echo decision” by adding a special marker to the path set. Increased to `InternalBufferSize = 64KB`.
- Reload reloads the entire folder (`VaultStore.Load`), so there is no need to trust individual paths — the event path is used **only for echo judgment and collision detection**, and the reflected content is always calculated as a snapshot diff (strong against event loss).

### 3.4 Echo Suppression — Self-Write Ledger

Two sources of echo noise: ① `VaultStore.SaveOne` always rewrites `.md` (VaultStore.cs:71-78) — Just moving/resizing the window causes the debounce save to touch the file. ② SaveOne This index is also rewritten, but the index is ignored because it is not caught in the FSW filter (`*.md`).

- **Ledger data structure**: `ConcurrentDictionary<string /*lowercase file name*/, DateTime /*recording time UTC*/>` in `VaultStore`.
  `SaveOne`/`Save` records the file in the ledger immediately after writing, and `DeleteFile` records it in the ledger immediately after deletion.
- **Judgment rule**: If the file name of the event path is in the ledger and `now - recording time <= 3 seconds`, echo. If **all** paths in the batch are echoes, the entire cycle is skipped; if even one is not, proceed normally (accuracy is guaranteed by the diff in ③ — below).
- **Accuracy Backstop**: Even if the echo decision is incorrect and the reload is performed, the cache update (`_notes[...] = note`) and the file write (`_store.SaveOne`) occur within the same `_gate` critical section (VaultRepository.cs:66-82), so the reload result is the same as the cache → diff is empty → harmless no-op that does not touch the window. **In other words, echo suppression is an optimization for IO saving, and diff integrity structurally prevents malfunctions (incorrect prompts/overwrites).** Hash comparison is unnecessary (overdesign).
- Ledger items are lazily cleaned after 3 seconds of judgment (no separate timer required).

### 3.5 Snapshot diff — `VaultRepository.ReloadWithDiffAsync()`

Maintain the existing `ReloadAsync()` (VaultRepository.cs:27-36), but add a variant that returns a diff for the watcher.

```csharp
public sealed record VaultDiff(
    IReadOnlyList<Note> Added, // new id
    IReadOnlyList<Guid> Removed, // disappeared id
    IReadOnlyList<(Note Fresh, string OldContent)> Changed); // Change content/title/format

public async Task<VaultDiff> ReloadWithDiffAsync()
{
    await _gate.WaitAsync().ConfigureAwait(false);
    try
    {
        var old = _notes.Values.ToDictionary(n => n.Id, n => (n.Content, n.Title, n.Format));
        _notes.Clear();
        foreach (var n in _store.Load()) _notes[n.Id] = n;
        // Calculate Added/Removed/Changed by comparing old vs _notes (Ordinal comparison)
    }
    finally { _gate.Release(); }
}
```

- **Changed decision**: When `Content`(Ordinal) or `Title` or `Format` are different. `ModifiedAt` is not used in judgment (it is a value that is updated every time we write, so it is noisy).
- **Reason for returning `OldContent` together**: Required for the window's "unsaved edit" determination (§3.7). Snapshots are atomic because diff calculation and cache replacement are in the same critical section.
- The display status, such as position and color, comes from the sidecar, so it does not change through external `.md` editing — diff only looks at the content axis. The trash can flag is also under the jurisdiction of the sidecar, so it is not subject to diff.

### 3.6 ID duplication policy — prevent conflicting copies from obscuring the original (fixes existing risk)

The current `VaultStore.Load` overwrites `_files[note.Id] = filename` / `result.Add` in the enumeration order (VaultStore.cs:48-50, and `_notes[n.Id] = n` in the `VaultRepository` constructor · `ReloadAsync`), so that **two files with the same frontmatter id** (= conflicting copies of If there is a typical file, later files in alphabetical order cover the original. Example: A crash path where `shoppinglist(conflicted copy).md` pushes `shoppinglist.md` out of the cache, and then SaveOne writes **to the conflicted copy file**.

**Policy** (resolved inside Load, read-only pass where files are never rewritten):

1. When encountering a second file with the same ID, keep **the page whose file name matches the `File` item in the sidecar index** as the authentic copy.
2. If it is impossible to determine by index (both sides do not match, etc.), `ConflictCopyDetector` non-matching page → If it is still ambiguous, the page read first is used as the authentic copy.
3. The pushed file is **loaded as a separate note with a new Guid** (as the title says → user identifies it as "○○ (conflict copy)" in the list). The new id is in-memory only — the file is not rewritten, so the id may change between reloads, but the moment the user **edits** that note, the new id is persisted to frontmatter via the SaveOne path. Acceptable trade-off (assuming conflicting copies are identified and cleaned up).

### UI application of 3.7 diff — `WindowManager.ApplyVaultDiffAsync(VaultDiff)`

`ReloadAsync()` (WindowManager.cs:52-58, reconfigure after discarding the previous window) is left for backup retrieval, and a fine-grained update method is created. **Call only on the UI thread** (watcher passes to `Dispatcher.InvokeAsync`).

- **Changed**: Finds a window in `_windows` with `w.ViewModel.Id == fresh.Id` (all notes exist because the window is created at startup).
  - If the current content of the window (`vm.Content`) is equal to `OldContent` → No unsaved edits → `window.ApplyExternalContent(fresh)` is applied quietly.
  - If different → There are unsaved edits → §2.1 prompt. Applies only when Yes.
  - Apply = `ReplaceEditorText` + `vm.UpdateContent(fresh.Content, fresh.Format)` + Preview re-render — Extract the body of `NoteWindow.ReloadLinkedFileAsync`(NoteWindow.xaml.cs:757-760) with `internal void ApplyExternalContent(string text, NoteContentFormat fmt)` so that the two paths are Share. If WYSIWYG editing is in progress (`_wysiwygOn`), it is treated as “there are unsaved edits” and an unconditional prompt is displayed (the internal state of the editor is not arbitrarily replaced).
  - **Caution**: To prevent the VM's debounce save immediately after application from writing the same content to the file again, a path that does not trigger the save schedule after `UpdateContent` is required. `ApplyExternalContent` calls `vm.MarkSynced(fresh.Content)` at the end, and `NoteViewModel.SaveAsync` is functionally harmless as it will just overwrite the same value anyway if the content is the same as the cache in vault mode — However, replacing the editor with `_suppressEditorSync` to prevent one unnecessary SaveOne (=new echo) `UpdateContent` The existing method of blocking re-entry (NoteWindow.xaml.cs:763-778) is used as is, and the VM is updated using a dedicated method of hydrate nature (`vm.HydrateExternal(content, format)`: Schedule suppression with `_hydrating` toggle, see NoteViewModel.cs:25·134-142).
- **Added**: Only creates a window with `BuildWindow(note)` and does not `Show()` (§2.1 policy). One tray notification regardless of batch size.
- **Removed**: If there are no unsaved edits in the window, display the `vm.Dispose()` target and remove it from `RequestClose()` and `_windows`. If there are unsaved edits, prompt §2.1 (Yes=Keep: Next save will regenerate the file — `SaveContentAsync` ignores ids that are not in the cache (VaultRepository.cs:76), so **if you choose to keep, you must re-register them in the cache with `UpsertAsync`**).
  - The "ignore missing id" convention of `SaveContentAsync` already ensures that a late flush (`OnClosing` → `FlushAsync`, NoteWindow.xaml.cs:796-812) does not revive the file when the window is closed.
- At the end, update the note list window (§2.1) and 1 line of Serilog summary log (`Vault sync: +{a} ~{c} -{r}`).

### 3.8 Conflict Copy Detector — `ConflictCopyDetector`

Pure static function `bool IsConflictCopy(string fileName)` + `string Describe(...)`. Pattern (ignoring case):

| Source | Pattern (file name, extension exclusion criteria) | Yes |
|------|------|-----|
| Dropbox (EN) | `\(.+ conflicted copy( \d{4}-\d{2}-\d{2})?( \(\d+\))?\)$` | `notes (DESKTOP's conflicted copy 2026-07-01).md` |
| Dropbox (KO) | `\(conflict[^)]*copy[^)]*\)$` | `Notes (conflicting copy 2026-07-01).md`, `Name (conflicting copy).md` |
| OneDrive | `-<Environment.MachineName>(-\d+)?$` **and** only if `.md` with the suffixed name exists in the vault | `todo-DESKTOP-ABC123.md` (if `todo.md` exists) |
| Syncthing | Contains `\.sync-conflict-\d{8}-\d{6}(-\w+)?` | `plan.sync-conflict-20260701-093012-ABCDEF.md` |

- OneDrive pattern often has false positives for hyphenated file names, so **machine name matching + original existence** double condition. The rest are judged based on the file name alone (since the original may have already been organized).
- Set the signature to `IsConflictCopy(string fileName, Func<string,bool> siblingExists)` to enable unit testing without a file system.

### 3.9 External app detection/execution — `ExternalAppLocator`

- **Obsidian**: Existence of the `URL Protocol` value for the `obsidian` key in `Registry.ClassesRoot` or `HKCU\Software\Classes` (read-only, exception is false). Execute: `Process.Start(new ProcessStartInfo($"obsidian://open?path={Uri.EscapeDataString(vaultPath)}") { UseShellExecute = true })` — `NoteWindow.OpenExternal`(NoteWindow.xaml.cs:637-641) pattern. If the vault is not registered in Obsidian, Obsidian will display its own instructions (not our responsibility).
- **VS Code**: Search order ① `%LOCALAPPDATA%\Programs\Microsoft VS Code\Code.exe` ② `%ProgramFiles%\Microsoft VS Code\Code.exe` ③ `code.cmd` in PATH (search PATH partition without `where`) ④ Register `vscode://` protocol. If ①~③, `Process.Start(codePath, $"\"{vaultPath}\"")` (folder argument), if ④, `vscode://file/{path}` URI. Be careful about slash normalization (`\`→`/`) and quoting — the path is exactly the bolt path you chose, and no shell string concatenation is done (using an argument array).
- Explorer: `Process.Start("explorer.exe", $"\"{vaultPath}\"")`. If a folder disappears (cloud relocation, etc.), a warning MessageBox is displayed after checking `Directory.Exists` first.
- The detection function is called every time the tray menu is `Opened`, so it is kept light (registry 1-2 times, file existence 3 times or less).

### 3.10 Tray Notifications — `ITrayService.ShowNotification`

Added `void ShowNotification(string title, string message)` to `ITrayService`. The best-effort implementation wraps `_icon?.ShowNotification(title, message, NotificationIcon.Info)`(H.NotifyIcon 2.1.4) in a try/catch — only logs when tray initialization fails (TrayService.cs:61-64 path). The monitoring service is injected with `Action<string,string>` notification callbacks (connected when assembling App.xaml.cs) without directly referencing `ITrayService` — avoiding circular dependencies.

### 3.11 Threading Summary

| section | thread |
|------|--------|
| FSW callback → path accumulation → DebounceAction | Threadpool (lock-free set accumulation) |
| Echo judgment·Collision detection·`ReloadWithDiffAsync` | Threadpool (serialize to storage path with `_gate`) |
| `ApplyVaultDiffAsync`(window manipulation·prompt) | `Application.Current.Dispatcher.InvokeAsync` |
| tray notification | Dispatcher (requires H.NotifyIcon) |

Re-entry defense: Only one watch cycle at a time with `SemaphoreSlim(1,1)`. As new events arrive during the cycle, they are accumulated in the path set and moved on to the next debounce (no misses).

### 3.12 New resource string (en)

`Strings.resx` / `Strings.resx`:

| key | en Example |
|----|---------|
| `Vault_OpenInExplorer` | Open Folder (Explorer) |
| `Vault_OpenInObsidian` | Open in Obsidian |
| `Vault_OpenInVsCode` | Open in VS Code |
| `Vault_ConflictTitle` | Vault Crash Copy Found |
| `Vault_ConflictBodyFormat` | "{0}" — Appears to be a cloud sync conflict. Check the contents and organize them. |
| `Vault_ConflictManyFormat` | {0} conflicting copies found: {1} and others |
| `Vault_NotesAddedFormat` | {0} new notes added to your vault. |
| `Vault_FileDeletedPrompt` | The files in this note have been deleted from the vault. Would you like to resave the edits you are making as a new file? |
| `Vault_FolderMissingFormat` | Vault folder not found: {0} |

External modification prompts reuse the existing `Note_FileChangedPrompt`/`Note_FileChangedTitle`.

---

## 4. Work decomposition + risk classification

| # | work | file | Risk | Evidence |
|---|------|------|--------|------|
| T1 | `VaultStore` id duplication policy + self-writing ledger | `VaultStore.cs` | **High Risk (Opus Direct)** | Change the load semantics of the storage engine — a data incident path where mistakes cause original notes to be saved as conflicting copy files. Requires verification of interaction with existing 107 tests. |
| T2 | `VaultRepository.ReloadWithDiffAsync` + `VaultDiff` | `VaultRepository.cs` | **High Risk (Opus Direct)** | Atomic snapshot in `_gate` critical section·diff — Save loss/ghost prompt in case of concurrency mistake. |
| T3 | `VaultWatcherService`(FSW·Debounce·Echo Suppression·Cycle Serialization) | New | **High Risk (Opus Direct)** | Threading (threadpool↔dispatcher)·event burst·buffer overflow·life cycle — the technical core of this combination. |
| T4 | `WindowManager.ApplyVaultDiffAsync` + `NoteWindow.ApplyExternalContent`/`NoteViewModel.HydrateExternal` | `WindowManager.cs`, `NoteWindow.xaml.cs`, `NoteViewModel.cs` | **High Risk (Opus Direct)** | State machine for open windows, unsaved edits, WYSIWYG, and delete prompts. If something goes wrong, the user edits will be lost. |
| T5 | `ConflictCopyDetector` + unit test | new `Utils/` | **Low Risk (Sonnet Delegation)** | Pure function + regular expression — Regardless of file system or UI, completed by test. |
| T6 | `ExternalAppLocator` + 3 tray menus | New `Utils/`, `TrayService.cs` | **Low Risk (Sonnet Delegation)** | Read-only detection + Process.Start — hide menu/localize warning even if it fails. Following the existing `BuildVaultMenu`·`Opened` pattern. |
| T7 | `ITrayService.ShowNotification` + notification wiring | `TrayService.cs`, `ITrayService.cs` | **Low Risk (Sonnet Delegation)** | Best-effort 1 method added. |
| T8 | Add resource string (en) | `Strings.resx`, `Strings.resx`, `Strings.cs` | **Low Risk (Sonnet Delegation)** | Mechanical addition. |
| T9 | DI registration/start/end wiring | `App.xaml.cs` | **High Risk (Opus Direct)** | Although it is a small amount, the life cycle sequence (Restore → Tray → Watcher startup, shutdown reverse order) is combined with the T3 design. Proceeds as one with T3. |
| T10 | Subjects T1·T2·T3 during test (§5) | `Stickmark.Tests/Tests.cs` | **High Risk (Opus Direct)** | Concurrency/scenario design directly verifies specifications. |
| T11 | Documentation (CHANGELOG, README vault section, replace wiki "reflect on restart" statement) | `docs/`, `CHANGELOG.md` | **Low Risk (Sonnet Delegation)** | Working with documents. |

Recommended order: T1 → T2 → T5 (parallel) → T3+T9 → T4 → T6/T7/T8 (parallel) → T10 → T11.

---

## 5. Test/verification plan

### 5.1 Unit tests (`Stickmark.Tests/Tests.cs`, added to existing 107)

**VaultStore / VaultRepository (T1·T2):**
1. ID duplication: The file pointed to by the index is loaded as the original, and other files become separate notes with a new Guid (Check that file content is immutable — read-only pass).
2. ID duplication + index determination impossible: The conflict pattern file is pushed out.
3. `ReloadWithDiffAsync`: Add external file → Added / Delete → Removed / Modify content → Changed (OldContent correct) / No change → All 3 sets are empty.
4. diff atomicity: Repeating simultaneous calls to `ReloadWithDiffAsync` and `SaveContentAsync` (Task.WhenAll loop) — no exceptions or losses.
5. Reload immediately after SaveOne → diff empty set (echo backstop protocol).
6. Self-writing ledger: Right after SaveOne/DeleteFile, querying the file name is true, after 3 seconds (time injection), it is false.

**ConflictCopyDetector (T5):** Dropbox EN/KO·OneDrive (machine name match/mismatch·original presence/absence)·Syncthing·normal file name (including hyphen) No false positives — 12+ cases based on table.

**ExternalAppLocator (T6):** Path candidate search order · null if not present — Test by making the filesystem root injectable (registry dependent branches are delegated to manual verification).

**VaultWatcherService (T3):** FSW real event test is timing vulnerable — core logic (path accumulation → echo determination → cycle execution) exposed and conclusively verified as `internal Task ProcessBatchForTestAsync(IEnumerable<string>)`: full echo batch → no reload call / mixed batch → one call / re-entry during cycle → serialization. The actual FSW combination is only one temporary folder + generous polling wait (5 seconds) smoke.

### 5.2 Manual harness scenarios (pre-release checklist — done with temporary vault folders, no live vaults)

| # | Scenario | expectations |
|---|----------|------|
| H1 | Edit/save the `.md` of the vault with notepad while running | Replace the note window contents within ~1 second, no pop-ups |
| H2 | External modification immediately after typing in the note window (within 0.5 seconds) | Display prompt, No → Keep/Resave my edits, Yes → File contents |
| H3 | Copy 5 new `.md`s to vault | No window, tray notification "5", 5 in list |
| H4 | External deletion of `.md` in open note | Auto close window (prompt if editing) |
| H5 | create `name(conflict copy).md` (same frontmatter id as original) | One tray conflict notification, original note window remains normal, copy appears separately in list |
| H6 | Moving/resizing/typing notes in the app | No tray notification/prompt/window blinking (ignore echo), no sync cycle spam in log |
| H7 | Open the same vault with Obsidian and cross-edit both sides | Converges to last save first, prompt only for unsaved edits |
| H8 | Tray menu: Open Explorer/Obsidian/VS Code (installed/uninstalled respectively) | Open when installed, hide item when not installed; In LiteDB mode, all 3 items are not displayed |
| H9 | Restart app (add/delete files while unmonitored) | Reflected in one matching cycle immediately after starting |
| H10 | Reconfirm H1·H5·H8 with single file distribution output (`dotnet publish` result) | Same operation as Debug |

H10 is a practice to prevent recurrence of this repository's existing incident history (missing asset in single file deployment, `1a9dd0d`). When performing manual verification, do not terminate the running actual instance or touch the live vault (temporary folder + separate settings environment).

---

## 6. Propose expected size and release version

| Item | estimate |
|------|------|
| New File | 4 (`IVaultWatcher`, `VaultWatcherService`, `ConflictCopyDetector`, `ExternalAppLocator`) ≈ 450~550 LOC |
| edit file | 8 (`VaultStore`, `VaultRepository`, `WindowManager`+`IWindowManager`, `NoteWindow.xaml.cs`, `NoteViewModel`, `TrayService`+`ITrayService`, `App.xaml.cs`, `Strings.*`) ≈ 250~350 LOC |
| test | +25~30 units (107 → 130 units) |
| Feeling the workload | High-risk axes (T1~T4·T9·T10) account for 70% of the total - Parallelization space is limited to low-risk axes (T5~T8·T11) |

**Version: v2.3.0** — Minor upgrade is appropriate with the addition of 3 types of user-visible functions (backwards compatible, no schema changes).
Synchronize `.csproj` `Version/AssemblyVersion/FileVersion` (Stickmark.csproj:12-14) with InnoSetup `AppVersion`;
Before release, README, CHANGELOG, and Wiki are updated with the phrase "External changes are reflected upon restart."
