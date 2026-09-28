---
Name: "Secrets: Write-Only Entry, Split Identities"
Category: Architecture
Description: Nobody touches Key Vault by hand. Every secret is entered through a write-only GUI in the app that owns it, written by ONE governed writer identity that can list, set, delete and recover but never read a value, and read only by the pods through a reader identity that can do nothing else. What the reader and writer hold, where each GUI lives, how the fingerprint tag works, how pods pick a change up, what break-glass is, and the CI guard that keeps new hand-run vault commands out of the tree.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="11" width="18" height="10" rx="2"/><path d="M7 11V7a5 5 0 0 1 10 0v4"/><path d="M9 16h6"/></svg>
---

# Secrets: Write-Only Entry, Split Identities

**Nobody accesses Key Vault by hand** (policy [`secrets-write-only-entry`](../PolicyNotProse)). Every
secret the fleet uses has a GUI in the app that owns it. That GUI sets or replaces a value and shows only its
**status and fingerprint**, never the value itself. Behind it the vault has exactly two kinds of access:

| identity | may | may NEVER | who holds it |
|---|---|---|---|
| **reader** — the CSI Secrets Store identity the pods mount through | `get` a named secret | list, read metadata, set, delete | the pods, nothing else |
| **writer** — `secret-writer`, used only by the governed write path | list (names, attributes, tags), set, set attributes, delete, recover, purge | `get` (read a value) | the operator Jobs a secret action runs, nothing else |
| humans | — | either of the above | nobody holds standing access |

Operators and agents therefore never need to set or show a vault secret by hand for
normal operations. A command like that in a runbook is an instruction to go around the design, and
the CI guard (below) refuses a new one.

## Why two identities, and why neither can do the other's job

A write-only GUI is only write-only if the thing behind it cannot read. If the writer could `get`, any
code path that reaches the writer (a mis-planned step, a debugging session, a compromised action)
could read every value in the vault. So the writer never gets `get`. It can still show everything a
person needs to know about a secret, because Key Vault's `list` returns every object's attributes and
tags without its value:

- whether the object exists and whether it is enabled;
- when it was created and last updated, and when it expires;
- whether it is soft-deleted, and until when it can be recovered;
- the tags the writer stamped when it wrote the value (below).

The reader is the mirror image: the CSI driver fetches each secret **by name** (and version), and the
rotation poll does the same. Neither needs `list` or metadata, so the reader holds `get` and nothing else.
Scoped per secret where the scope can express it, a pod's identity can then fetch exactly the objects its
`SecretProviderClass` names.

## Status without reading: the write-time tags

The writer stamps four tags in the same call that stores the value (`hosting-kv-set`):

| tag | value |
|---|---|
| `mw-fp` | the **fingerprint**: `sha256:` + the first 12 hex digits of SHA-256 of the value, or `withheld:low-entropy` |
| `mw-set-by` | who asked for the write (the principal on the governed action) |
| `mw-set-at` | the UTC instant of the write |
| `mw-source` | `paste` (a person supplied it) or `generate` (minted server-side) |

The fingerprint lets a person compare the two ends of a pairing (a webhook secret on the sender and the
receiver, a key on the control instance and on a deployment) without either end being shown. It is
published **only when the value is estimated at 128 bits of entropy or more**: its length × log2 of the
alphabet its character classes span. Every generated value and every real API key, client secret or PEM
clears that bar easily. A short human-chosen password does not, and a published hash prefix of one
would let anyone guess it offline, so its fingerprint reads `withheld:low-entropy`. There is
deliberately no keyed HMAC: its key would be one more secret both ends would have to **read**, which is
exactly the reader this design removes. The operator's `hosting::fingerprint` is the reference
implementation today. The portal-side twin, `SecretFingerprint.Of` in `MeshWeaver.Mesh.Security`, is
**owed**: it arrives with the core change that adds the write-only secret control, and it must produce
the same string byte for byte.

`Enable`/`Disable` never pass tags: `set-attributes` replaces the whole tag set and would wipe the
fingerprint. Who toggled a secret is recorded on the governed action node instead.

## The governed write path

Every write runs **as system, through a governed action**, never in a browser session and never under a
person's own Azure credential:

1. A person with the right to manage that instance opens the GUI (see *Where each GUI lives* below)
   and pastes a value into a password field, or presses **Generate**.
2. The value is encrypted with the platform key protector (`enc:`) onto a `Hosting/InstanceAction` node
   on the control instance. It is refused, not stored, if encryption is unavailable. The field is
   cleared and nothing is bound back.
3. The control plane authorises the invoker. Every **write** (Set, Replace, Generate: a `SetSecrets`) and every
   lifecycle change parks on its governed action node until a global administrator other than the requester
   approves it. On a single-admin installation the configured maintainer (`Hosting:Operator:Maintainer`)
   may approve their own request instead. **Delete** and **Purge** are also marked destructive for that
   approver. A **status** read changes nothing and needs no approval.
4. One operator Job, running under the **writer** identity, decrypts the value for exactly that Job and
   writes it through a mode-600 file (`--file`, never argv, never a log line). It stamps the tags,
   confirms the new version through `list-versions` (a metadata read), and then removes the ciphertext.

The verbs are `hosting-kv-set` (set, `--generate`), `hosting-kv-status` (status from metadata only) and
`hosting-kv-state` (enable, disable, delete, recover, purge; a purge is refused unless the object is
already soft-deleted). Their behaviour tests run against a stub `az` that plays the writer: it **refuses**
`secret show`, so a verb that reads a value is red in CI.

### Interim: the value-free verbs on the Actions lane

🚧 **TRANSITIONAL** (policy `secret-actions-interim-actions-lane`, **proposed**: it takes effect once its executor half, MeshWeaver.Plugins#2528, is rolled onto the control instance; until then the control plane refuses both verbs at *Check executor*). The writer identity is not provisioned yet. The control instance runs its actions on the Actions executor (Systemorph/Memex `aks-ops.yml`), not as in-cluster Jobs. **Once that half is live**, and until the writer exists and every secret action moves to the in-cluster Job under it, the two verbs that carry **no value** run on that lane as `hosting-operator`:

- **Generate** (`SetSecrets` with only `generate:`): the lane runs `hosting-kv-set --generate`. The value is
  minted inside the run, written through a mode-600 file and never shown. It is never in the dispatch payload
  or the bundle. Like every `SetSecrets`, the action **parks for approval** on its governed action node, the
  gate described in step 3 of *The governed write path* above (the same gate a Roll waits on). The lane then
  re-verifies the signed approval token before it runs.
- **Read status** (`SecretStatus`): the lane runs `hosting-kv-status`, a metadata read. It needs no approval.

**The accepted interim risk.** This path does not have the property the design exists for: the identity
that writes cannot read. `hosting-operator` holds **get, list, set** on the vault (the table in *Where the fleet
stands*, below), and also the inert *Key Vault Secrets Officer* RBAC role. So during the interim a secret is
written by an identity that *could* read it back. The two verbs never do: their scripts call only
`list`/`list-versions`/`set` (the behaviour tests' stub `az` refuses `secret show`), and no value crosses the
lane. What compensates is that nothing value-bearing travels, and the write is approved in the mesh. The lane holds `hosting-operator` through its existing federated OIDC credential; no stored credential was added for this interim. Once the
exception is in force, each such run records a `writerIdentityNote` beginning `TRANSITIONAL` on its node and in its log, so a reader can
tell it apart from a writer-identity run. The invariants above (*exactly two kinds of access*; *one operator
Job under the writer identity*) are the target. For these two verbs they do not hold until the exception ends.

**What stays refused on that lane:** a **pasted** value and every **lifecycle** verb. A paste's value would sit
in a dispatch payload anyone reading the repo's Actions can decode. The lane's classifier refuses such a
bundle as well (`--object` on a write, or `HOSTING_SECRETS` in its environment). This leaves one **accepted
gap**: a secret a **third party must hold too** (below: minted in the portal, shown once, then filed like a
paste) has **no write path** during the interim. Such secrets wait for the writer identity. None may be
entered by hand in the meantime; break-glass (below) is the only exception.

The whole exception ends when the writer identity is provisioned **and** every secret action runs in the
in-cluster Job under it. That covers this section, the policy row and the TRANSITIONAL mark on
`hosting-kv-set` in `.github/manual-keyvault.allow`.

### Generate: shown once, or never

- A secret that **no third party has to paste anywhere** (a master key, a bootstrap secret, an in-fleet
  pairing whose two ends both read the vault) is minted **in the Job** (`--generate`, 32 random bytes) and
  is never shown to anyone.
- A secret a **third party must hold too** (a webhook secret pasted into GitHub or Stripe) is minted **in
  the portal**, shown to the person **once**, and then filed exactly like a paste. The portal keeps no
  copy.

## Pods pick the change up without a manual step

A new value reaches a pod in two stages. First the CSI driver's rotation poll re-reads the object and
updates the synced Kubernetes Secret. Then the pod must restart, because environment variables are
read only at start. The secret action does both:

- `hosting-kv-set --wait <object>=<syncedSecret>/<key>` waits until every pod mounting the object
  reports the new VERSION in its `SecretProviderClassPodStatus` (metadata, never a value) before the plan
  continues. No Kubernetes Secret is read, so the writer's ServiceAccount holds no Secret `get`;
- with **Restart after** set, the plan then rolls the portal onto it.

A Reconcile that **adds** a key to a class is different: the driver syncs only the objects a class
already names, and a pod mounting the old class never sees the new key. The governed path therefore
follows a class change with a restart (the Reconcile plan's own step), so no one has to remember it.

## Where each GUI lives

The rule is the same everywhere: one reusable write-only control, placed where the owner of the secret
already looks.

| secrets | GUI | owner |
|---|---|---|
| a deployment's vault objects, grouped by the domain that reads them (AI provider keys, database connections, sign-in and mail client secrets, Stripe keys, GitHub App and webhook secrets, the registry instance key) | the control instance's **domain app** for that domain, per deployment: `/Hosting/{Ai,Databases,SignIn,Email,Payments,Integrations}/Deployment/<id>` (status list + Set / Generate / Disable / Delete / Recover) — see [Domain Configuration Apps](../DomainConfigurationApps) | fleet admins |
| fleet pairings (`Hosting-PlatformWebhookSecret`, `Hosting-OperationsSigningKey`, control-inbox secrets, the per-deployment announcement key) | the **Integrations** app on the control instance, and the target instance's Admin app (**Control lane**), compared by fingerprint | fleet admins; the target's own admin for its announcement key |
| per-user AI provider keys | the user's **Model providers** app (already write-only, `enc:` in the mesh) | the user |
| an instance's first-run sign-in secret | the instance's **Setup** wizard hand-off | the instance's first admin |
| the platform master key (`Ai:KeyProtection:MasterKey`) | none — it encrypts every `enc:` value, and replacing it makes them unreadable; break-glass only | — |

The platform pieces are the `SecretStatus` contract, the `WriteOnlySecretSection` control and the
`SecretInventorySection` list over a scope of them ([Instance Secrets](../InstanceSecrets)). An app
wires those pieces up. It never builds its own form, and it never binds a value back into a view.
The Deployments page's **Set Key Vault secrets…** dialog predates the domain apps and is removed as
each domain claims its keys.

## Break-glass

If the GUI or the control plane itself is down and a secret must change to bring it back, the vault's
Owner-level administrators (subscription `Owner`) can grant themselves access. That is **break-glass**,
never a procedure:

- a documented break-glass step carries `kv-break-glass: <why no GUI can do this>` on or up to three
  lines above its command. A bare marker exempts nothing;
- the grant is removed again in the same session, and the value is re-set through the GUI afterwards,
  so the tags and the audit trail describe the value actually in the vault.

## The CI guard

`.github/scripts/check-manual-keyvault.py` fails on any tracked doc, runbook, skill or script that holds
a Key Vault secret data-plane command (the `az keyvault secret` verbs set, show, download, set-attributes, delete, purge, recover, backup and restore,
or `set-policy … --secret-permissions`). There are two exceptions:

- a path listed in `.github/manual-keyvault.allow`, with a reason: the governed operator verbs, their
  tests, and the guard itself;
- a line carrying `kv-break-glass: <reason>` (see above).

A command split over lines with a trailing backslash is joined before it is matched. A stale allow entry
fails too, so the list only shrinks. Core runs the guard on itself. The node repos and
Systemorph/Memex do NOT run it yet: MeshWeaver.Plugins still carries such commands, and wiring the
shared `node-repo-validate` lane before its cleanup merges would red every Plugins pull request. The lane
wiring follows that cleanup.

## Where the fleet stands, and the migration

Measured on vault `Systemorph`: the vault is in **access-policy** mode, not RBAC mode, and six
principals hold secret permissions on it:

| principal | secrets today | target |
|---|---|---|
| CSI add-on identity (the READER, one identity for every pod in the cluster) | get, list | get |
| `hosting-operator` (operator Jobs, `infra-deploy`, and Memex `aks-ops.yml`, which exchanges its OIDC token for this identity through a federated credential that already existed for the lane's other work) | get, list, set | none: its secret work moves to the writer |
| `github-actions-deploy` (Memex `helm-release` / `infra-deploy`) | get, list, set | none |
| a user principal | backup, delete, get, list, recover, restore, set | none: break-glass is an Owner re-granting temporarily |
| two principals the directory no longer resolves | get (+ delete, set) | removed |
| `secret-writer` (new) | — | list, set, delete, recover, purge; never get |

`hosting-operator` also holds the RBAC role *Key Vault Secrets Officer* on the vault, which is **inert**
in access-policy mode. It changes nothing today and would become live, with value reads, if the vault
flipped to RBAC, so it goes as well.

The order is chosen so that nothing breaks: build every replacement first, then narrow the policies,
and do every change to the vault through infrastructure-as-code.

1. **The split, with access policies, vault-wide (now).** Add the writer (`list, set, delete, recover,
   purge`) and narrow the reader to `get`. With no `get`, `list` still returns attributes and tags, and
   `set` covers attribute updates. Systemorph/Memex `infra/estate.bicep` declares both. The reader
   narrowing is flagged separately, so it can be switched on after a pod restart has been verified on
   `get` alone.
2. **Remove every value read from the governed path**, so `hosting-operator` needs no `get`:
   - `hosting-kv-copy`: a `copyFrom` becomes a record reference to the same object, or a re-issue at the
     source entered through the GUI;
   - `hosting::pg_password`: the connection string is minted together with a per-instance database
     password in one writer step, instead of being composed from the admin password;
   - done: every existence check (`hosting-kv-ensure`, `-kv-copy`, `-signin-app`, `-registry-register`) uses `list` through `hosting::kv_exists`, never `secret show`, and a refused listing is a refusal, never "absent";
   - `hosting-registry-register` and `hosting-kv-rotate`: the registry key is issued straight into the
     vault and never read back;
   - `hosting-image-mirror` and `hosting-pull-secret`: the registry credentials are mounted through a
     CSI class in the operator's namespace, the same reader path the pods use;
   - `hosting-deploy` and Memex's `helm-release`: the helm "vault half" is retired, and each value in it
     becomes its own vault object mapped on the record.

   Each of these files is marked TRANSITIONAL in `.github/manual-keyvault.allow` until its rewrite
   lands. After that, `hosting-operator` and `github-actions-deploy` lose their access policies.
3. **Humans and orphans.** The user principal's standing policy and the two unresolvable principals are
   removed. Break-glass is a subscription Owner re-granting temporarily (above).
4. **A new RBAC-mode fleet vault with per-secret scope.** Access policies cannot scope below the vault,
   so the per-secret step is a NEW vault in RBAC mode. It uses two custom roles, because the built-in
   *Secrets Officer* and *Secrets User* roles both include metadata reads:
   - *Secret Writer*: `…/secrets/readMetadata/action`, `…/secrets/setSecret/action`,
     `…/secrets/delete`, `…/secrets/recover/action`, `…/secrets/purge/action`;
   - *Secret Reader*: `…/secrets/getSecret/action` only.

   The migration, per record:
   1. the writer re-enters or generates each of the record's objects in the new vault through the GUI.
      Values are never copied out of the old vault, because nothing may read them;
   2. the reader is assigned per secret, from the objects the record's `SecretProviderClass` lists. A
      per-instance reader identity then replaces the one cluster-wide CSI identity;
   3. the record's `keyVault` moves to the new vault and a Reconcile rolls the pods onto it;
   4. the old objects are deleted, recoverable for the retention period.

   The legacy objects that belong to no instance (disk-encryption keys, certificates) stay in the old
   vault.

The identities, the policies and later the custom roles and assignments are provisioned in
Systemorph/Memex `infra/estate.bicep`, through the governed `InfraDeploy` action (what-if, then an
approved deploy). They are never created with `az` by hand. Which identity each instance holds is in
Systemorph/Memex `docs/control-instance.md` → *Secrets and identities*.

## Related

- [Operator Credentials Travel by Name](../OperatorCredentialsByName): the operator is handed a vault
  object's name, never a value
- [Self-Update Announcement Key](../SelfUpdateAnnouncementKey): a per-deployment key, set through the
  deployment's Secrets page
- [Deployment on AKS](../DeploymentAKS): how Key Vault secrets are declared on the record and rendered
  into the chart
- [The Dependabot Secret Store](../DependabotSecretStore): the GitHub-side secret store, which this
  design does not cover
