---
Name: Instance Repository Access
Category: Architecture
Description: Which repositories an instance installs packages from, and the GitHub identity it reads them with, are the instance's own business — kept on the instance, set by its global administrators, never on the control instance. The boundary rule, the instance-local package sources, the instance-owned GitHub App created through GitHub's manifest flow, and the security review of that flow.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="6" cy="6" r="2.5"/><circle cx="6" cy="18" r="2.5"/><circle cx="18" cy="9" r="2.5"/><path d="M6 8.5v7"/><path d="M18 11.5c0 3-4 3.5-9.5 5.5"/></svg>
---

# Instance Repository Access

> **The control instance knows an instance's BASIC configuration and nothing more.** Which
> repositories an instance installs packages from, which GitHub identity reads them, its users and
> its approvals live ON the instance, set by that instance's own global administrators — policy
> `control-knows-basic-config-only`.

In the words the policy was set with: *"control should only know about the basic config of the
instance, nothing more."* — *"even plugins list can be kept locally."* — *"should probably set up
github app for it."*

## The boundary

| lives on the CONTROL instance (the deployment record) | lives on the INSTANCE |
|---|---|
| host, namespace, database, image line, replica floor, sign-in app, the registries it consumes, the vault references it mounts at start | the package sources it adds itself (`Admin/PackageSource-*`) |
| | the GitHub identity it reads them with (`Admin/GitHubApp` + its secrets) |
| | its users, their access, its approvals |

What the record renders (`PluginCatalog:Sources:N:*`, `PluginCatalog:Registries`, `GitHub:App:*`)
keeps working exactly as before — this page adds the instance's own half beside it and changes
nothing the record declares. A record that renders a source and an administrator who adds the same
repository on the instance agree on one source: the configured one wins, because it is declared
(policy `declared-not-derived`).

## Package sources, stored on the instance

One node per source, `Admin/PackageSource-{id}` (node type `InstancePackageSource`, content
`InstancePackageSourceContent`):

| field | meaning |
|---|---|
| `repo` | the repository URL, e.g. `https://github.com/acme/reporting` |
| `ref` | the branch, `main` when blank |
| `name` | the source's name — what `Source/Package` install patterns and logs call it; the repository's name when blank |
| `installByDefault` | the package ids this instance installs by default from this source — the instance's own counterpart of the record's `PluginCatalog:InstallByDefault` |
| `addedBy`, `addedAt` | who added it (a user id; attribution only) |

**Why the Admin partition and not the Store node.** The Store node is system-synced: an unclaimed
Store node is re-imported from the shipped `Store/index.json` on the next Store update, so an edit
there is undone. The Admin partition is not synced from any repository, so a node there survives
every Store update, every roll and every restart — the same place the instance's own secrets
(`Admin/Secret-*`, [Instance Secrets](../InstanceSecrets)) and its update policy
(`Admin/UpdatePolicy`) live.

**Who may change them.** Every verb (`InstancePackageSources.Add`, `.Remove`,
`.SetInstallByDefault`) checks rights first, as the CALLER: the caller must be a global administrator
(`hub.IsGlobalAdmin`). Only then does it write, as system — the shape of the Instance Secrets
verbs, and of policy `authorize-as-caller-execute-as-system`. A refused call writes nothing and says
why, in the viewer's language.

**Who reads them.** `InstancePackageSourceCatalog`, a mesh-scoped singleton holding ONE live query
(`namespace:Admin nodeType:InstancePackageSource`, as system) — the Instance Secrets catalog's
shape. Its readers:

- **The Store's package feed** (`StoreManifestSource.Resolve`, MeshWeaver.Plugins) takes the
  instance's sources as one more input beside the node's declared sources, the configured
  registries and the configured git sources. An instance source is always polled: an administrator
  added it on purpose, and a fetch that fails says so on its `StoreFeedStatus` row rather than the
  source silently not being there.
- **The default install** (`InstanceAutoRegistrationService`) reads the instance's sources beside
  the configured ones, and each source's `installByDefault` ids as `{name}/{package}` entries beside
  `PluginCatalog:InstallByDefault`. They never turn the record's set into a different set: the
  preInstalled-baseline decision stays the record's. A commercial package an instance source names
  is not installed unattended — that needs the deployment configuration's authority, which only the
  record carries; the administrator installs it from its Store card instead, as the authorizing
  principal. An instance source is listed AFTER the deployment's sources and registries, so it never
  shadows a package they list, and it never carries their authority, whatever it is named.
- **The credential resolution** (`GitSourceCredentials`, below).

The configured list (`PackageSources.FromConfiguration`) is unchanged, so a REGISTRY instance never
serves an instance-local source over `/api/plugins`: what a registry publishes stays what its record
declares.

## The instance-owned GitHub App

A private repository needs an identity. Before this page there were two, both delivered through the
deployment record: the platform's GitHub App (`GitHub:App:*`) and a per-source token
(`PluginCatalog:Sources:N:TokenKey`, valued from the vault). Both put the instance's repository list
into the control instance's hands. The third identity is the instance's OWN GitHub App, created by
its administrator in a few clicks through GitHub's
[manifest flow](https://docs.github.com/en/apps/sharing-github-apps/registering-a-github-app-from-a-manifest),
and stored on the instance.

### The flow

```mermaid
sequenceDiagram
    participant A as Global admin (browser)
    participant I as This instance
    participant G as github.com
    A->>I: Administration › Package sources › Connect GitHub (organization)
    I->>I: mint state, create its flow node Admin/GitHubAppFlow-{hash} (hash, expiry, admin), set state cookie
    I-->>A: form that POSTs the manifest to github.com/organizations/{org}/settings/apps/new?state=…
    A->>G: submit (GitHub shows the app to be created; the admin confirms)
    G-->>A: redirect to {instance}/admin/github-app/callback?code=…&state=…
    A->>I: callback
    I->>I: cookie == state, flow node found, not expired, same admin — then DELETE the flow node (one use)
    I->>G: POST /app-manifests/{code}/conversions
    G-->>I: id, slug, client_id, pem, webhook_secret, owner
    I->>I: store the credential bundle (app id, client id, private key) + webhook secret ENCRYPTED (Admin/Secret-*), metadata on Admin/GitHubApp
    I-->>A: back to the page: "Install the app on your repositories"
    A->>G: install on the selected repositories
```

The manifest the instance posts:

| manifest field | value | why |
|---|---|---|
| `name` | `MeshWeaver {host}` (GitHub appends a suffix if taken) | identifies which instance owns it |
| `url` | `https://{host}` | |
| `redirect_url` | `https://{host}/admin/github-app/callback` | the manifest flow returns the one-time code HERE, to this instance only |
| `hook_attributes` | `url: https://{host}/webhooks/github`, `active: true` | sources sync on push (policy `sources-sync-on-push`) |
| `default_events` | `push` | the only event the sync needs |
| `default_permissions` | `contents: read`, `metadata: read` | read-only; nothing else |
| `public` | `false` | a private App can be installed only on the account that owns it — the organization the admin chose |

The administrator then installs the App on **selected repositories** — GitHub's own installation page,
linked from the instance's page (`https://github.com/apps/{slug}/installations/new`).

🚨 **The App must be created in the organization that owns the repositories.** A private GitHub App
installs only on its owning account, so the administrator who runs the flow needs the right to
create Apps in that organization (an organization owner, or a member with the GitHub App manager
role).

### What is stored, and where

| what | where | how |
|---|---|---|
| App id, client id, private key (PEM) — ONE bundle, with the instant its flow was consumed | `Admin/Secret-github--instanceapp--credentials` (key `GitHub:InstanceApp:Credentials`) | [Instance Secrets](../InstanceSecrets): `enc:`-tagged AES-256-GCM with the instance's master key (`Ai:KeyProtection:MasterKey`), write-only, never shown, never logged. One value, replaced as a whole: a reader never combines one App's client id with another App's key |
| webhook secret | `Admin/Secret-github--instanceapp--webhooksecret` (key `GitHub:InstanceApp:WebhookSecret`) | the same; used only while the bundle is live |
| slug, name, owner login, html url, who connected it and when, the last disconnect (`revokedBefore`) | `Admin/GitHubApp` (node type `InstanceGitHubApp`) | plain content — none of it is a credential; the page needs the slug to link the installation page |
| a flow in progress | its own node, `Admin/GitHubAppFlow-{first 32 hex of SHA-256(state)}` (node type `InstanceGitHubAppFlow`): hash, expiry, who started it, the organization | the hash only; the state itself lives in the browser's cookie and GitHub's redirect |
| the OAuth client secret GitHub also returns | **nowhere** | the instance never acts as an OAuth client of this App, so it does not keep a credential it never uses |

Nothing of this reaches the control instance: no record field, no InstanceAction, no announcement.

### How the identity is used

`GitHubAppTokenService` resolves its identity on every mint:

1. the deployment's App (`GitHub:App:ClientId` + `:PrivateKey`) when it is configured — unchanged;
2. otherwise the instance's own App, read live from the secret catalog — so a connect, a rotation or
   a delete takes effect on every replica as soon as the change feed delivers it, with no restart.

`IsConfigured` is true for either. Every existing consumer therefore picks the instance App up with
no change: GitSync's import and branch reconcile of the instance's Spaces (`GitHubSyncService`), the
package sources' listing and install (`PackageSources`), and the Store feed's default token.
`RegistryPackages.HasGitIdentity` counts it.

**Per source.** `GitSourceCredentials` reads an instance-local source's repository with the
instance's own App whenever it is connected — also on an instance that has a deployment App, because
the instance App is what its administrator installed on that repository. A configured source asks for
it explicitly with `PluginCatalog:Sources:N:Identity = instance`; on an instance without a connected
App that refuses by name, like every declared credential (it never degrades to another identity).

**What does NOT widen.** The Store's supply-chain rule that drops an anonymous-only git source keeps
asking about the DEPLOYMENT's App for the sources the Store node and the record declare: an instance
App is installed on the repositories its administrator selected, so handing it the platform's own
repositories would poll them into a permanent 404 and a stale-catalog banner. It reads the instance's
own sources.

**Webhooks.** `/webhooks/github` verifies a delivery against `GitHub:Webhook:Secret` OR the instance
App's webhook secret — whichever signs it. Either may be absent; with neither, the endpoint answers
503 as before. **What a delivery may carry depends on which secret signed it.** The deployment's
secret keeps every event. The instance App's secret is held by the instance's administrators — a
platform role with no data rights — and its App subscribes to `push` only, so a delivery it signs
reaches the push trigger (and `ping`) and nothing else: any other event signed with it is refused
with 403. The push trigger reads the named repository at the named commit with this instance's own
credentials, so it carries no content of its own; `issues`, `issue_comment` and `workflow_run`,
which write the payload into Spaces as System or record build completions, stay behind the
deployment's signer. Pinned by `GitHubWebhookSignerScopeTest`.

## Security review

**CSRF and state.** The state is 32 random bytes, bound three ways: a `Secure`, `HttpOnly`,
`SameSite=Lax` cookie in the admin's browser, scoped to the callback path; a flow node named by its
SHA-256 with a 10-minute expiry and the admin's user id; and GitHub's redirect. The callback requires
all three to agree (constant-time comparison); a mismatched cookie is refused before any node is read.
It then CONSUMES the flow by deleting its node — a delete the node's owner decides, so of two callbacks
for one flow exactly one proceeds, whichever replicas they land on, and only that one exchanges the
code. A refused callback for that flow deletes it too. Every flow is its own node, so a callback can
never touch another flow, a newer one included. A code from a flow somebody else started, a replayed
callback, an expired one or a different admin's all fail, by name, and nothing is stored. The manifest's `redirect_url` names this instance
only, so GitHub sends the one-time code nowhere else, and the code itself is good for one conversion
within an hour.

**Admin only.** Both endpoints require a signed-in user, and the start and the callback check
`hub.IsGlobalAdmin` as that user before anything is written; the writes then run as system. The
records themselves accept no other writer: `InstanceRepositoryAccessWriteGuard` refuses a direct
create, update or delete of a source, the App's metadata or a flow by anyone but the platform — an
Admin-partition Editor is not a global administrator. The
Store page that offers the button is an admin-only Administration tab — the gate there is
convenience; the endpoint's check is the control.

**Key rotation.** GitHub owns the App's keys. To rotate: generate a new private key on the App's
GitHub settings page, paste it into the page's write-only **Private key** field
(`InstanceGitHubApp.RotatePrivateKey` replaces the key inside the bundle, as one value; the next mint signs with it — tokens already minted stay valid until their hour ends), then
delete the old key on GitHub. The webhook secret rotates the same way. Every replacement is an
Instance Secrets `Set`, so its fingerprint, who set it and when are recorded.

**Delete.** **Disconnect** FIRST records the revocation instant on `Admin/GitHubApp`
(`revokedBefore`) and only then deletes the secrets (recoverable for the Instance Secrets recovery
window, then gone). A bundle consumed at or before the last disconnect is dead — including one a
callback already in flight publishes after the disconnect — so a race cannot undo a disconnect. (The
two instants come from two replicas' clocks; a connect consumed within clock skew of a disconnect is
judged by those clocks.) From the next mint the instance has no App; nothing falls
back to it. The App itself still exists on GitHub until the administrator deletes it there — the page
links its settings page and says so, because an App that is only disconnected keeps its installation
and its keys on GitHub's side.

**Restore from a backup.** The secrets are ciphertext in the instance's database, so a backup carries
them, and they decrypt only with the same master key:

- restored onto the SAME instance (same `Ai:KeyProtection:MasterKey`): the App works as before;
- restored onto a different instance or under a different master key: the catalog logs that the
  value is stored but cannot be decrypted, `IsConfigured` is false, and the page shows the App as
  connected-but-unreadable. The administrator disconnects and runs the flow again, which creates a
  new App; the old one is deleted on GitHub.

A backup is therefore no more sensitive than the master key that guards it — the same property every
portal-entered secret has.

**Multi-replica consistency.** Nothing lives in a pod: a flow is a node, the secrets are nodes, and
every replica's catalogs follow them live. A replica decides "this instance has no App" only after
both catalogs have LISTED the store (`InstanceGitHubApp.WhenReadable`, `GitHubAppTokenService.WhenReady`),
so a cold replica neither mints anonymously nor refuses a webhook its App signed. The per-repository
credential choice is reactive (`GitSourceCredentials.TokenOrDefault`): a repository is never read
with the default identity because the instance's sources had not been read yet. Both catalogs fail
closed while their query is down — the App's metadata too, because the revocation lives there. Writing a source or the metadata decides existence from a fresh listing,
never a lagging snapshot. The start can run on one replica and
the callback on another. Each replica mints and caches its own installation tokens (keyed by client
id and installation owner, so a different App never reuses another's token).

**Never shown, never logged.** The callback holds the PEM and the webhook secret only between the
conversion response and the encrypted write. Logs name the App's slug and owner, never a value. The
conversion response is never logged, not even on failure — only its HTTP status.

### Where the private key lives — the decision

| option | for | against |
|---|---|---|
| **A. On the instance, encrypted with its master key** (Instance Secrets) — *chosen* | the boundary holds: nothing reaches control; live on every replica with no restart; the precedent for portal-entered credentials (provider keys, the self-update announcement key) | the database plus the master key unlocks it; a restore under another key needs a re-connect |
| B. In the deployment's Key Vault, written by the control instance | keys never in the database | control would hold the instance's repository identity, which is what the boundary forbids; needs a roll to take effect |
| C. In a vault the instance writes itself | keys never in the database | no instance holds vault-write rights today, and granting them is a wider change than this feature |

Option A follows the precedent. The master key itself is delivered to the pod from the deployment's
secret store (on Kubernetes, a Secret; never on the database volume), which is the separation that
makes "encrypted at rest" real.

## What this does not do

- It does not create a repository or a webhook on GitHub other than the App's own.
- It does not give an instance App write access, and GitSync's EXPORT (a push) does not use it.
- It does not publish an instance-local source to anyone: a registry serves what its record declares.

## Related

- [Instance Secrets](../InstanceSecrets) — the encrypted, write-only store the App's credentials live in
- [Syncing a Space with GitHub](../GitHubSync) — the import and reconcile the App reads for
- [Access Control](../AccessControl) — what a global administrator is
- [Policy Not Prose](../PolicyNotProse) — `control-knows-basic-config-only`
