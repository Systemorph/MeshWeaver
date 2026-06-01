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

MeshWeaver speaks to multiple LLM providers — Claude via Anthropic, GPT-class models via the Azure AI Services multi-model gateway, embedding models, and more — but the configuration surface is intentionally small: **one shared key, a handful of endpoints, and no hardcoded model lists.** This page explains the design and points you to the right knobs when you need to extend it.

> **Why read this?** The previous shape used provider-specific `*__Models__*` environment variables. Those have been removed. If you're wondering where the model list went, this page has the answer.

---

## One Azure Foundry Key, Two Providers

A single Aspire parameter — `azure-foundry-key`, declared in [`memex/aspire/Memex.AppHost/Program.cs:51`](../../../../memex/aspire/Memex.AppHost/Program.cs) — backs both the Anthropic and AzureAIS credentials:

| Env var | Provider | Endpoint path |
|---|---|---|
| `Anthropic__ApiKey` | Claude (Anthropic) | `/anthropic/...` |
| `AzureAIS__ApiKey` | Multi-model gateway | `/models/...` |

Both routes share one credential under the Azure Foundry resource. **You do not need a separate Anthropic key in this deployment.** A dedicated Anthropic key only makes sense if you route directly to `api.anthropic.com` rather than through Foundry.

---

## Endpoints Are Always Parametrised

Endpoints are never literal strings in source code. In development they come from `dotnet user-secrets` on the AppHost; in production they are injected as GitHub Actions secrets and Azure Container Apps environment variables.

| Aspire parameter | Environment variable |
|---|---|
| `anthropic-endpoint` | `Anthropic__Endpoint` |
| `azure-foundry-endpoint` | `AzureAIS__Endpoint` |
| `embedding-endpoint` | `Embedding__Endpoint` |
| `embedding-model` | `Embedding__Model` |

The embedding pair establishes the canonical pattern — a sibling `endpoint` + `model` parameter per provider. Chat providers follow the same shape.

---

## Models Live in Agent Definitions, Not in Config

The AppHost no longer ships any `*__Models__*` environment variables. The authoritative source for "which model should this agent use" is the agent's own definition:

- **`AgentConfiguration.PreferredModel`** — a pinned model name for this specific agent.
- **`AgentConfiguration.ModelTier`** — `"heavy"`, `"standard"`, or `"light"`, resolved through the Aspire parameters `ModelTier__Heavy`, `ModelTier__Standard`, and `ModelTier__Light`.

Factories such as `AzureOpenAIChatClientAgentFactory` and `AzureClaudeChatClientAgentFactory` read agent config when constructing the chat client.

> **The agent's `PreferredModel` wins over the global chat-dropdown selection (`CurrentModelName`).** This is intentional — agents are tuned for specific models. The dropdown is for the user's free-form chat with the default agent, not a global override for all agents.

---

## Why the Model Dropdown Is Empty

`ModelAutocompleteProvider` populates the dropdown from the union of every `IChatClientFactory.Models[]` array. With `*__Models__*` env vars gone, those arrays are empty by default, so **the dropdown is normally empty — and that is by design.** Users see the agent's own model selection rather than a global override list.

If you want to surface a curated list to humans, populate `Models[]` from a small, explicit config section. Resist the urge to mirror "everything the provider sells."

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

- [Agentic AI](AgenticAI.md) — what agents are and how they're composed
- [MCP Authentication](McpAuthentication.md) — how external clients authenticate to MeshWeaver
