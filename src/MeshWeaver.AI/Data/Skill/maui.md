---
nodeType: Skill
name: /maui
description: Build a feature in the Memex MAUI client — the native cross-platform app (iOS/Android/macOS/Windows) that hosts the portal, on-device voice, and a SignalR mesh participant
icon: DeviceMobile
category: Skills
order: 20
---

You are building a feature in the **Memex MAUI client** at `memex/Memex.Client` — one .NET MAUI Blazor Hybrid project, multi-targeted (iOS, Android, macOS/Mac Catalyst, Windows). It hosts the MeshWeaver **portal** (WebView), **on-device voice** (Whisper), and joins the mesh as a **SignalR participant**. Read [Data Binding in a MAUI Client](/Doc/GUI/DataBindingMaui) before touching mesh data.

# 1. The project is insulated from the repo build on purpose

`memex/Memex.Client` has its own `Directory.Build.props` (empty) and `Directory.Packages.props` (`ManagePackageVersionsCentrally=false`). That stops the repo-root props — which force a single `<TargetFramework>net10.0</TargetFramework>` and Central Package Management — from breaking MAUI's multi-targeting and the template's inline `$(MauiVersion)` versions. **Keep those stubs.** It is NOT in `MeshWeaver.slnx` (the framework CI has no MAUI workloads); build it on its own.

# 2. Mesh data: bind, never copy

Read with `hub.GetMeshNodeStream(path)`, write with `.Update(...)` — the same single-source-of-truth rule as Blazor, marshalling UI updates with `MainThread.BeginInvokeOnMainThread`. Full pattern + the worked `MemexClient` example: [Data Binding in a MAUI Client](/Doc/GUI/DataBindingMaui). The one local store is the **bootstrap** (installation id + first portal URL + token in `SecureStorage`); everything else is a node.

- Resolve the participant hub from DI: it's registered via `AddMessageHubs(CreatePortalAddress(id), c => c.UseSignalRClient(url, accessTokenProvider: () => SecureStorage.Default.GetAsync("mesh.token")))`.
- Config lives on the **`MemexClient`** node at `{user}/Client/{installationId}` (`MemexClientNodeType.PathFor`). Create it once (`meshService.CreateNode`); bind from then on.
- Ordinary `async/await` is fine for app concerns; mesh reads/writes stay `IObservable` + `Subscribe`.

# 3. Auth — the participant must carry identity

Writes only pass RLS if the SignalR connection is authenticated. The client sends the API token via `AccessTokenProvider`; the server validates it and stamps every injected message. No token ⇒ Anonymous ⇒ writes denied. See [SignalR Mesh Participant](/Doc/Architecture/SignalRMeshParticipant) → Identity.

# 4. Voice (on-device)

Whisper runs locally via `Whisper.net` (ships native libs for iOS incl. Metal/GPU, Android, macOS, Windows). Mic capture is `Plugin.Maui.Audio` at 16 kHz mono; it also has silence/VAD listeners for record-until-silence. Constrain auto-detect to `{de, en}` so Swiss German routes to German. Speaker diarization (`sherpa-onnx`) has **no iOS NuGet runtime** — keep it server-side or add an xcframework. A true wake word needs a wake-word engine (Picovoice Porcupine, custom keyword); otherwise tap-to-talk.

# 5. UI gotchas (hard-won)

- **`BlazorWebView` renders blank inside a `TabbedPage`/`FlyoutPage` detail on several platforms.** App-level chrome (URL switcher, settings, voice toggle) belongs in a native flyout/menu OR an in-app browser (`Browser.Default.OpenAsync(url, BrowserLaunchMode.SystemPreferred)`) — keep the BlazorWebView a single page.
- **No `.Take(1)`** on a stream feeding a live view (freezes it).
- Surface errors in the UI (`status` text); a failed hub resolve/connect must not blank the page — guard it so on-device features still work offline.

# 6. Build & ship

- Verify locally on the **Windows head**: `dotnet build memex/Memex.Client -f net10.0-windows10.0.19041.0 -t:Run`. The managed compile also runs for `-f net10.0-ios` on Windows, but the **AOT/native link + signing happen only on macOS**.
- iOS device install = **TestFlight**: the `.github/workflows/memex-ios-testflight.yml` workflow builds on a macOS runner and uploads (needs Apple Developer Program + signing secrets). That CI run is the first real iOS AOT link — fix any trimming/AOT there (`<MtouchLink>`, `[DynamicDependency]`).

# 7. Verify

Windows + iOS heads compile (`0 Error(s)`). For a mesh feature, confirm the bound node round-trips: edit on the device → the change shows on the same node in the portal (and vice-versa) — proof the binding, not a local copy, is the store.
