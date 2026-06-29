---
Name: Feature Flags
Description: "Deploy-time capability toggles for a Memex deployment — the complete Features configuration reference (AI providers/CLIs, onboarding mode, Orleans clustering), how they are bound, and how to set them."
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 15s1-1 4-1 5 2 8 2 4-1 4-1V3s-1 1-4 1-5-2-8-2-4 1-4 1z"/><line x1="4" y1="22" x2="4" y2="15"/></svg>
Category: Architecture
---

# Feature Flags

Memex deployments are shaped by **deploy-time capability toggles** bound from the `Features`
configuration section into `MemexFeatureOptions`.
A flag declares which capabilities a deployment ships — independent of whether a given credential
key happens to be present. **A disabled flag is the operator's intent and wins even if a key is
configured.**

> **No-regression default.** Every flag defaults to its permissive value, so an **absent**
> `Features` section preserves current behaviour. Operators turn capabilities *off* (or opt *in*
> to invitation-only) explicitly.

---

## How flags are bound

`MemexConfiguration.ConfigureMemexServices` binds the section once at startup so application code
resolves the toggles through standard DI (`IOptions<MemexFeatureOptions>`):

```csharp
services.Configure<MemexFeatureOptions>(
    builder.Configuration.GetSection(MemexFeatureOptions.SectionName)); // "Features"
```

Consumers inject `IOptions<MemexFeatureOptions>` (e.g. the onboarding gate in `Onboarding.razor`)
rather than re-reading configuration ad hoc.

### Where values come from

Configuration is layered (last wins):

1. `appsettings.json`
2. `appsettings.{Environment}.json`
3. **Environment variables** — section nesting uses the double-underscore form
   (`Features__Ai__Providers__OpenAI=false`). This flows identically through Azure Container Apps
   env, Kubernetes `env`, Docker-compose `.env`, and ARM `createUiDefinition` → container env.

---

## Reference

| Key | Type | Default | Effect |
|---|---|---|---|
| `Features:Ai:Providers:Anthropic` | bool | `true` | Ships the in-process Anthropic chat provider (bring-your-own-key). |
| `Features:Ai:Providers:AzureFoundry` | bool | `true` | Ships the Azure AI Foundry provider. |
| `Features:Ai:Providers:AzureOpenAI` | bool | `true` | Ships the Azure OpenAI provider. |
| `Features:Ai:Providers:OpenAI` | bool | `true` | Ships the OpenAI provider. |
| `Features:Ai:Clis:ClaudeCode` | bool | `true` | Ships the co-hosted Claude Code CLI provider (per-user Connect login). |
| `Features:Ai:Clis:Copilot` | bool | `true` | Ships the co-hosted GitHub Copilot CLI provider. |
| `Features:Onboarding:AllowSelfOnboarding` | bool | `true` | When `false`, registration is **closed** — only the first-ever user may onboard. |
| `Features:Onboarding:InvitationOnly` | bool | `false` | When `true`, only an email with a Pending invitation may onboard. See [Invitation-Only Onboarding](/Doc/Architecture/InvitationOnlyOnboarding). |
| `Features:Orleans:Clustering` | string | `AzureTables` | Cluster-membership provider: `AzureTables`, `AdoNet` (PostgreSQL), or `Localhost` (single in-process silo; dev only). |

A related, separate section — `Email` — configures outbound system mail (used by invitations). It
is documented in [Invitation-Only Onboarding → Email](/Doc/Architecture/InvitationOnlyOnboarding#sending-email-microsoft-graph).

---

## AI flags — symmetric gating

AI provider/CLI flags gate **two tiers symmetrically** so a provider can never half-register
(which would crash on first use):

- **Services tier** — the in-process `IChatClientFactory` / CLI Connect strategy registration in
  `ConfigureMemexServices`.
- **Mesh tier** — the catalog source in `ConfigureMemexMesh`.

```csharp
if (features.Ai.Clis.ClaudeCode)
    services.AddClaudeCode(config => builder.Configuration.GetSection("ClaudeCode").Bind(config));
// …and the matching catalog source is gated by the same flag in ConfigureMemexMesh.
```

`MemexFeatureOptions.HasAnyChatCapability` is `true` when the deployment ships at least one provider
**or** one CLI. When false the portal has no built-in chat via catalog sources (users may still
bring their own keys via Model Providers) — surfaced as a startup warning, not a hard failure.

API providers work **bring-your-own-key** (users add endpoint + key per provider under
**Settings → Models**); see [Model Providers](/Doc/Architecture/ModelProviders). The co-hosted CLIs require the
per-user Connect login.

---

## Onboarding modes

The two onboarding flags combine into three modes. The **first-user bootstrap exception** always
applies: a brand-new deployment with zero existing User nodes always lets the very first user
onboard (and become platform admin), so the platform can never lock itself out.

| `InvitationOnly` | `AllowSelfOnboarding` | Mode | Who may onboard |
|---|---|---|---|
| `false` | `true` (default) | **Open** | Anyone who authenticates. |
| `false` | `false` | **Closed** | First user only — everyone else sees "Registration Closed". |
| `true` | *(any)* | **Invitation-only** | Only an email with a Pending invitation (plus the first user). |

`InvitationOnly` **takes precedence**: when it is on, the gate is "has a Pending invitation"
regardless of `AllowSelfOnboarding`. The security boundary is enforced at the `CreateUser` call in
`Onboarding.razor` (not just the UI). Full treatment: [Invitation-Only Onboarding](/Doc/Architecture/InvitationOnlyOnboarding).

---

## Examples

**appsettings.json** — close self-registration and require invitations:

```json
{
  "Features": {
    "Onboarding": { "AllowSelfOnboarding": false, "InvitationOnly": true },
    "Ai": { "Providers": { "OpenAI": false } }
  }
}
```

**Environment variables** (ACA / compose / ARM):

```bash
Features__Onboarding__InvitationOnly=true
Features__Ai__Providers__OpenAI=false
Features__Orleans__Clustering=AdoNet
```

**Kubernetes** (set on the running deployment):

```bash
kubectl -n memex set env deployment/memex-portal-deployment \
  Features__Onboarding__InvitationOnly=true \
  Features__Ai__Clis__Copilot=false
```

See [Memex Cloud Deployment → Enable / Configure AI Providers](/Doc/Architecture/MemexCloudDeployment) for the
full operational walkthrough.

---

## Related

- [Invitation-Only Onboarding](/Doc/Architecture/InvitationOnlyOnboarding) — the `InvitationOnly` flag end-to-end + `Email` configuration.
- [Extensible Defaults](/Doc/Architecture/ExtensibleDefaults) — system defaults + mesh-level extensions (the pattern behind several capabilities).
- [Memex Cloud Deployment](/Doc/Architecture/MemexCloudDeployment) — operating these flags in production.
