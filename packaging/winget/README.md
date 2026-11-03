# winget manifests (Stickmark)

This directory is reserved for [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs) manifests.

Stickmark is distributed as an **Inno Setup installer**:

- PackageIdentifier: `dameng324.Stickmark`
- InstallerType: `inno`
- Expected asset: `Stickmark-vX.Y.Z-win-x64-setup.exe`
- Checksum asset: `Stickmark-vX.Y.Z-win-x64-setup.exe.sha256`

## Publishing a new version

After a GitHub Release exists with both installer assets, generate the manifest from the release URL:

```powershell
wingetcreate update dameng324.Stickmark `
  --version <version> `
  --urls https://github.com/dameng324/stickmark/releases/download/v<version>/Stickmark-v<version>-win-x64-setup.exe `
  --submit
```

`wingetcreate` downloads the installer, computes its SHA-256, updates the three manifest files, and can open the PR automatically. Verify the generated SHA-256 against the release `.sha256` asset before submitting.

## Manifest layout

winget-pkgs stores manifests at:

```
manifests/<first letter of publisher>/<Publisher>/<PackageName>/<Version>/
  dameng324.Stickmark.yaml
  dameng324.Stickmark.installer.yaml
  dameng324.Stickmark.locale.en-US.yaml
```

Validate locally before submitting:

```powershell
winget validate --manifest <manifest-directory>
winget install --manifest <manifest-directory>
```
