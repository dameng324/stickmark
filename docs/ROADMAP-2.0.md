# Stickmark 2.0 Roadmap

If 1.x was an increment on top of existing features (export, multiple languages, templates, batch operations, etc.), **2.0 is not an addition of features, but a headline change that redefines the identity of the app + the foundation construction that supports it**.

## Headline: Vault & Sync

The key is the **"Vault" mode**, which places all notes as individual `.md` files in a folder. This extends the already existing “disk `.md` file opening/two-way linkage” feature to **the entire app** rather than just a single note — LiteDB remains in the index/cache role, and the source of truth becomes human-readable `.md` files.

This allows you to sync your notes folders across multiple devices simply by placing them in a cloud sync folder (OneDrive, Dropbox, etc.) or a Git repository. Of course, if you touch the same file on multiple devices, a conflict will occur, so a **conflict resolution UI** is needed.

No matter what we do, Stickmark's core DNA — **local-first, no account** — remains. Cloud synchronization is just a folder that users add themselves; there is no Stickmark server.

Alternatives/extensions being considered as candidates (not required for 2.0):
- Mobile companion app (viewer/editor that reads the same vault folder)
- Expansion into reminder and task management

## Infrastructure construction

The foundations that must be laid first to support the 2.0 headline.

### 1. Data model · Migration

**✅ Initiated.** `SchemaMigrator` (`Stickmark/Services/SchemaMigrator.cs`) has established the basis for sequential migration (v1→v2→…) based on LiteDB’s built-in `UserVersion`. If the schema version (`CurrentVersion`) expected by the app is different from the actual DB version, registered migrations are applied one by one, and if the DB is newer than the app (downgrade situation), it safely moves on without touching it.

What you need next:
- Note ↔ `.md` file round trip (metadata preserved with frontmatter)
- Stable note ID system across multiple devices/file systems
- Merge rules when synchronizing — define conflict cases where last modification first (LWW) alone is not enough

### 2. Trust · Distribution

Code signing (EV certificate + HSM) is expensive, so it is not a requirement for **2.0 and is separated into a separate track**. Instead, start with free/low-cost alternatives:
- **winget manifest** (free) — Reduces the frequency of SmartScreen first-run warnings by enabling installation with `winget install`.
- **SmartScreen guidance document** — In [`docs/INSTALL.md`](INSTALL.md), a summary of why a warning appears, why it is safe, and how to directly verify it with a hash is already provided.
- If needed later, **Microsoft Store (MSIX) deployment** — For an individual developer registration fee of approximately $19 (one-time), store signature is applied and the SmartScreen warning itself disappears.

Update integrity (SHA-256 checksum verification of download exe) is **already completed** (v1.7.0).

### 3. Quality

- Expanded VM/integration testing (`SchemaMigrator` unit test is the starting point)
- (Opt-in) Crash report collection
- Check accessibility (keyboard navigation, screen reader labels)

### 4. Encryption (optional)

Encrypt entire vault or note-by-note — an option for users who find it burdensome to have plain text `.md` in their cloud folders.

### 5. Performance

Optimized UI virtualization and indexing to ensure smooth listing/search even at scale of thousands of notes.

## steps

1. **Preparation for 1.x** — Schema Migrator ✅, other typical incremental features such as reminders and dark mode.
2. **2.0-alpha** — Introduced bolt mode, implemented Note ↔ `.md` round trip.
3. **2.0-beta** — Cloud Folders/Git sync + conflict resolution UI, (optional) encryption.
4. **2.0 official** — Existing 1.x DB → Vault automatic migration wizard, complete document revision, winget/Store distribution.
