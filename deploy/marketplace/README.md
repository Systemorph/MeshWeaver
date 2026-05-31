# Memex — Azure Marketplace (Azure Application)

This folder packages the Memex portal as an **Azure Application** offer (customer deploys into
their own subscription; they own the infra + data; we ship updates as new offer versions).

## What's here

| File | What |
|---|---|
| `mainTemplate.json` | The ARM template, **generated** from the Aspire model — not hand-authored. |
| `createUiDefinition.json` | The deploy-wizard UI (region, sizing, AI providers + key, master key, self-onboarding). |

## How `mainTemplate.json` is generated (single source of truth = Aspire)

```bash
# 1. Aspire emits the ACA bicep from the dedicated image-based AppHost's `azure` mode:
aspire publish --apphost deploy/aspire/Memex.Deploy.AppHost/Memex.Deploy.AppHost.csproj \
    -o deploy/aca -- --mode azure
# 2. Convert bicep -> ARM JSON:
az bicep build --file deploy/aca/main.bicep --outfile deploy/marketplace/mainTemplate.json
```

The bicep (`deploy/aca/`) and this ARM both come from the same `AddMemex` model that produces the
Docker Compose (`deploy/compose/`) and Helm (`deploy/helm/`) artifacts — one model, three targets.

## 🚧 Reconciliations required before this is a publishable offer

The generated template proves the pipeline but is **not yet a turn-key Marketplace solution
template**. Four gaps, each tracked:

1. **Deployment scope.** The generated `mainTemplate.json` is `subscriptionDeploymentTemplate`
   (`targetScope = 'subscription'`, it *creates* the resource group — the azd/Aspire pattern). A
   Marketplace **solution template** is **resource-group-scoped** (the RG is chosen in the wizard's
   Basics step). Either adapt to RG-scope (drop the `Microsoft.Resources/resourceGroups` resource +
   re-scope the nested deployments) or publish as a **Managed Application** (which accepts a
   subscription-scoped appliance). The latter also lets us retain ops access if desired.

2. **Parameterize customer inputs.** The generated ARM bakes the app config (image tag, the
   `Ai:KeyProtection:MasterKey`, model-provider key, `Features:*`, self-onboarding) as **literals**
   inside the module resources, so `createUiDefinition` has nothing to bind to yet. Surface them as
   Aspire **`AddParameter(...)`** in `Memex.Deploy.AppHost` (Aspire renders parameters as ARM
   `parameters`, secrets as `secureString`); then wire `createUiDefinition.json` `outputs` →
   those params. The wizard here already collects the intended inputs.

3. **Managed Postgres.** The image-based `AddMemex` runs a **pgvector container** (great for
   Compose/Helm self-host). For a production Azure offer, switch the Azure target to **Azure
   Database for PostgreSQL Flexible Server** (the `Backend=Azure` branch of `AddMemex` — the
   Azure-unification work) so the customer's data lives on managed, backed-up infra.

4. **Public images.** `mainTemplate.json` references `ghcr.io/systemorph/memex-portal-ai` +
   `memex-migration`. These must be **publicly pullable** by arbitrary customer subscriptions —
   built + pushed by the GHCR CI workflow.

## Non-code, your action (long lead)

- Microsoft **Partner Center** account enrolled in the Commercial Marketplace program.
- Offer listing assets (logo, screenshots, description, privacy/terms URLs, support contact).
- A test tenant for **Preview** validation, plus **ARM-TTK** (`Test-AzTemplate`) on the packaged
  offer before go-live.

## Validate locally

```bash
az deployment group validate --resource-group <rg> --template-file mainTemplate.json   # after RG-scope reconcile
# ARM-TTK:
Test-AzTemplate -TemplatePath deploy/marketplace
```
