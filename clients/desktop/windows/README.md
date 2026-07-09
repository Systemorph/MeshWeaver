# MeshWeaver — Windows desktop app

The Windows twin of the macOS app (`clients/desktop/macos`): a native **WebView2** shell that bundles
the **LocalMesh** (the in-process SQLite "monolith" mesh) as a sidecar and shows the packaged
React-Native web UI. Same model as macOS — one shared self-contained backend, a platform-native window.

- Starts `localmesh\Memex.LocalMesh.exe` (self-contained; no .NET needed on the box) on launch.
- Creates a **fresh SQLite database on first launch** under `%LOCALAPPDATA%\Memex`.
- Serves the UI on `http://localhost:5250`; the WebView2 window loads it and **auto-grants the mic** so
  speech/dictation works.
- Kills the mesh child process on close.

## Build (on Windows)

Prerequisites: **Windows 10/11**, the **.NET 10 SDK**, **Node** (for the web export), and the
**WebView2 Evergreen runtime** (preinstalled on Windows 11).

```powershell
pwsh clients\desktop\windows\build.ps1
# → clients\desktop\windows\dist\MeshWeaver\MeshWeaver.exe
```

`build.ps1` exports the RN web UI into `wwwroot`, publishes LocalMesh self-contained (`win-x64`), builds
the WebView2 shell, and lays out `dist\MeshWeaver\`.

> This project targets `net10.0-windows` (WinForms + WebView2), which only builds on Windows with the
> Windows Desktop SDK — so it is intentionally **not** part of `MeshWeaver.slnx` and the Linux/macOS CI
> does not build it. The **backend** (`Memex.LocalMesh` win-x64 self-contained) cross-publishes from any
> OS and is verified in CI.

## Publish / distribute

1. **Code-sign** `MeshWeaver.exe` (and the installer) with an Authenticode certificate:
   `signtool sign /fd SHA256 /a /tr http://timestamp.digicert.com /td SHA256 MeshWeaver.exe`
   (An **EV** cert clears SmartScreen immediately; an **OV** cert builds reputation over time.)
2. **Package** as **MSIX** (Store-ready; declare the `microphone` capability in the manifest) or an
   MSI/Inno Setup installer (chain the WebView2 bootstrapper for Windows 10 targets).
3. **Distribute** via direct download or the **Microsoft Store** (Partner Center).

App icon: drop an `AppIcon.ico` here and add `<ApplicationIcon>AppIcon.ico</ApplicationIcon>` to the csproj.
