---
Name: Domain Configuration Apps
Category: Architecture
Description: Configuration and secrets live in the app that owns their domain — AI, Databases, Sign-in, Email, Payments, Integrations — never as ad-hoc panels and dialogs on the Deployment record page. The app map, where each app lives (the control instance per deployment, and each instance's Admin app for its own settings), what each reads and writes, who may use it, how a change reaches the running portal, and the inventory of every surface that exists today and where it moves.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="3" width="7" height="7" rx="1.5"/><rect x="14" y="3" width="7" height="7" rx="1.5"/><rect x="3" y="14" width="7" height="7" rx="1.5"/><rect x="14" y="14" width="7" height="7" rx="1.5"/></svg>
---

# Domain Configuration Apps

**Configuration and secrets live in the app that owns their domain** (policy
[`domain-config-apps`](../PolicyNotProse)). There is one app for AI providers and their keys, one for
databases, one for sign-in, and so on. A person who wants to change the AI setup of a deployment opens
the **Models** app (the AI domain's fleet app), not the deployment's record page, and finds every AI
setting and every AI key there.

This page is the design: the inventory of what exists today, the app map, and what each app reads,
writes and gates. It builds on three existing pieces and invents none of their parts:

- [The Admin App](../AdminApp): `/Admin` on every instance, the home of instance-level admin settings;
- [Secrets: Write-Only Entry, Split Identities](../SecretsWriteOnlyEntry): every secret is entered
  through a write-only control and written by the governed writer identity, never by hand;
- [Instance Secrets](../InstanceSecrets): the platform's `WriteOnlySecretSection` /
  `SecretInventorySection` controls and the instance's own encrypted store.

## Why: the record page became a junk drawer

Every new need for a config value or a secret on a live deployment landed as another button, dialog
or panel on the control instance's `Deployments/<id>` page: **Set Key Vault secrets…** (a paste
dialog over every vault object the record names), a vault-secrets inventory, an announcement-key
panel. Each was reasonable alone. Together they made the record page the one place where everything
is typed, with no grouping by what the value is FOR. Someone rotating an OpenRouter key had to know
which vault object name the chart maps to `OpenRouter__ApiKey`. Someone checking a deployment's EU
residency had to read JSON.

The domain is the unit people think in. The apps group by domain, and the record page goes back to
being the record: identity, status, fields, and links to the apps.

## Two scopes, one set of domain names

A setting belongs to exactly one of two scopes, and the scope decides where its app lives:

| scope | what it is | changed by | reaches the portal | lives in |
|---|---|---|---|---|
| **fleet** — a deployment's recorded shape | the `Deployments/<id>` record's blocks (`ai`, `signIn`, `email`, `gitHubApp`, database fields, …) and the vault objects the record maps into the pod | a global admin of the **control instance** | a governed `Reconcile` (record → helm values → rollout), or a governed `SetSecrets` (vault write, optional restart) | the **domain apps** on the control instance, one page per deployment |
| **instance** — what an instance changes about itself, live | the instance's own mesh: `Admin/Secret-*` (InstanceSecrets), the `Provider/*` catalogs, `Admin/UpdatePolicy`, … | a global admin of **that instance** | at once, no restart (the value is read from the mesh) | that instance's **Admin app** (`/Admin/Settings/{tab}`) |

Both scopes use the **same six domain names**, so a person finds the same word in both places: AI,
Databases, Sign-in, Email, Payments, Integrations.

### Why the fleet apps are separate apps and not Admin-app tabs

The Admin app exists on every instance and administers THAT instance. A fleet domain app administers
OTHER instances, one deployment at a time. It exists only where the Hosting package is installed (the
control instance), and every page carries a deployment. Putting it inside `/Admin/Settings` would mix
"this instance" and "that deployment" on one page, which is the same confusion the record page had.
So the control instance's Admin app LINKS to the six fleet apps; it does not host them.

## The app map

### Fleet apps — control instance, Hosting package

Each app is one node in the Hosting package: the NodeType `Hosting/{X}App` (sources
`Hosting/{X}App/Source/`) and the app node `Hosting/{X}` of that type. All six share one shell,
`Hosting/DomainApp/Source` (the picker, the record resolution, the admin gate, **Apply**). The
default area `Deployments` is a **deployment picker** (every `Hosting/Deployment` record the viewer
may read). The per-deployment page is the area `Deployment` with the deployment id as area id, so
every page is deep-linkable.

| app | address | reads and writes on the record | vault objects (write-only) | notes |
|---|---|---|---|---|
| **Models** (the AI domain) | `/Hosting/Models` → `/Hosting/Models/Deployment/{id}` (the former `/Hosting/Ai/…` addresses redirect here) | `ai`: providers (`anthropic`, `azureAis`, `azureFoundry`, `openRouter`, `openRouterEU`: endpoint, models, order, enabled, `dataResidency`, `dataRetention`), `requiredDataResidency`, `tiers` (heavy/standard/light/utility) | the objects mapped to `{Section}__ApiKey` (`Anthropic__ApiKey`, `AzureFoundry__ApiKey`, `OpenRouter__ApiKey`, …) | the EU/origin badges; the EU route holds no key of its own (it borrows OpenRouter's) |
| **Databases** | `/Hosting/Databases` → `/Hosting/Databases/Deployment/{id}` | `database`, `databaseServer`, `databaseHost`, `databasePort`, `databaseUsername`, `inClusterPostgres`, `inClusterDatabase`, `migrationImageRepository`, `backupStore` | `databaseConnectionSecret` (default `{prefix}db-connection`), the Orleans connection, `storageConnectionSecret` | read-only migration status (the schema version and the last migration Job's verdict) and the backup list; a restore stays a governed `Restore` action |
| **Sign-in** | `/Hosting/SignIn` → `/Hosting/SignIn/Deployment/{id}` | `signIn`: Microsoft client id + tenant, Google, LinkedIn, Apple client ids, `enableDevLogin` | `Authentication__{Microsoft,Google,LinkedIn}__ClientSecret`, `GitHub__OAuth__ClientSecret`, `Bootstrap__Secret` | an empty client id turns a scheme off; blank leaves it to the vault half (see `SignInSpec`) |
| **Email** | `/Hosting/Email` → `/Hosting/Email/Deployment/{id}` | `email`: enabled, sender app client id, tenant, mailbox, managed identity, inbound, webhook base URL, forward address | `Email__ClientSecret` | the agent-send mode (`Email:AgentSend`) is instance config, not a secret |
| **Payments** | `/Hosting/Payments` → `/Hosting/Payments/Deployment/{id}` | the commerce base URL (today an `extraPortalConfig` entry; owed: a `payments` block on the record) | `Commerce__Stripe__SecretKey`, `Commerce__Stripe__WebhookSecret` | live vs test is DERIVED from the key prefix (`sk_live_` / `sk_test_`), so it is shown, never toggled; the webhook secret is generated in the portal and shown once, because Stripe must hold it too |
| **Integrations** | `/Hosting/Integrations` → `/Hosting/Integrations/Deployment/{id}` | `gitHubApp`, `opsGitHubApp` (client id, installation id, owner), `webhookInbox` slots, `registryInstanceId`, `announcementKeySecret`, `controlLaneKeySecret` | `GitHub__App__PrivateKey`, `GitHub__Webhook__Secret`, the registry instance key (issued straight into the vault, never read back), the per-deployment announcement key | the announcement key is **generated on the target instance** and only **registered** here (below) |

**The Models app was called the AI app** and lived at `/Hosting/Ai`. The node `Hosting/Ai` is now a
`Redirect` (`scope: Subtree`, see [Moved-Node Redirects](../NodeRedirects)), so a bookmark to
`/Hosting/Ai/Deployment/{id}` lands on `/Hosting/Models/Deployment/{id}`. Links written from now on
use the new address; the Deployment record page builds its link with
`DeploymentRecordPage.AppHref("Models", id)`.

### Instance scope — each instance's Admin app

These are tabs of `/Admin` (see [The Admin App](../AdminApp)), listed here by domain — the app itself
groups its tabs by section (People & sign-in, Operations, Commercial, Fleet):

| domain | tab | what it changes live |
|---|---|---|
| AI | `/Admin/Settings/AiAdmin` (exists) | the instance's model credit bill and provider catalog; the catalogs themselves stay on the AI menu (`/Provider/AiModels`, tiers, providers) |
| Sign-in | `/Admin/Settings/SignInProviders` (exists) | shows the effective schemes; the first-run hand-over |
| Integrations | `/Admin/Settings/ControlLane` (exists) | the instance's announcement key: generated here, its fingerprint shown, **Test connection** |
| Databases | owed: `/Admin/Settings/Database` | read-only: schema version, the last migration, storage backend |
| Email | owed: `/Admin/Settings/Email` | read-only: whether mail is on, the mailbox, inbound state, the last send's result |
| Payments | the Store's payments admin (the Store is its own app) | the payment-path audit |

**A person's own AI keys are neither.** They belong to the person, not to an admin, and stay in the
person's providers app (`Providers/ProvidersApp`, the `ProviderSetup` view: `enc:` in the mesh,
write-only).

### The announcement key: generated on the target, registered on the control instance

> **Target design, not yet the procedure.** Until MeshWeaver.Plugins#2435 lands, the shipped
> procedure is the one in [Self-Update Announcement Key](../SelfUpdateAnnouncementKey): the control
> instance ISSUES the key and the target's administrator enters it. Follow that page to operate
> today. The change set that moves the flow here also rewrites that page and the
> [Instance Secrets](../InstanceSecrets) summary.

The key that signs a deployment's self-update hand-over is generated where it is used: on the target
instance, in its Admin app's **Control lane** tab (InstanceSecrets `Hosting:ControlInbox:Secret`,
live, no restart). The control instance only has to VERIFY signatures, so it holds the same value
under the per-sender slot `Hosting:PlatformWebhookSecret:{id}`. The Integrations app's
**Announcement key** section is a `WriteOnlySecretSection` over that slot: an admin pastes the key
the target generated, and the page shows both fingerprints side by side, so a match is visible
without either value being shown. The deployment record keeps only the vault object's NAME
(`announcementKeySecret`) for the case where the key is mounted rather than entered.

## What every fleet app page is made of

Every per-deployment page is the same three pieces, and none of them is new:

1. **The record's block, bound to the record.** The form is the framework's form generator over the
   block's type (`ModelProvider`, `ModelTiers`, `SignInSpec`, `EmailSpec`, …) with a node-bound data
   context pointing INTO the record's content: `LayoutAreaReference.GetMeshNodeDataContext(recordPath,
   bindContent: true, subPath: "ai/openRouter")`. Every field reads from the record's node
   stream and writes one field back through `GetMeshNodeStream(recordPath).Update(...)`. There is no
   `/data` copy and no Save button (see [Data Binding](/Doc/GUI/DataBinding)). A viewer without
   `Update` on the record gets the same form read-only.
2. **The domain's vault objects, write-only.** A `SecretInventorySection` over the record's declared
   vault objects whose config key belongs to the domain (the Models app claims `*__ApiKey` of the AI
   sections, the Sign-in app `Authentication__*__ClientSecret`, …). Save and Generate file a governed
   `SetSecrets` action; Disable/Enable/Delete/Recover file a `SecretLifecycle` action; the status
   list is the operator's `hosting-kv-status` reading (names, states, fingerprints; never a value).
   A domain claims config keys by prefix (`VaultSecretDomains`, next to the Plugins
   `VaultSecretSteward` that files the actions), so a module that adds a provider adds its key to
   the Models app by naming it, with no change to the app. An object no domain claims is counted on the
   picker, never dropped silently. 🚨 **`Ai__KeyProtection__MasterKey` belongs to NO domain**,
   although its name starts with `Ai__`: it encrypts every `enc:` value on the instance (instance
   secrets, every person's provider keys), and replacing it makes all of them unreadable. It is a
   platform root key, changed only as break-glass, and no app lists it.
3. **Apply.** A button that files a governed `Reconcile` for this deployment. The record edit alone
   changes nothing on the running portal: a pod reads its configuration at start. Reconcile renders
   the record into helm values, runs the upgrade, and restarts when a secret class changed. It needs
   approval like every other non-read action, so the page shows the action it filed and links to it
   rather than a success message.

Everything on the page is platform controls in a real layout (`Stack`, `DataGrid`, the form
generator, `WriteOnlySecretSection`); no page builds its own form or markup.

## Adding a domain app: the shape, and the traps already paid for

Each app is a thin copy of `Hosting/AiApp` (MeshWeaver.Plugins — the Models app's NodeType kept its
original id when the app was renamed; only its node, address and name moved): a NodeType `Hosting/{X}App` whose
sources list `shared=@Hosting/DomainApp/Source` (the picker, the page frame, the admin gate, Apply),
an app node `Hosting/{X}`, a form record that mirrors the record's block with `[Description]` +
`[Translation]` (the Deployment contract assembly cannot carry `[Translation]`), a module text
table in English and German, and a `Test/` folder whose cases the `meshTests` lane runs.

- **One content record PER APP.** `validate-repos` refuses a content type claimed by two NodeTypes
  (it would stay an untyped `JsonElement`), so the shell carries none; each app declares its own
  `{X}AppContent`.
- **The picker declines node provenance** (`DomainAppShell.BoardProvenance`): it is a board over
  other records, and the app node's created/updated says nothing about them.
- **A partial listing is a fault, not a fleet.** The shell turns a frame with silent providers or an
  empty coverage into `Listing.Fault` and shows it above whatever rows came back.
- 🚨 **An app's `Test/` code references ONLY its own `Test/` folder.** The in-mesh compile of
  `Hosting/{X}App` includes the folders its NodeType lists — its own `Source`/`Test` and the shared
  `Source` folders (`Hosting/DomainApp/Source`, `Hosting/Deployment/Source`, …) — but never another
  type's `Test/`. A case that calls a helper from `Hosting/Deployment/Test` compiles locally and
  fails in CI with `CS0103` (measured on MeshWeaver.Plugins#2473's first run). The local
  `MeshWeaver.Fleet.Control` build cannot catch it: it compiles every Hosting folder into ONE
  assembly, so every `Test/` class is visible to every other. Copy the helper into the app's own
  `Test/` folder, or move it to a shared `Source` folder if production code needs it too.
- **Hosting's minor version is main + 1 at the merge you do after another app lands** — two app
  pull requests in flight otherwise both claim the same number.

## Who may use which app

| surface | gate |
|---|---|
| the fleet apps (control instance) | global admin of the control instance (`hub.IsGlobalAdmin()`), checked live on every area; the Hosting package itself is enterprise-tier. Writing the record additionally needs `Update` on the record, and every governed action is authorised again by the control plane when it runs. Destructive secret operations (Delete, Purge) need a second approver. |
| the Admin app tabs (each instance) | global admin of THAT instance: an `Admin` grant in `Admin/_Access` (see [The Admin App](../AdminApp)) |
| a person's own provider keys | the person |

A global admin of a customer instance is not a global admin of the control instance, so a customer
can never change its own recorded shape through a fleet app. That is intentional: the record is the
operator's contract.

## How a change reaches the running portal

| change | path | when it is live |
|---|---|---|
| a record field (any block) | the field edit writes the record → **Apply** files `Reconcile` → approval → helm upgrade → rollout | after the rollout completes |
| a vault object | `SetSecrets` (`hosting-kv-set --wait`) → approval → the CSI driver syncs → **Restart after** rolls the pods | after the restart; without it, at the next pod start |
| an instance secret (Admin app) | InstanceSecrets write, as system after the admin check | at once, on every replica, with no restart |

The record is git-synced to Systemorph/Memex, so every fleet edit is also a commit, and the history
of a deployment's AI setup is that file's history.

## What the Deployment record page keeps

Identity, purpose, status and audit strips, the field table, the outbound links, and ONE link row to
the six domain apps for this deployment. It carries no secret entry of its own: the **Set Key Vault
secrets…** dialog, the vault-secrets inventory button and the announcement-key panel move to the
apps above.

## Inventory: every entry point today, and where it moves

| today | where | kind | moves to |
|---|---|---|---|
| **Set Key Vault secrets…** (`SecretDialog`) | control instance, `Deployments/<id>` page | paste values for every declared vault object; files `SetSecrets` | each domain app's vault section, by config key; the dialog is removed |
| vault-secrets inventory (MeshWeaver.Plugins#2434, draft) | same page | status, generate, lifecycle | its verbs (`SecretStatus`, `SecretLifecycle`, the compose functions) are what every domain app's vault section files; the page button is removed |
| announcement-key panel (MeshWeaver.Plugins#2435, held) | same page | issue/rotate/revoke | Integrations app, **Announcement key** section, register-only |
| the record's `ai` block | edited as raw record content | JSON | Models app, bound form |
| EU residency (MeshWeaver.Plugins#2448): the region/origin badges, the EU-only switch | chat model picker, model catalog columns | read + `RequiredDataResidency` | the badges stay where models are picked; the per-deployment switch (`ai.requiredDataResidency`) and the per-provider `dataResidency` are edited in the Models app, which shows the same badges |
| coding-assistant harnesses (Claude Code, GitHub Copilot, Codex, Cursor, Grok, OpenCode, Antigravity) | were separate Store apps with their own home tiles; which ones run is `Features:Ai:Clis:*` plus the loaded modules | config + per-user install | options of the **Threads** app (`AI/AiThreads`): each harness shows whether this instance offers it, its processing and origin marks, install, and its settings; per thread the choice is `/harness`. Owed: the fleet switch as an `ai` field in the Models app |
| OpenRouter EU route (MeshWeaver.Plugins#2454) | seeded from config (`OpenRouterEU__*`) | config | Models app, the `openRouterEU` provider row (endpoint override, models, residency, retention); no key field, because the route borrows OpenRouter's |
| per-user provider keys | `Providers/ProvidersApp` (`ProviderSetup`) | write-only, `enc:` in the mesh | stays: it is the person's, not an admin's |
| AI Admin, Token Usage | `/Admin/Settings/AiAdmin`, `/Admin/Settings/TokenUsage` | instance | stays (instance scope, domain AI) |
| sign-in providers | `/Admin/Settings/SignInProviders` | instance, read + hand-over | stays; the fleet half is the Sign-in app |
| first-run setup wizard | `/setup` (pre-mesh), the provisioned wizard | writes `instance.json` / the setup hand-over | stays: it runs before any app exists. It asks for the same domains in the same order. |
| database fields, connection secrets | Fleet Console **Set up a new instance** dialog, the record | provision-time | provisioning stays in the Fleet Console; a LIVE deployment's database settings move to the Databases app |
| migrations | the helm migration Job, `DbVersionGate` ([Planning a Database Migration](../PlanningADatabaseMigration)) | read | Databases app shows the status; running one stays a governed action |
| Email | the record only | JSON | Email app |
| Stripe keys | vault objects only, no UI | — | Payments app |
| GitHub App ids, private key, webhook secret | the record + vault, no UI | — | Integrations app |
| control-lane key (instance side) | `/Admin/Settings/ControlLane` | instance, generated there | stays |
| registry instance key | `RotateRegistryKey` / `RevokeRegistryKey` actions | governed | Integrations app lists the object and files those actions |

## Build order

1. **Models app** (the AI domain, `/Hosting/Models`) — the most-edited block, and the one the EU residency work already reshaped. First,
   end to end: picker, bound `ai` form with the badges, the AI vault section, Apply.
2. **Integrations** — it unblocks MeshWeaver.Plugins#2435 (the announcement key), which is held for
   exactly this.
3. **Sign-in**, then **Email** — small bound blocks and one or two secrets each.
4. **Databases** — the widest: fields, three secrets, migration status and backups.
5. **Payments** — needs a `payments` block on the record first, so it is last.

After each app lands, the Deployment page loses the matching surface. The dialog goes when the last
domain claims its last key.

## Related

- [The Admin App](../AdminApp) — the instance-scope home
- [Secrets: Write-Only Entry, Split Identities](../SecretsWriteOnlyEntry) — the vault half
- [Instance Secrets](../InstanceSecrets) — the instance's own store and the write-only controls
- [Self-Update Announcement Key](../SelfUpdateAnnouncementKey) — the key the Integrations app registers
- [Deployment on AKS](../DeploymentAKS) — how the record renders into the chart
- [Data Binding](/Doc/GUI/DataBinding) — the node-bound form every app page uses
