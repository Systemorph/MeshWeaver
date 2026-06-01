---
Name: AI Model Provider Settings
Description: "Settings → Models UX: per-provider-type layout, model lists for API providers, and delegating Claude Code / GitHub Copilot to their CLI login."
---

# AI Model Provider Settings

The **Settings → Models** page is the user's single destination for wiring AI into Memex — adding API keys, enabling specific models, and connecting CLI-based providers like Claude Code and GitHub Copilot. This document is the actionable implementation spec: it identifies the exact files to touch, the behavioral seams to introduce, and the testing approach.

---

## Two provider kinds, two different UIs

The fundamental insight driving this design: API providers and CLI providers need **completely different layouts**. Rendering both as a key/endpoint form is wrong.

| Provider kind | Examples | What the card shows |
|---|---|---|
| **API** (bring-your-own-key) | Azure AI Foundry, Azure OpenAI, Anthropic, OpenAI | Endpoint / key form **+ a fetched list of models** to enable |
| **CLI** (co-hosted, subscription) | Claude Code, GitHub Copilot | **Login status** — no key form, no model list; a button that delegates to the CLI's own auth flow |

Additionally: the AI settings tab is currently **missing its icon**, which this spec fixes.

---

## Current state

Before building, understand what is already wired:

- The catalog chain registers all providers in `MemexConfiguration` via `.AddAnthropic().AddAzureFoundry().AddAzureOpenAI().AddOpenAI().AddClaudeCode().AddCopilot()`, each gated by its `Features:Ai:Providers:*` / `Features:Ai:Clis:*` flag.
- `memex/Memex.Portal.Shared/Settings/ModelsSettingsTab.cs` (`BuildModelsContent`) renders a **single BYO-key form** for every provider. CLI providers (Claude Code, Copilot) are currently **filtered out** — they show no UI at all.
- Per-user credentials live in `ModelProvider` mesh nodes (`ModelProviderService`, `ModelProviderNodeType`), with keys encrypted via `Ai:KeyProtection:MasterKey` (sourced from Key Vault).
- The CLI connect flow is scaffolded in `src/MeshWeaver.AI/Connect/ConnectModels.cs` but is not yet implemented — no session manager, no login-status check, no UI.

---

## Design

### 1  Introduce a `ProviderKind` seam

Add an explicit `ProviderKind` enum (`Api` | `Cli`) to the provider catalog entry instead of relying on the implicit "has a key form" test. CLI providers (`AddClaudeCode`, `AddCopilot`) report `Cli`; everything else reports `Api`. `ModelsSettingsTab` then switches the rendered card on `ProviderKind` — this single branch drives the entire different-layout requirement.

### 2  API providers — key/endpoint form + model list

- Render the existing endpoint/key form. Azure providers also take an **endpoint**; Anthropic and OpenAI take only a key.
- After a key is saved and validated, **fetch the provider's model list** (via its `IChatClientCatalog` list-models call) and show the models as selectable rows so the user enables the ones they want. Persist the selection on the `ModelProvider` node.
- API providers are the **only** kind that produce a model list — CLI providers deliberately do not.

### 3  CLI providers — login status + delegate to the CLI

No key form. No model list. The card shows exactly two states:

- **Logged in** → green "Connected as …" indicator + a "Re-connect / Log out" affordance.
- **Not logged in** → a **"Connect / Log in"** button that drives the CLI's native auth flow.

The backend lives in `src/MeshWeaver.AI/Connect/`:

**`IConnectStrategy`** — one implementation per CLI.
- `ClaudeConnectStrategy`: spawns `claude` under the user's `CLAUDE_CONFIG_DIR`, probes the CLI (or its `.credentials.json`) for login status, and if absent runs `claude setup-token` (or `/login`), scrapes the auth URL, surfaces it, and captures the pasted code/token.
- `CopilotConnectStrategy`: runs the Copilot SDK device-flow — surfaces the device URL + code, polls `GetAuthStatusAsync`.
- Both reuse the subprocess shape from `MeshPlugin`/`KernelExecutor` (`RedirectStandardInput`); `Observable.FromAsync` is used only at the process boundary.

**`ConnectSessionManager`** — a mesh-scoped singleton that holds the live `Process` between "show URL" and "paste code", keyed per user (instance `ConcurrentDictionary`, **never static**), with a 5-minute timeout that calls `Kill(entireProcessTree:true)`.

**Login-status check** is the cheap, always-on part: each card calls `strategy.IsLoggedIn(userConfigDir)` on render. Only the not-logged-in branch shows the login button.

**On token capture** → `ModelProviderService.CreateProvider(ownerPath, "ClaudeCode"|"Copilot", token)` (already calls `Protect()` on the key); re-connect uses `RotateKey`. The CLI agent factory already injects this.

### 4  Fix the missing icon

The AI settings tab is registered without an `Icon`. Add one where the Settings tabs are declared (the AI/Models tab registration in the portal settings) — a FluentUI `Sparkle` or `Bot` icon, consistent with other tabs.

---

## UI — inline login

The CLI login expands **inside the provider card** — no modal, no side panel. This is the lightest-weight option and keeps the user in context. The card is a small state machine.

### Models tab layout

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

### CLI card — connecting states

**Claude Code (paste-a-code flow):**
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

**GitHub Copilot (device code, auto-poll — nothing to paste):**
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

**Connected:** `✓ Connected as <name>   [ Disconnect ]` · **Error/Expired:** red status line + `[ Retry ]`

### Inline state machine

```
NotConnected ──[Connect]──▶ Connecting ──(code submitted / device poll OK)──▶ Connected
     ▲                          │  ▲                                              │
     └──────[Disconnect]────────┘  └──(5-min timeout · Cancel · auth error)──▶ Error/Expired
                                            └──────────────[Retry]──────────────────┘
```

- **Connecting** is driven by `ConnectSessionManager` + the provider's `IConnectStrategy`. It holds the live CLI `Process`, exposes the auth URL (and, for Copilot, the device code), and either accepts a pasted code (`RequiresPastedCode = true`) or polls auth status. A 5-minute timeout disposes the process via `Kill(entireProcessTree:true)`.
- **On success** the strategy returns the token → `ModelProviderService.CreateProvider/RotateKey` (encrypted with the Key-Vault master key) → the card flips to **Connected** reactively.
- The card body is chosen off `strategy.RequiresPastedCode` (paste field vs. device code) and `IsLoggedIn(userConfigDir)` (Connected vs. NotConnected on first render).

---

## Files to touch

| File | Change |
|---|---|
| `memex/Memex.Portal.Shared/Settings/ModelsSettingsTab.cs` | Switch on `ProviderKind`; render API card (form + model list) or CLI card (login status + connect button); fix the tab icon |
| `src/MeshWeaver.AI.ClaudeCode/ClaudeCodeExtensions.cs` | Expose `ProviderKind = Cli` + `IConnectStrategy` |
| `src/MeshWeaver.AI.Copilot/*` | Same — `ProviderKind = Cli` + `CopilotConnectStrategy` |
| `src/MeshWeaver.AI/Connect/` (new) | `ConnectSessionManager`, `IConnectStrategy`, `ClaudeConnectStrategy` |
| `memex/Memex.Portal.Shared/Models/` | `IConnectTokenSink` and related connect models |
| `memex/Memex.Portal.Shared/MemexConfiguration.cs` | Register `ConnectSessionManager` + the strategies |

---

## Testing

> No mocks. Use `MonolithMeshTestBase` / `AITestBase`.

Three test scenarios:

1. **Rendering** — `ModelsSettingsTab` renders a model list for an API provider and a connect button (no list) for a CLI provider. Assert on the control tree.

2. **Connect flow** — a committed **fake CLI** (prints an auth URL, reads stdin, prints a token) drives `IConnectStrategy`: `IsLoggedIn` returns false → connect → strategy captures the token → a `ModelProvider` node is written with an `enc:`-tagged key that round-trips through `ChatClientCredentialResolver`.

3. **Login status** — with the fake CLI reporting "logged in", the card renders the connected state and shows **no** login button. Real-CLI end-to-end is gated by `CLAUDE_CONNECT_E2E=1` (developer-run only).

---

## Scope note

This is **Phase 1** (per-user CLI Connect + Models-tab rework) — a focused build covering the UI and the CLI login backend, followed by a clean image rebuild and redeploy. The icon fix and `ProviderKind` layout split are the quick visible wins; the CLI login backend (`ConnectSessionManager` + strategies) is the substantive part.

---

## Model picker: provider-first selection and empty state

Providers and models are mesh nodes discovered via a `nodeType:` fan-out query — not a flat config list. The picker lists providers first; selecting one loads only that provider's models. When nothing is configured it routes the user directly to Settings.

### Providers and models are nodes

- A provider is a `ModelProvider` node; its models are child `LanguageModel` nodes.
- Canonical path: `{space|user}/_Provider/{Provider}/{modelId}` — for example, `Systemorph/_Provider/AzureFoundry/gpt-5` or `rbuergi/_Provider/ClaudeCode`. `_Provider` is the satellite segment used consistently across code and tests.
- The built-in system catalog at `_Provider/{provider}/{model}` is served as real, queryable `MeshNode`s by `StaticNodePartitionStorageProvider` (`BuiltInLanguageModelProvider`), so configuration values (`AzureFoundry:Models`) materialise as nodes — there is no parallel, non-mesh path.

### Provider-first, lazy model load

The picker does not eager-load every model from every space. It operates in two steps:

1. **List providers** — fan-out `nodeType:ModelProvider scope:descendants` over root `_Provider` and every space the user can read. Listing providers (not models) is cheap, making it safe to broaden across spaces without loading the full model universe.

2. **Select a provider → load its models** — the provider's path is appended to `{user}/_Provider/_Selection.SelectedProviderPaths`; the selected-path query `namespace:{providerPath} nodeType:LanguageModel scope:selfAndDescendants` (`AgentPickerProjection`) loads just that provider's models.

`_Selection` is the per-user selection store. It is seeded empty at onboarding, so the `RoutingGrain NotFound: {user}/_Provider/_Selection` read no longer occurs against a missing node.

### Empty state → Settings

When the provider fan-out returns nothing (no provider configured), the model picker does not render an empty dropdown. Instead it shows an actionable empty state: **"No model provider configured"** with a link that navigates to **Settings → Models** (`action://settings/models` / the `OnActionLink` hook) where the user can add an API key, connect a CLI, or select an org provider.

### Org-default provider

An admin may pre-create an org provider node — `Systemorph/_Provider/AzureFoundry` with model children sourced from the `s-meshweaver` Foundry resource (endpoint `https://s-meshweaver.services.ai.azure.com/models`, key in Key Vault) — that every user with read access can select. This complements per-user BYO-key and Connect flows; it does not replace the empty-state link. `ModelProvider` is a **creatable** node type (search-hidden), so it can be authored in the UI by anyone with `Permission.Api`, not only through configuration.

### Managing a provider's models

Selecting a provider in Settings → Models lists its child `LanguageModel` nodes, where the user can add, remove, or enable individual models (CRUD on `{provider}/{modelId}` nodes).

- **Fetch from provider** — where the provider exposes a list API (Azure Foundry / Azure OpenAI deployment list, OpenAI / Anthropic list-models), a "Fetch models" action queries it and offers the returned IDs for import as child `LanguageModel` nodes, so the catalog does not need to be hand-typed.
- **Refresh** — re-runs the fetch and reconciles against the current children (adds newly-deployed models, flags dropped ones). Manual button now; can become periodic later.
