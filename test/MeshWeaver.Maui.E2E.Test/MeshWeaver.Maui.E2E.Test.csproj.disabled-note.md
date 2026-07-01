# MAUI native E2E (Appium) — how to run

These tests drive the **running maccatalyst app** via Appium's `mac2` driver — the native
equivalent of the Blazor portal's Playwright suite. They are **skipped automatically** unless a
local Appium server is reachable at `http://127.0.0.1:4723`, so a normal `dotnet test` / CI run
is unaffected.

## One-time setup
```bash
brew install node                 # if missing
npm install -g appium             # Appium server
appium driver install mac2        # macOS native driver (builds WebDriverAgentMac via Xcode)
```
Then grant **Accessibility** permission: System Settings → Privacy & Security → Accessibility →
enable **the terminal app you start `appium` from** (Terminal.app / iTerm), and, when prompted on
first run, `WebDriverAgentRunner`. The `mac2` driver controls the app through macOS accessibility,
so this grant is required.

> **Must be run from your own Terminal.** Verified on Xcode 26.5: WebDriverAgentMac *builds* fine,
> but the first session fails with `Failed to initialize for UI testing … Timed out while enabling
> automation mode.` That timeout IS the Accessibility gate — the XCUITest runner inherits the TCC
> identity of the launching terminal, so the grant only takes effect when Appium is launched from a
> terminal you've added to Accessibility. (An Appium server spawned by another tool/agent can't
> receive this grant, so run the three steps below yourself.)

## Run
```bash
# 1. build the app
dotnet build memex/Memex.Client/Memex.Client.csproj -f net10.0-maccatalyst -c Debug
# 2. start the Appium server (separate terminal)
appium
# 3. point the tests at the built app (optional; a sensible default is used) and run
export MEMEX_APP_PATH="$PWD/memex/Memex.Client/bin/Debug/net10.0-maccatalyst/maccatalyst-arm64/Memex.Client.app"
dotnet test test/MeshWeaver.Maui.E2E.Test
```

Selectors are MAUI `AutomationId`s: `mesh-search` (shell search box), `chat-composer`,
`chat-send` (the agent chat). Add more `AutomationId`s as the suite grows.
