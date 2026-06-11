---
NodeType: Markdown
Name: "AI Provider Configuration"
Abstract: "How AI provider keys, endpoints, and models are wired in MeshWeaver: one shared Azure Foundry key, parameterised endpoints, and per-agent model selection — no provider-specific model lists."
Icon: "<svg viewBox='0 0 24 24' xmlns='http://www.w3.org/2000/svg'><rect width='24' height='24' rx='4' fill='#1565c0'/><circle cx='12' cy='12' r='3' fill='none' stroke='white' stroke-width='2'/><path d='M12 5v3M12 16v3M5 12h3M16 12h3M7 7l2 2M15 15l2 2M7 17l2-2M15 9l2-2' stroke='white' stroke-width='2' stroke-linecap='round'/></svg>"
Thumbnail: "images/agenticai.svg"
Authors:
  - "Roland Buergi"
Tags:
  - "AI"
  - "Configuration"
  - "Aspire"
  - "Providers"
---

> **The model-provider docs at a glance:** [Model Providers](/Doc/Architecture/ModelProviders) — the architectural pattern · [Provider Configuration](/Doc/AI/ProviderConfiguration) — framework config & chat-client factories · [Model Provider Setup](/Doc/AI/ModelProviderSetup) — operational setup & troubleshooting · [Model Provider Settings](/Doc/AI/ModelProviderSettings) — the settings UI. **This page: framework config & chat-client factories.**


MeshWeaver speaks to multiple LLM providers — Claude via Anthropic, GPT-class and open-weight models via the Azure AI Services multi-model gateway, embedding models, and more — but the configuration surface is intentionally small: **one shared key, a handful of endpoints, and a short per-provider model list.** This page explains the credential/endpoint wiring and the factory routing. For getting models to actually appear in the picker — provider/model **mesh nodes**, the space/user layers, and the install-time gotchas — read the operational guide first: **[Setting Up Model Providers](/Doc/AI/ModelProviderSetup)**.

> **Why read this?** This page is about *credentials, endpoints, and which factory handles a model*. If your question is "why is the model picker empty / how do I add models," start with [Setting Up Model Providers](/Doc/AI/ModelProviderSetup) — the picker is fed by `ModelProvider` / `LanguageModel` mesh nodes, which this deployment seeds from the config sections below.

<svg viewBox="0 0 760 320" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;">
  <defs>
    <marker id="arr" markerWidth="8" markerHeight="8" refX="7" refY="3" orient="auto">
      <path d="M0,0 L0,6 L8,3 z" fill="currentColor" fill-opacity=".55"/>
    </marker>
  </defs>
  <rect x="0" y="0" width="760" height="320" rx="12" fill="none"/>
  <rect x="20" y="20" width="160" height="70" rx="10" fill="#37474f"/>
  <text x="100" y="48" text-anchor="middle" font-family="sans-serif" font-size="12" font-weight="bold" fill="#fff">Azure Foundry</text>
  <text x="100" y="64" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#cfd8dc">azure-foundry-key</text>
  <text x="100" y="80" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#90a4ae">+ endpoint params</text>
  <rect x="20" y="120" width="160" height="50" rx="10" fill="#1e88e5"/>
  <text x="100" y="142" text-anchor="middle" font-family="sans-serif" font-size="12" font-weight="bold" fill="#fff">Anthropic__ApiKey</text>
  <text x="100" y="160" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#bbdefb">Claude provider</text>
  <rect x="20" y="190" width="160" height="50" rx="10" fill="#5c6bc0"/>
  <text x="100" y="212" text-anchor="middle" font-family="sans-serif" font-size="12" font-weight="bold" fill="#fff">AzureAIS__ApiKey</text>
  <text x="100" y="230" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#c5cae9">Multi-model gateway</text>
  <rect x="20" y="260" width="160" height="40" rx="10" fill="#26a69a"/>
  <text x="100" y="278" text-anchor="middle" font-family="sans-serif" font-size="12" font-weight="bold" fill="#fff">Embedding__*</text>
  <text x="100" y="294" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#b2dfdb">endpoint + model</text>
  <line x1="100" y1="90" x2="100" y2="118" stroke="currentColor" stroke-opacity=".4" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="100" y1="90" x2="100" y2="188" stroke="currentColor" stroke-opacity=".4" stroke-width="1.5"/>
  <line x1="100" y1="90" x2="100" y2="258" stroke="currentColor" stroke-opacity=".4" stroke-width="1.5"/>
  <rect x="310" y="80" width="160" height="60" rx="10" fill="#43a047"/>
  <text x="390" y="105" text-anchor="middle" font-family="sans-serif" font-size="12" font-weight="bold" fill="#fff">Agent Definition</text>
  <text x="390" y="122" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#c8e6c9">PreferredModel / ModelTier</text>
  <rect x="310" y="170" width="160" height="60" rx="10" fill="#f57c00"/>
  <text x="390" y="195" text-anchor="middle" font-family="sans-serif" font-size="12" font-weight="bold" fill="#fff">AgentChatClient</text>
  <text x="390" y="212" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#ffe0b2">GetFactoryForModel</text>
  <line x1="390" y1="140" x2="390" y2="168" stroke="currentColor" stroke-opacity=".4" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="180" y1="135" x2="308" y2="105" stroke="currentColor" stroke-opacity=".4" stroke-width="1.5" stroke-dasharray="5,3" marker-end="url(#arr)"/>
  <line x1="180" y1="215" x2="308" y2="195" stroke="currentColor" stroke-opacity=".4" stroke-width="1.5" stroke-dasharray="5,3" marker-end="url(#arr)"/>
  <rect x="570" y="80" width="160" height="55" rx="10" fill="#1e88e5"/>
  <text x="650" y="103" text-anchor="middle" font-family="sans-serif" font-size="11" font-weight="bold" fill="#fff">AzureClaude</text>
  <text x="650" y="119" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#bbdefb">ChatClientFactory</text>
  <text x="650" y="131" text-anchor="middle" font-family="sans-serif" font-size="9" fill="#90caf9">claude-* → Anthropic</text>
  <rect x="570" y="160" width="160" height="55" rx="10" fill="#5c6bc0"/>
  <text x="650" y="183" text-anchor="middle" font-family="sans-serif" font-size="11" font-weight="bold" fill="#fff">AzureFoundry</text>
  <text x="650" y="199" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#c5cae9">ChatClientFactory</text>
  <text x="650" y="211" text-anchor="middle" font-family="sans-serif" font-size="9" fill="#9fa8da">gpt-*, o*, Mistral-*</text>
  <rect x="570" y="240" width="160" height="40" rx="10" fill="#37474f"/>
  <text x="650" y="258" text-anchor="middle" font-family="sans-serif" font-size="10" font-weight="bold" fill="#cfd8dc">Custom Factory</text>
  <text x="650" y="274" text-anchor="middle" font-family="sans-serif" font-size="9" fill="#90a4ae">Supports() predicate</text>
  <line x1="470" y1="195" x2="568" y2="108" stroke="currentColor" stroke-opacity=".4" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="470" y1="200" x2="568" y2="188" stroke="currentColor" stroke-opacity=".4" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="470" y1="205" x2="568" y2="255" stroke="currentColor" stroke-opacity=".4" stroke-width="1.5" marker-end="url(#arr)"/>
  <text x="100" y="14" text-anchor="middle" font-family="sans-serif" font-size="10" fill="currentColor" fill-opacity=".5">Credentials &amp; Endpoints</text>
  <text x="390" y="14" text-anchor="middle" font-family="sans-serif" font-size="10" fill="currentColor" fill-opacity=".5">Agent Config</text>
  <text x="650" y="14" text-anchor="middle" font-family="sans-serif" font-size="10" fill="currentColor" fill-opacity=".5">Factory Routing</text>
</svg>

*One shared Azure Foundry key backs multiple providers; the agent definition selects the model; `AgentChatClient` routes to the matching factory via `Supports()` predicates.*

---

## One Azure Foundry Key, Two Providers

A single Aspire parameter — `azure-foundry-key`, declared in `memex/aspire/Memex.AppHost/Program.cs:51` — backs both the Anthropic and AzureAIS credentials:

| Env var | Provider | Endpoint path |
|---|---|---|
| `Anthropic__ApiKey` | Claude (Anthropic) | `/anthropic/...` |
| `AzureFoundry__ApiKey` | Multi-model gateway (open-weight: DeepSeek, Llama, Mistral, Phi…) | `/models/...` |

Both routes share one credential under the Azure Foundry resource. **You do not need a separate Anthropic key in this deployment.** A dedicated Anthropic key only makes sense if you route directly to `api.anthropic.com` rather than through Foundry.

---

## Endpoints Are Always Parametrised

Endpoints are never literal strings in source code. In development they come from `dotnet user-secrets` on the AppHost; in production they are injected as GitHub Actions secrets and Azure Container Apps environment variables.

| Aspire parameter | Environment variable |
|---|---|
| `anthropic-endpoint` | `Anthropic__Endpoint` |
| `azure-foundry-endpoint` | `AzureFoundry__Endpoint` |
| `embedding-endpoint` | `Embedding__Endpoint` |
| `embedding-model` | `Embedding__Model` |

> **Section-name caveat (latent bug):** the code binds the **`AzureFoundry:`** section (`AzureFoundryConfiguration`, `AddAzureFoundry`, and the catalog source). The Aspire AppHost currently emits `AzureAIS__Endpoint` / `AzureAIS__ApiKey`, which **nothing in `src/` binds** — so that config is dead. Use `AzureFoundry__*`. The Helm chart (`deploy/helm`) already uses the correct names; the AppHost (`memex/aspire/Memex.AppHost/Program.cs`) should be renamed `AzureAIS__* → AzureFoundry__*`.

The embedding pair establishes the canonical pattern — a sibling `endpoint` + `model` parameter per provider. Chat providers follow the same shape.

---

## Model Selection — Composer First, Tier as Optional Fallback

Two things select a model, at two different layers. **A deployment** advertises which models exist by listing them in each provider's `{Section}:Models` config, which `BuiltInLanguageModelProvider` turns into `LanguageModel` mesh nodes for the picker (see [Setting Up Model Providers](/Doc/AI/ModelProviderSetup)). What the AppHost does *not* do is hardcode model ids in framework C#.

Which model a conversation actually runs on resolves in this order (see `ChatClientAgentFactory.ResolveTierModel` and the concrete factories):

1. **The chat composer selection** (`ThreadComposer.ModelName` → `CurrentModelName`) — the user's explicit pick always wins.
2. **The agent's `AgentConfiguration.ModelTier`** — a strictly **optional** hint (`"heavy"` / `"standard"` / `"light"` / `"utility"`), resolved through the `ModelTier:*` config (Aspire parameters `ModelTier__Heavy/Standard/Light/Utility`). It only fills the gap where nobody picked a model — headless flows like notification triage and the icon/description/thread-naming micro-jobs. Only the built-in background micro-agents declare it; interactive agents leave it unset. With no tier declared or no `ModelTier:*` config, this step is a no-op.
3. **The provider's first configured model** — the last-resort default.

---

## How the Model Picker Is Populated

The picker is **node-based**, not factory-based. `AgentPickerProjection` runs `nodeType:LanguageModel|ModelProvider` queries over the `_Provider` namespaces (system, the user's selected providers) and shows the resulting `LanguageModel` nodes, grouped by provider. Those nodes come from two places: the system catalog `BuiltInLanguageModelProvider` materialises from each `{Section}:Models` config list, and space/user `ModelProvider` nodes authored in the mesh.

So an empty picker means **no provider/model nodes are visible to the user** — almost always because the deployment carries no `{Section}:Models` config signal (the classic Helm/AKS gap) or the user's `_Provider/_Selection` points at a provider that doesn't exist. The full diagnosis + fix is in **[Setting Up Model Providers → Troubleshooting](/Doc/AI/ModelProviderSetup#troubleshooting-an-empty-picker)**.

> Don't try to mirror "everything the provider sells." List a short, curated set in `{Section}:Models` (the deployment's catalog) — the user picks from it in the composer.

---

## Model-to-Factory Routing

When an agent needs a chat client for a given model name, `AgentChatClient.GetFactoryForModel` iterates the registered `IChatClientFactory` implementations in `Order` (lower first) and calls `Supports(string)` on each. Routing works without any populated `Models[]` array because the concrete factories implement shape-aware predicates:

| Factory | `Supports` predicate |
|---|---|
| `AzureClaudeChatClientAgentFactory` | `name.StartsWith("claude", IgnoreCase)` |
| `AzureFoundryChatClientAgentFactory` | catch-all for non-claude names (`gpt-*`, `o*`, `Mistral-*`, `DeepSeek-*`, …) |

The default `IChatClientFactory.Supports` falls back to the legacy `Models[]` lookup, so factories that don't override still work through explicit `Models` config — useful for tests or for serving a curated subset.

---

## Adding a New Provider

To wire in a new provider (a second Azure OpenAI deployment, a hosted local model, etc.):

1. **Implement `IChatClientFactory`** and register it via DI (`services.AddAzureOpenAI(...)` or similar).
2. **Bind its options** from a new section in `MemexConfiguration.cs` — endpoint and auth fields only, **not** model names.
3. **Add Aspire parameters** in `memex/aspire/Memex.AppHost/Program.cs` for the endpoint (and a key, if it doesn't share `azure-foundry-key`).
4. **Reference the new model** in agent definitions via `PreferredModel` or `ModelTier`.

> **Do not hardcode model identifiers in framework code.** If you find yourself writing `"gpt-4o"` or `"claude-sonnet-4-5"` in a `.cs` file outside an agent definition, that is precisely the pattern this page exists to prevent.

---

## Related

- [Agentic AI](/Doc/AI/AgenticAI) — what agents are and how they're composed
- [MCP Authentication](/Doc/AI/McpAuthentication) — how external clients authenticate to MeshWeaver
