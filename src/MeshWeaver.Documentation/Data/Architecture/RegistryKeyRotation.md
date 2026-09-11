---
Name: Registry-key rotation
Category: Architecture
Description: How an instance's plugin-registry key is rotated and revoked — at the registry that holds the instance, in two phases, authorised by possession of the key — why the first design could leave a portal locked out after a quiet failure, and the exact steps to roll the fix out and run a rotation.
Icon: Key
---

# Registry-key rotation

Every portal authenticates to its plugin registry with an **instance key** (`mwi_…`). The registry
stores only the key's SHA-256 (`MeshWeaverInstance.KeyHash`) and an index entry that routes the hash
to the instance. Rotating a key is a `Hosting/InstanceAction` of kind `RotateRegistryKey`; revoking
one is `RevokeRegistryKey` (MeshWeaver#2802). This page states the design; the operator's view is the
Hosting guide in MeshWeaver.Plugins.

## The defect this design replaces

The first rotation ran in this order:

1. the operator job minted the key, **wrote it to Key Vault**, and printed its hash;
2. the control plane read the hash and called `IInstanceKeyRegistry.AdoptKeyHash` **on its own hub**,
   which retired the old key at once;
3. the job waited for the synced Secret, restarted the portal, and verified.

Two things were wrong with it.

- **Adoption went to the wrong store.** Every Memex portal registers an `IInstanceKeyRegistry`, but
  the instances live only in the registry's store (memex.meshweaver.cloud). Instance actions run on
  the control instance (memex.systemorph.com), whose store holds no instance, so the adoption failed,
  *after* Key Vault already held the new key.
- **Nothing stopped the job.** The operator job is a separate Kubernetes Job. A failed adoption
  failed the *run node*, but the Job carried on: the CSI driver synced the new value into the Secret,
  and the next pod restart presented a key the registry had never adopted. The result was a 401 on
  every catalog read, update check and registration, some time after a rotation that failed quietly.

Even where adoption succeeded, it retired the old key **before** the new one had reached the pods,
so any later failure — the sync wait timing out, the restart failing, or an inline `env:` shadow —
had the same outcome.

## The design

### Rotation happens at the registry, authorised by the key itself

The registry serves a small key-lifecycle surface (`InstanceKeyEndpoints`, mapped by
`MapInstanceRegistration`, so every host that registers instances serves it):

| Route | Presents | Does |
|---|---|---|
| `GET /api/instances/self` | a key of the instance | answers `{instanceId, key: current\|staged, staged}` — or **401** when this registry does not accept the key |
| `POST /api/instances/self/key/stage` `{keyHash}` | the **current** key | stages the next key's HASH — both keys now authenticate |
| `POST /api/instances/self/key/commit` | the **staged** key | promotes it, and retires the previous key |
| `POST /api/instances/self/key/revoke` | any key of the instance | that key stops authenticating, with no successor |

Every call is authorised by **possession**: the caller presents a key of the instance and may act
only on that instance, in the slot the key occupies. There is deliberately no admin token that can
re-key another instance. The keys this replaces sat in plaintext pod specs (MeshWeaver#3201), and a
credential able to re-key the whole fleet is the worst thing such a leak could carry. Short-lived
`mwa_` tokens are refused on these routes. Only hashes are ever sent in a request body.

"Which store?" is no longer a question anyone has to answer. The store that acts is the one that
authenticated the key, and a portal that does not hold the instance answers **401** before anything
is minted. The per-hub `IInstanceKeyRegistry` is still there for code running *on* the registry, but
a lookup by id on a store without the instance throws `InstanceNotRegisteredException`, which names
the id, and never quietly succeeds.

### Two slots, and a strict rule for when a key is retired

`MeshWeaverInstance` has a **current** key (`KeyHash`) and a **staged** one (`PendingKeyHash`); the
authenticator accepts both. The rules are in `InstanceKeyRotation`, pure and unit-tested:

- **Stage** needs the current key. It may replace an earlier staged hash, but a staged key can never
  stage another. The registry cannot tell which key the running pods present, since they re-read it
  only on restart, so a stage may never retire the current key.
- **Commit** needs the staged key. Presenting the current key while a key is staged is refused (409):
  whoever holds it has not received the new one. A commit is the **only** transition that retires the
  previous key.
- **Revoke** retires exactly the presented key. `IInstanceKeyRegistry.RevokeKey(instanceId)` retires
  every key of an instance by id. That is a **global administrator's** act on the registry, for a key
  whose value nobody should need to know.

### The operator's order

`InstanceActionPlan.RotateRegistryKeySteps` (MeshWeaver.Plugins, Hosting ≥ 1.18):

1. `hosting-kv-rotate` goes through these steps in order:
   1. refuse under an inline `env:` shadow of the key;
   2. read the key the synced Secret carries and **ask the registry** whether it accepts it, as the
      instance the record names — refuse otherwise, **nothing minted**;
   3. mint, **stage** the hash, **prove** the new key authenticates;
   4. only then write Key Vault — the vault **object the token's class reads** — and wait for the
      synced Secret.
2. Restart the portal and wait for the rollout.
3. `hosting-registry-key commit` presents the key the synced Secret carries — the key the restarted
   pods read — and the registry retires the previous key.
4. `hosting-verify`.

The registry is the one the record's key authenticates at: its single consumer `pluginRepos` mount,
or its own host when `isPluginRegistry`. Two registries, or none, is refused. The vault object comes
from the Key Vault class that **declares** the token, never from the prefix rule. memex maps its
token from the un-prefixed `PluginCatalog-RegistryToken` while its prefix is `memexsystemorph-`, so
the old plan would have written a key into an object nothing reads.

### Failure matrix

| Stops at | Key Vault | Registry accepts | Pods |
|---|---|---|---|
| registry refuses the current key (401/404/503, or another instance) | unchanged | the current key only | unchanged |
| stage refused, or the new key cannot be proven | unchanged | the current key (plus a staged hash nobody holds) | unchanged |
| vault write fails | unchanged | current + staged | unchanged |
| synced Secret never catches up | **new** key | current + staged | not restarted — either key works on any restart |
| restart / rollout fails | new key | current + staged | whichever they read — both work |
| commit refused (Secret still holds the old key) | new key | current + staged | both work; nothing retired |
| verify fails after commit | new key | the new key | read the new key (proven by the commit) |

A run that stopped after the vault write is **resumed** by the next `RotateRegistryKey`: when a key is
staged and Key Vault holds it, the operator mints nothing, waits for the Secret, and the restart and
commit finish the rotation. A staged key held by neither the vault nor the Secret belongs to nobody,
so a fresh stage replaces it.

### Revocation

`RevokeRegistryKey` names a Secret and key in the instance's namespace (`revokeSecret`,
`revokeSecretKey`). `hosting-registry-key revoke` reads the key there and presents it to the
registry's revoke, then reads it back as **refused** before it reports `key_revoked=1` and the
`revoked_instance` it belonged to. It refuses if that key is the one the pods present (the token
class's synced Secret) — that key is rotated, never revoked. A key the registry already refuses
reports `key_revoked=already`.

## Rolling it out — three independent halves

| Half | Where | Needed by |
|---|---|---|
| the key-lifecycle routes + the staged slot | **core** image on the **registry** (memex-cloud) | every rotation and revocation |
| `hosting-kv-rotate` + `hosting-registry-key` | the **operator image** on the control instance (`Hosting:Operator:Image`, applied by memex's `helm-release deploy`, not by any record) | the job's steps |
| the plan (Hosting ≥ 1.18) | the **Hosting module** on the control instance (memex.systemorph.com) | the steps it plans |

Every mixed state refuses **before anything is minted**:

- an old plan on a new operator image fails at `missing required flag --registry-url`;
- a new plan on an old operator image fails at `unknown argument '--object'`;
- a new operator image against an old registry gets a 404 on `/api/instances/self`.

memex-cloud does not need Hosting 1.18. Only the control instance plans rotations.

## Runbook

1. **Registry.** Roll memex-cloud onto a set carrying the core change. Check that it serves the
   surface: an anonymous `GET https://memex.meshweaver.cloud/api/instances/self` answers **401**
   (a 404 means it has not been rolled yet).
2. **Operator image.** Publish the operator image from core `main`, set it as memex's
   `Hosting:Operator:Image` (the record's `operator.image` plus the Memex overlay), and run memex's
   `helm-release deploy`.
3. **Hosting 1.18** on memex.systemorph.com. A dry run of each action below must show `--registry-url`
   and a `Retire the previous key at the registry` step.
4. **Rotate memex-cloud**, then **rotate memex**. On the control instance, create
   `{deployment, requestedAction: RotateRegistryKey, confirmation: <id>}`, first with `dryRun: true`.
   A Done run logs `registry_instance`, `key_staged=1` (or `key_resumed=1`), `kv_rotated=1`,
   `key_committed=1` and `verify`. Rotating memex restarts the control instance itself; the commit
   runs inside the operator Job, so it still happens. Read the result with a `Sample` if the run node
   reports the outcome as not measured.
5. **Revoke the outranked key** on memex, after memex is rotated:
   `{deployment: memex, requestedAction: RevokeRegistryKey, revokeSecret: memex-portal-secrets,
   revokeSecretKey: PluginCatalog__RegistryToken, confirmation: memex}`. The run names the instance
   that key belonged to. Afterwards, drop the dead value from memex's chart-Secret values and from the
   record's `vaultValuesKeys`.
6. **After each step, check the catalog loads.** Run a `Logs` action over the following 15 minutes
   for `401` and `RegistryPackageSource` (there should be none), and open the Store on the rotated
   portal to see its packages. If the Store is empty, the registry's own log names the refused hash
   prefix.

## See also

- [Operating from the portal](../OperatingFromThePortal) — where instance actions run
- [Instance identity and setup](../InstanceIdentityAndSetup) — how instances and their keys are issued
- [Deployment env layers](../DeploymentEnvLayers) — which layer a key reaches the pod from
- [Instance lifecycle — state of record](../InstanceLifecycleStateOfRecord)
