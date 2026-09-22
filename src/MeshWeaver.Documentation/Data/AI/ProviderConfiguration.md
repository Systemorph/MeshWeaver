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
  <text x="390" y="122" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#c8e6c9">ModelTier</text>
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

A single Aspire parameter — `azure-foundry-key`, declared in `../MeshWeaver.Plugins/src/Memex.AppHost/Program.cs:51` — backs both the Anthropic and AzureAIS credentials:

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

> **Section-name caveat (latent bug):** the code binds the **`AzureFoundry:`** section (`AzureFoundryConfiguration`, `AddAzureFoundry`, and the catalog source). The Aspire AppHost currently emits `AzureAIS__Endpoint` / `AzureAIS__ApiKey`, which **nothing in `src/` binds** — so that config is dead. Use `AzureFoundry__*`. The Helm chart (`deploy/helm`) already uses the correct names; the AppHost (`../MeshWeaver.Plugins/src/Memex.AppHost/Program.cs`) should be renamed `AzureAIS__* → AzureFoundry__*`.

The embedding pair establishes the canonical pattern — a sibling `endpoint` + `model` parameter per provider. Chat providers follow the same shape.

---

## Model Selection — Composer First, Tier as Optional Fallback

Two things select a model, at two different layers. **A deployment** advertises which models exist by listing them in each provider's `{Section}:Models` config, which `BuiltInLanguageModelProvider` turns into `LanguageModel` mesh nodes for the picker (see [Setting Up Model Providers](/Doc/AI/ModelProviderSetup)). What the AppHost does *not* do is hardcode model ids in framework C#.

Which model a conversation actually runs on resolves in this order (see `ChatClientAgentFactory.ResolveTierModel` and the concrete factories):

1. **The chat composer selection** (`ThreadComposer.ModelName` → `CurrentModelName`) — the user's explicit pick always wins. The one exception is **Auto**, the default selection for a new thread: Auto is a router, so it is *dispatched* rather than served (see below).
2. **The agent's `AgentConfiguration.ModelTier`** — a USAGE tier (`utility` / `chat` / `reasoning` / `coding`), resolved against the `tier` label on the model NODES. This is also what **Auto** dispatches on. Optional: with no tier declared, or a tier no model carries, it falls through. See [Model Tiers](/Doc/AI/ModelTiers).
3. **The deprecated `ModelTier:*` config** (`ModelTier__Heavy/Standard/Light/Utility`) — still read so an existing deployment keeps its mapping, and only ever consulted for a tier no model node carries.
4. **The deployment default** — the lowest-`order` model whose credentials resolve.

Every step after the first skips models with no usable credential, and skips the router. Resolution never fails: the only outcome with no model is an entirely-unusable catalog, which fails the round audibly.

---

## When a Selected Model Is Unusable — the Fallback Is Honest, Never Silent

A pinned model can stop resolving (its provider node lost its key, the catalog was refactored,
the model was deleted). `AgentChatClient.ApplyStaleModelFallback` then swaps it for a working
model so the thread keeps running. Three rules make that swap honest:

- **The substitute is health-checked.** The fallback ranks the catalog through
  `ChatClientCredentialResolver.HasUsableCredential` — a NON-EMPTY ApiKey, not merely a
  non-`Missing` resolution — the same predicate `AgentPickerProjection` uses for the composer
  default. A keyless, endpoint-only entry is skipped even when it sorts first by `Order`, so the
  round never lands on a model every factory refuses with "ApiKey is missing".
- **The round records what ACTUALLY answered.** `ThreadMessage.ModelName` carries the effective
  model on every terminal path (Completed, Cancelled **and** Error), and the per-model
  `TokenUsage` satellite is keyed by it — so cost is attributed to the model that ran. When the
  effective model differs from the pick, `ThreadMessage.RequestedModelName` carries the requested
  id alongside it. That pair is the substitution marker: an automation detects "I did not get the
  model I asked for" from the node, without reading the chat text. An operator additionally gets a
  `MODEL_SUBSTITUTED` warning naming both models. There is deliberately no user-facing chat notice
  — an unusable pin is a configuration problem the user cannot act on mid-round.
- **Nothing usable ⇒ the round FAILS.** If the selection is unusable and the catalog offers no
  usable replacement *and* no agent could be built, the round terminates with
  `ThreadMessageStatus.Error` and a localized message naming the situation (`chat.noUsableModel`,
  resolved off the round's own `AccessContext.Locale`); the raw factory error stays in the log. It
  does not proceed to produce a raw provider error under a `Completed` status, which any automation
  would read as success.
  (Exhausted-fallback alone is not fatal: a deployment whose keys live in factory config is
  invisible to the credential resolver yet builds agents and runs normally.)

### The credential check cannot see quota — so the refusal has to read well

A usable credential means the deployment will *answer*, not that it will *serve*. A model with a
perfectly good key can still refuse every round because it is out of quota (HTTP 429) or because the
deployment itself is faulting (HTTP 5xx). No local check can predict that — only the provider's
answer reveals it — so the requirement is not "never fall back onto a throttled model", it is **fail
legibly when the provider refuses**.

`ProviderFailureClassifier` names the condition from the exception chain (typed
`HttpRequestException.StatusCode`, else the conventional `Status: NNN` banner that Azure.Core and
System.ClientModel both render), and `ThreadExecution` builds the prose at write time off the round's
own `AccessContext.Locale`:

- **402** → `chat.modelQuotaExhausted`. Separate from 429 because **waiting does not help**: the
  account is out of credit, so the remedy is a top-up or a model on a provider that still has budget.
- **404** → `chat.modelNotFound`. A stale or mistyped model id in the deployment's configuration —
  an actionable configuration fault, so it must never read as "submit again later".
- **429** → `chat.modelRateLimited`, naming the model that actually served.
- **5xx** → `chat.modelProviderError`, naming the model and the status.
- **A credential the provider REJECTS** → `chat.modelCredentialRejected`, naming the model and the
  status. This is a **permanent verdict, not a transient one**, which is what distinguishes it from
  every entry above: the key has to be replaced before any round on that model can succeed, so the
  prose says so and deliberately does not offer "submit again later". See the gap note below — the
  platform string exists; the engine-side classification that selects it is the counterpart half.
- **The stream ended abnormally** — no HTTP status is involved, so these are named ahead of the
  status switch, from the exception rather than from a code: the provider went silent mid-answer
  (`chat.modelStreamStalled`) or sent a payload the wire protocol cannot represent
  (`chat.modelStreamFaulted`, e.g. an OpenAI-compatible gateway ending the stream with a
  `finish_reason` the protocol does not define). Both carry a `PROVIDER_STREAM_*` warning so
  monitoring can separate a recurring upstream stall from a one-off fault.
- **The thread's own history could not be loaded** → `chat.historyLoadFailed`. Not a provider
  condition at all, but it shares the discipline and is the reason it is listed here: a history load
  that did not complete is **reported, never substituted**. Answering on an empty or holed history
  would be a silent wrong answer under a `Completed` status, which is strictly worse than a failed
  round — so the fault rides through to the terminal `Error` write.
- **Substituted rounds add one sentence** (`chat.modelSubstitutionNote`) naming the requested model
  and the one used instead. This is the only place the swap is spelled out to the user, and it earns
  its place: the failure names a model they never picked, which is otherwise inexplicable.
- **Anything unclassified keeps its own message verbatim** — for a tool fault or a bug in our code
  that message *is* the diagnosis, and generic prose would erase it. 🚨 Read the boundary precisely:
  that rule is right for a fault carrying **no** recognisable transport status, and wrong for one
  that does. Once the classifier has established that a provider answered with an HTTP status, the
  SDK's raw banner is never the right text for a reader — so a status that no branch above claims is
  a **gap in the branch table**, not an invitation to fall through.

The raw transport text is never discarded, only relocated: it stays on the `LogError(ex, …)` that
precedes the terminal write, alongside a `PROVIDER_REFUSED` warning carrying the status, the serving
model and the requested one. What changed is what the *user* reads — previously `ex.Message` went
straight into the cell's Text and Summary, which for these failures is the status line plus the
response body plus the complete HTTP header block.

### Where this code lives — and why the catalog key leads

🚨 **The classifier and the switch that consumes it are NOT in this repository.** The AI engine is a
module hosted in `MeshWeaver.Plugins` (`MeshWeaver.AI`, `MeshWeaver.AI.Anthropic`,
`MeshWeaver.AI.OpenAI`, `MeshWeaver.AI.ClaudeCode`); core holds **only** the localization catalog
these conditions render through, plus `LocalizationCatalog` itself
(`src/MeshWeaver.Messaging.Hub/Localization/`). A grep of core's `src/` for `AgentChatClient`,
`ProviderFailureClassifier` or `ThreadExecution` finds comments and XML-doc cross-references and no
executable code at all, and core's solution declares no `MeshWeaver.AI*` project — so a search over
this repository alone can neither confirm nor refute anything about how a provider failure is
handled. Establish the subject's home before reading absence here as absence.

That split sets the **order of the two halves, and it is inverted from the usual dependency rule**:
the platform's catalog key lands FIRST and the engine-side branch follows. The engine tolerates the
gap in that direction by construction — it checks `LocalizationCatalog.Keys.Contains(key)` before
resolving and falls back to its own purpose-written English, because a module can ship a condition
before the image carrying the string does. Rendering a raw `chat.…` token to a user is the failure
that check exists to prevent. The reverse order has no such tolerance: a branch selecting a key the
loaded platform does not define is what that guard is defending against.

### The gap this table currently has: a rejected credential

> 🕐 **This subsection is provisional and names its own expiry.** It describes a half-landed pair — the
> platform string exists here, the engine-side branch does not yet. **Delete it the moment
> `ProviderFailureClassifier` gains a 401 predicate and the switch gains its arm**, and fold
> `chat.modelCredentialRejected` into the list above as an ordinary entry. Left standing past that
> point it becomes an actively false claim about a repository this page cannot see, which is the
> failure mode the subsection above warns about — so do not re-measure it here, measure it there.

A provider that **rejects the credential** answers `401` (Anthropic renders it `PermissionDenied`).
Measured against `MeshWeaver.Plugins@main`, nothing claims it: `ProviderFailureClassifier` has
predicates for 402, 404, 429 and 5xx and none for 401; the `providerStatus` switch in
`ThreadExecution` has cases for exactly those four ranges and a `_ => null` default; the Anthropic
client maps 401 to no typed condition; and the only mention of `401` anywhere in `ThreadExecution` is
a comment about the *CLI harness* path. So the round falls to the unclassified default and pastes the
SDK's own sentence — `Response status code does not indicate success: 401 (PermissionDenied).` —
into the user's cell: raw, English-only whatever the viewer's locale, and naming no remedy. That is
precisely the defect the rest of this section exists to prevent, still live for the one condition an
operator is most able to fix.

Two things follow, and they are easy to conflate:

- **The retry half is a different question from the legibility half, and only one of them is a
  defect.** `AnthropicChatClient.SendWithRetryAsync` retries `500`, `502`, `503` and `429` only, so a
  401 is already treated as terminal and goes straight to `EnsureSuccessStatusCode()` — correctly,
  because a rejected credential is a permanent verdict and retrying it could never do anything but
  waste the round. A report that a 401 is *retried* does not survive reading that set. What is wrong
  is what the user is shown afterwards.
- **Adding a retry would be the wrong fix twice over** — it is a band-aid, and it contradicts the
  verdict the status carries. The fix is classification: name the condition, render
  `chat.modelCredentialRejected`, keep the provider's own text on the `LogError` where an operator
  reads it.

**What this note does not establish.** Only `401` is measured, from production occurrences. Whether
`403` and other 4xx statuses should also be named — and under which prose — is deliberately left
open: `403` means different things at different providers (permission, region, content policy), and a
confidently wrong name is worse for a reader than a generic one. `chat.modelProviderError` already
interpolates its status and is the obvious home for a widened default, but its current wording ends
in "Submit again later", which is true of a 5xx and false of most 4xx — so widening the branch is a
wording decision, not a mechanical one, and it is not made here.

---

## How the Model Picker Is Populated

The picker is **node-based**, not factory-based. `AgentPickerProjection` runs `nodeType:LanguageModel|ModelProvider` queries over the platform `Provider` catalog, the context's `{path}/Provider` subtrees, and the user's own `{user}/_Memex` namespace, and shows the resulting `LanguageModel` nodes, grouped by provider. Those nodes come from two places: the system catalog `BuiltInLanguageModelProvider` materialises from each `{Section}:Models` config list (imported into the `Provider` partition on boot and served from the DB), and space/user `ModelProvider` nodes authored in the mesh.

So an empty picker means **no provider/model nodes are visible to the user** — almost always because the deployment carries no `{Section}:Models` config signal (the classic Helm/AKS gap) or the user's `{user}/_Memex/Selection` points at a provider that doesn't exist. The full diagnosis + fix is in **[Setting Up Model Providers → Troubleshooting](/Doc/AI/ModelProviderSetup#troubleshooting-an-empty-picker)**.

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
3. **Add Aspire parameters** in `../MeshWeaver.Plugins/src/Memex.AppHost/Program.cs` for the endpoint (and a key, if it doesn't share `azure-foundry-key`).
4. **Label the new model's node** with a `tier` (`ModelDefinition.Tier`), so agents reach it by declaring `modelTier` in their front matter. An agent names a tier, never a model id — there is no per-agent "preferred model" field.

> **Do not hardcode model identifiers in framework code.** If you find yourself writing `"gpt-4o"` or `"claude-sonnet-4-5"` in a `.cs` file outside an agent definition, that is precisely the pattern this page exists to prevent.

---

## Related

- [Agentic AI](/Doc/AI/AgenticAI) — what agents are and how they're composed
- [MCP Authentication](/Doc/AI/McpAuthentication) — how external clients authenticate to MeshWeaver
