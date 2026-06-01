---
Name: AI Model Provider Settings
Description: "Design for the Settings → Models UX: per-provider-type layout, model lists for API providers, and delegating Claude Code / GitHub Copilot to their CLI login."
---

# AI Model Provider Settings — design

The build plan for the **Settings → Models** experience. Two provider *kinds* with deliberately
**different** layouts, plus a missing icon to fix. This doc is the actionable spec; it names the
files to touch and the seams, so the implementation can proceed as a focused, testable pass.

## Goal (the spec)

| Provider kind | Examples | Settings layout |
|---|---|---|
| **API** (bring-your-own-key) | Azure AI Foundry, Azure OpenAI, Anthropic, OpenAI | endpoint/key form **+ a fetched list of models** to enable/select |
| **CLI** (co-hosted, subscription) | Claude Code, GitHub Copilot | **no model list** — show **login status**; if not logged in, a button that **delegates to the CLI's native login** |

Plus: the **AI settings tab is missing its icon**, and today **every provider renders the same**
form regardless of kind.

## Current state

- The catalog chain registers all providers (`MemexConfiguration` →
  `.AddAnthropic().AddAzureFoundry().AddAzureOpenAI().AddOpenAI().AddClaudeCode().AddCopilot()`),
  gated by the `Features:Ai:Providers:*` / `Features:Ai:Clis:*` flags.
- The Models tab (`memex/Memex.Portal.Shared/Settings/ModelsSettingsTab.cs`,
  `BuildModelsContent`) renders a single BYO-key form per provider. **Keyless CLI providers are
  currently filtered out** of that form — so Claude Code / Copilot have no UI at all.
- Per-user credentials are `ModelProvider` nodes (`ModelProviderService`,
  `ModelProviderNodeType`), keys encrypted with `Ai:KeyProtection:MasterKey` (now in Key Vault).
- The CLI binaries ship in the `memex-portal-ai` image; the per-user **Connect** flow is only
  scaffolded (`src/MeshWeaver.AI/Connect/ConnectModels.cs`) — no session manager, no
  login-status check, no UI.

## Design

### 1. A provider *kind* seam
Expose, on the provider catalog entry, an explicit `ProviderKind` (`Api` | `Cli`) instead of the
implicit "has a key form" test. CLI providers (`AddClaudeCode`, `AddCopilot`) report `Cli`; the
rest `Api`. `ModelsSettingsTab` switches the rendered card on `ProviderKind` — this is the one
branch that drives the whole different-layout requirement.

### 2. API providers — key/endpoint form + model list
- Render the existing endpoint/key form (Azure providers also take an **endpoint**; Anthropic/OpenAI
  just a key).
- After a key is saved + validated, **fetch the provider's model list** (the provider's
  `IChatClientCatalog`/list-models call) and show them as selectable rows so the user enables the
  models they want. Persist the selection on the `ModelProvider` node.
- This is the only kind that produces a model list (per the spec, CC/GH do **not**).

### 3. CLI providers — login status + delegate to the CLI
No key form, **no model list**. The card shows:
- **Logged in** → a green "Connected as …" state + a "Re-connect / Log out" affordance.
- **Not logged in** → a **"Connect / Log in"** button that drives the CLI's native login.

Mechanism (Phase 1, to build under `src/MeshWeaver.AI/Connect/`):
- **`IConnectStrategy`** per CLI. `ClaudeConnectStrategy`: spawn `claude` under the user's
  `CLAUDE_CONFIG_DIR`, **check login status** (probe the CLI / its `.credentials.json`), and if
  absent run `claude setup-token` (or `/login`), scrape the auth URL → surface it → capture the
  pasted code/token. `CopilotConnectStrategy`: the Copilot SDK device-flow (surface the device URL +
  code, poll `GetAuthStatusAsync`). Reuse the subprocess shape from `MeshPlugin`/`KernelExecutor`
  (`RedirectStandardInput`); `Observable.FromAsync` only at the process boundary.
- **`ConnectSessionManager`** — a mesh-scoped singleton holding the live `Process` between
  "show URL" and "paste code", per user (instance `ConcurrentDictionary`, **never static**), 5-min
  timeout + `Kill(entireProcessTree:true)`.
- **Login-status check** is the cheap, always-run part the spec asks for: each card calls
  `strategy.IsLoggedIn(userConfigDir)` on render; only the not-logged-in branch shows the login button.
- Captured token → `ModelProviderService.CreateProvider(ownerPath, "ClaudeCode"|"Copilot", token)`
  (already `Protect()`s the key); re-connect = `RotateKey`. The CLI agent factory already injects it.

### 4. The missing icon
The AI settings tab is registered without an `Icon`. Add one where the Settings tabs are declared
(the AI/Models tab registration in the portal settings) — e.g. a FluentUI `Sparkle`/`Bot` icon —
consistent with the other tabs' icon usage.

## UI — inline login (chosen)

The CLI login expands **inside the provider card** (no modal, no panel) — lightest weight, stays in
context. The card is a small state machine.

### Models tab
```
Settings ▸ ✦ AI / Models

API providers — add a key, choose models
┌─ Azure AI Foundry ──────────────────────────────── [API] ┐
│ Endpoint  https://….services.ai.azure.com   [Save]  ✓     │
│ API key   ••••••••••••••••                                 │
│ Models    ☑ gpt-4o   ☑ o3-mini   ☐ cohere-embed-v-4-0      │
└────────────────────────────────────────────────────────────┘
┌─ Anthropic ─────────────────────────────────────── [API] ┐
│ API key  •••••••• [Save]    Models  ☑ claude-opus-4         │
└────────────────────────────────────────────────────────────┘

CLI providers — log in with your subscription (no key, no model list)
┌─ Claude Code ───────────────────────────────────── [CLI] ┐
│ ● Not connected — uses your Claude subscription            │
│                                  [ Connect Claude Code ]   │
└────────────────────────────────────────────────────────────┘
┌─ GitHub Copilot ────────────────────────────────── [CLI] ┐
│ ✓ Connected as @rbuergi                    [ Disconnect ]  │
└────────────────────────────────────────────────────────────┘
```

### CLI card states
**Connecting — Claude (paste-a-code):**
```
┌─ Claude Code ──────────────── [CLI] ┐
│ ● Connecting…                       │
│ 1  Authorize in your browser:       │
│    claude.ai/oauth/auth?…  [Copy][Open]
│ 2  Paste the code Claude shows:     │
│    [ __________________ ] [Submit]  │
│    ⏳ waiting for code… (4:58)       │
└──────────────────────────────────────┘
```
**Connecting — Copilot (device code, auto-poll, nothing to paste):**
```
┌─ GitHub Copilot ───────────── [CLI] ┐
│ ● Connecting…  enter code at         │
│   github.com/login/device            │
│        ┌───────────────┐             │
│        │  AB12-CD34     │  [Copy]     │
│        └───────────────┘             │
│   ⏳ auto-checking…                   │
└──────────────────────────────────────┘
```
**Connected:** `✓ Connected as <name>   [ Disconnect ]`  ·  **Error/Expired:** red line + `[ Retry ]`.

### Inline state machine
```
NotConnected ──[Connect]──▶ Connecting ──(code submitted / device poll OK)──▶ Connected
     ▲                          │  ▲                                              │
     └──────[Disconnect]────────┘  └──(5-min timeout · Cancel · auth error)──▶ Error/Expired
                                            └──────────────[Retry]──────────────────┘
```
- **Connecting** is driven by `ConnectSessionManager` + the provider's `IConnectStrategy`: it holds
  the live CLI `Process`, exposes the auth URL (+ device code for Copilot), and either accepts a
  pasted code (Claude, `RequiresPastedCode`) or polls auth status (Copilot). 5-min timeout disposes
  the process (`Kill(entireProcessTree:true)`).
- **On success** the strategy hands back the token → `ModelProviderService.CreateProvider/RotateKey`
  (encrypted via the Key-Vault-sourced master key) → the card flips to **Connected** reactively.
- The card renders the right inline body off `strategy.RequiresPastedCode` (paste field vs device
  code) and `IsLoggedIn(userConfigDir)` (Connected vs NotConnected on first render).

## Files to touch
- `memex/Memex.Portal.Shared/Settings/ModelsSettingsTab.cs` — switch on `ProviderKind`; API card
  (form + model list), CLI card (login status + connect button); fix the tab icon.
- AI catalog entries / `src/MeshWeaver.AI.ClaudeCode/ClaudeCodeExtensions.cs` +
  `src/MeshWeaver.AI.Copilot/*` — expose `ProviderKind = Cli` + an `IConnectStrategy`.
- `src/MeshWeaver.AI/Connect/` (new) — `ConnectSessionManager`, `IConnectStrategy`,
  `ClaudeConnectStrategy`; `src/MeshWeaver.AI.Copilot/CopilotConnectStrategy.cs`.
- `Models/ModelProviderService.cs` — already supports create/rotate; add an `IsCli` projection if
  needed for the list-vs-connect filter.
- `MemexConfiguration.cs` — register `ConnectSessionManager` + the strategies.

## Testing (no mocks; `MonolithMeshTestBase` / `AITestBase`)
- `ModelsSettingsTab` renders a **model list** for an API provider and a **connect button** (no list)
  for a CLI provider (assert on the control tree).
- A committed **fake CLI** (prints an auth URL, reads stdin, prints a token) drives
  `IConnectStrategy`: `IsLoggedIn` false → connect → captures the token → `ModelProvider` written with
  an `enc:`-tagged key that round-trips through `ChatClientCredentialResolver`.
- Login-status: with the fake CLI "logged in", the card renders the connected state and **no** login
  button. Real-CLI E2E gated by `CLAUDE_CONNECT_E2E=1` (developer-run only).

## Scope note
This is **Phase 1 (per-user CLI Connect) + the Models-tab rework** — a focused build (UI + the CLI
login backend), then a clean image rebuild + redeploy. The icon + the `ProviderKind` layout split are
the quick visible wins; the CLI login backend is the substantive part.
