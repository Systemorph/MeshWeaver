---
NodeType: "Doc/Article"
Title: "AI Provider Configuration"
Abstract: "How AI provider keys, endpoints, and models are wired in MeshWeaver: one shared Azure Foundry key, parameterised endpoints, and per-agent model selection — no provider-specific model lists."
Icon: "Settings"
Published: "2026-05-05"
Thumbnail: "images/agenticai.svg"
Authors:
  - "Roland Buergi"
Tags:
  - "AI"
  - "Configuration"
  - "Aspire"
  - "Providers"
---

## Overview

MeshWeaver talks to multiple LLM providers (Claude via Anthropic, GPT-class models via the Azure AI Services multi-model gateway, embedding models, etc.) but configures them in a deliberately small surface: one shared key, a handful of endpoints, and zero hardcoded model lists. This page exists because the previous shape — provider-specific `*__Models__*` env vars — has been removed, and contributors will reasonably ask "where did the model list go?"

## One Azure Foundry key, multiple endpoints

A single Aspire parameter, `azure-foundry-key` (declared in [`memex/aspire/Memex.AppHost/Program.cs:51`](../../../../memex/aspire/Memex.AppHost/Program.cs)), backs both `Anthropic__ApiKey` and `AzureAIS__ApiKey`. The two providers reach different endpoint paths under the Azure Foundry resource:

- `/anthropic/...` for Claude
- `/models/...` for the multi-model gateway

but they share one credential. **Anthropic does not need its own key in this deployment.** Adding a separate Anthropic key only makes sense if you ever route directly to api.anthropic.com instead of through Foundry.

## Endpoints come from Aspire parameters, never hardcoded

Endpoints are passed in as parameters; they are never literal strings in source. In dev they are set via `dotnet user-secrets` on the AppHost; in prod they come from GitHub Actions secrets and Azure Container Apps environment variables.

| Parameter             | Consumed as          |
| --------------------- | -------------------- |
| `anthropic-endpoint`  | `Anthropic__Endpoint` |
| `azure-foundry-endpoint` | `AzureAIS__Endpoint` |
| `embedding-endpoint`  | `Embedding__Endpoint` |
| `embedding-model`     | `Embedding__Model`    |

The embedding pair (`embedding-endpoint` + `embedding-model`) is the established pattern; chat providers follow the same shape.

## Models live in agent definitions, not in config

The AppHost no longer ships any `*__Models__*` environment variables. The source of truth for "which model should this agent use" is the agent's own definition:

- `AgentConfiguration.PreferredModel` — a per-agent pinned model name.
- `AgentConfiguration.ModelTier` — `"heavy"`, `"standard"`, or `"light"`, resolved through the Aspire parameters `ModelTier__Heavy`, `ModelTier__Standard`, `ModelTier__Light`.

Factories such as `AzureOpenAIChatClientAgentFactory` and `AzureClaudeChatClientAgentFactory` read agent config when constructing a chat client. **The agent's `PreferredModel` wins over the global chat-dropdown selection (`CurrentModelName`).** That is intentional: agents are tuned for specific models; the dropdown is for the user's free-form chat with the default agent.

## Why the model dropdown is empty

`ModelAutocompleteProvider` displays the union of every `IChatClientFactory.Models[]` array. With the `*__Models__*` env vars gone, those arrays are empty by default — so the dropdown is normally empty too. **This is by design.** Users see the agent's own selection, not a global override list. If you want to surface a curated list to humans, populate `Models[]` from a small, explicit config section — but resist the urge to mirror "everything the provider sells."

## Open question: model-to-factory routing

`AgentChatClient.GetFactoryForModel` currently picks a factory by `factory.Models.Contains(modelName)`. With every factory's `Models[]` empty, this falls back to the first registered factory, which may not actually serve the agent's `PreferredModel`. This is a known gap and will be addressed via an `IChatClientFactory.Supports(string modelName)` predicate (or an equivalent name-shape matcher) so that Claude model names route to the Claude factory regardless of which provider is registered first.

## Adding a new provider

To wire in a new provider (e.g. a second Azure OpenAI deployment or a hosted local model):

1. Implement `IChatClientFactory` and register it via DI (`services.AddAzureOpenAI(...)` or similar).
2. Bind its options from a new section in `MemexConfiguration.cs` — endpoint and any auth fields, **not** model names.
3. Add Aspire parameters in `memex/aspire/Memex.AppHost/Program.cs` for the endpoint (and a key, if it doesn't share `azure-foundry-key`).
4. Reference the new model in agent definitions via `PreferredModel` or `ModelTier`.

Do **not** hardcode model identifiers anywhere in framework code. If you find yourself writing `"gpt-4o"` or `"claude-sonnet-4-5"` in a `.cs` file outside an agent definition, that is the smell this page exists to prevent.

## Related

- [Agentic AI](AgenticAI.md) — what agents are and how they're composed
- [MCP Authentication](McpAuthentication.md) — how external clients authenticate to MeshWeaver
