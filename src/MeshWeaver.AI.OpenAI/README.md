# MeshWeaver.AI.OpenAI

OpenAI-wire-protocol model providers for the MeshWeaver AI framework. One assembly
serves every provider that speaks the OpenAI chat-completions protocol — they differ
only by base URL and credentials:

| Provider (`AddXxx`)      | `ProviderName`      | Endpoint                                   |
|--------------------------|---------------------|--------------------------------------------|
| `AddOpenAI`              | `OpenAI`            | `https://api.openai.com` (SDK default)     |
| `AddAzureOpenAI`         | `AzureOpenAI`       | your `*.openai.azure.com` deployment       |
| `AddOpenAICompatible`    | `OpenAICompatible`  | any base URL you supply (OpenRouter, Groq, Together, vLLM, …) |

Each `AddXxx` self-registers a `LanguageModelCatalogSource` (so the provider appears
in the chat model picker and the **Settings → Language Models** tab) plus its
`IChatClientFactory`. There is no central registry — a host opts into each provider it
needs, gated by the `Features:Ai:Providers:*` flags.

## Registration

```csharp
meshBuilder
    .AddOpenAI()             // direct api.openai.com
    .AddAzureOpenAI()        // Azure-hosted OpenAI deployment
    .AddOpenAICompatible();  // generic custom-URL provider (OpenRouter, …)
```

## Credentials

Credentials are **not** read from `appsettings` per user. Each saved provider is a
`nodeType:ModelProvider` mesh node carrying its endpoint + (encrypted) API key; each
`LanguageModel` child points back at it via `ModelDefinition.ProviderRef`. At chat time
`ChatClientCredentialResolver` follows that reference, so the factory builds an
`OpenAIClient` aimed at the right endpoint with the right key — and several distinct
OpenAI-compatible gateways coexist without collision.

A system-default `OpenAI:` / `AzureOpenAI:` config section is still honoured as a
fallback for the built-in catalog. `OpenAICompatible` has no system default — the user
supplies the URL + key and fetches the model list live in the Settings tab.

## Key components

- `OpenAIChatClientAgentFactory` — serves `OpenAI` **and** `OpenAICompatible` (plain
  `OpenAIClient` at the resolved endpoint).
- `AzureOpenAIChatClientAgentFactory` — serves `AzureOpenAI` (`AzureOpenAIClient`).
- `OpenAIExtensions` / `AzureOpenAIExtensions` — the `AddXxx` builder extensions.
- `OpenAIConfiguration` / `AzureOpenAIConfiguration` — system-default config shapes.

## Related

- [MeshWeaver.AI](../MeshWeaver.AI/README.md) — core AI abstractions, the provider/credential resolver, the catalog node types.
- [MeshWeaver.AI.AzureFoundry](../MeshWeaver.AI.AzureFoundry/README.md) — Azure AI Foundry + direct Anthropic.
