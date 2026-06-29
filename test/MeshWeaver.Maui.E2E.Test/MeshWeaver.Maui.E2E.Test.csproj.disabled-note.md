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
enable your terminal (and, when prompted on first run, `WebDriverAgentRunner` / the Appium helper).
The `mac2` driver controls the app through macOS accessibility, so this grant is required.

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
