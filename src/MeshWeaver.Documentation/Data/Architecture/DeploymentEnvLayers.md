---
Name: Deployment env layers — what a record must be able to hold
Category: Architecture
Description: The five layers that decide a portal's configuration, why a Deployment record that can hold only one Key Vault class silently destroys the other on a re-render, and the shape that fixes it
Icon: Layer
---

# Deployment env layers: what a record must be able to hold

A `Hosting/Deployment` record is supposed to be the ONE source of an instance's deployment: change
the record, re-render, deploy. That claim is only as good as the record's ability to describe what
the instance actually runs — and on 2026-09-06 it could not. Re-rendering `memex` from its own
record and diffing the result structurally against the deployed overlay gave **41 differences**, and
every one of them was a silent loss. This page records the five layers, what each one is, and the
record shape that can hold all of them.

## The five layers, lowest precedence first

A portal pod's configuration is assembled from five sources. Kubernetes keeps the **last** `envFrom`
entry when two supply the same key, and an inline `env:` entry outranks every `envFrom`. So the
list is a precedence order, and the last source that carries a key is the value the portal reads.

| # | Layer | Rendered by | Recorded as |
|---|---|---|---|
| 1 | ConfigMap `memex-portal-config` | the chart, from `config.memex_portal` | the typed fields + `extraPortalConfig` |
| 2 | Secret `memex-portal-secrets` | the chart, from the Key Vault **values half** | `vaultValuesKeys` (names only) |
| 3… | Secret per **SecretProviderClass** | the chart, from `keyVaultSecrets` / `keyVaultSecretClasses` | `keyVaultSecrets` + `keyVaultSecretClasses` |
| last | inline `env:` on the Deployment | **nothing** — `kubectl` put it there | `inlineEnv` (declarative) |

Layers 2 and 5 are the ones a record could not previously see at all. Layer 2 comes from a values
file captured into Key Vault (`helm-values-memex`), which no repository holds; layer 5 comes from
somebody's `kubectl set env`, which no file renders. `HelmValues.EnvPrecedence` folds all five into
one answer per key: every layer that supplies it, and therefore which one wins.

## Why one Key Vault class was not enough

Until this change the record had exactly one slot for a `SecretProviderClass`
(`keyVaultSecrets`), plus a legacy escape hatch that could only *point at* a hand-made one by name.
`memex` runs **two** classes:

- `memex-kv` → Secret `memex-kv-secrets`, hand-made, 13 keys — the mesh connection string, the
  GitHub App private key, the AI provider keys, the webhook secrets;
- `memex-portal-keyvault` → Secret `memex-portal-keyvault`, chart-owned, the plugin-registry token.

With one slot the record had to CHOOSE, and whichever it named, a re-render destroyed the other. It
named `memex-kv` while the deployed overlay named `memex-portal-keyvault`, so a re-render would
have:

1. **deleted both `PluginCatalog` mappings** — the fix that had made the registry poll work that
   same morning after 740 consecutive 401s;
2. **dropped `extraEnvFrom: memex-kv-secrets`**, detaching all 13 keys of the hand-made class from
   the pod;
3. **added a mapping for a vault object that does not exist**
   (`memexsystemorph-PluginCatalog-RegistryToken`, derived from the record's prefix) — and a
   declared object the vault does not hold fails the *whole* CSI mount, so every new pod stays in
   `ContainerCreating` and the rollout stalls.

None of that is visible from the record; it is a data-loss bug wearing the shape of a formatting
change.

## One vault object may serve several keys

The projection used to refuse `maps vault object X onto more than one key — state distinct vault
objects`. That rule was wrong, and it refused the shape that fixes the registry poll: `memex` reads
one credential under **two** names because two code paths look for two names —
`PluginCatalog__RegistryToken` (the legacy single-registry key) and
`PluginCatalog__Registries__0__Token` (the per-registry key of the named registry) — both from the
one vault object `PluginCatalog-RegistryToken`. Nothing about that is ambiguous: both land, both are
read, and rotating the object rotates both.

The same rule was silently losing a key on `memex-cloud`, where one object
(`memexcloud-AzureAIS-ApiKey`) has served both `AzureAIS__ApiKey` and `AzureFoundry__ApiKey` since
before the record existed: the record could hold only one of the two.

**The correct rule is the other way round.** A KEY has exactly one home — two sources for one key
means `envFrom` order alone decides what the portal reads, which is the 2026-08-30
`EmailConfigurationGuard` crash re-created. An OBJECT may have as many keys as the portal reads it
under. `HelmValues.Problems` now checks the first, across classes as well as within one, and permits
the second.

## Recording an inline `env:` entry does not create one

`inlineEnv` and `vaultValuesKeys` render **nothing**, and that is the point.

An inline entry is out-of-band by construction: the chart never emits one, and `helm upgrade` does
not remove one either — three-way merge removes only what helm previously *owned*. That was measured
on helm v3.21.1 and v4.2.4 with a positive control; see
[Chart Drift — what a deploy actually does](/Doc/Architecture/ChartDriftSemantics). So an inline
entry survives every deploy and keeps outranking the ConfigMap and every synced Secret.

Recording one therefore neither creates nor deletes it. What recording buys is that the record stops
silently **disagreeing** with the pod — and once the known, deliberate entries are declared, anything
left over is a surprise rather than noise. Each entry says what it stands over (`shadows`), whether
that source is known to agree (`agreesWithShadowed`), why it is still there, and what retires it.

Three things the fleet's own entries show, none of which was written down anywhere before:

- **A "shadow" is often the SOLE source.** `PluginCatalog__RegistryUrl` is inline on both portals
  while the ConfigMap renders it *empty*. Deleting the inline entry would blank the key, not fall
  back to anything.
- **Some entries disagree with the ConfigMap, and the pod wins.**
  `Features__Ai__Providers__AzureOpenAI` runs `true` on `memex` while every committed file says
  `false`; `PreWarm__GateReadiness` runs `false` on `memex-cloud` while the ConfigMap says `true`,
  which renders the NodeType bake gate inert there. Removing that one entry is what *arms* the gate
  on `memex-cloud` — the opposite act to `memex`, where the inline entry already agrees.
- **Some have no other home at all.** `Features__Ai__Clis__ClaudeCode` / `__Copilot` on `memex`, and
  `Speech__{Endpoint,Enabled,Language}` on `memex-cloud`, correspond to no chart key whatsoever.

### Never a credential's value

These records sync to a git repository. An inline entry that carries a credential — both portals
hold a plugin-registry instance key inline, in plaintext, readable by anything that can
`get deploy` — sets `isSecret` and leaves `value` unset. Stating both is **refused** by
`HelmValues.Problems`, so the one shape that would put a live token into a git-synced node fails the
render instead of shipping.

## What else the record gained

- **`gates`** — the language gate sidecars (`python`, `node`, `pandas`). The chart has been able to
  render them from `grpc.gates` all along, but nothing declared them: `memex` has run two of them
  since a `kubectl patch` on 2026-08-24, so the record described a one-container pod while three
  containers ran.
- **`vaultValuesKeys`** — the keys the chart's own Secret supplies. Six of `memex`'s eleven are also
  supplied by a declared class, which sits later in `envFrom` and therefore wins; without the list,
  nothing said the chart's Secret carried them at all.

## The chart half

`keyVaultSecrets` in values still holds one class, unchanged, so every existing environment renders
byte-identically. Additional classes go in `keyVaultSecretClasses`, a list of the same shape;
`templates/memex-portal/_keyvault.tpl` resolves both into one ordered list that the
`SecretProviderClass` template, the `envFrom`, the volumes and the mounts all read, so the four can
never disagree about a name.

On an entry in the plural list `name`, `volumeName` and `mountPath` are **required**: a second class
falling back to the chart's defaults would collide with the first on all three at once, and two pod
volumes of one name is an invalid spec while two classes syncing into one Secret race each other.
`syncedSecret` still defaults to the class's own name, and the vault coordinates fall back to the
singular block's — a namespace's classes normally read one vault with one add-on identity.

## The falsification test

The shape is only worth having if rendering the record reproduces what is actually running. Rendering
`memex`'s extended record through `HelmValues` and then through `helm template`, and comparing the
result against the live cluster objects, reproduces: the same two `SecretProviderClass` objects with
the same vault, tenant and identity; all 2 + 13 object→key mappings in the same order, duplicates
included; the same four `envFrom` sources in the same order; the same CSI volumes and mount paths;
and the same three containers. `memex-cloud` reproduces the same way, including its
one-object-two-keys `AzureAIS` mapping.

One entry does **not** reproduce, and it is a real defect rather than a modelling gap: both committed
overlays declare an `Embedding-ApiKey` secret whose vault object does not exist. It is not on either
live `SecretProviderClass` because it has not been deployed yet — and when it is, the missing object
will fail the whole mount. The record states it because it states the overlay's intent; provisioning
the vault secret is what unblocks the next deploy.

## Related

- [Chart Drift — what a deploy actually does](/Doc/Architecture/ChartDriftSemantics) — what a
  `helm upgrade` does and does not remove, measured.
- [Deployment on AKS](/Doc/Architecture/DeploymentAKS) — the `keyVaultSecrets` block, and why a
  declared object the vault lacks stalls a rollout.
