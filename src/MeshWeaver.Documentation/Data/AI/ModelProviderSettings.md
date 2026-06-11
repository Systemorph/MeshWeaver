---
Name: AI Model Provider Settings
Description: "Settings → Models UX: per-provider-type layout, model lists for API providers, and delegating Claude Code / GitHub Copilot to their CLI login."
---

> **The model-provider docs at a glance:** [Model Providers](/Doc/Architecture/ModelProviders) — the architectural pattern · [Provider Configuration](/Doc/AI/ProviderConfiguration) — framework config & chat-client factories · [Model Provider Setup](/Doc/AI/ModelProviderSetup) — operational setup & troubleshooting · [Model Provider Settings](/Doc/AI/ModelProviderSettings) — the settings UI. **This page: the settings UI.**


# AI Model Provider Settings

The **Settings → Models** page is the user's single destination for wiring AI into Memex — adding API keys, enabling specific models, and connecting CLI-based providers like Claude Code and GitHub Copilot. This document is the actionable implementation spec: it identifies the exact files to touch, the behavioral seams to introduce, and the testing approach.

> **Setting up models (admin or user)?** This page is the *UI design spec*. For the operational how-to — provider/model mesh nodes, the system/space/user layers, which query goes where in a user's namespace, the open-weight tier choices, and the install-time config gaps — read **[Setting Up Model Providers](/Doc/AI/ModelProviderSetup)**.

---

## Two provider kinds, two different UIs

The fundamental insight driving this design: API providers and CLI providers need **completely different layouts**. Rendering both as a key/endpoint form is wrong.

<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 760 320" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;">
  <defs>
    <marker id="arr" markerWidth="8" markerHeight="8" refX="7" refY="3.5" orient="auto">
      <path d="M0,0 L8,3.5 L0,7 Z" fill="currentColor" fill-opacity="0.6"/>
    </marker>
  </defs>
  <rect x="0" y="0" width="760" height="320" rx="12" fill="none" stroke="currentColor" stroke-opacity="0.08"/>
  <rect x="30" y="20" width="200" height="52" rx="10" fill="#5c6bc0"/>
  <text x="130" y="42" text-anchor="middle" font-family="sans-serif" font-size="12" font-weight="700" fill="#fff" opacity="0.85">Settings → Models</text>
  <text x="130" y="60" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#fff" opacity="0.7">ProviderKind dispatch</text>
  <line x1="130" y1="72" x2="130" y2="104" stroke="currentColor" stroke-opacity="0.5" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="130" y1="104" x2="290" y2="104" stroke="currentColor" stroke-opacity="0.4" stroke-width="1.5"/>
  <line x1="130" y1="104" x2="530" y2="104" stroke="currentColor" stroke-opacity="0.4" stroke-width="1.5"/>
  <line x1="290" y1="104" x2="290" y2="128" stroke="currentColor" stroke-opacity="0.5" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="530" y1="104" x2="530" y2="128" stroke="currentColor" stroke-opacity="0.5" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="160" y="128" width="260" height="52" rx="10" fill="#1e88e5"/>
  <text x="290" y="150" text-anchor="middle" font-family="sans-serif" font-size="13" font-weight="700" fill="#fff">API Provider</text>
  <text x="290" y="168" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#fff" opacity="0.8">Azure AI Foundry · Azure OpenAI · Anthropic · OpenAI</text>
  <rect x="400" y="128" width="260" height="52" rx="10" fill="#43a047"/>
  <text x="530" y="150" text-anchor="middle" font-family="sans-serif" font-size="13" font-weight="700" fill="#fff">CLI Provider</text>
  <text x="530" y="168" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#fff" opacity="0.8">Claude Code · GitHub Copilot</text>
  <line x1="230" y1="180" x2="200" y2="210" stroke="currentColor" stroke-opacity="0.5" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="290" y1="180" x2="290" y2="210" stroke="currentColor" stroke-opacity="0.5" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="350" y1="180" x2="380" y2="210" stroke="currentColor" stroke-opacity="0.5" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="108" y="210" width="184" height="48" rx="8" fill="#1565c0" opacity="0.9"/>
  <text x="200" y="231" text-anchor="middle" font-family="sans-serif" font-size="11" font-weight="600" fill="#fff">Endpoint / Key form</text>
  <text x="200" y="249" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#fff" opacity="0.75">saved &amp; validated</text>
  <rect x="252" y="210" width="160" height="48" rx="8" fill="#1565c0" opacity="0.9"/>
  <text x="332" y="231" text-anchor="middle" font-family="sans-serif" font-size="11" font-weight="600" fill="#fff">Fetch model list</text>
  <text x="332" y="249" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#fff" opacity="0.75">IChatClientCatalog</text>
  <line x1="290" y1="258" x2="290" y2="280" stroke="currentColor" stroke-opacity="0.5" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="200" y="280" width="182" height="36" rx="8" fill="#0d47a1"/>
  <text x="291" y="303" text-anchor="middle" font-family="sans-serif" font-size="11" font-weight="600" fill="#fff">Enable selected models</text>
  <line x1="470" y1="180" x2="470" y2="210" stroke="currentColor" stroke-opacity="0.5" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="530" y1="180" x2="530" y2="210" stroke="currentColor" stroke-opacity="0.5" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="590" y1="180" x2="590" y2="210" stroke="currentColor" stroke-opacity="0.5" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="392" y="210" width="156" height="48" rx="8" fill="#2e7d32" opacity="0.9"/>
  <text x="470" y="231" text-anchor="middle" font-family="sans-serif" font-size="11" font-weight="600" fill="#fff">Check IsLoggedIn</text>
  <text x="470" y="249" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#fff" opacity="0.75">IConnectStrategy</text>
  <rect x="508" y="210" width="156" height="48" rx="8" fill="#2e7d32" opacity="0.9"/>
  <text x="586" y="231" text-anchor="middle" font-family="sans-serif" font-size="11" font-weight="600" fill="#fff">CLI auth flow</text>
  <text x="586" y="249" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#fff" opacity="0.75">ConnectSessionManager</text>
  <line x1="530" y1="258" x2="530" y2="280" stroke="currentColor" stroke-opacity="0.5" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="440" y="280" width="182" height="36" rx="8" fill="#1b5e20"/>
  <text x="531" y="303" text-anchor="middle" font-family="sans-serif" font-size="11" font-weight="600" fill="#fff">Store encrypted token</text>
</svg>

*Two provider kinds — API providers add a key and pick models; CLI providers delegate to their own auth flow.*

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
