---
Name: Settings by Owner — the Instance, Person and Node Apps
Category: Architecture
Description: Every settings tab lives in the app of the thing it CHANGES. /Admin is the instance app, titled with the instance's name; /{user}/Settings is the person app, titled with the person's name; a node's settings open from its ⋯ menu. The ownership rule, the mapping of every tab, the gates, the redirects that keep old links working, the Inbox app, and how it reaches an instance.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z"/><circle cx="12" cy="11" r="3"/></svg>
---

# Settings by Owner — the Instance, Person and Node Apps

**The rule: every settings tab lives in the app of the thing it CHANGES.** There are three owners, and
so three apps:

| App | Where | Titled with | Who opens it | What it holds |
|---|---|---|---|---|
| **Instance app** | `/Admin` (node type `AdminApp`) | the instance's name — its public host, e.g. `portal.example.com` (`AdminAppNodeType.InstanceName`) | platform admins only | what changes the INSTANCE |
| **Person app** | `/{user}/Settings` (the person's own partition root) | the person's name | the person only | what changes the PERSON |
| **Node settings** | the node's ⋯ menu → **Settings…** (`/{node}/Settings`) | the node's name | anyone who may read the node; each tab under its own permission | what changes THAT node |

Two corollaries the old pages broke:

- **A tab that acts on the signed-in person never appears on a node's or the instance's page.** API
  tokens, connected instances and notification preferences were registered on EVERY node hub with
  `Permission.None`, so they showed on every Space's settings page. Language and time zone were edited
  in two places. "Who am I" sat in the instance app.
- **A tab that acts on a Space appears only on the Space ROOT.** Node types, Groups, GitHub sync,
  GitHub Issues, the Code workspace and Git history were offered on every node below the root, each
  acting on the whole Space. And a per-node probe (Effective access) in the instance app read as a
  global answer it never was.

A platform administrator is exactly what [Access Control](../AccessControl) defines: an `Admin`-role
grant in `Admin/_Access`, read through `hub.IsGlobalAdmin()`. It is not a data superuser, and nothing
here changes that.

## The mapping

### Instance app — `/Admin`

| Section | Tab | Id | Owner |
|---|---|---|---|
| — | **Overview** — the instance facts no other tab carries (public host, closed type set), then the About page ONCE (version, commit, serving since, runtime, "is it current?", installed plugins) | `Overview` | core (`memex/Memex.Portal.Shared`) |
| People & sign-in | **Administrators** — the `Admin/_Access` grants, add and remove: ONE tab where there were two (Global Administration + the Admin node's default Access Control, which listed the same grants) | `GlobalAdmin` | core |
| People & sign-in | Sign-in providers | `SignInProviders` | Plugins |
| People & sign-in | Invitations · Privacy · Published to the web | `Invitations` · `Privacy` · `Published` | core (seeded `UiContribution`s) |
| Operations | Updates · Control lane · Inbox | `UpdatePolicy` · `ControlLane` · `Inbox` | core (seeded `UiContribution`s) |
| Operations | Data sources | `DataSources` | core |
| Operations | Registration (+ Control lane, merged Plugins-side) · Partitions | `Registration` · `PartitionSync` | Plugins |
| Commercial | Coupons · Instance grants · Composition | `Coupons` · `InstanceGrants` · `Composition` | core (`MeshWeaver.PluginCatalog`) |
| Commercial | AI usage & cost (Token usage + AI admin, one tab) | `TokenUsage` / `AiAdmin` | Plugins |
| Fleet | Fleet (the AKS instances overview; control instance / `Instances:Enabled` only) | `Instances` | Plugins |

The AI usage tab reads EVERY person's usage with a pathless `nodeType:TokenUsage` query. A usage row
is a `_Thread` satellite (`{ns}/_Thread/{id}/_Usage/{model}`, placed in the `threads` table by its
segment), so `TokenUsage` is listed on the `_Thread` entry of `SatelliteTableMapping.Defaults`: without
it the pathless query read the primary table on Postgres and, in memory, dropped every row as a
satellite path in a non-satellite query — the tab read nothing on either backend.

**Not in the instance app** any more: Who am I (the person app's Account), Effective access (a node
probe), the Admin node's own Access Control (part of Administrators), Metadata, Node types, Groups,
Versions, and every personal tab.

### Person app — `/{user}/Settings`

| Tab | Id | Order | Owner |
|---|---|---|---|
| **Profile** — picture, display name, e-mail, bio, links, showcase, contributed profile sections: the `/{user}/EditProfile` area embedded, never copied | `Profile` | 0 | core |
| **Account** — identity, how the session was established, platform-admin yes/no with the grant path, effective permissions on the own partition and on `Admin` (the view `whoami` answers over MCP and REST) | `Account` | 10 | core |
| **Preferences** — language, time zone AND theme, in ONE place (the profile no longer carries language and time zone) | `Preferences` | 20 | core |
| Notifications | `Notifications` | 30 | core (`memex`) |
| API tokens | `ApiTokens` | 40 | core (`memex`) |
| Connected instances — the person's own MeshWeaver installations and their keys | `MeshWeaverInstances` | 50 | core (`memex`) |
| Subscription | (Store) | 60 | Plugins |
| **Sharing** — Access control on the person's own partition root | `Sharing` | 70 | core |

**Not in the person app:** Metadata, Node types, Groups, Effective access, the raw Access control
(it is Sharing), Versions, Files, Partitions.

The page opens **for its owner only**: a User node is public-read, so another signed-in viewer gets a
refusal (`settings.ownSettingsOnly`), never the tabs that demand no permission of their own.

### Node settings — ⋯ → Settings…

| Tab | Where | Note |
|---|---|---|
| Metadata | every node | |
| Versions | every node | the Versions area, embedded — offered where that area has a renderer |
| Access control | every node | |
| **Check access** (`EffectiveAccess`) | every node | "what can this person do on THIS node" — labelled so |
| Node types · Groups | the Space ROOT only | `RestrictSettingsTabsToPartitionRoot` |
| GitHub sync · GitHub Issues & PRs · Code workspace · Git history | the Space ROOT only | also self-filtered to Spaces the viewer may update |
| Content indexing | the Space ROOT only | Plugins (registers through the same call) |

**Removed from node settings:** Appearance (the theme is the viewer's — person app, Preferences) and
Files (the ⋯ menu's **Files** opens the same browser). Stop/Resume synchronization stays a ⋯ entry.

The ⋯ entry is `MeshNodeLayoutAreas.GetSettingsMenuItem` in the default `$Menu:Node` provider (order
45, ⚙️, needs Read; on a person's root, the owner only), so every client that renders `$Menu:Node` —
Blazor, portal-next, React Native — gets it without code of its own.

### Not an app of anyone's — `/_Setting`

**About** and **What's New** stay on the global settings page: they describe the platform to every
signed-in viewer and change nothing.

## The API a module uses

| To … | call |
|---|---|
| add an instance-app tab | `config.AddAdminAppTab(tab)` — yields the tab only on `/Admin`, only while `IsGlobalAdmin` confirms the viewer LIVE (`AdminAppNodeType.LiveAdminVerdict`); put it in a section with `Group = AdminAppNodeType.{People,Operations,Commercial,Fleet}Group` (+ `…GroupKey`, order band `…Order`) |
| add a person-app tab | `config.AddPersonAppTab(tab)` — may ride every node hub; yields the tab only on the viewer's own root (`PersonApp.IsPersonAppHub`); slots are `PersonApp.*Order` |
| keep a Space tab on the Space root | `config.RestrictSettingsTabsToPartitionRoot(tabId)` |
| merge two tabs | `config.AliasSettingsTab(retiredId, survivingId)` on the hub whose page carries them |
| title an app | `config.WithSettingsTitle((host, node) => …)` |
| contribute a tab as DATA | a `UiContribution` with `Context: NodeSettings`; the instance app's are gated `Gates: { AdminOnly: true, NodeTypes: [AdminApp] }` |
| contribute a tab to ONE app's settings as DATA | a `UiContribution` with `Context: AppSettings` and `Host: <the app's path>` (`Admin` for an instance-app section, `AI/AiThreads` for Threads › Models), embedding `Area` of `Address` (inside the contribution's own partition; unset ⇒ the host's hub). It joins that host's settings page only; the closed gates still narrow it (`AdminOnly` for an Administration section). `UiContributionSeedValidation` reports a hostless one |
| contribute a PERSON-APP tab as DATA | a `UiContribution` with `Context: PersonApp`, embedding `Area` of `Address` (inside the contribution's own partition); add `Gates: { RequireAddressAccess: true }` for an [in-app extension](../InAppExtensions) so the tab shows only once the viewer holds it |

`AddAdminAppTab` / `AddPersonAppTab` providers yield nothing anywhere else, so it is safe — and was
always the practice — to register them on the default node hub.

**A data-contributed tab is gated against the viewer resolved on the RENDER turn.** The
settings page projects the `NodeSettings` contributions inside an `Observable.Defer` (so a hub
without a node stream fails into the lane's `Catch`, not out of the aggregator). The admin verdict
must NOT be resolved inside that `Defer`: the parameterless `IsGlobalAdmin()` reads the ambient
`AccessService` context at the moment it is called, and at subscribe time on a distributed mesh
that context is gone — the viewer reads as anonymous, `AdminOnly` never passes, and every seeded
instance-app tab (Invitations, Privacy, Published to the web, Updates, Control lane, Inbox) is
missing from `/Admin` while the same seeds pass the same gates in the node menu. The lane binds
`AdminAppNodeType.LiveAdminVerdict(hub, viewer)` to the viewer captured before the `Defer`; the
profile-section lane does the same. Pinned by
`AdminAppTest.SeededAdminTabs_SurviveASubscriptionOffTheViewersDelivery` (the stream is built with
the viewer set and subscribed with it — and the test host's fallback identity — cleared) and
`AdminAppTest.ASeededAdminTab_ShowsInItsSection`. Measured on memex.meshweaver.cloud (3.0.0-ci.9554)
and memex.systemorph.com (3.0.0-ci.9526): the six seeds listed in `$Menu:NodeSettings` and absent
from the `/Admin` nav.

**…and the nav never paints before that verdict — or the catalog — has ANSWERED.** Binding the
viewer correctly was not enough: the lane still opened its verdict with a synthetic `false`, and the
contribution catalog opened with an empty set. The page waits for the viewer's permissions on the
node (the same evaluator fold) before it renders, and each lane subscribes its OWN verdict — so
which lanes made the first frame was a race the compiled Admin-app tabs won and the seeded ones
lost. Anything that reads ONE frame — an MCP `get @Admin/area/Settings`, a first paint — saw the
six seeds missing. Settings-nav lanes now take `AdminAppNodeType.AnsweredAdminVerdict` (no seed;
`AdminOnlyTab` and the contributed lane both), and `UiContributionCatalog.Contributions` emits the
catalog query's answer, never a placeholder (the node menus seed their own slice, as before).
Pinned by `AdminAppTest.ASeededAdminTab_IsInTheFirstRenderedNav` and, with the real seeds,
a runtime admin grant and a request-only identity, by
`AdminAppFirstFrameTest.TheFirstRenderedNav_CarriesTheSeededInstanceTabs_InTheirSections`. Measured on
memex.meshweaver.cloud (3.0.0-ci.9606, which carries the render-turn fix above) and
memex.systemorph.com (3.0.0-ci.9590).

**The Overview's installed-plugin list is the registry's answer, read as the viewer.**
`CatalogLayoutAreas.ObserveInstalledManifests` emits nothing until the install-registry query
answers (no empty seed rendered as "No plugins are installed on this instance"), and it stamps the
query with the viewer the page was opened for (`LayoutAreaHost.ViewerContext` →
`MeshQueryRequest.ForViewer`) instead of leaving it to the ambient context at subscribe time. The
seed is the reproduced cause; the stamp is hardening — a view rendered on a live emission runs off
the viewer's delivery, and there an unstamped read would be anonymous, which on an instance with
`Access:DenyAnonymous` is an empty registry even though `Plugins` is `PublicRead` (not reproduced
on the in-process test mesh, whose reads keep the caller's context). Both instances above said "No plugins are installed" over dozens of installs. Pinned
by `AdminAppFirstFrameTest.TheInstalledPluginSection_FirstSaysWhatIsInstalled` (red before: the
section's first word was the empty seed).

## Old links keep working

A settings page asked for a tab it no longer carries answers with a REDIRECT to the tab's home, never
by silently opening its own first tab (`SettingsRedirect`), in this order:

1. an alias on this hub — `/Admin/Settings/AccessControl` and `/Admin/Settings/EffectiveAccess` →
   `/Admin/Settings/GlobalAdmin`; `/{user}/Settings/Appearance` → `Preferences`;
   `/{user}/Settings/AccessControl` → `Sharing`;
2. the retired node `Files` tab → the node's `Files` area;
3. a Space-root tab asked for below the root → the same tab on the root
   (`/Space/Doc/Settings/Groups` → `/Space/Settings/Groups`);
4. a tab that moved into the instance app → `/Admin/Settings/{id}` (`RelocateSettingsTabsToAdminApp`,
   automatic with `AddAdminAppTab`) — e.g. `/{user}/Settings/GlobalAdmin`, `/_Setting/GlobalSettings/Invitations`;
5. a tab that moved into the person app → `/{viewer}/Settings/{id}` (`RelocateSettingsTabsToPersonApp`,
   automatic with `AddPersonAppTab`) — e.g. `/Space/Settings/ApiTokens`, `/Admin/Settings/WhoAmI` →
   `/{viewer}/Settings/Account`.

The `{user}/Instance` area redirects to the Registration tab, so every "This instance" tile keeps working.

## Entry points

The same three doors in every client (Blazor, portal-next, React Native):

- the avatar menu: **your name** → the person app; **the instance's name** → the instance app (platform
  admins only); What's New stays public;
- the node's ⋯ menu: **Settings…** → that node's settings;
- no top-bar settings gear — node settings live in ⋯.

The clients are MeshWeaver.Plugins code (`MeshWeaver.Blazor.Portal`, portal-next, `app/react-native`);
core ships the three apps, the ⋯ entry and `AdminAppNodeType.InstanceName`, which they label the
instance entry with.

## Why `/Admin` is a type of its own

The node at `Admin` is the root of the system partition that holds the platform-admin grants
(`Admin/_Access`), the update policy (`Admin/UpdatePolicy`), the setup-link claim (`Admin/SetupLink`)
and the platform catalogs. Before `AdminAppNodeType` no code registered that node, so a fresh database
synthesized a TYPELESS placeholder root (an empty page) and long-lived databases held it as a `Space`
(listed among the admin's workspaces, rendering an empty Space). `AdminAppNodeType` (core,
`MeshWeaver.Graph`) registers the `AdminApp` NodeType and the `Admin` root node in code; its default
area is the node's settings page, so `/Admin` opens the app and every tab is linkable as
`/Admin/Settings/{tabId}`. Long-lived databases are retyped by migration V59 (MeshWeaver.Plugins,
`Memex.Database.Migration`).

**A non-admin opening `/Admin`** is refused by the partition itself — the Admin partition is readable
only by platform admins — and even if the page rendered, no tab would: every one waits for a positive
admin verdict. Nothing about the app widens a read on `Admin/_Access`.

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

All of it is compiled platform code — core (`MeshWeaver.Graph`, `MeshWeaver.GitSync`,
`MeshWeaver.PluginCatalog`, `memex/Memex.Portal.Shared`) and the portal hosts and modules in
MeshWeaver.Plugins — so it reaches an instance by an **image roll**; nothing here is node content,
and no migration is needed beyond V59 above. The control image (`Mesh:ClosedTypeSet=true`) is the same
portal host, so the `AdminApp` type is part of its closed type set. A roll ends the activations on the
pods it replaces, so the new tab sets are what the next activation binds; a portal that absorbed a
build WITHOUT a roll keeps serving the old settings pages until the `Admin` and user-root activations
are recycled (see [Stale State Until a Recycle](../StaleStateUntilRecycle)).
