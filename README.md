# Stickmark

> 📝 A lightweight, fast sticky‑note app for Windows — instant capture, rich text, tags, and wiki‑style links, all stored locally.

Stickmark lives in your system tray and puts a note on your desktop in a single keystroke. Notes are colorful, resizable, always‑on‑top‑capable, and searchable. Everything is saved locally in an embedded database — no account, no cloud, no telemetry.

<sub>Built with **.NET 10 · WPF · MVVM**. Windows 10/11, x64.</sub>

---

## Table of contents

- [Highlights](#highlights)
- [Screenshots](#screenshots)
- [Features](#features)
- [Keyboard shortcuts](#keyboard-shortcuts)
- [Getting started](#getting-started)
  - [Download](#download)
  - [Build from source](#build-from-source)
- [Usage guide](#usage-guide)
- [Where your data lives](#where-your-data-lives)
- [Architecture](#architecture)
- [Privacy](#privacy)
- [Roadmap](#roadmap)
- [License](#license)
- [Acknowledgements](#acknowledgements)

---

## Highlights

- ⚡ **Instant capture** — `Ctrl+Shift+N` drops a new note anywhere, even when no window is focused.
- 🎨 **Six color themes** — Yellow, Pink, Blue, Green, Purple, and a dark Gray.
- ✍️ **Rich text** — bold, italic, underline, strikethrough, lists, task checkboxes, inline code, code blocks, alignment, and per‑selection font sizes.
- 🧩 **Eye-first Markdown preview** — notes open as rendered Markdown in a WebView2 preview. Double-click the text to edit; click elsewhere to return to preview.
- 🏷️ **Tags & search** — type `#tag` inside a note; filter and full‑text search from the **All notes** window with match highlighting.
- 🔗 **Wiki links** — write `[[Note title]]` to link notes together; click to jump.
- 📂 **Open & link `.md` files** — open a Markdown/text file from disk (double‑click, drag‑drop, or `Ctrl+O`) and Stickmark renders it in a note. Edits save straight back to the file, and external changes reload automatically — a two‑way link, not a copy.
- 🗑️ **Safe deletes** — a Recycle Bin keeps deleted notes for 30 days before auto‑purging.
- 🖥️ **Stays out of the way** — tray icon, per‑note opacity, always‑on‑top pin, multi‑monitor aware.
- 💾 **Local‑first** — an embedded LiteDB file on your machine, with one‑click JSON backup export/import.
- 🌐 **English UI** — clean English labels throughout, with a language selector reserved for future localization.
- 🗄️ **Vault mode** — store your notes as human‑readable `.md` files in a folder you choose (Obsidian‑compatible). Put it in a cloud folder for multi‑device sync. Opt in under **Settings → Storage**; the built‑in database stays the default. External edits (cloud sync, Obsidian) are picked up live while the app is running.
- ✅ **Unified to-do view** — every checkbox from every note, gathered in one tab; check it off right there and the source note updates. Plus a fuzzy **quick switcher (Ctrl+P)** to jump to any note.

## Screenshots

Screenshots are intentionally omitted from this fork until updated English captures are available.

## Features

### Notes & editing
- Rich‑text editor (WPF `RichTextBox`) with a toolbar that appears on hover/focus and hides in preview mode.
- **Eye-first Markdown preview** — notes are rendered with [Markdig](https://github.com/xoofx/markdig) in a themed **WebView2** view. Double-click the rendered text to enter WYSIWYG editing; click elsewhere or press `Ctrl+E` to return to preview, and links open in your browser.
- **WYSIWYG Markdown editing** — double‑click rendered note text to edit; markers hide as you type and editing ends when focus leaves. Saved as plain Markdown (offline).
- **Formatting:** bold, italic, underline, strikethrough, bulleted & numbered lists, left/center/right alignment, inline code and code blocks, and a per‑selection font‑size picker.
- **Task checkboxes** — insert inline `☐` items you can tick right inside the note.
- **Preview / Edit toggle** (`Ctrl+E`) locks a note read‑only so you can't fumble the text while reading it.
- **Fullscreen note** — press `F11` or use the fullscreen button; `Esc` restores the previous window size.
- **Persistent zoom** — per-note WebView zoom is kept when switching between preview and editing, and across restarts.
- **Auto title** — the first non‑empty line becomes the note's title (used for the list and for `[[links]]`).
- **Autosave** — edits are debounced and persisted automatically; no save button.
- **Templates** — start a new note from a built‑in template (Daily log · Meeting notes · To‑do list) from a note's **⋯** menu or the tray.

### Organization
- **Bulk actions** — tick multiple notes in the All‑notes window, then delete them or change their color in one go.
- **`#tags`** are parsed live from the note body; the **All notes** window shows a tag sidebar with counts and one‑click filtering.
- **Search** across title, tags, and body with scored ranking and inline highlighting of matches.
- **`[[Wiki links]]`** resolve by note title — clicking opens (or focuses) the target note; missing targets are reported.
- **Auto‑linked URLs** — `http(s)://…` addresses become clickable and open in your browser.

### Linked files (open `.md` from disk)
- **Open a Markdown/text file** three ways: double‑click it (once Stickmark is set as its *Open with* app), **drag‑drop** it onto any note, or press **`Ctrl+O`** — it opens as a rendered note.
- **Two‑way sync** — the note *is* the file: your edits are written back to the original `.md`, and if the file changes in another editor the note reloads automatically (with a conflict prompt if you had unsaved edits).
- **Non‑destructive** — deleting/trashing a linked note only removes the note; the **original file is never deleted**. Re‑opening the same file reuses the existing note instead of duplicating it.
- **Safe by design** — reading detects the file's encoding (BOM‑aware); writing is UTF‑8. The file path shows on the header hover tooltip.

### Window behavior
- Move, resize (grip), and recolor each note; position/size/color/opacity persist per note.
- **Per‑note opacity** (50–100%) and an **always‑on‑top** pin.
- **Multi‑monitor safe** — notes that would open off‑screen are nudged back into view on startup.
- Closing a note with content **hides** it (kept alive in the tray); closing an empty note discards it.

### System integration
- **System‑tray icon:** left‑click toggles show/hide‑all, double‑click makes a new note, right‑click opens the menu.
- **Settings window** — toggle auto‑start, enable/disable global hotkeys, and **rebind the hotkeys** with a click‑and‑press capture field.
- **Global hotkeys:** new note and All‑notes list from anywhere, fully configurable (or disabled).
- **Start with Windows** — optional auto‑start via the current‑user `Run` registry key.
- **Automatic updates** — on launch (and from the tray's *Check for updates…*) Stickmark checks GitHub Releases, verifies the installer checksum, and runs the Inno Setup updater on restart. Toggle it in Settings.
- **Verified updates** — downloaded updates are checked against a published SHA‑256 checksum before they're applied.
- **Single instance** — launching again just surfaces the running app (and forwards any file passed on the command line to it, so double‑clicking an `.md` opens it in the running instance).
- **`.md` file association** — Stickmark registers itself (per‑user, no admin) in the Windows *Open with* list for `.md`/`.markdown`, without hijacking your default editor.
- **Language** — English UI. The language selector is retained for future localization.

### Data & backup
- **Backup export/import** to a portable JSON file (tray menu).
- **Export notes as text** — dump all active notes to a readable Markdown/`.txt` file (tray menu).
- **Export from the All‑notes window** — the *Export ▾* button exports the selected notes (or the current filtered list, if nothing is selected) as a single styled **HTML** document, a single **PDF** (rendered via an offscreen WebView2), or **one Markdown file per note** (with YAML front matter) into a folder you choose.
- **Export/print a single note** — from a note's header **⤓** menu, save the current note as PDF, HTML, or Markdown, or send it to the printer.
- **Vault (folder of .md)** — export all notes to / import from a folder of round‑trippable Markdown files (tray → Vault).
- **Recycle Bin** — deleted notes are soft‑deleted, restorable, and permanently purged after 30 days (a cleanup also runs at startup).
- **Rolling logs** kept for 7 days for troubleshooting.

## Keyboard shortcuts

**Global (system‑wide)** — defaults; rebind them in **Settings**:

| Shortcut | Action |
| --- | --- |
| `Ctrl` + `Shift` + `N` | New note |
| `Ctrl` + `Shift` + `L` | Open the **All notes** window |

**Inside a note:**

| Shortcut | Action |
| --- | --- |
| `Ctrl` + `B` / `I` / `U` | Bold / Italic / Underline |
| `Ctrl` + `Shift` + `X` | Strikethrough |
| `` Ctrl + ` `` | Inline code |
| `Ctrl` + `E` | Toggle Preview / Edit (read‑only lock) |
| `Ctrl` + `O` | Open a Markdown/text file from disk (linked, two‑way) |

> Alignment, lists, task items, code blocks, font size, color, opacity, and pin are available from the on‑hover toolbar.

## Getting started

### Download
Download the latest **Inno Setup installer** from the [**Releases**](https://github.com/dameng324/stickmark/releases) page and run it.

> ⚠️ Windows may show a SmartScreen "unknown publisher" warning (the app isn't code‑signed). It's safe — see [Install & verify](docs/INSTALL.md) for why and how to check the SHA‑256 checksum.

The release installer is self‑contained and does not require a separate .NET runtime. The Markdown preview uses the **[WebView2 runtime](https://developer.microsoft.com/microsoft-edge/webview2/)** — preinstalled on Windows 11 (and most Windows 10).

### Build from source

**Prerequisites**
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Inno Setup 6](https://jrsoftware.org/isinfo.php) (only needed to build the installer)
- Windows 10/11 (WPF is Windows‑only)

**Clone & run**
```bash
git clone https://github.com/dameng324/stickmark.git
cd stickmark
dotnet run --project Stickmark
```

**Build the installer**
```powershell
dotnet publish Stickmark -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -o publish

iscc /DAppVersion=3.0.0 packaging\Stickmark.iss
```

Open `Stickmark.sln` in Visual Studio 2022 or later if you prefer the IDE.

## Usage guide

- **Make a note:** press `Ctrl+Shift+N`, or double‑click the tray icon.
- **Tag it:** type `#idea`, `#todo`, `#work` anywhere in the body — tags appear in the All‑notes sidebar.
- **Link notes:** write `[[Project ideas]]`; it becomes a clickable link that opens the note titled "Project ideas".
- **Find anything:** press `Ctrl+Shift+L`, type in the search box — title, tags, and body are searched with highlights.
- **Export a batch of notes:** in the All‑notes window, tick the checkbox on each note card you want (selection persists across search/filter, with a "N selected" count and *select all*/*deselect*), then *Export ▾* → **As HTML document… **, **PDF… **, or **Markdown file for each note… **. With nothing ticked, it exports the currently filtered list instead.
- **Recolor / pin / fade:** use the note's hover toolbar to switch color, pin it on top, or adjust opacity.
- **Delete safely:** deleting moves a note to the **Trash** tab; restore it there, or empty the bin. Anything left 30 days is purged automatically.
- **Back up:** tray menu → *Export backup…* writes a JSON file; *Import backup…* restores it (notes with the same id are overwritten).

### Markdown preview and editing

Each note opens in the read-only preview state, showing rendered Markdown. There is no separate mode picker, Eye toggle, or split editor.

> `Pin` = always on top · `Full` = fullscreen

**How it works:**
1. The note opens as a rendered Markdown preview.
2. **Double-click the rendered text** to enter WYSIWYG editing.
3. Click elsewhere, switch windows, or press **`Ctrl+E`** to save and return to preview.

WYSIWYG editing uses offline CodeMirror 6. The saved content remains plain Markdown source, so Vault `.md` files round-trip cleanly. Use `Ctrl` + mouse wheel to zoom; the zoom level is remembered per note and shared by preview and editing.

> **Pasting:** pasted text is handled as plain Markdown by the editor, so markup is not silently converted into rich text.
### Open & link a `.md` file from disk

Point Stickmark at a Markdown/text file on your machine and it opens as a **rendered, live‑linked** note:

- **Drag‑drop** the `.md` onto any note, **press `Ctrl+O`** / the 📂 header button, or use the tray menu → *Open markdown file…*
- **Double‑click** from Explorer/desktop: right‑click the file → *Open with* → **Stickmark** (Stickmark registers itself in that list on first run; choose *Always* to make double‑click open it).

Once open:

1. The note shows the **rendered Markdown** first; press **`Ctrl+E`** / the 👁 button to edit the source.
2. **Your edits are saved back to the original file** automatically (debounced) — the note and the file stay in sync.
3. If the file is **changed by another program**, the note reloads it. If you had unsaved edits, Stickmark asks whether to keep yours or take the disk version.
4. **Deleting the note keeps the file.** It only unlinks; your `.md` on disk is untouched.

> The header's hover tooltip shows the linked file's full path. Supported: `.md`, `.markdown`, `.txt`, and similar text files.

## Where your data lives

Everything is stored under your local profile:

```
%LocalAppData%\Stickmark\
├─ notes.db         # LiteDB embedded database (all notes + trash)
├─ settings.json    # preferences (hotkeys, auto-start, etc.)
└─ logs\
   └─ app-YYYYMMDD.log   # rolling logs, 7-day retention
```

To move Stickmark to a new machine, copy `notes.db` (or use *Export backup…*).

## Architecture

A small, layered WPF application using the MVVM pattern and dependency injection.

**Tech stack**
- **UI:** WPF (.NET 10) + [WPF‑UI](https://github.com/lepoco/wpfui)
- **MVVM:** [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet)
- **Host & DI:** `Microsoft.Extensions.Hosting` / `DependencyInjection`
- **Storage:** [LiteDB](https://www.litedb.org/) (embedded NoSQL)
- **Rendering:** [Markdig](https://github.com/xoofx/markdig) (Markdown) + [WebView2](https://developer.microsoft.com/microsoft-edge/webview2/) (HTML preview)
- **Logging:** [Serilog](https://serilog.net/) (file + debug sinks)
- **Tray:** [H.NotifyIcon.Wpf](https://github.com/HavenDV/H.NotifyIcon)
- **Hotkeys:** [NHotkey.Wpf](https://github.com/thomaslevesque/NHotkey)

**Project layout**
```
Stickmark/
├─ App.xaml(.cs)        # startup, DI host, single-instance, global hotkeys
├─ Models/              # Note, AppSettings, NoteColor/Theme, content format
├─ ViewModels/          # NoteViewModel, NotesListViewModel (+ summaries)
├─ Views/               # NoteWindow, NotesListWindow (XAML + code-behind)
├─ Services/            # repository, settings, tray, window manager,
│                       #   hotkeys, auto-start, backup (interface + impl)
└─ Utils/               # text extraction, search matcher, converters, icons
```

**Notable design points**
- **Debounced autosave** in `NoteViewModel` coalesces rapid edits before hitting the DB.
- `SaveContentAsync` deliberately preserves trash state so a closing window's stale copy can't "undelete" a note (a subtle bug this codebase guards against explicitly).
- The **`WindowManager`** owns note‑window lifetime, off‑screen correction, and rebuild‑from‑DB on import/restore.
- The tray icon is created synchronously (`ForceCreate`) to avoid an async icon path that could leave the tray empty.

## Privacy

Stickmark is **local‑first and offline**. There is no account, no network calls, and no analytics. Your notes never leave your machine unless *you* export a backup file.

## Roadmap

Ideas under consideration (contributions welcome):
- Additional Markdown themes and export styles
- Image paste / attachments
- Note grouping and reminders
- Cloud sync (optional)

## License

Released under the [MIT License](LICENSE) © 2026 BaeTab and Stickmark contributors. You're free to use, modify, and distribute it — see the LICENSE file for details.

## Acknowledgements

Thanks to the maintainers of WPF‑UI, CommunityToolkit.Mvvm, LiteDB, Serilog, H.NotifyIcon, and NHotkey — Stickmark stands on their shoulders.
