# Spec 3 — "First Impressions" Combination: App Dark Theme · First Run Tour · What's New

- Target version: v2.2.2 → **v2.3.0 suggested**
- Creation date: 2026-07-06
- Status: Draft (pre-implementation specification). No code modifications — this document was written after checking the actual code (paths/lines quoted below).

---

## 0. Summary of current code survey (premise of spec)

| Item | Verification result |
|---|---|
| WPF-UI 3.0.5 | Reference in `Stickmark/Stickmark.csproj:35`. In `Stickmark/App.xaml:9-10`, `<ui:ThemesDictionary Theme="Light" />` + `<ui:ControlsDictionary />` is **already merged and in use**. However, the theme is hard-coded as Light, and the `ui:` namespace control·`Wpf.Ui` C# API is not used anywhere (all 3 lines in App.xaml as a result of the entire grep). In other words, the implicit style of standard controls (Button/TextBox/CheckBox/ComboBox/ContextMenu, etc.) already uses WPF-UI, so **If you swap ThemesDictionary to Dark, the controls will automatically become dark.** The only remaining problem is the hard-coded hex embedded in the window/text/card. |
| Note Palette | `Stickmark/Models/NoteTheme.cs` — 6 colors for `NotePalette` (`Yellow/Pink/Blue/Green/Purple/Gray`). **Gray is already a dark tone** (background `#373A40`, ​​foreground `#ECEEF2`). There is no separate “dark palette variant” item. |
| Note Window Chrome | `NoteWindow.xaml` does not use app chrome colors, but uses `BackgroundBrush/HeaderBrush/… of `NoteViewModel`. Binding to `(= `NotePalette.For(Color)`, `ViewModels/NoteViewModel.cs:83-88`) — **independent of app theme**. Exception: Color Picker Popup Background `#FFFFFF` (`NoteWindow.xaml:166`). |
| Hardcoded color | `NotesListWindow.xaml`(`#FAFAFA`, `White`, `#EFEFEF`, `#1976D2`, `#666/#888/#999`, `#E5E5E5`, `#EAF3FF`, `#FBE9E7`, `#E3F2FD`, `#B85C5C`), `SettingsWindow.xaml`(`#FAFAFA`, `#888`, `#C62828`). Full list in §3.2. |
| Setting pattern | `Models/AppSettings.cs` — Flat POCO + `System.Text.Json`. `Language`("system"/"en"/"ko")·`StorageMode` string option precedent. `SettingsService.Load()` (`Services/SettingsService.cs:28-41`) defaults to file absence — **file presence not exposed** (required for first run detection, §3.5). The UI is `LanguageOption` record + ComboBox pattern of `SettingsViewModel` (`ViewModels/SettingsViewModel.cs:19, 52-57`). |
| Start flow | `App.xaml.cs:128-175` — `ApplyCulture` → Recycle Bin purge → `WindowManager.RestoreAllAsync()` → Tray init → InstanceChannel → Open file argument → Hotkey → Check for updates. `RestoreAllAsync`(`Services/WindowManager.cs:35-50`) **automatically creates 1 blank note if there are 0 notes**. Empty placeholder notes are organized by `DiscardEmptyPlaceholders` (`WindowManager.cs:264-271`). |
| Get version | `Services/UpdateService.cs:198-199` `CurrentVersion()` (Assembly version, private) / `Compare()` (internal, Major.Minor.Build comparison, test exists). csproj `<Version>2.2.2`. |
| WebView2 Surface | ① Preview `Preview` (script OFF) — `Utils/HtmlRenderer.Render(source, format, NoteTheme)` wrapped in note theme color (`HtmlRenderer.cs:30-57`). Since the overlay is fixed to `rgba(0,0,0,…)`, the contrast between code blocks and table boundaries is low in gray notes. ② WYSIWYG `WysiwygEditor` — `Utils/MarkdownWysiwyg.cs` navigates to `editor.html?lang=xx` (around line 89, query parameter precedent). The `Assets/mdeditor/editor.html` toolbar is fixed to light (`rgba(255,255,255,.55)`, text `#2a2a2a`), and the CM6 theme of `tools/mdeditor/editor.js` is `color: inherit`/`currentColor`, so the text color of the body can be controlled with host CSS. Editor assets are embedded as an EmbeddedResource and then extracted at runtime (single file distribution supported, csproj:23-30 annotation). |
| i18n | `Resources/Strings.resx` + `Strings.resx`, manual accessor `Resources/Strings.cs` (`Get(nameof(...))` pattern). |
| CHANGELOG | Root `CHANGELOG.md`, Keep-a-Changelog format, `## [X.Y.Z] - Date` header. Focused on Korean. **Not included in build artifacts** (single file exe — requires embedding for use in What's New). |
| test | `Stickmark.Tests/Tests.cs` single file, 72 `[Fact]`/`[Theory]` (107 cases). `InternalsVisibleTo` set (csproj:20). |

---

## 1. Goal / Non-goal

### Goal
1. **App Chrome Dark Theme** — “App Chrome” supports 3 options: Follow system/Light/Dark, including note list, settings, (new) What’s New window, tray/note context menu, etc. Apply immediately after saving (no restart required).
2. **First Run Tour** — Onboarding with the product itself (sticky notes) by creating **3 sample notes explaining key features** instead of 1 blank note on first run. Skip by deleting, re-create in settings.
3. **What's New** — Changes in the new version are displayed once on the first run after the update (embed/recycle CHANGELOG.md). Not displayed on first installation.
4. Base shared by all three features: `AppSettings` extension (`AppTheme`/`LastSeenVersion`/`OnboardingCompleted`) + startup flow gate.

### Non-target
- **No change to the note's own color palette.** The note window follows its own palette (6 colors) as it does now. Dark palette "variant" review results in §3.4 — Decided not to introduce **in this release** (with rationale), leaving only the design.
- Export HTML/PDF/Print Document (`HtmlRenderer.RenderDocument`) is for printing/sharing, so **always remain lite**.
- Dark variant of Win32 `MessageBox`·file/folder dialog·system tray icon (OS specific, documented only due to known limitations).
- Interactive overlay type tour (Coachmark) and animation — Not suitable for multi-window tray apps, replaced with sample note method.
- Enhanced update notifications and multilingualization of automatic release notes.

---

## 2. UX definition

### 2.1 Theme settings
- Added **"Theme"** ComboBox to the settings window (just below the language combo, same layout): `Follow system settings` (default) / `Light` / `Dark`.
- **Application time: Immediately after clicking [Save].** No restart required (leave the existing `Settings_RestartNote` phrase as it is only applicable to language/repository). All open app Chrome windows are refreshed immediately (DynamicResource).
- When selecting ‘Follow system settings’, OS light/dark switching (personal setting changes) during execution is also reflected in real time.
- The note window **maintains the color of your notes** in any theme (users who want dark notes can use the existing Gray color — a line is mentioned in the guidance note).
- Apply DWM dark title bar so that the standard title bar does not remain white in dark (§3.1.4).

### 2.2 First Run Tour (Sample Notes)
- **Exposure conditions (all met)**: ① First run when `settings.json` does not exist, ② There are 0 notes in the repository. → Instead of the existing “automatically create 1 blank note”, 3 sample notes are seeded and displayed on the screen as is (cascaded arrangement).
- If an existing user upgrades (setting file exists), **Never activates.** Even an existing user who deletes all notes maintains the existing operation (1 blank note).
- The sample note is **an ordinary Markdown note**: tags and checkboxes actually work, appear in the list window, and can be deleted (=skipped) like any other note. At the top of the first page, it says “delete when you’re done reading.”
- **Review**: Link-type button at the bottom of the settings window "Recreate Start Guide Note" — Click to create 3 new samples (added regardless of existing notes). Independent operation from the save button.
- Sample note configuration (content localized to en as resource, `{{date}}` substitution not required):

| # | color | Title (H1) | Content summary |
|---|---|---|---|
| 1 | Yellow | Welcome to Stickmark | Drag header to move, left color dot=change color, Pin=always on top, x=hide (saved), left click on tray icon=show/hide all. “After reading these three guidance notes, delete them with Del.” |
| 2 | Blue | Markdown and Edit Mode | Toggle M for Markdown; double-click rendered text for WYSIWYG editing; Eye toggles preview. Checkbox `- [ ]` example, `#tag` example (indexed by actual tag and appearing in list filter). |
| 3 | Green | All Notes/Vaults/Backup | `Ctrl+Shift+L` list pane (search·tag filter·export), `Ctrl+Shift+N` new note, tray menu (backup·vault mode `.md` folder storage), one line toggle dark theme in settings. |

### 2.3 What's New
- **Exposure rules (once when all are met)**: ① Not the first installation run (mutually exclusive with §2.2 — if it is the first run, only `LastSeenVersion` is recorded as current and the display is omitted), ② There is no saved `LastSeenVersion` or it is lower than the current version (`UpdateService.Compare` reused, if downgraded, only the record is updated without display), ③ This run is **file argument execution. Not** (does not interrupt the moment it is turned on by double-clicking `.md` — in this case it is postponed to the next normal run).
- Display time: `RestoreAllAsync` · After tray initialization is completed (separate from startup failure path, try/catch + log so that failure does not affect app startup).
- **Save as `LastSeenVersion = current version` as soon as the window opens** — Does not re-display if the user turns it off without reading (priority to avoid annoyance).
- Content: Extract **only the current version section** from the embedded CHANGELOG.md and render it (§3.6). If the section is not found, the window itself does not appear (silent skip + log). One "close" button at the bottom of the window, `ShowInTaskbar=false`, fixed size 460×540, follows app theme.
- Language: Since CHANGELOG is mainly Korean, Korean text may be visible to English UI users — Documented with **acceptable limits** (localize only window titles and buttons).

---

## 3. Technical design

### 3.1 Theme Infrastructure (Key Decisions)

#### 3.1.1 Decision: **Leverage WPF-UI** instead of own theme system
Compare:

| | A. Utilization of WPF-UI `ApplicationThemeManager` (adoption) | B. Own ResourceDictionary theme system |
|---|---|---|
| Control Style | `ControlsDictionary` is already restyled standard controls → Just swap `ThemesDictionary` to darken all Button/TextBox/CheckBox/ComboBox/ContextMenu/ScrollBar/Menu | Write all the control dark styles yourself (hundreds of lines) or it will conflict with WPF-UI |
| Additional dependencies | None (see 3.0.5 · Merge completed) | None, but in fact, removal of WPF-UI must be done first to ensure consistency |
| Maintenance | Following the WinUI token key (documented public resource) | Key system and color values ​​are all at your own expense |
| Risk | Global visual regression when swapping themes (requires verification of all windows) | Same + larger amount of writing |

→ **A adopted.** However, app-specific colors (brand accent `#1976D2`, trash can red, etc.) that are insufficient with only WPF-UI tokens are supplemented with a pair of thin dictionaries (§3.1.3).

#### 3.1.2 `ThemeService` (new `Services/ThemeService.cs` + `IThemeService.cs`)
```csharp
public enum AppThemeKind { Light, Dark }

public interface IThemeService
{
    AppThemeKind Effective { get; } // Actual theme after resolving system tracking
    void Apply(string? setting);             // "system" | "light" | "dark"
    event EventHandler<AppThemeKind>? Changed;
}
```
- `Apply` operation: ① If `"system"`, resolved by reading registry `HKCU\...\Themes\Personalize\AppsUseLightTheme`, ② `Wpf.Ui.Appearance.ApplicationThemeManager.Apply(ApplicationTheme.Light|Dark, WindowBackdropType.None, updateAccent: false)` — Merged in App.xaml Runtime swap of `ThemesDictionary`, ③ swap its own auxiliary dictionary (`AppColors.Light/Dark.xaml`), ④ reapply DWM dark titlebar to all open `Windows` (§3.1.4), ⑤ utterance of `Changed`.
- When `"system"`, OS change is reflected in real time with `Microsoft.Win32.SystemEvents.UserPreferenceChanged` (General category) subscription. Unsubscribe when it changes to `"light"/"dark"`. (`SystemEvents` are static events — released on app exit. Package `Microsoft.Win32.Registry` already referenced: csproj:46)
- **Caution (implementation-time verification item)**: Pre-verify with a demo that the `ApplicationThemeManager.Apply` overload signature of WPF-UI 3.0.5 and the standard control implicit style actually refer to the theme brush as a DynamicResource (first step in high-risk task #1).
- DI: `services.AddSingleton<ITemeService, ThemeService>()` (block `App.xaml.cs:92-123`). When called: Immediately after `LocalizationService.ApplyCulture` in `App.OnStartup` and before `RestoreAllAsync` (near `App.xaml.cs:130`) — Applied before any window opens.

#### 3.1.3 Resource layer
- **First layer — WPF-UI tokens (used as is, DynamicResource)**: `ApplicationBackgroundBrush` (window background), `TextFillColorPrimaryBrush`/`SecondaryBrush`/`TertiaryBrush` (body/secondary/faint text), `CardBackgroundFillColorDefaultBrush` + `CardStrokeColorDefaultBrush` (note card), `ControlFillColorSecondaryBrush` (chip/hover). In the first stage of implementation, the presence of Silky is checked with the Wpf.Ui source (Themes/Light.xaml·Dark.xaml), and if not found, it is lowered to the second floor.
- **2nd layer — self-auxiliary dictionaries (new `Themes/AppColors.Light.xaml`·`Themes/AppColors.Dark.xaml`)**: Added to App.xaml MergedDictionaries and swapped by ThemeService. Keys (all `SolidColorBrush`):

| key | Light (keep current value) | Dark (suggestion) | Use |
|---|---|---|---|
| `App.AccentBrush` | `#1976D2` | `#4FA3E3` | Tab selection·Tag text·Selection border |
| `App.AccentSubtleBrush` | `#EAF3FF` | `#1E3A5F` | Select card background |
| `App.InfoSubtleBrush` | `#E3F2FD` | `#1C3550` | Restore button background |
| `App.DangerSubtleBrush` | `#FBE9E7` | `#4A2320` | Empty trash/permanent delete button background |
| `App.DangerTextBrush` | `#C62828` | `#EF9A9A` | Verification error/deletion date text |
| `App.ChipBrush` | `#EFEFEF` | `#3A3D42` | Tag chip·tab hover background |
| `App.PopupBackgroundBrush` | `#FFFFFF` | `#2B2D31` | NoteWindow Color Picker Popup |

- The dark color value can be fine-tuned after checking the contrast (WCAG AA, text 4.5:1) with the screenshot harness (§5) during implementation — The value of the specification is the starting point.

#### 3.1.4 Dark title bar (new `Utils/WindowThemeHelper.cs`)
- `DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE = 20, ref BOOL, 4)` P/Invoke. Failure is ignored (best-effort, no need for fallback before Win10 1809 — if 20 is not supported, only the light title bar remains).
- Application point: Once in `SourceInitialized` of each Chrome window (`NotesListWindow`/`SettingsWindow`/`WhatsNewWindow`) + reapplying `Application.Current.Windows` traversal when `ITemeService.Changed`. `NoteWindow` is not a target because `WindowStyle=None`.

### 3.2 Hardcoded color migration list (mechanical replacement — low-risk delegation target)

`NotesListWindow.xaml`:

| position (line) | Current | substitution |
|---|---|---|
| 12 `Window Background` | `#FAFAFA` | `{DynamicResource ApplicationBackgroundBrush}` |
| 37 TabToggle hover | `#EFEFEF` | `{DynamicResource App.ChipBrush}` |
| 40-41 TabToggle Select | `#1976D2`/`White` | `{DynamicResource App.AccentBrush}` / Maintain (White) |
| 69, 103 count text | `#666` | `{DynamicResource TextFillColorSecondaryBrush}` |
| 151 Recycle Bin Count | `#999` | `{DynamicResource TextFillColorTertiaryBrush}` |
| 157 Empty Trash Button | `#FBE9E7` | `{DynamicResource App.DangerSubtleBrush}` |
| 189 tags chip background | `#EFEFEF` | `{DynamicResource App.ChipBrush}` |
| 196 chip count | `#888` | `{DynamicResource TextFillColorTertiaryBrush}` |
| 224-225 card background/border | `White`/`#E5E5E5` | `{DynamicResource CardBackgroundFillColorDefaultBrush}` / `{DynamicResource CardStrokeColorDefaultBrush}` |
| 229-230 Selection Card | `#EAF3FF`/`#1976D2` | `{DynamicResource App.AccentSubtleBrush}` / `{DynamicResource App.AccentBrush}` |
| 268 Text excerpt | `#666` | `{DynamicResource TextFillColorSecondaryBrush}` |
| 276 tag lines | `#1976D2` | `{DynamicResource App.AccentBrush}` |
| 287 Modification date | `#999` | `{DynamicResource TextFillColorTertiaryBrush}` |
| 293 Deletion Date | `#B85C5C` | `{DynamicResource App.DangerTextBrush}` |
| 311 Restore Button | `#E3F2FD` | `{DynamicResource App.InfoSubtleBrush}` |
| 320 Permanent delete button | `#FBE9E7` | `{DynamicResource App.DangerSubtleBrush}` |
| 339, 353, 358 empty list/footer | `#888` | `{DynamicResource TextFillColorTertiaryBrush}` |

`SettingsWindow.xaml`:

| position (line) | Current | substitution |
|---|---|---|
| 9 `Window Background` | `#FAFAFA` | `{DynamicResource ApplicationBackgroundBrush}` |
| 94, 155 Hint Text | `#888` | `{DynamicResource TextFillColorTertiaryBrush}` |
| 164 Verification error | `#C62828` | `{DynamicResource App.DangerTextBrush}` |

`NoteWindow.xaml` (Note Chrome maintains the Notes palette — **not** subject to app theme, except below):

| position (line) | Current | substitution |
|---|---|---|
| 166-168 Color Picker Popup | `#FFFFFF`/`#33000000` | `{DynamicResource App.PopupBackgroundBrush}` / `{DynamicResource CardStrokeColorDefaultBrush}` |
| Other `#11000000`~`#33000000` overlays | Maintenance | Relative overlay on note color — no changes (Gray note contrast issues follow §3.4) |

Search for `HighlightedText` (`Utils/HighlightedText.cs`) Check if the highlight color is in the code (check at implementation time — no change if Foreground inheritance).

### 3.3 WebView2 surface dark response

Principle: **Preview·WYSIWYG follows the "note theme", not the app theme** (since it is drawn over the note background). Therefore, the exact meaning of “dark response” is *ensuring readability in dark notes (currently Gray, future dark variant)*.

1. **`HtmlRenderer.Wrap`(`Utils/HtmlRenderer.cs:30-57`)** — Preserve signature (`NoteTheme` already received). Internally, we add a dark judgment with background luminance:
   ```csharp
   // Relative luminance (approx. sRGB) < 0.4 → considered dark note
   private static bool IsDark(Color bg) => (0.2126*bg.R + 0.7152*bg.G + 0.0722*bg.B) / 255.0 < 0.4;
   ```
   When dark is determined: `code`/`pre` background `rgba(0,0,0,.06)` → `rgba(255,255,255,.08)`, table border·`hr` `rgba(0,0,0,…)` → `rgba(255,255,255,…)`, link `#1565C0` → `#6FB1E8`. When judging light, the current value remains the same → **For the existing 5-color light note, the render result is unchanged in bytes** (no regression).
2. **`editor.html`** — Add `&theme=dark|light` to the navigation URL of `MarkdownWysiwyg.LoadAsync` (currently `?lang=xx`, `Utils/MarkdownWysiwyg.cs:80`) (determination is the same luminance function as above — `NoteViewModel.Theme.Background` passed in `NoteWindow.xaml.cs`). Added `:root[data-theme="dark"]` variant to inline CSS in `editor.html`: toolbar background `rgba(255,255,255,.55)` → `rgba(0,0,0,.35)`, button text `#2a2a2a` → `#e6e6e6`, separator/hover inversion. The boot script (`tools/mdeditor/editor.js`) reads `theme` from the querystring and specifies `document.documentElement.dataset.theme` (next to the existing `lang` parsing code). CM6 text has `color: inherit` (editor.js:80), so `body{color:… Just give }` for each theme. Link color (editor.js:20-21 `#2563eb`) diverges highlight style from dark to `#7cb0f5`. **Bundle rebuild required** (`tools/mdeditor/build.mjs`) — Rebuild the lockfile as is to avoid being caught in CI's bundle-source consistency verification (CHANGELOG 2.2.1 item).
3. Auxiliary: Set `CoreWebView2.Profile.PreferredColorScheme` to match the note theme (dark the browser's basic UI, including scrollbars, best-effort — try/catch).
4. `RenderDocument` (export) is unchanged (non-target).

### 3.4 Notes Dark Palette Variants — Review Results: **Except for this release**

- Evidence: ① Because the identity of sticky notes is “colored paper,” users have different preferences for notes with dark backgrounds — Gray already covers the demand for dark notes, ② `NoteViewModel.Theme => NotePalette.For(Color)`(`NoteViewModel.cs:83`) is a static query, so if you enter app theme dependency, you get a matrix of 6 note colors × 2 app themes. `HtmlRenderer`/WYSIWYG/Regression verification of the surface is required before exporting — too risky to include with the other two functions in this combination. ③ Only the `NoteColor` enum is stored in the vault/DB, so there is no data migration at all even if added later (easy to add structure).
- Follow-up design note (next): `NotePalette.For(NoteColor, AppThemeKind)` overload + dark transformation table (e.g. Yellow → background `#4A4322`/foreground `#F2ECCB`), `NoteViewModel` re-notifies 5 types of brushes after subscribing to `ITemeService.Changed`. Thanks to the luminance judgment in §3.3, the render layer is automatically matched without modification.

### 3.5 First Run Tour

#### Settings/Detection
- Add to `AppSettings` (pattern: `Models/AppSettings.cs`):
  ```csharp
  /// App Chrome theme: "system" (depending on OS), "light", "dark".
  public string AppTheme { get; set; } = "system";
  /// The version of the app that last marked (or omitted) What's New. null=no record.
  public string? LastSeenVersion { get; set; }
  /// Whether the first run sample notes have been seeded (avoiding reseeding).
  public bool OnboardingCompleted { get; set; }
  ```
  If there are no keys in the existing settings.json, System.Text.Json will fill them with default values — no migration required.
- in `ISettingsService`/`SettingsService` `bool LoadedFromFile { get; }` Added — Preserve `File.Exists(_path)` result in `Load()` (`SettingsService.cs:32`). **First installation verdict = `!LoadedFromFile`** (upgrade users will not trigger tours because the file exists even if `OnboardingCompleted=false` — this gate is key).

#### `OnboardingService` (new `Services/OnboardingService.cs` + interface)
```csharp
public interface IOnboardingService
{
    /// Seed 3 sample notes into the repository. If force=false, only for first installation + 0 notes.
    Task<bool> SeedAsync(bool force = false);
}
```
- Dependencies: `INoteRepository`, `ISettingsService`. Search for content in `Strings` (Key: `Onboarding_Note1Title/Body` … `Note3…` — The body is a full Markdown text string, `NoteContentFormat.Markdown`).
- Note creation follows the field filling method of `WindowManager.CreateAndShowNew(NoteTemplate)` (`WindowManager.cs:77-103`): `PlainText = TextExtraction.ToPlainText(...)`, `Title = DeriveTitle(...)`, `Tags = ExtractTags(...)` — Tags and search indexing are similar to real notes. The tour content (tag filter demo) is established only when it operates the same way.
- Placement: `X/Y = 120+i*40, 140+i*40`, `Width/Height = 320×380` (onscreen corrections are handled by `MonitorHelper.EnsureOnScreen` in `BuildWindow` — no extra work).
- After seeding `OnboardingCompleted = true` + `SaveAsync()`.

#### Relationship to `RestoreAllAsync` (theorems asked about)
- Seed** just before calling `RestoreAllAsync()` in `App.OnStartup` (between `App.xaml.cs:145-146`):
  ```csharp
  var isFirstRun = !settingsService.LoadedFromFile;
  if (isFirstRun && !settings.OnboardingCompleted)
      await onboarding.SeedAsync().ConfigureAwait(true);
  await manager.RestoreAllAsync().ConfigureAwait(true);
  ```
- This will **not modify** the "0, then 1 empty note" (`WindowManager.cs:39-43`) branch of `RestoreAllAsync` — if the seed is successful, there are 3 notes, so it will not enter that branch, and if the seed fails (e.g. storage error), the existing 1 empty note fallback becomes a safety net. Since the sample note has content, it is not affected by `DiscardEmptyPlaceholders`(`WindowManager.cs:264-271`) and empty note immediate purge(`OnWindowDismissed`).
- Replay (settings button) requires a path that **only brings up new notes as a window**, not `WindowManager.ReloadAsync()` after `SeedAsync(force: true)` — Add a small method `Task ShowNotesAsync(IReadOnlyList<Note>)` to `IWindowManager` (reuse internal `BuildWindow`+`Show`) or SeedAsync opens Note list Returns and delegates display of the window to the SettingsViewModel. The former is recommended.

### 3.6 What's New

#### Data: Embed CHANGELOG.md (optional instead of hardcoding summary)
- Rationale: Eliminate double management (summary hardcoding) for each release and maintain a single source of truth. Embed as an **EmbeddedResource**, following the principles of single-file deployment written in memory ("Content assets don't run in the exe — embed them"). Add next to the existing mdeditor embed block (csproj:23-30) in csproj:
  ```xml
  <EmbeddedResource Include="..\CHANGELOG.md" Link="Assets\CHANGELOG.md" />
  ```
- Parser (new `Utils/ChangelogReader.cs`, `internal static` — easy for testing):
  ```csharp
  /// CHANGELOG Extracts the "## [X.Y.Z]" section (from that header to just before the next "## [") from markdown. If not, null.
  internal static string? ExtractSection(string changelogMarkdown, Version version);
  ```
  Header matching is `^## \[v?{Major}.{Minor}.{Build}\]` regular expression (bracket-escaped, anchored) — Conservative style, such as `UpdateService.IsSafeReleaseTag`.
- Obtain version: `UpdateService.CurrentVersion()` (`UpdateService.cs:198-199`) is private — Since it is one line of logic, promote it to the new `Utils/AppVersion.cs` (`internal static Version Current()`) and organize UpdateService to use it as well (behavior unchanged). Version comparison reuses existing `UpdateService.Compare`.

#### Window: `Views/WhatsNewWindow.xaml(.cs)` (new)
- Configuration: Title ("What's New — v2.3.0", localization format string) + **Script OFF WebView2** (same as `Preview` Security Profile: `NavigateToString`, keep default of scripts/webmessages disabled) + Close button. App chrome color uses §3.1 resources.
- Render: Added a small wrapper to `HtmlRenderer` for app chrome —
  ```csharp
  /// For app Chrome documents, such as What's New: Markdown is wrapped and rendered in app theme (light/dark) colors.
  public static string RenderAppDocument(string markdown, bool dark);
  ```
  Inside, the CSP (‘default-src none; style-src unsafe-inline …’) of the existing `Render`/`Wrap` is copied as is, and only the background/foreground is replaced with the app theme color (`#FAFAFA/#1F2328` vs `#202124/#E8EAED`). When WebView2 initialization fails (lack of runtime, etc.), only a log is left without opening a window (silent).
- Trigger (`App.OnStartup`, after tray init — after `App.xaml.cs:148`):
  ```csharp
  var current = AppVersion.Current();
  var last = Version.TryParse(settings.LastSeenVersion, out var v) ? v : null;
  var isUpgrade = !isFirstRun && (last is null || UpdateService.Compare(current, last) > 0);
  if (settings.LastSeenVersion != current.ToString(3))
  { settings.LastSeenVersion = current.ToString(3); _ = settingsService.SaveAsync(); }
  if (isUpgrade && startupFiles.Length == 0) /* Show WhatsNewWindow (try/catch) */;
  ```
  (Downgrade/first installation only updates records and omits display — 1:1 correspondence with §2.3 rules)

### 3.7 Summary of settings UI·i18n·DI changes
- `SettingsViewModel`: `ThemeOption(string Value, string Label)` record + `ThemeOptions`(system/light/dark) + `[ObservableProperty] string _selectedAppTheme` — Clone `LanguageOption` pattern (`SettingsViewModel.cs:19, 52-57`). In `SaveAsync`, save `current.AppTheme = SelectedAppTheme;` and then call `_theme.Apply(SelectedAppTheme)` (add constructor injection `IThemeService`). `[RelayCommand] ReseedOnboardingAsync` for “Reseed Onboarding Note”.
- `SettingsWindow.xaml`: Insert theme row (add 1 Grid RowDefinition) under language combo (Row 7), add 1 link-type button at the bottom.
- New resource keys (1 set each en, accompanied by `Strings.cs` accessor): `Settings_Theme`, `Settings_ThemeSystem/Light/Dark`, `Settings_ReseedOnboarding`, `Onboarding_Note1Title/Body`~`Note3Title/Body`, `WhatsNew_WindowTitle`, `WhatsNew_TitleFormat`, `Common_Close` — About 15 keys.
- 2 DI registrations (`IThemeService`, `IOnboardingService`) — `App.xaml.cs:113-122` block.

---

## 4. Work decomposition + risk classification

> Principles: **Theme Infrastructure·Startup Flow·Data Seed = High Risk (Opus Direct)** — Global regression/point of contact with user data. **Pattern replication/mechanical substitution/content = low risk (Sonnet delegation)** — precedents are in files, failures are local.

| # | work | Risk | In charge | Evidence |
|---|---|---|---|---|
| T1 | `ThemeService` + App.xaml `ThemesDictionary` runtime swap + `AppColors.Light/Dark.xaml` + System detection (SystemEvents) + DWM title bar helper. WPF-UI 3.0.5 API verification included | **Go** | Opus | Affects all windows, requires validation of WPF-UI internal behavior, manages static event lifetime (risk of leaks) |
| T2 | Start flow gate: `AppSettings` 3 fields, `LoadedFromFile`, first run/upgrade verdict, What's New trigger, seed call order (`App.xaml.cs`) | **Go** | Opus | In case of misjudgment, the tour is incorrectly triggered for existing users and the sample notes are incorrectly seeded in the real DB — User data contamination path |
| T3 | `OnboardingService` seed logic(+`IWindowManager.ShowNotesAsync`) | **Go** | Opus | Repository Write + `RestoreAllAsync` Race/Fallback Interaction |
| T4 | `ChangelogReader` parser + CHANGELOG embed + `AppVersion` promotion + **Embed verification in single file publish build** | **Go** | Opus | Single file asset incident precedent (memory·CHANGELOG 2.1.1) — must be confirmed by publish output |
| T5 | `HtmlRenderer` luminance judgment + dark note CSS branching, `RenderAppDocument` | Middle → **High** treatment | Opus | Render regression spreads to all markdown notes (light 5 color invariant condition verification required) |
| T6 | `NotesListWindow.xaml`/`SettingsWindow.xaml` hard-coded color → DynamicResource substitution (as in §3.2 table) | **Me** | Sonnet | 1:1 correspondence table provided, even if it fails, there will be local color error |
| T7 | `NoteWindow.xaml` Color Picker Popup 2-color replacement | **Me** | Sonnet | single point |
| T8 | Settings UI: Theme combo + reseed button (XAML+VM), cloning existing Language pattern | **Me** | Sonnet | Replicating precedents within a file |
| T9 | `WhatsNewWindow` XAML/code-behind (render call uses T5 output) | **Me** | Sonnet | New independent window, failure isolation (try/catch) |
| T10 | Sample Note Chapter 3 Markdown body writing + resx en 15 keys + `Strings.cs` accessor | **Me** | Sonnet | Content Operations |
| T11 | `editor.html` dark CSS + `editor.js` theme parameter + bundle rebuild, `MarkdownWysiwyg` URL parameter | Middle | Opus recommended | Bundle-source CI matching verification/security CSP immutability verification required (change itself is small) |
| T12 | Add test (§5) | Middle | Written by Opus·Sonnet Assistant | Gate logic verification is essential |
| T13 | Documentation: CHANGELOG 2.3.0 section, README updated screenshots on both sides | **Me** | Sonnet | Compliance with Document Rules (Prior Renewal) |

Recommended order: T1 → T6·T7 (substitution) → T2 → T3·T4 → T5·T11 → T8·T9·T10 → T12 → T13. T6~T7 can be paralleled with T1's dictionary style verification (demo).

---

## 5. Test/verification plan

### 5.1 Unit tests (added to `Stickmark.Tests/Tests.cs`, maintain current 107 cases + new)
- **Theme Resolve**: Separate `ThemeService.Resolve("system"/"light"/"dark", systemIsDark)` into a pure function and verify 4 combinations. Unknown value (`"blue"`, null) → Light fallback.
- **Gate Matrix** (separate decision logic into `internal static` and test without window):

| case | LoadedFromFile | LastSeenVersion | number of notes | file arguments | expectations |
|---|---|---|---|---|---|
| New Installation | false | null | 0 | None | Tour O/WN
| Upgrade | true | 2.2.2 | n | None | TOUR X/WN O |
| First introduced upgrade | true | null | n | None | TOUR X/WN O |
| Rerun the same version | true | 2.3.0 | n | None | both
| Downgrade | true | 9.9.9 | n | None | Both X, record breaking |
| Upgrade + double click file | true | 2.2.2 | n | Yes | WN on hold (LastSeen renewal as per policy) |
- **ChangelogReader**: Normal extraction (intermediate version), latest section, non-existent version → null, `v` prefix tag header, last section (no next `## [`), empty input.
- **OnboardingService**: Seed empty in-memory storage → 3 notes·`Format=Markdown`·`Tags` not empty·`PlainText` filled; Note exists+force=false → no-op; force=true → create append; `OnboardingCompleted=true` after seeding.
- **HtmlRenderer**: `IsDark` threshold (Gray `#373A40` → dark, Yellow `#FEF3B0` → light); When inputting 5 colors of light, **string is the same** as the existing output (golden comparison — regression lock); When entering dark, include `rgba(255,255,255`) and do not include `#1565C0`.
- **AppSettings Serialization**: Deserialize old JSON (no new keys) → default(`"system"`, null, false); Round trip conservation.

### 5.2 Screenshot Harness (two themes)
- Follows the technique of project memory: **isolated demo DB + RenderTargetBitmap self-capture** (no screen grabs), WebView2 surface `CapturePreviewAsync`. Do not touch the app/real DB while running.
- Capture Matrix (Light/Dark → Save as `*-light.png`/`*-dark.png` in `docs/screenshots/`, use crop when updating README.
- Manual checklist: Title bar dark in dark (NotesList/Settings/WhatsNew), tray context menu·Notes ⋯ menu dark, open list window color switched immediately after saving settings, OS theme change (system mode) reflected in real time, MessageBox left light (check known limitations).

### 5.3 Deployment Geometry Verification (Required — Memory Rules)
- `dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true` yields a single exe: ① What's New displays the CHANGELOG section (check for embeds), ② creates tour 3 in a new install scenario (temporary `%LOCALAPPDATA%\Stickmark` redirect or clean VM), ③ WYSIWYG dark parameter behavior. **Prohibit completion processing only by confirming Debug execution.**
- Passing the entire test (`dotnet test`) as a release gate (using existing CI gate).

---

## 6. Expected size and release version

| Item | estimate |
|---|---|
| New File | Around 9 — `ThemeService(.I)`, `OnboardingService(.I)`, `ChangelogReader`, `AppVersion`, `WindowThemeHelper`, `WhatsNewWindow.xaml(.cs)`, `Themes/AppColors.Light/Dark.xaml` |
| edit file | About 12 — `App.xaml(.cs)`, `AppSettings`, `SettingsService(.I)`, `SettingsViewModel`, `SettingsWindow.xaml`, `NotesListWindow.xaml`, `NoteWindow.xaml(.cs)`, `HtmlRenderer`, `MarkdownWysiwyg`, `UpdateService` (version helper delegate), `WindowManager(.I)`, csproj, `Strings.cs`+resx 2, `editor.html`/`editor.js` (+bundle) |
| Net increase in LOC | Production ~900, Testing ~350, Resources/Content ~150 |
| workload | High risk track (T1~T5, T11) 1.5~2 days + Low risk parallel track 0.5~1 day + Verification 0.5 day |
| Release version | **2.3.0** (Function addition/setting backwards compatibility → minor). CHANGELOG 2.3.0 Section Before Tags Required — Write with user-facing copy quality, as What's New displays that section as is |
| Rollback Safety | New 3 settings keys are ignored by older versions (Json deserialization tolerance) — No side effects when downgrading. The sample note is a regular note, so the data schema is not affected |

### Unresolved questions (check before starting implementation)
1. Whether the `ApplicationThemeManager.Apply` signature/standard control style in WPF-UI 3.0.5 is a DynamicResource — Confirmed as a demo in the first stage of T1 (maintaining a fallback design that absorbs the first-layer key into the second-layer's own dictionary in case of mismatch).
2. Acceptability of What's New's English user experience (Korean CHANGELOG exposure) — If unacceptable, there is an alternative to switching to the resx summary hard-coding method (3 to 5 bullets per release) (T4 parser can be reused as is).
3. Sample note body final text — 1 post-draft review at T10.
