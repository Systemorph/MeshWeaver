---
Name: The Admin App and the Settings App
Category: Architecture
Description: /Admin is the Admin app — every platform-admin surface in one place, installed on every instance including the control image — and /{user}/Settings is the person's own settings app. What moved where, the gate on each, the redirects that keep old links working, the Inbox app every user gets, and how it reaches an instance.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z"/><circle cx="12" cy="11" r="3"/></svg>
---

# The Admin App and the Settings App

Two apps, one split rule:

- **`/Admin` — the Admin app.** Everything only a platform administrator may see or change. Nothing
  personal.
- **`/{user}/Settings` — the person's own settings app.** The signed-in person's own things. Nothing
  admin-only, and nobody else may open it.

A platform administrator is exactly what [Access Control](../AccessControl) defines: an `Admin`-role
grant in `Admin/_Access`, read through `hub.IsGlobalAdmin()`. It is not a data superuser, and nothing
here changes that.

## What `/Admin` was, and why

The node at `Admin` is the root of the system partition that holds the platform-admin grants
(`Admin/_Access`), the update policy (`Admin/UpdatePolicy`), the setup-link claim (`Admin/SetupLink`)
and the platform catalogs. Until this change no code registered that node, so:

- on a **fresh database** (a freshly installed control instance) there was no row, and path resolution
  synthesized a TYPELESS placeholder root (`PathResolutionService.SynthesizePartitionRoot`) whose hub
  activated on the default configuration — an empty page;
- on **long-lived databases** the row existed as a `Space` (measured on memex.systemorph.com and
  memex.meshweaver.cloud, both `nodeType: Space`, no content), which the `auth` mirror put into the
  Spaces catalog — so a platform admin found "Admin" among their workspaces, and `/Admin` rendered an
  empty Space.

## What `/Admin` is now

`AdminAppNodeType` (core, `MeshWeaver.Graph`) registers the `AdminApp` NodeType **and** the `Admin` root
node in code. Its default area is the per-node Settings page of the Admin node, so `/Admin` opens the
app and every tab is linkable as `/Admin/Settings/{tabId}`. The root is hidden from search, create and
content listings. Long-lived databases are retyped by migration **V59** (MeshWeaver.Plugins,
`Memex.Database.Migration`): the `Space` row becomes `AdminApp` and its stale `auth` mirror row is
removed; nothing else in the partition is touched.

A module adds a tab with `config.AddAdminAppTab(definition)`: the tab appears only on the Admin hub
and exactly while `IsGlobalAdmin` says the viewer is a platform admin — the verdict is observed LIVE
(`AdminAppNodeType.LiveAdminVerdict`), so a grant revoked while the app is open removes the tab, and
the "Who am I" grid follows the same stream. A data-contributed tab (a
`UiContribution`) uses `Context: NodeSettings` with `Gates: { AdminOnly: true, NodeTypes: [AdminApp] }`.

**A non-admin opening `/Admin`** is refused by the partition itself — the Admin partition is readable
only by platform admins, exactly as before — and even if the page rendered, no administration tab
would: every one waits for a positive admin verdict. Nothing about the app widens a read on
`Admin/_Access`; the "Who am I" answer a non-admin needs lives in their own settings app.

## Inventory — surface, before, after, gate

### Moved into the Admin app

| Surface | Before | Now | Gate |
|---|---|---|---|
| **Overview** — this instance: host, platform version (image tag), commit, runtime, plugin registry, registry instance id, closed type set; then the About page (build, "is it current?", installed plugins) | did not exist (the About part is the global About tab, which stays) | `/Admin` (first tab, `Overview`) | platform admin |
| **Who am I** — user id, name, e-mail, how the session was established, platform admin yes/no with the grant path, effective permissions on the own partition and on `Admin` | only `whoami` over MCP / REST | `/Admin/Settings/WhoAmI` (and `/{user}/Settings/Account`) | platform admin (Admin app); the owner (settings app) |
| **Global Administration** — the `Admin/_Access` grants and "+ Add Admin" | `/{user}/Settings/GlobalAdmin` on the admin's own page | `/Admin/Settings/GlobalAdmin` | platform admin |
| **Data Sources** | `/_Setting/GlobalSettings/DataSources` — the FIRST tab every signed-in user landed on — and a second list inside Global Administration | `/Admin/Settings/DataSources` | platform admin |
| **Access Control / Effective Access** of the Admin partition | not reachable (the root was an empty page) | `/Admin/Settings/AccessControl`, `/Admin/Settings/EffectiveAccess` | permission on `Admin` (platform admins) |
| **Registration** ("This instance" — consent, registry registration, plan, catalogue) | area `/{user}/Instance`, tile `{user}/_App/Instance` | `/Admin/Settings/Registration`; the old address redirects | platform admin |
| **Instances** (AKS overview, control instance) | a tab on EVERY node's settings page for an admin | `/Admin/Settings/Instances` | platform admin + `Instances:Enabled` |
| **Partitions** (partition sync overview) | `/{user}/Settings/PartitionSync` | `/Admin/Settings/PartitionSync` | platform admin |
| **Sign-in providers** | `/{user}/Settings/SignInProviders` | `/Admin/Settings/SignInProviders` | platform admin, and only on the Admin hub (`SignInSetupAccess.Decide`) |
| **Invitations**, **Inbox** (non-user mail), **Updates** (`Admin/UpdatePolicy`), **Published to the web**, **Privacy** (statement editor) | `/_Setting/GlobalSettings/{id}` | `/Admin/Settings/{id}` | `AdminOnly` + `NodeTypes: [AdminApp]`; each area re-asserts the gate |
| **Token Usage** (instance-wide spend) | `{node}/Settings/TokenUsage` gated on `Permission.All` of the page's node — which every person holds on their own partition, so it showed on everybody's settings page | `/Admin/Settings/TokenUsage` | platform admin |
| **AI Admin** (model credit bill, providers) | `/{user}/Settings/AiAdmin` | `/Admin/Settings/AiAdmin` | platform admin |
| **Coupons**, **Instance grants**, **Composition** | `/{user}/Settings/{id}` | `/Admin/Settings/{id}` | platform admin |

### Stays in the person's own settings app — `/{user}/Settings`

| Surface | Gate |
|---|---|
| **Account** (Who am I, new) | owner |
| **Preferences** (time zone, language), **Notifications** (per feature), **API Tokens**, **MeshWeaver Instances** (keys for the person's own installations) | owner |
| the node's own tabs (Metadata, Access Control, …) | owner, then the tab's own permission |

The whole page now opens **for its owner only**: a User node is public-read, so before this change
any signed-in viewer could open another person's settings page and see every tab that demanded no
permission of its own. Another person gets a refusal.

### Stays on the global settings page — `/_Setting`

**About** and **What's New** — every signed-in viewer may read them, and the profile menu links them.

### Not moved, deliberately

- the notification bell's platform-addressed feed — chrome, already admin-gated, not a settings page;
- **Store** provisioning (`/Store`) — the Store is its own app and the only door to `SystemInstall`;
- the fleet app `Hosting/Admin` — a control-instance package, not part of every image;
- the AI menu catalogs (Models, Providers, Tiers) and a person's own AI bill — reached from the AI menu.

## Old links keep working

A relocated tab id is recorded on the hub configuration (`RelocateSettingsTabsToAdminApp`, done
automatically by `AddAdminAppTab`). A settings page — per-node or global — asked for such an id answers
with a redirect to `/Admin/Settings/{id}` instead of silently opening its first tab. The
`{user}/Instance` area redirects to the Registration tab, so every "This instance" tile already seeded
keeps working.

## Navigation

The profile menu offers **My settings** (`/{user}/Settings`) to everyone and **Administration**
(`/Admin`) to platform admins. The header's settings button at the root opens `/Admin` for a platform
admin and the person's own settings app for everyone else. (Both used to point at `/User/{user}…`,
a pre-v10 path.)

## The Inbox app — installed for every user

`/{user}/Inbox` renders the inbox legs (`InboxQueries`) for the owner — Needs you, Running, Recent — each
anchored on the owner's own partition and projected, each distinct query once. Running and Recent split
the owner's activities on the explicitly STAMPED terminal states (Running negates them, Recent matches
any of them): `Running` is the enum default and is omitted from stored content, so neither a positive
nor a negated match on it can tell the two apart. The tile's label is catalog text: the record carries
`App.LabelKey = inbox.title`, which the launcher resolves in the viewer's language, and its stored name
is seeded in the owner's. The seed's write re-establishes the owner's identity at the write site
(`RunAs`), since the runner may subscribe it on another action's completion thread. The tile
`{user}/_App/Inbox` is seeded by the run-once logon action `seed-inbox-app`, for the first
administrator of a fresh instance and every invited user alike, independently of
`Admin/HomeConfig.DefaultApps` (a deployment's own list cannot leave it out). Create-if-absent: a
re-run never duplicates, and a person who removes the tile keeps it removed. Because the ledger key is
new, a person who existed before gets the tile once, on their next sign-in.

## How it reaches an instance

All of it is compiled platform code — core (`MeshWeaver.Graph`, `MeshWeaver.PluginCatalog`,
`memex/Memex.Portal.Shared`) and the portal hosts and modules in MeshWeaver.Plugins — so it reaches an
instance by an **image roll**, and the roll runs migration V59 (`DbVersion.Latest` 59) before the new
pods serve. The control image (`Mesh:ClosedTypeSet=true`, Route A) is the same portal host, so the
`AdminApp` type — registered in code — is part of its closed type set; no package install is needed.
After the roll, a live `Admin` activation bound to the old (Space or typeless) configuration keeps
serving it until a `DisposeRequest` reaches it: recycle `Admin` once (see
[Stale State Until a Recycle](../StaleStateUntilRecycle)).
