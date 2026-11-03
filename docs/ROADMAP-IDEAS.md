# Stickmark Roadmap Ideas (as of 2026-07-06, v2.2.2)

Organize candidates for feature addition, supplementation/improvement, and UI/UX advancement in one place.
Notation: ⭐ Recommended (excellent value/effort ratio) · 🔥 High impact · 🧪 Requires verification (risk) · 🎈 Light (less than half a day)

---

## 1. Add features

### 1.1 Content Enhancement
| Idea | Description | Remarks |
| --- | --- | --- |
| ⭐🔥 Paste Image/Attachment | Paste image into note → Save bolt `./attachments/` + insert `![](...)` | Large blank space in Notes app, Obsidian compatible |
| Table Editing Assistant | Insert table/Add row/column button (current table is only source) | WYSIWYG follow-up |
| 🎈 Code block syntax highlighting | Highlighting the ```lang block in the preview | Markdig extension |
| Formulas/Diagrams | KaTeX (formula)·Mermaid (flowchart) offline render | For power users |
| Hand-drawn notes | Free Canvas Sketch Note Type | Large scale |

### 1.2 WYSIWYG completeness (continued from v2.1~2.2)
| Idea | Description | Remarks |
| --- | --- | --- |
| ⭐ Clickable checkbox | `-[ ]` toggle by clicking (currently text) | Editor/Preview Both |
| `/` slash command | When entering `/`, insert title, list, table, and code menu (Notion style) | CM6 autocomplete |
| ⭐🎈 Automatic continuation of list | Enter in list → Auto next item, turn off if empty item | Feels great |

### 1.3 Navigation · Connection
| Idea | Description | Remarks |
| --- | --- | --- |
| ⭐🔥 Quick Switcher (Ctrl+P) | Jump to notes instantly with title fuzzy search | The more notes, the more essential |
| Backlink Panel | "Notes linked to this note" list (using the existing `[[Title]]`) | |
| Tag Management | Change/merge tag names | |
| Folder/Notebook | Group notes | Change data model (using schema migrator) |

### 1.4 Things to do · Time
| Idea | Description | Remarks |
| --- | --- | --- |
| ⭐🔥 Integrated to-do view | View/check `- [ ]` of all notes on one screen | Recycle existing checkboxes |
| Reminder/Due Date | Assign date to note → Tray notification | Local Scheduling |
| Date Token Agenda | Body `@2026-07-10` recognition → Upcoming schedule list | |
| Daily Note/Journal | Automatic creation of one photo per day + date calendar view | Habit formation (retention) |

### 1.5 Capture · Input
| Idea | Description | Remarks |
| --- | --- | --- |
| ⭐🔥 Global fast capture | Shortcut anywhere → Mini input window → New note (automatic selection text/clipboard) | Explosion in usage frequency |
| Capture screen area | Drag part of the screen → Image note | Prerequisite for pasting images |
| 🎈 Tray Drop | Drop text/file into tray icon → Create note | |

### 1.6 Sharing · Interoperability
| Idea | Description | Remarks |
| --- | --- | --- |
| 🎈 Rich Copy | Copy notes as rich text/HTML (maintain formatting in email/Word) | |
| Single HTML export | Entire note in one self-contained HTML sheet | |
| 🎈 Open with Obsidian/VS Code | Button to open vault folder with external tool | |
| Share notes as images | Convert notes to Sticky Card PNG (SNS/Messenger) | Combined with fun elements |
| Transfer to phone with QR | Display note contents as QR | |
| LAN peer synchronization | Direct synchronization between PCs on the same network | Large scale, can be replaced with cloud folders |

### 1.7 Context awareness
| Idea | Description | Remarks |
| --- | --- | --- |
| App linkage display | Notes (coding cheat sheets, etc.) that appear only when a specific app is activated | Point of differentiation |
| 🎈 Follow Active Monitor | Create a new note on the current monitor | |
| 🎈 Keyboard note cycling | Move the focus of an open note with a shortcut key | |

### 1.8 Trust · Data
| Idea | Description | Remarks |
| --- | --- | --- |
| Lock Note (Encrypted) | Local encryption of specific notes | Strengthening privacy strengths |
| Version History | Automatically commit the vault to the git repository → History/Restore | Vault Mode Synergy |
| Automatic backup schedule | Periodic backup + storage count management | |
| 🎈 Read-only lock | Avoid accidental editing (lighter than encryption) | |
| Duplicate note detection | Notify/merge similar notes | |

### 1.9 Fun · Emotion
| Idea | Description | Remarks |
| --- | --- | --- |
| 🎈 Completed Firecracker | confetti when checking the last to-do | |
| Crumple and Throw Delete | When deleted, it crumbles into a paper ball and flies away | |
| Note Aging | Notebooks that haven't been touched in a long time are turning yellow | Subtly useful (neglect detection) |
| Wash Tape/Skin | Masking tape decoration, paper texture, seasonal direction | |
| Countdown Note | D-day flip clock | |
| sound | Paper flipping/pen square sound effect (toggle) | |

---

## 2. Supplementation/improvement (Engineering)

### 2.1 Performance (Measured on v2.2.2)
- Measurement results: The app itself is ~31MB (light), the main memory culprit is the WebView2 renderer (30–60MB) per note, and the 76MB download is the self-contained .NET runtime.
- ✅ Completed: Hidden Notes WebView2 `MemoryUsageTargetLevel=Low` (v2.2.2)
- 🧪 **Delayed restoration of bulk notes** — Instead of creating the previous notes window at startup, hidden notes are created when needed. The effect is great, but it changes the window life cycle → requires mass verification of harness.
- 🧪 **Download Lighter** — Framework-dependent deployment (76MB→~2MB). Requires user to install the .NET 10 runtime → Must be combined with InnoSetup runtime verification logic. Need to decide on distribution method.
- Review of WebView2 environment sharing — Reduce the number of browser processes by sharing UDF/environment between notes (UDF is already being shared, review explicit sharing of environment objects).

### 2.2 Distribution · Trust
- 🔥 **Code Signing** — SmartScreen “unknown publisher” resolution. MS Store ($19 one-time) or Azure Trusted Signing (small monthly fee). *(Wait for user decision)*
- **Automated winget manifest updates** — Not updated since `packaging/winget/1.7.0` (currently 2.2.2). From release CI to automatic manifest generation/PR.
- InnoSetup installation version — Installation option other than single exe (+ Start menu/uninstallation support). Lightweight (framework dependency) and natural combination.
- Updated UX — Current download + SHA-256 verification. Room for improvement in auto-run/apply steps after download.

### 2.3 Vault Mode Sequel
- 🔥 **Real-time folder monitoring** — Automatic reflection of external changes (currently requires restart, planned for v2.1). FileSystemWatcher + Debounce + Conflict Policy.
- Vault conflict handling — Cloud conflict copy (`-conflict`) detection and notification.
- Show vault status — Show current storage mode/vault path in tray/list.

### 2.4 Testing/Diagnosis
- UI auto-smoke — Promote demo harness (isolated DB/separate mutex) to auto-smoke for CI (including single file publish validation, 2.1.0 regression lesson learned).
- Bulk note load testing — 500-1000 note scenarios.
- Export diagnostics — log + environment summary zip at once (for reporting issues).

### 2.5 Accessibility / Internationalization follow-up
- Keyboard-only navigation overhaul (tab order and focus display), high-contrast theme support.
- Multilingual expansion (Japanese, etc.) — resx structure is ready.

### 2.6 Security Follow-up (recommended docs/SECURITY-REVIEW.md)
- Instantly organize temporary files and clean them at startup.
- Import (backup/vault) size and number upper limit.
- Opt-in for live preview remote images.

---

## 3. UI/UX advancement

### 3.1 Appearance · Theme
| Idea | Description | Remarks |
| --- | --- | --- |
| ⭐🔥 App-wide dark theme | Lists, settings, etc. Even app chrome is dark (current note color only) | Most requested basics |
| Windows 11 Style | Mica background, rounded corners, and snap layout support | WPF-UI package already referenced |
| Select note font | Font (including handwriting) and size presets for each note | |
| grid snap | Snap alignment when moving notes | 🎈 |

### 3.2 Note Window UX
| Idea | Description | Remarks |
| --- | --- | --- |
| ⭐ Roll up notes | Double-click the header → Collapse leaving only the title bar | Clean up your screen, 🎈 |
| Title inline editing | Click on the title of the header to edit it immediately (leading to the first line of the current body) | |
| edge docking | Hide slide/show hover when attached to the end of the screen | |
| Desktop widget mode | Freeze background + click-through | Sticky Identity 🔥 |
| size preset | Small/Medium/Large One Click | 🎈 |

### 3.3 List window UX
| Idea | Description | Remarks |
| --- | --- | --- |
| Preview panel | Preview the render on the right when selecting from the list | |
| Sort Options | In addition to the order of modification, order of creation/title/color | 🎈 |
| Pinboard view | Placement/drag arrangement like a corkboard | |
| list virtualization | Scrolling Performance at Hundreds of Notes | 2.1 Performance and connectivity |

### 3.4 Onboarding · Help
| Idea | Description | Remarks |
| --- | --- | --- |
| ⭐ First Run Tour | Function introduction with 3 sample notes (mode, tag, bolt) | Preventing new user churn |
| What's New | Show new feature highlights after update 1 time | 🎈 CHANGELOG Recycle |
| Shortcut Cheat Sheet | `?` or shortcut key in tray | 🎈 |

### 3.5 Cleanup · Minor touches
- Tray menu grouping (more items — into Backup/Vault/Tools submenus).
- Settings window tab structure (General/Shortcuts/Storage/Information) + Version/link in the Information tab.
- Owner/focus consistency of note deletion confirmation dialog (2.0.1 DialogOwner follow-up inspection).

---

## 4. Recommended next step (suggest combination)

1. **"One more inch of editor"** — Automatically concatenate list + click checkbox + code highlight (complete WYSIWYG experience)
2. **"Evolved into a hub"** — Unified to-do view + quick switcher (Ctrl+P) (catches users with more notes)
3. **"First Impressions"** — App-wide dark theme + First-run tour + What's New (new/returning users)
4. **"Capture Machine"** — Global Quick Capture + Tray Drop + Paste Image (Toolize Collection)
5. **"Vault Completion"** — Real-time folder monitoring + conflict notification + opening with Obsidian (finalizing 2.x Kanban features)
