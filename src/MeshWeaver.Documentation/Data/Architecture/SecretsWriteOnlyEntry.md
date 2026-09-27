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
exactly the reader this design removes. `MeshWeaver.Mesh.SecretFingerprint.Of` and the operator's
`hosting::fingerprint` compute the same string.

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
3. The control plane authorises the invoker. Destructive operations (**Delete**, **Purge**) need a second
   person's approval.
4. One operator Job, running under the **writer** identity, decrypts the value for exactly that Job and
   writes it through a mode-600 file (`--file`, never argv, never a log line). It stamps the tags,
   confirms the new version through `list-versions` (a metadata read), and then removes the ciphertext.

The verbs are `hosting-kv-set` (set, `--generate`), `hosting-kv-status` (status from metadata only) and
`hosting-kv-state` (enable, disable, delete, recover, purge; a purge is refused unless the object is
already soft-deleted). Their behaviour tests run against a stub `az` that plays the writer: it **refuses**
`secret show`, so a verb that reads a value is red in CI.

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

- `hosting-kv-set --wait <object>=<syncedSecret>/<key>` waits until the synced Secret carries the new
  value (compared by hash) before the plan continues;
- with **Restart after** set, the plan then rolls the portal onto it.

A Reconcile that **adds** a key to a class is different: the driver syncs only the objects a class
already names, and a pod mounting the old class never sees the new key. The governed path therefore
follows a class change with a restart (the Reconcile plan's own step), so no one has to remember it.

## Where each GUI lives

The rule is the same everywhere: one reusable write-only control, placed where the owner of the secret
already looks.

| secrets | GUI | owner |
|---|---|---|
| a deployment's vault objects (connection strings, sign-in and mail client secrets, registry instance key, AI platform keys) | control instance → `Deployments/<id>` → **Secrets** (status list + Set / Generate / Disable / Delete / Recover) | fleet admins |
| fleet pairings (`Hosting-PlatformWebhookSecret`, `Hosting-OperationsSigningKey`, control-inbox secrets, the per-deployment announcement key) | the same page on each end of the pairing, compared by fingerprint | fleet admins |
| per-user AI provider keys | the user's **Model providers** settings (already write-only, `enc:` in the mesh) | the user |
| an instance's first-run sign-in secret | the instance's **Setup** wizard hand-off | the instance's first admin |
| payment keys (Stripe secret key, webhook secret) | **Store → Payments** admin, with a *Test* action that lists the webhook endpoints | store admins |

The platform pieces are the `SecretStatus` contract and the `WriteOnlySecretSection` control, plus a list
view over a scope of them. An app wires those pieces up. It never builds its own form, and it never
binds a value back into a view.

## Break-glass

If the GUI or the control plane itself is down and a secret must change to bring it back, the vault's
Owner-level administrators (subscription `Owner`) can grant themselves access. That is **break-glass**,
never a procedure:

- a documented break-glass step carries the marker `kv-break-glass` on or just above its command, and the
  prose around it says why the GUI could not be used;
- the grant is removed again in the same session, and the value is re-set through the GUI afterwards,
  so the tags and the audit trail describe the value actually in the vault.

## The CI guard

`.github/scripts/check-manual-keyvault.py` fails on any tracked doc, runbook, skill or script that holds
a Key Vault secret data-plane command (the `az keyvault secret` verbs set, show, download, set-attributes, delete, purge, recover, backup and restore,
or `set-policy … --secret-permissions`). There are two exceptions:

- a path listed in `.github/manual-keyvault.allow`, with a reason: the governed operator verbs, their
  tests, and the guard itself;
- a line carrying the `kv-break-glass` marker (see above).

A stale allow entry fails too, so the list only shrinks. Core runs the guard on itself. Every node repo
gets it through the shared `node-repo-validate` lane, and Systemorph/Memex runs it in its own build.

## Where the fleet stands, and the migration

Measured on vault `Systemorph`: the vault is in **access-policy** mode, not RBAC mode. That decides what
can be expressed today:

- **Access policies can already express both roles, vault-wide.** Reader = `get`; writer =
  `list, set, delete, recover, purge`. Without `get`, `list` still returns attributes and tags, and `set`
  covers attribute updates. This is step 1, and it needs no vault migration.
- **Per-secret scope needs RBAC mode**, with custom roles: a *Secret Writer* with
  `…/secrets/readMetadata/action`, `…/secrets/setSecret/action`, `…/secrets/delete`,
  `…/secrets/recover/action` and `…/secrets/purge/action`, and a *Secret Reader* with
  `…/secrets/getSecret/action` only. The built-in roles do not fit, because both *Secrets Officer* and
  *Secrets User* include metadata reads. Flipping a vault to RBAC drops every access policy **at once**,
  so the assignments must exist first. The chosen shape is a new RBAC-mode vault for the fleet with
  per-secret assignments, leaving the legacy non-fleet objects where they are. That is step 2.
- **The operator still reads values in four places**, and each must be rewritten before the writer can
  lose `get`: `hosting-kv-copy` (`copyFrom`), `hosting::pg_password` (composes connection strings from
  the admin password), `hosting-registry-register`, and the Memex `helm-release` / `infra-deploy`
  workflows (the helm "vault half" and the control database password). Until then those steps keep
  running as `hosting-operator`. That is step 3.
- **Humans:** the standing user access policies on the vault are removed once the GUIs above cover their
  secrets. That is the last step, and the only one that needs the maintainer.

The identities, the custom roles and the assignments are provisioned as infrastructure-as-code in
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
