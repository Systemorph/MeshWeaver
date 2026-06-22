---
Name: Platform Provider Setup (memex)
Category: AI
Description: First-run / operator guide — configure the platform AI provider on a memex deployment so chat works. Providers and models are mesh nodes under the Provider partition, not config.
Icon: Key
---

# Platform Provider Setup (memex)

A fresh memex deployment has **no working chat until a platform AI provider is configured**. Providers and their models are **mesh nodes**, not `appsettings`/Helm values — so you configure them once, through the platform, and they survive every redeploy. This guide is the operator/first-run path.

> The built-in/config provider (`BuiltInLanguageModelProvider`) is only a **sync source** — it may seed default provider/model nodes on first boot, but it can be **absent entirely**. The source of truth is the DB, under the [Provider catalog](../Architecture/ModelProviders). You never have to touch config to add or change a provider.

## What you are creating

The provider catalog is a NodeType catalog under the top-level **`Provider`** partition — the same shape as `Agent` / `Skill` / `Harness` (see [NodeType Catalogs](../Architecture/NodeTypeCatalogs)):

```
@Provider                                  the catalog partition (Space)
@Provider/_Policy        PartitionAccessPolicy   PublicRead = true
@Provider/Anthropic      ModelProvider           endpoint + (encrypted) key
@Provider/Anthropic/claude-opus-4-8    LanguageModel    a model, NESTED under its provider
@Provider/Anthropic/claude-sonnet-4-6  LanguageModel
@Provider/OpenRouter     ModelProvider
@Provider/OpenRouter/{slug}            LanguageModel
```

Models are **nested inside their provider**. The model the chat picker uses by default is the `LanguageModel` node with the lowest `Order` (the `order:-1` convention).

## Who can configure it

Platform admins (`Auth:GlobalAdmins`, seeded by `GlobalAdminSeed`) hold write on the `Provider` partition. Everyone else gets **read** via `Provider/_Policy` (`PublicRead = true`) so the model picker resolves under any user's identity. Non-admins never see or edit keys (`ModelProvider.ApiKey` is gated by `Permission.Api`).

## Setup — through the mesh catalog (no config, no redeploy)

You manage providers and models in the **mesh catalog** (the standard search/browse UI), which supports create, edit, and a permission-gated delete (trash) affordance + keyboard shortcuts. There is no bespoke settings form.

1. **Create the provider.** Add a `ModelProvider` node at `Provider/{ProviderName}` — e.g. `Provider/Anthropic`. Set its `Endpoint` (the provider's base URL) and paste the API **key** (stored encrypted at rest via `ProviderKeyProtector`; admins with `Permission.Api` only).
2. **Add models.** Add one `LanguageModel` child per model under the provider — `Provider/{ProviderName}/{modelId}` (e.g. `Provider/Anthropic/claude-opus-4-8`). A model can be listed live from the provider (see [Listing models](#listing-models-from-a-provider)) or added by id.
3. **Set the default.** Give the model you want the chat default `Order = -1`; clear any other default. The picker resolves the lowest-`Order` model as the default composer model.
4. **Verify.** Open chat → the `/model` picker lists the catalog → start a thread → it round-trips on the configured provider. Then **restart the portal pod and confirm the providers, keys, and default survive** — the proof that nothing is config-stamped.

### Listing models from a provider

When the GUI lists a provider's available models, it calls the provider's `GET {baseUrl}/models` endpoint. That outbound HTTP call goes through `IIoPool` (never `Observable.FromAsync`) per [Controlled IO Pooling](../Architecture/ControlledIoPooling) — the same bounded, off-hub edge every external call uses.

## During onboarding

A platform that has never had a provider configured prompts for one as an **onboarding step**: the operator creates the first `Provider/{name}` node (endpoint + key) and picks a default model, then chat is live. This is the same create flow as above, surfaced once at first run so a new deployment is never left with a dead chat.

## Per-deployment defaults (reference)

| Deployment | Default provider (`order:-1` model) | Notes |
|---|---|---|
| atioz | Anthropic (Claude via Azure Foundry) | endpoint `…/anthropic/…`, Foundry key |
| memex / memex-cloud | OpenRouter | `https://openrouter.ai/api/v1`, models auto-listed |

These are **mesh nodes**, set per deployment through the catalog — not baked into any config. A user can additionally configure **their own** providers/models in their dotfile namespace `{user}/_Memex/…`; their selection of which providers are active is stored at `{user}/_Memex/Selection`.

## See also

- [Model Providers](../Architecture/ModelProviders) — the full data model + credential resolution
- [Model Provider Setup](ModelProviderSetup) — provider/model node shapes
- [NodeType Catalogs](../Architecture/NodeTypeCatalogs) — why the catalog is rooted the way it is
- [Controlled IO Pooling](../Architecture/ControlledIoPooling) — how outbound provider API calls are bounded
