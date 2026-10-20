# Build and install Foldex

## Requirements

* Windows 10 1809 or later, x64.
* .NET SDK 10.0.100 or later (`global.json` rolls forward to the latest feature band). On a machine without it, install
  it per user (no administrator rights needed):

  ```powershell
  Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile $env:TEMP\dotnet-install.ps1
  & $env:TEMP\dotnet-install.ps1 -Channel 10.0 -InstallDir "$env:LOCALAPPDATA\Microsoft\dotnet"
  $env:PATH = "$env:LOCALAPPDATA\Microsoft\dotnet;$env:PATH"; $env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"
  ```

* Internet access on the first installer build: the script downloads Inno Setup 7.1.0 (pinned SHA-256) into
  `build/.cache` and the small bundled engines rhwp and 7-Zip (pinned, verified) — the same as Filee's release CI.

## Build and test

```powershell
dotnet build Filee.slnx -c Release
dotnet test tests/Filee.App.Tests -c Release          # UI tests (headless Avalonia, writes screenshots)
dotnet test Filee.slnx -c Release --filter "FullyQualifiedName!~Filee.App.Tests"
```

Screenshots of every page land in `tests/Filee.App.Tests/bin/Release/net10.0/screenshots`.

Run from source without touching your settings (a separate data folder; also gives a separate single instance):

```powershell
$env:FILEE_DATA_DIR = "$env:TEMP\filee-studio-dev"; dotnet run --project src/Filee.App -c Release
```

## Build the installer

From the repository root, in Windows PowerShell or PowerShell 7:

```powershell
.\build\build-installer.ps1 -Portable
```

This publishes `Foldex.exe` (the app; built as Filee.dll) and `filee-cli.exe` (Release, self-contained, ReadyToRun, `FileeRelease=true`) into
`publish\win-x64`, adds rhwp and 7-Zip under `engines\`, and compiles `installer\Filee.iss`. Output in `Releases\`:

| File | What |
|---|---|
| `Foldex-1.0.0-win-Setup.exe` | The installer |
| `Foldex-1.0.0-win-Portable.zip` | The same app without installing (separate build artifact for testing; unzip and run `Foldex.exe`) |
| `Foldex-1.0.0-SHA256SUMS.txt` | Checksums |

The version comes from `<Version>` in `Directory.Build.props` (1.0.0); `-Version x.y.z` overrides it.
The files are **not code-signed**.

## Install, use, uninstall

* Run `Foldex-1.0.0-win-Setup.exe` (one UAC prompt: it installs for all users into
  `C:\Program Files\Foldex`). Options: Explorer "Convert with Foldex" verb, start with Windows, desktop icon,
  `foldex` command on PATH (off by default), optional engines to download.
* Start menu: **Foldex**. The app lives in the notification area; the window opens from the tray or the shortcut.
* Settings: `%APPDATA%\Foldex`. Downloaded engines: `%LOCALAPPDATA%\Foldex\engines`. Logs: in the settings
  folder.
* Uninstall from Settings → Apps → *Foldex*. The uninstaller removes the Explorer verb, Send To shortcut, startup
  entry and downloaded engines; settings stay (delete `%APPDATA%\Foldex` to remove them).
* Upgrades: a later Foldex installer with the same AppId (`5B0E3E57-9C1A-4E0B-A7E4-2F6C1D8B9A31`) updates in place
  and keeps the user's choices.

## Coexistence with Filee and rollback

Foldex and Filee are separate apps for Windows: different AppId, install folder, settings, engines folder,
single-instance name, Explorer verb, Send To shortcut and startup entry. Installing or uninstalling one doesn't change
the other. Foldex's installer never takes over Filee's old per-user (Velopack) copy and never registers or removes
Filee's Windows 11 menu package.

Rollback: uninstall *Foldex* from Settings → Apps. Filee keeps working as before. The source baseline is commit
`49911afab6eef81f144543710f1aa8021c617ff1` (`main`); all changes are on branch `feature/pdf-workspace`.
