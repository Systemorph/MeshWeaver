---
Name: Instance keys rotate through the Hosting operator — never through a terminal
Category: Feature
Description: A registry instance key can now be rotated as a lifecycle verb — the operator job mints the key straight into Key Vault and reports only its hash; the registry adopts the hash and retires the old one. Nothing prints, logs or stores the secret.
Icon: Key
Order: -20260830
---

# Instance keys rotate through the Hosting operator — never through a terminal

A `mwi_` instance key authenticates a deployment to the plugin registry. Until now it could be
*issued* (at registration) but not cleanly *rotated*: `ReissueKey` existed with nothing calling it,
and the only way to move a new value into a running portal was by hand — through a terminal that
would have to see the key.

This change ships the platform half of a rotation that no human, log or node ever sees in the clear:

- **`hosting-kv-rotate`** — a new operator script, the one deliberately allowed to overwrite a vault
  secret (`hosting-kv-ensure` never will, because the master key it guards seals stored
  credentials). It mints a fresh key, writes it to Key Vault, discards it, reports the key's
  **hash** through the job's marker channel, and waits for the synced Kubernetes Secret to carry the
  new value — compared by hash, never by value — before the portal is restarted.
- **`IInstanceKeyRegistry.AdoptKeyHash`** — the registry-side half, on the contract assembly so the
  Hosting plugin can resolve it from inside the portal: the instance takes the new hash and issued-at,
  a fresh index entry points at it, and the previous index entry is **deleted**, so the old key stops
  authenticating the moment the adoption lands.

The Hosting plugin's `RotateRegistryKey` verb, which drives both halves, follows in
`MeshWeaver.Plugins`.

## Why the split matters

The exposure this repairs came from an *audit* — a check that selected a secret's `.value` while
asking whether it was stored in the clear. The design here makes that mistake structurally
impossible: the only process that holds the raw key is the operator job, and the only thing it
hands back is a hash.
