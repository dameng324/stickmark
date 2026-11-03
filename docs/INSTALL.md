# Install & verify

## Installation

Download and run the latest `Stickmark-vX.Y.Z-win-x64-setup.exe` from the [**Releases**](https://github.com/dameng324/stickmark/releases) page. The installer is self-contained, so there is no need to install a separate .NET runtime. It installs per-user under `%LocalAppData%\Programs\Stickmark`.

## Windows SmartScreen warning

### Why does it appear?

Stickmark is an open source app that does not yet use a (paid) code signing certificate. So when you download an executable and run it for the first time, Windows may pop up a “Windows has protected your PC” / “Unknown publisher” warning. **This does not mean it is a virus** — it just means that the distributor did not purchase a signing certificate.

### Why it's safe

- **Open Source** — All source code is open, so anyone can see what is running.
- **Local First** — No accounts, no cloud transfers, no usage analytics (telemetry) collection. Notes are only saved in `%LocalAppData%\Stickmark\` on your PC.
- **Verified Auto-Update** — Auto-update matches received files against published **SHA‑256 checksums** and applies only if they match (rejects application if mismatch/missing).

### How to proceed

When the SmartScreen window opens:

1. Click **“More info”**.
2. Click the **“Run anyway”** button.

## Directly check download integrity

With each release, the `...exe.sha256` asset is published along with the executable. To manually check that the exe you received has not been corrupted or tampered with, calculate the hash of the file and compare it to the published value.

**PowerShell**
```powershell
Get-FileHash .\Stickmark-vX.Y.Z-win-x64-setup.exe -Algorithm SHA256
```

**cmd**
```cmd
certutil -hashfile Stickmark-vX.Y.Z-win-x64-setup.exe SHA256
```

If the output hash value is **identical** to the value written in the `.sha256` file of the same release, the file is genuine and undamaged.

---

## English (summary)

Stickmark isn't code-signed with a (paid) certificate yet, so Windows SmartScreen may flag the downloaded exe as coming from an "unknown publisher" on first run. **This does not mean it's malware** — it just means the publisher hasn't purchased a signing certificate. Stickmark is open source, local-first (no accounts, no cloud upload, no telemetry), and its auto-updater only applies an update after verifying it against a published SHA-256 checksum. To proceed, click **"More info" → "Run anyway"** in the SmartScreen dialog. To verify the download yourself, compare its hash against the published `.sha256` file:

```powershell
Get-FileHash .\Stickmark-vX.Y.Z-win-x64-setup.exe -Algorithm SHA256
```
```cmd
certutil -hashfile Stickmark-vX.Y.Z-win-x64-setup.exe SHA256
```
