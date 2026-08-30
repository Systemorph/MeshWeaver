---
Name: Key Vault secrets are declared in the chart
Category: Fix
Description: The Helm chart now renders a portal's SecretProviderClass, CSI volume, mount and envFrom from one `keyVaultSecrets` values block — names only, never values — so which vault secrets a pod reads is readable from the values file instead of only from the cluster, and a hand-made copy of a configuration can no longer race the chart's on every pod start.
Icon: ShieldKeyhole
Order: -20260830
---

# Key Vault secrets are declared in the chart

The `memex` install crashed at boot on 2026-08-30 with `EmailConfigurationGuard`: `Email:Enabled`
was true and `Email:ClientId` unset. Its Email configuration existed twice on the live pod. The
chart's ConfigMap rendered the defaults (`Email__Enabled: "false"`, an empty client id), while a
hand-made Kubernetes Secret had been patched onto the live Deployment as explicit `env` entries in
a different letter case (`EMAIL__ENABLED`, `EMAIL__CLIENTID`, …). .NET's environment configuration
provider is case-insensitive, so which copy won was decided by enumeration order on every pod
start — a coin toss per boot that no file in any repository could see, because the
`SecretProviderClass` objects behind the fleet's vault secrets were hand-made too.

The chart now owns that declaration. A `keyVaultSecrets` block in values names the vault, the
tenant, the identity that reads it, and the secrets as `{vaultSecret, key}` pairs — the vault
object's name and the environment key it lands as, never a value. From that one block the chart
renders the `SecretProviderClass`, the CSI volume, its mount and the `envFrom` on the synced
Secret, so the four can never disagree on a name. Empty means nothing renders, and every
environment that has not opted in renders byte-identically to before; a half-declared block fails
the render naming the missing key instead of producing an object that syncs nothing.
`extraEnvFrom` / `extraVolumes` stay as the legacy escape hatch for a namespace whose
`SecretProviderClass` is still hand-made.

The fleet's `Hosting/Deployment` records project their own `keyVaultSecrets` section onto this
block, deriving the vault name from the key by the convention
`<keyVaultSecretPrefix><Section>-<Key>` — which makes the record the only place a secret's name
lives. The Hosting plugin's `Configuration` page walks the whole path, for every kind of value:
record field → render → chart template → ConfigMap / SecretProviderClass / synced Secret → env.
