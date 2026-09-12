---
Name: A registry-key rotation refuses under an inline shadow
Category: Fix
Description: RotateRegistryKey no longer rotates a plugin-registry key that an inline env entry on the Deployment would keep shadowing. Before, the new key reached Key Vault while the portal kept presenting the old one — which the registry had just stopped accepting — so every catalog read failed behind a rotation that reported success.
Icon: ShieldCheckmark
Order: -20260911
---

# A registry-key rotation refuses under an inline shadow

`RotateRegistryKey` mints a new plugin-registry key into Key Vault, has the registry adopt it, and
restarts the portal onto it. The registry retires the **old** key the moment it adopts the new one.

If the portal's Deployment also carried the key as an **inline** environment variable — as both
production portals did — that inline value outranks the vault copy. The rotation then landed the
new key where nothing read it, the restarted pods kept presenting the old key, and the registry
refused it. Every catalog read, update check and registration failed, while every check the
rotation itself runs stayed green: it confirms the new key reached the synced Secret, and its final
probe accepts an unauthorised answer as "the instance is up".

## What changed

The rotation now refuses before anything is minted, and says why:

- **From the record** — while the Deployment record lists an inline entry for any key the registry
  key is delivered as, whether or not that entry is already marked retired.
- **From the cluster** — the operator reads the Deployment first and refuses when any container
  still sets the key inline.

To rotate such an instance, retire the inline entry first — mark it `retiredBy` on the record and run
`Reconcile` — then drop the entry from the record once the audit no longer reports it, and rotate.
The order is in [Operating from the portal](/Doc/Architecture/OperatingFromThePortal).
