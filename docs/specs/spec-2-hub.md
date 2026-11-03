# Spec 2 — “Evolved into a Hub”: Unified To-Do View + Quick Switcher (Ctrl+P)

- Target version: v2.2.2 → **v2.3.0** (minor upgrade suggested)
- Creation date: 2026-07-06
- Status: Draft (for review before implementation)
- Commit related code: `1a9dd0d` (main)

---

## 1. Goal / Non-goal

### 1.1 Goal

| # | Features | One line definition |
|---|------|-----------|
| A | **Integrated To Do View** | Collect the `- [ ]` / `- [x]` checkbox lines scattered across all active notes by grouping them by note in the new "To Do" tab of the note list window, and if you check/uncheck them on the spot, the corresponding line in the original note is updated. Provides a toggle to hide completed items. |
| B | **Quick Switcher (Ctrl+P)** | Title + tag fuzzy search pop-up. Filter immediately by typing, immediately jump to any note with ↑↓ Enter (`FocusNoteById`). |

### 1.2 Non-targets (excluding primary scope)

- **Checkbox in RichTextXaml Note**: The checkbox in Rich Note is not text, but an actual `CheckBox` control inside an `InlineUIContainer` (`NoteWindow.xaml.cs:1058-1062`). There is no way to safely perform partial substitution in a XAML serialization string, and I don't want to open a new parse path that bypasses the security filter (`ContainsDangerousXaml`) of `TextExtraction.ToPlainText`. → **RichTextXaml·Html format notes are excluded from to-do collection**. The target is only two formats: `PlainText` / `Markdown`.
- **Code fence recognition**: `- [ ]` text inside a Markdown code fence (``` ``` ```) is also recognized as to-do in the first step (simple parsing on a line-by-line basis). It is only documented due to its known limitations as it is rarely used in practice.
- **Global Ctrl+P hotkey**: Refer to judgment 2.2.1 below — In-app shortcut is sufficient. There is no global registration.
- **Inline text editing / due date / customization of to-do items**: View+Toggle+Jump only. Editing is from the original notes.
- **Todo in trash note**: Natural exclusion since `GetAllAsync()` already excludes it.

### 1.3 Success Criteria

- Check in the To-do tab → Only 1 character in the corresponding line of the original note source `[ ]`↔`[x]` is changed (line breaks, indentation, and remaining text byte units are preserved).
- If the note is open, it is immediately reflected in the open windows (source editor, preview, and WYSIWYG), and the debounce save of the window does not reverse the toggle.
- Complete note jumping without mouse by pressing Ctrl+P → Typing → Enter.

---

## 2. UX definition

### 2.1 Integrated To Do View

#### Entry path
1. **Third tab of the note list window**: Existing `Active | Added `Todo(n)` tab next to the `Trashcan` toggle (`n` = number of incompletes). Reasons for choosing a tab rather than a separate window:
   - `NotesListWindow` already has a hide-reuse lifecycle (`ShowAndReloadAsync`, `Closing→Hide`) and a tab toggle style (`TabToggle`), so reuse cost is minimal.
   - Given the “hub” concept, it is natural for note navigation and to-do checking to be gathered in one window.
2. **Tray Menu**: Add ‘View To Do’ item under ‘All Notes’ → Open the list window in the To Do tab.

#### Screen composition (when the To-Do tab is activated)
- Leave the existing search box as is and operate as a **to-do text filter** (simple contains, ignoring case).
- Tag chip area, selection tool, and export menu are hidden (maintained for active tab only).
- Body: List of groups by note.
  - Group header: Note color accent bar (reuse `AccentBrush` from existing card) + Note title (`NoteList_Untitled` if untitled) + `Incomplete/Total` count. Click on the header → Jump to the relevant note (`FocusNoteById`).
  - Item: `CheckBox`(IsChecked=Complete) + Item text. Click text → jump to note. Completed items are struck through + gray.
- Top right: `Hide completion` ToggleButton. The state is persistent as `AppSettings.TodoHideCompleted` (default false).
- Empty state text: Announces "There are no notes with checkboxes (`- [ ]`)" when there is nothing to do (extends `EmptyMessage`).

#### When to update
- Recollection when entering a tab or window `ShowAndReloadAsync`. No live watching (real-time reflection of note edits) — tab re-entry/window redisplay is sufficient (same policy as the existing list tab).

### 2.2 Quick Switcher (Ctrl+P)

#### 2.2.1 Global vs. in-app — Judgment: **In-app shortcuts are enough**
- Stickmark is a tray-resident type, so entry in a “no window visible” state is already handled by the global `Ctrl+Shift+L` (note list). The switcher is a tool that jumps to another note *when you are already working on a note*, so it assumes that the app window has focus.
- Global `Ctrl+P` conflicts with printing in almost all apps, so NHotkey registration itself is inconvenient, and the probability of `HotkeyAlreadyRegisteredException` failure is high.
- Room for expansion: All entries are converged to one method, `IWindowManager.OpenQuickSwitcher()`, so that if necessary in the future, it is sufficient to add a third ID (`Stickmark.QuickSwitcher`) to `HotkeyService`.

#### Entry path
- All `NoteWindow`: `Ctrl+P` in `InputBindings` (add `KeyBinding` same as existing pattern `NoteWindow.xaml.cs:119-130`). WPF `RichTextBox` has no default Ctrl+P binding, so no crashes.
- `NotesListWindow`: Ctrl+P handling in `PreviewKeyDown` (same place as existing Esc handling).
- Tray menu: `Quick switching… ` item (discoverability).

#### Popup behavior
- Appearance: `WindowStyle=None`, `ShowInTaskbar=false`, `Topmost=true`, top center of active monitor (30% vertical point), width 480. Top Search TextBox + Results ListBox (max display 20, scrollable).
- Keyboard: `↑`/`↓` selection movement (circulation at the end), `Enter` open + close, `Esc` close, other typing is a search word. Mouse click also opens + closes.
- Automatically closes when focus is lost (`Deactivated`).
- Empty search term: Top 20 most recently modified (`ModifiedAt` desc) — Role as "Recent Notes".
- Result row: colored accent bar + title (highlight matching characters) + tag line (format `#tag`, highlight matching). Reuse the existing `HighlightedText.Segments` attached property.

#### Fuzzy matching rules (new `FuzzyMatcher`)
The existing `SearchMatcher` is a token-level substring AND matching, so it is not "fuzzy (non-continuous subsequence)", so it is not touched, and a new utility dedicated to the conversion period is created.
- Target string: `Title + " " + Tagline` composite string.
- Tokenize the search term with a space, **each token must be matched with a subsequence of the target** (AND). Comparison is `OrdinalIgnoreCase` (Korean is case-independent, so it operates as is).
- Score (sorted in descending order):
  - Consecutive matching run bonus (+3 per consecutive letter)
  - Word start position matching bonus (+4; after space, `#`, punctuation mark)
  - Title area matching is more weighted than tag area matching (×2 per matching letter in the title)
  - The earlier the first matching position is, the bonus (−0.1×index)
  - If there is a tie, `ModifiedAt` is sorted by latest.
- Returns: `(bool Matched, double Score, IReadOnlyList<int> Positions)` — Create highlight segment with Positions.

---

## 3. Technical design

### 3.1 New files

| file | Content |
|------|------|
| `Stickmark/Utils/TodoExtraction.cs` | Pure function utility. Extract + Toggle. No UI dependency (easy for testing). |
| `Stickmark/Utils/FuzzyMatcher.cs` | Pure function fuzzy matching. |
| `Stickmark/ViewModels/QuickSwitcherViewModel.cs` | Transition VM (`ObservableObject`, `[ObservableProperty]`/`[RelayCommand]`). |
| `Stickmark/Views/QuickSwitcherWindow.xaml` + `.xaml.cs` | Pop-up window. Code-behind only focuses on key routing and focus (business logic prohibited). |

### 3.2 `TodoExtraction` — Extraction and toggle

```csharp
public static class TodoExtraction
{
    public readonly record struct TodoLine(int LineIndex, bool IsChecked, string Text, string RawLine);

    /// Extract checkbox lines from PlainText/Markdown source (Content).
    public static IReadOnlyList<TodoLine> ExtractTasks(string source);

    /// If the lineIndex line matches expectedRaw, [ ]↔[x] replaces only one mark character.
    /// Returns a new source. null if there is a mismatch (the note has been edited in the meantime) — recollected by the calling party.
    public static string? ToggleLine(string source, int lineIndex, string expectedRaw, bool check);
}
```

- Line pattern: `^(\s*)([-*+])\s+\[( |x|
- **Extracted source is `Note.Content`** (Content=main text for PlainText, Content=raw source for Markdown). Why you shouldn't use the `PlainText` projection: Markdig's ToPlainText removes the bullets, so you can't get the original line back when you toggle it.
- **Toggle replaces 1 character of absolute offset**: Finds the starting offset of the lineIndex line by scanning `\n` (only removing the trailing `\r` when comparing lines), finds the position of the mark character (` `/`x`) within the line by matching a regular expression, and replaces only one letter of the corresponding absolute offset of `source`. Since split/join is not used, **CRLF/LF mixing and trailing newlines are preserved**.
- **Optimistic concurrency**: The caller (VM) holds `RawLine` at the time of extraction and passes it to `ToggleLine`. If the note has been edited in the meantime and the lines have changed, it is restored by null → Recollect all to-do tabs (automatically, the check appears to have been returned to the user).

### 3.3 Persistent path for toggles — Synchronization strategy with open windows

Core risk: `NoteViewModel` `SaveContentAsync` copies its own memory with a 500ms debounce (`NoteViewModel.cs:19,114,175-201`). **If a note is open and the hub writes directly to the DB, the next flush in the open window overwrites the toggle.** Therefore, the path is dualized. `WindowManager.ReloadAsync()` (rebuild entire window) is not used as it is overkill as it causes all windows to blink at once — **Fine-grained update** adopted.

#### (a) Live path — when the notes window is open
Add 2 members to `IWindowManager`:

```csharp
/// If there is an open window, returns the latest content (including those not yet saved by debounce) of that window's VM.
bool TryGetLiveNoteContent(Guid id, out string content, out NoteContentFormat format);

/// Inject new content into the editor/VM of the open window. false if there is no window.
Task<bool> TryUpdateLiveNoteContentAsync(Guid id, string newContent);
```

- Reason why reading must be done in a live VM: DB is the default during the debounce window (≤500ms).
- `TryUpdateLiveNoteContentAsync` calls the new public method `ApplyExternalContentAsync(string text)` of `NoteWindow`. This method extracts and reuses the existing linked file external change reflection path (`NoteWindow.xaml.cs:757-760`: `ReplaceEditorText` + `vm.UpdateContent` + preview re-render), and additionally, in **WYSIWYG mode (`_wysiwygOn`), `MarkdownWysiwyg.SetMarkdownAsync(text)`** Update the copy of the CodeMirror page (`Utils/MarkdownWysiwyg.cs:85`). Block echo loop with `_suppressEditorSync` guard.
- Persistence is automatic: `vm.UpdateContent` → `Schedule()` → Debounce `SaveAsync` → DB + (linked notebook) `SyncToLinkedFileAsync` records even the original file. No separate save code required.
- Prerequisite: This path is only called in PlainText/Markdown notes (since RichTextXaml does not collect itself).

#### (b) Closed path — when there is no window
1. `INoteRepository.GetByIdAsync(id)` → `ToggleLine` → New Content.
2. Update `note.Content` + recalculate `PlainText`/`Title`/`Tags` (`TextExtraction.ToPlainText/DeriveTitle/ExtractTags` — same as existing pattern of `WindowManager.OpenFileAsync`).
3. `SaveContentAsync(note)` — Use the existing save API with trash can state preservation semantics (the reason for prohibiting `UpsertAsync` is the same as the `INoteRepository.cs:27-31` comment).
4. **If it is a linked note (`LinkedFilePath != null`), also record the original file with `LinkedFile.WriteAsync(path, newContent)`** and update `note.LinkedFileSyncedUtc` with the returned WriteUtc and save it. This is because if you only edit the DB in the note where the file is the original, it will be overwritten with the file contents the next time you view it. ⚠ When implementing, check whether `NoteRepository.SaveContentAsync` includes `LinkedFileSyncedUtc` in the storage target (if not included, modify to include it).

#### Orchestration (`ToggleTodoAsync` in NotesListViewModel)
```
Click to check item
 → source = TryGetLiveNoteContent(id) ?? (await repo.GetByIdAsync(id)).Content
 → newContent = TodoExtraction.ToggleLine(source, item.LineIndex, item.RawLine, newChecked)
 → If null: Recollect the to-do tab (ReloadAsync) and exit (stale)
 → Live side: await TryUpdateLiveNoteContentAsync(id, newContent)
 → Otherwise: closed path (b)
 → Local update of item.RawLine/IsChecked on success (only that item without re-collecting the entire item)
```

### 3.4 `NotesListViewModel` / `NotesListWindow` changes

- **View mode refactor**: `ShowTrash` bool There used to be 2 tabs per tab, but now there are 3, so switch to `enum NoteListViewMode { Active, Trash, Todos }` + `[ObservableProperty] NoteListViewMode viewMode`. To minimize breakage of existing XAML bindings, `IsTrashView`/`ShowTrash` are kept as computed properties (derived: `ViewMode == Trash`), and `IsTodoView` is added. The three tab toggles set the ViewMode with a code-behind click handler (existing `ActiveTab_OnClick` pattern).
- Additional states:
  - `ObservableCollection<TodoGroup> TodoGroups` — `TodoGroup(Guid NoteId, string Title, Brush AccentBrush, ObservableCollection<TodoItemViewModel> Items)`
  - `TodoItemViewModel`: `NoteId, LineIndex, RawLine, Text, [ObservableProperty] bool IsChecked` (`IsVisible` for + display is calculated by combining with HideCompleted and search term)
  - `[ObservableProperty] bool hideCompletedTodos` — Save `ISettingsService` when changed + refilter list
  - `OpenTodoCount` (tab badge)
- Collection: `TodoExtraction.ExtractTasks(note.Content)` only for `Format is PlainText or Markdown` notes targeting `_activeRaw` already loaded by `ReloadAsync`. No separate DB inquiry.
- Commands: `ToggleTodoCommand(TodoItemViewModel)`, `OpenTodoNoteCommand(Guid)` (=`FocusNoteById`), `ToggleHideCompletedCommand`.
- XAML: To-do tab content is added to the Grid in place of the existing ListBox by nesting `ItemsControl` (group) > `ItemsControl` (item) and switching Visibility to `ViewMode`. Visibility conditions of search box, tag chip, and selection tool are updated with ViewMode-based converter.

### 3.5 Transition configuration

- `QuickSwitcherViewModel` (DI `AddTransient`, `App.xaml.cs:121` pattern): Loads `INoteRepository.GetAllAsync()` snapshot every time it is opened (fast enough as it is LiteDB local, for dozens of active notes). Change `QueryText` → Recalculate `FuzzyMatcher` → Update `Results` + `SelectedIndex=0`.
- `QuickSwitcherWindow`: Code-behind is ↑↓/Enter/Esc routing, `Deactivated → Close`, TextBox focus only when opened. Open/Jump is a VM command (`FocusNoteById` is injected into `IWindowManager`).
- `WindowManager.OpenQuickSwitcher()`: Unlike the `_notesListWindow` cache pattern, **creates a new one every time** (popups are lightweight, and only stale snapshot/focus problems occur when hiding cache). Added `void OpenQuickSwitcher();` to `IWindowManager`.
- Entry wiring: `NoteWindow` InputBindings(Ctrl+P), `NotesListWindow.OnPreviewKeyDown`(Ctrl+P), `TrayService.BuildContextMenu` (line after `Tray_AllNotes`). `NoteWindow` is currently a structure that receives 5 callback delegates as a constructor (`WindowManager.BuildWindow`), so add 1 `openQuickSwitcher` callback in the same way.

### 3.6 i18n — New resource strings

Add all 3 places: `Strings.cs` accessor + `Strings.resx`(en) + `Strings.resx`(ko) (same as existing pattern):

```
NoteList_TabTodos To-do / To-dos
Todo_HideCompleted Hide completed / Hide completed
Todo_EmptyMessage No notes contain checkboxes (- [ ])
Todo_GroupCountFormat {0}/{1} done / {0}/{1} done
Tray_TodoView To-do view / To-do view
Tray_QuickSwitcher Quick switch… / Quick switch…
QuickSwitcher_Placeholder Search for note name or #tag… / Search note title or #tag…
QuickSwitcher_NoResults No matching notes
```

### 3.7 Settings

- Added `AppSettings.TodoHideCompleted` (bool, default false) — no need to migrate additive settings files.

---

## 4. Work decomposition + risk classification

| # | work | file | Risk | Evidence |
|---|------|------|--------|------|
| 1 | `TodoExtraction` utility (extraction+offset toggle) + unit test | `Utils/TodoExtraction.cs`, `Tests.cs` | **High Risk (Opus Direct)** | Note: the only logic that transforms the original text. Offset calculation mistake = data corruption. CRLF preservation and stale verification are the key to correctness. |
| 2 | Toggle orchestration (live/closed/linked 3 paths) + `NotesListViewModel` ViewMode refactor | `ViewModels/NotesListViewModel.cs` | **High Risk (Opus Direct)** | The entire concurrency design of this specification, including contention with debounce stores and preventing linked file overwriting, is gathered here. The existing tab state machine refactor also has a risk of regression. |
| 3 | `NoteWindow.ApplyExternalContentAsync` + `IWindowManager`/`WindowManager` live read/update members | `Views/NoteWindow.xaml.cs`, `Services/IWindowManager.cs`, `Services/WindowManager.cs` | **High Risk (Opus Direct)** | Editor tri-state (source/preview/WYSIWYG) synchronization + blocking `_suppressEditorSync` echo loop. Includes WebView2 asynchronous routes. |
| 4 | Reflection of `LinkedFile.WriteAsync` in closed linked notes + Check/reinforce whether `LinkedFileSyncedUtc` is saved in `SaveContentAsync` | `Services/NoteRepository.cs` et al | **High Risk (Opus Direct)** | Path to write original .md file — user file corruption in case of mistake. Requires integration with existing “prevent overwriting of empty content” defense lines. |
| 5 | `FuzzyMatcher` utility + unit test | `Utils/FuzzyMatcher.cs`, `Tests.cs` | Low Risk (Sonnet Delegation) | Pure function, no UI or storage involved. Fully verifiable through testing. |
| 6 | `QuickSwitcherWindow` XAML/code-behind + `QuickSwitcherViewModel` + DI registration | `Views/QuickSwitcher*`, `App.xaml.cs` | Low Risk (Sonnet Delegation) | Standard MVVM list popup. Reuse existing `HighlightedText`·`FocusNoteById`. |
| 7 | To Do Tab XAML (Group ItemsControl, Toggle Tab, Hide Done) | `Views/NotesListWindow.xaml`(+`.xaml.cs`) | Low Risk (Sonnet Delegation) | For display only. The logic is only #2 VM command binding. |
| 8 | Entry wiring: Ctrl+P 2 InputBindings + 2 tray menu items + `OpenQuickSwitcher` | `NoteWindow.xaml.cs`, `NotesListWindow.xaml.cs`, `TrayService.cs`, `WindowManager.cs` | Low Risk (Sonnet Delegation) | Level of replication of existing patterns. However, changing the `NoteWindow` constructor signature is done after #3 merge. |
| 9 | 3 types of i18n resources + `AppSettings.TodoHideCompleted` | `Resources/*`, `Models/AppSettings.cs` | Low Risk (Sonnet Delegation) | Mechanical addition. |
| 10 | Document: README·CHANGELOG·Version 2.3.0 upgraded, screenshot updated | `README.md`, `CHANGELOG.md`, `*.csproj`, `*.iss` | Low Risk (Sonnet Delegation) | Mandatory pre-release prerequisite (Global Guidelines §7). Synchronize `.csproj` and `.iss` `AppVersion`. |

**Dependent order**: #1 → #2·#3 (parallel possible) → #4 → #7·#8 / #5 → #6 → #8 / #9·#10 is the last step.

---

## 5. Test plan (existing 107 + about 25~30 added)

It follows the xUnit style of the existing `Stickmark.Tests/Tests.cs` (single file, area per class).

### 5.1 `TodoExtractionTests`
- **Extraction**: `-`/`*`/`+` bullets, maintain indentation (nesting), recognize `[x]`/`[X]` completion, line without text after `[ ]`, regular list (`- item`), not checkbox, `[ ]` in the middle of body not recognized, empty source → empty result.
- **Toggle round trip**: toggle→untoggle result is **byte-by-byte-byte-same** as the original. Preserve newlines in LF-only, CRLF-only, and mixed sources. The diff before and after the toggle is exactly 1 character.
- **stale detection**: `expectedRaw` mismatch → null. lineIndex out of range → null. Even if there are multiple lines of the same text, only the correct line is toggled using lineIndex.
- **Uncapitalize[X]**: If you cancel `[X]`, it becomes `[ ]`.

### 5.2 `FuzzyMatcherTests`
- Subsequence matching established/failed, case ignored, Korean title matching.
- Ranking: Continuous matching > scattered matching, word start matching > middle matching, title matching > tag matching.
- Blank multi-token AND, empty query → precedent match (Score 0), whether Positions is a valid index that can be highlighted.

### 5.3 Integration scenario — VM level (in-memory LiteDB, traditional `[Collection("LiteDb")]` pattern)
- Closed path: Toggle saved Markdown notes → Change 1 character of Content when rechecking `GetByIdAsync` + Check PlainText/Tags recalculation.
- Whether RichTextXaml/Html notes are excluded from collection.
- Combination of Hide Complete filter + Search term filter.

### 5.4 Manual verification checklist (requires UI thread, no automation)
- Toggle open notes (source mode) → Reflected immediately in editor, maintained even if app restarts after 500ms (no debounce save contention).
- Toggle WYSIWYG mode window → Reflect CodeMirror.
- Linked .md Note: Toggle closed state → change file content, file will not be covered with empty content.
- Ctrl+P: Opens in NoteWindow/list window, ↑↓ Enter Esc, closes out of focus.
- ⚠ Live DB Caution: Verification is conducted using an isolated demo DB (replacing actual DB/settings is prohibited).

---

## 6. Expected size and release version

| Item | estimate |
|------|------|
| New File | 5 (Utility 2, VM 1, View 2) |
| edit file | About 10 (VM·View·Services·Resources·Settings·Document) |
| Net increase in LOC | Approximately +1,000 to 1,300 (including testing) |
| New Test | About 25-30 (total ≈135) |
| Implementation effort | High risk 4 tasks are critical path. 1~2 working days after confirming specifications |

**Version suggestion: v2.3.0** — 2 new features (backwards compatible, no DB schema changes, settings are additive). Before release, CHANGELOG·README (including To Do tab·Ctrl+P screenshot)·`stickmark_installer.iss` and `AppVersion` must be synchronized. When verifying single file distribution, check that there are no new assets (all code and resources are embedded) — Smoke test with Release published build to prevent recurrence of WYSIWYG asset issue (`1a9dd0d`).
