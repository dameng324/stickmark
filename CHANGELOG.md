# Changelog

All notable changes to Stickmark are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
This file starts from the point it was introduced — earlier history lives in the git log.

## [3.0.0] - 2026-11-03

### Changed
- Renamed the application and repository to **Stickmark** (`Stickmark.exe`, `Stickmark.sln`, `Stickmark` namespace, and `%LocalAppData%\Stickmark`). Existing `%LocalAppData%\StickyPad` data is migrated automatically on first launch.
- Removed the Text / Markdown / HTML mode controls and the split editor. Notes now open in a read-only Eye preview; double-clicking the rendered text enters WYSIWYG editing, and losing focus returns to preview.
- Added a fullscreen note mode (`F11` / header button).
- Removed the redundant Eye button and moved Delete into the overflow menu with red emphasis.
- Reworked the close button as a vector icon and tightened the top spacing in preview mode.
- Persisted per-note WebView zoom across preview/editing switches and restarts.
- Release builds now publish an **Inno Setup installer** instead of a bare executable. The in-app updater downloads and verifies the installer before applying it.
- Retargeted the application and test projects to `net10.0-windows`.
- Removed the Korean localization resources and translated repository comments and documentation to English.

## [2.4.0] - 2026-07-06

### Added
- **Integrated To-Do View** — In the new "To-Do" tab of the note list window, check boxes (`- [ ]`) scattered across all notes are collected for each note, and if you check/uncheck them on the spot, the corresponding line in the original note is updated (instant synchronization of open note windows and linked `.md` files, and byte-by-byte preservation of line breaks and formatting). Supports Hide Done toggle and text filters. Go directly to the tray menu “View to-do”.
- **Quick switch (Ctrl+P)** — Open the title/tag fuzzy search pop-up with Ctrl+P anywhere in the note window or list window, and immediately jump to any note with ↑↓ Enter. Empty search terms are listed in order of recent modification.

## [2.3.0] - 2026-07-06

### Added
- Toggle the `- [ ]` checkbox in WYSIWYG editing by **clicking** — It is displayed as an actual checkbox in the line without the cursor, and when clicked, `[ ]`↔`[x]` in the source is replaced with equal length, and the cursor and formatting are preserved.
- **Code block syntax highlighting** — Fence code blocks such as ```` ````js ```` are highlighted in both the WYSIWYG editor (14 CodeMirror language packs) and render preview/HTML/PDF export (ColorCode, inline style). Maintains existing CSP/script blocking immutability with 0 lines of script.
- Fixed list automatic continuation regression — The keymap priority was specified so that automatic continuation of list markers, number increment, and empty item release upon Enter continues to operate, and Node self-tests (21 types) were incorporated into CI.
- **Vault real-time synchronization** — External changes to the `.md` of the vault folder (cloud synchronization, Obsidian/VS Code editing) are automatically reflected during execution (no restart required). If there are unsaved edits in an open note, it is checked before overwriting, and notes added from outside are quietly registered (without opening a window) and notified with a single tray notification.
- **Conflict copy notification** — OneDrive/Dropbox/Syncthing conflict file patterns are detected and notified through tray notifications (no automatic merging or deletion). Since the conflicting copy had the same ID as the original, the potential data accident path that could have leaked into the copy file after being saved was also blocked.
- **Open vault with external app** — Added "Open folder (Explorer)"/"Open in Obsidian"/"Open in VS Code" to tray vault menu (automatically hides uninstalled apps).

### Fixed
- Normalized the vault `.md` round trip to be completely idempotent — Ghost changes (unnecessary resynchronization, notifications) that occurred immediately after saving, in rich (formatted) notes, in empty titles, and in manually added files without frontmatter have disappeared. Files without a front matter receive a stable ID based on the file name, and the window position is maintained between restarts.

### Dependencies
- Added ColorCode-Universal (ColorCode.Core/ColorCode.HTML 2.0.15, MIT, CommunityToolkit). Editor bundle size +283KB (language pack).

## [2.2.2] - 2026-07-06

### Changed
- Lower WebView2 (preview/WYSIWYG edit) memory usage for notes hidden in the tray, and restore them when displayed again — Reduce background memory when hiding multiple notes (best-effort, script continues to run).

## [2.2.1] - 2026-07-06

### Fixed
- An issue where the WYSIWYG editor's toolbar tooltips and placeholders were only displayed in Korean regardless of the app language — Localized to follow the app UI language (Korean/English).

### Changed
- Added a gate to ensure the release workflow passes pre-deployment testing (preventing failed builds from going out as tags).
- When schema migration is actually needed, the DB can be automatically snapshot (`notes.db.v{version}.bak`) and reverted.
- Pin the WYSIWYG editor dependency (CodeMirror 6) with a lockfile and verify with CI that the bundle does not deviate from the source.
- Supports screen readers by adding an accessibility name (AutomationProperties) to the icon-only button in the note window.
- Document the results of WYSIWYG Edit WebView security review (docs/SECURITY-REVIEW).

## [2.2.0] - 2026-07-05

### Added
- Addition of **formatting toolbar and shortcuts** to WYSIWYG editing — Instead of entering Markdown syntax directly, use buttons (bold, italics, strikethrough, inline code, title, bullets/numbers/checkbox list, quote, link) or shortcuts (`Ctrl+B`/`Ctrl+I`, `Ctrl+Shift+X`, ``Ctrl+` ``, `Ctrl+K`, Apply formatting with `Ctrl+Alt+1~3`, etc.). While editing like a word processor, saving is still pure Markdown source, so it is round-trip compatible with Vault (`.md`).

## [2.1.1] - 2026-07-05

### Fixed
- An issue where WYSIWYG editing did not start with “The specified path cannot be found” in the installed version (single file distribution) — Edited to work with a single exe by embedding the editor asset (CodeMirror bundle/host HTML) in the assembly and extracting it to a local folder at runtime.

## [2.1.0] - 2026-07-05

### Added
- **WYSIWYG Markdown editing** — Live preview editing with ✎ toggle in Markdown notes. For lines without a cursor, the markdown markers (`#`·`**`·`` ` ``·`>`) are hidden and appear formatted, and only the line being edited is exposed to the original (Obsidian style). It is based on offline CodeMirror 6, and the save is a pure Markdown source, so it is round-trip compatible with Vault (`.md`). Default values ​​can be set through settings and will fall back to editing the original source at any time.

### Security
- WYSIWYG editing allows scripts/web messages only in the edit-only WebView2 (render preview keeps script OFF), completely blocks the network (CSP `default-src 'none'`, virtual host `DenyCors`, moving off-page/blocking new windows), and notes contents are exchanged only as string data and do not create a code execution path.

## [2.0.1] - 2026-07-05

### Fixed
- An issue where even trash and empty notes were saved as `.md` when migrating to the vault — Modified to only transfer notes that are not active or empty (the vault folder matches the note list).
- An issue where an extra empty placeholder note was left behind when creating a new note from a template or opening a file — Cleaned up empty notes when opening an actual note.
- An issue where the folder selection dialog box in the tray menu (vault import/export, etc.) immediately disappears without an owner window — Always give a valid owner window.

## [2.0.0] - 2026-07-05

### Added
- **Vault mode (live).** Switch the storage backend to a "Vault folder (.md files)" in **Settings → Storage**. Each note is saved as a human‑readable `.md` file (YAML front matter with `id`/`title`/`format`/`color`/`tags`/dates, plus the body — Obsidian‑compatible), while Stickmark‑specific state (window geometry, trash, etc.) lives in a sidecar `.stickmark-index.json`. Put the folder in OneDrive/Dropbox/etc. and your notes sync across machines (changes made externally are picked up on restart). On first switch, existing notes are safely migrated into the vault (only when the vault is empty). The built‑in database remains the default — Vault mode is strictly opt‑in, with zero risk to existing users.
- **Vault export/import** (tray → *Vault*) — export all notes to a folder of round‑trippable `.md` files, or import a folder of `.md` files back into Stickmark.
- **winget manifest** — Stickmark can now be packaged/submitted via winget (see `packaging/winget/`).
- **Install & SmartScreen guide** (`docs/INSTALL.md`) — explains why the unsigned‑app SmartScreen warning appears and how to verify the release's SHA‑256 checksum.

### Changed
- Introduced a DB schema migrator under the hood, enabling safe, versioned data‑model evolution going forward.

## [1.7.0] - 2026-07-05

### Added
- **Selection-based bulk actions in the All‑notes window.** Notes can now be checked and, via a "Bulk actions ▾" (Bulk actions) menu, bulk‑deleted (moved to Trash) or bulk‑recolored in one step.
- **Note templates.** New notes can be created from a built‑in template (Daily log / Meeting notes / To‑do list), available from a note's **⋯** More menu and the tray's *New note from template* submenu. Templates substitute `{{date}}` with today's date.
- **Toolbar overflow "⋯" menu.** The note header's less‑used buttons (new note, open file, all notes, export/print, templates) are consolidated into a single **⋯** menu, fixing toolbar clipping at narrow note widths.

### Security
- **Auto‑update integrity verification.** The updater now verifies the downloaded exe against a published SHA‑256 checksum (a `.sha256` release asset produced by CI) before applying it, and refuses to apply on mismatch or missing checksum (fail‑closed).

## [1.6.1] - 2026-07-04

### Security
- **Backup import Dangerous marker guard (`ContainsDangerousXaml`) was also added to both `NoteWindow.LoadEditorContent` / `TextExtraction.ToPlainText` XAML sinks for in-depth defense.
- **Removal of `LinkedFilePath` of imported notes.** Blocked the path where note contents are quietly written to a random path (overwriting arbitrary files) when editing.
- **Restrict WebView2 `NewWindowRequested` scheme to http/https.** Prevented local execution from being induced by `file:`/UNC/protocol link in note markup.
- **Block remote resources in export document CSP.** HTML/PDF/print documents only allow `data:` images, media, and fonts to mitigate tracking/SSRF risks.
- **Name pipe (`Stickmark.OpenFile.v1`) input verification.** Same as command line, file existence and supported format check + maximum 20 limit applied.
- **Enhanced auto-update tag·URL validation and staging.** Validation of GitHub release tag format, limiting download URLs to https + GitHub hosts, and using unpredictable GUID staging folders.
- Added security regression testing. For more information, see [`docs/SECURITY-REVIEW.md`](docs/SECURITY-REVIEW.md).

## [1.6.0] - 2026-07-04

### Added
- **Selection-based export in the All‑notes window.** Each note card now has a checkbox; export targets the checked notes, or the current searched/filtered list if none are checked. Selection persists across search/filter changes, with a live "N selected" count and *Select All* / *Unselect* buttons. Checkboxes/selection apply to the active view only (not the Trash tab).
- **"To an HTML document..." export** — renders the target notes into a single styled `.html` document: each note gets a left accent bar in its note color, Markdown/HTML notes render with their real formatting, and plain/rich‑text notes render as text. The document ships with a strict Content-Security-Policy that blocks scripts.
- **"To PDF..." export** — prints the same generated HTML document to a single `.pdf` using an offscreen WebView2 instance (the same WebView2 runtime already used for note preview).
- **“To a Markdown file per note…” export** — prompts for a destination folder and writes one `.md` file per note, named from the note title (sanitized, with reserved Windows names handled and filenames de‑duplicated so existing files are never overwritten). Each file includes YAML front matter with `title`, `tags`, `color`, `created`, and `modified`.
- Export operations report success/failure back to the user after completing.
- **Export/print the current note** — the note window's header **⤓** menu can now save the open note as PDF, HTML, or Markdown, or send it straight to the printer.
- **Localized UI resources** — the original bilingual resources were retired in 3.0.0 in favor of English-only.
- **Localized README** — retired in 3.0.0.

### Changed
- The All‑notes window's *Export* control is now a *Export ▾* dropdown offering the three formats above, replacing the previous single text-dump behavior for that button.

The tray menu's *Export backup…* / *Import backup…* (portable JSON) and *Export notes as text…* (single-file Markdown/`.txt` dump of all active notes) are unchanged.
