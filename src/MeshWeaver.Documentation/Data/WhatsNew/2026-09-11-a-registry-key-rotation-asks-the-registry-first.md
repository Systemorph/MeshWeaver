---
Name: A registry-key rotation asks the registry first, and retires the old key last
Category: Fix
Description: RotateRegistryKey no longer writes a new plugin-registry key into Key Vault before the registry has agreed to it. It asks the registry that holds the instance, stages the new key there, proves it, stores it, restarts the portal and only then retires the old key, so no failed step leaves a portal presenting a key the registry rejects. A key can also be revoked without anyone reading it.
Icon: ShieldCheckmark
Order: -20260911
---

# A registry-key rotation asks the registry first, and retires the old key last

Every portal authenticates to the plugin registry with an instance key. `RotateRegistryKey` replaces
that key with a new one, and the old one stops working — in the order set out under *What changed*
below: the registry is asked first, and the old key is retired last.

## What went wrong

The old rotation minted and stored the new key **first** and told the registry afterwards — but it
told whichever registry the *control instance* happened to host, and the instances live only on
the registry. So the registry never learned the new key, the rotation carried on, and the next
restart of the portal presented a key the registry did not know: every catalog read, update check
and registration failed, some time after a rotation that had quietly failed.

## What changed

The rotation now talks to **the registry the key belongs to**, and in this order:

1. It asks that registry whether it accepts the key the portal presents today. If it does not —
   the answer any portal that is not the registry gives — the rotation stops, and nothing is minted.
2. It mints a new key and **stages** it at the registry. From here the registry accepts both keys.
3. It proves the new key works, and only then stores it in Key Vault.
4. The portal restarts onto the new key.
5. The registry **retires the old key** — only now, once the key the restarted portal reads has been
   presented back to it.

A rotation that stops at any step leaves a portal that still authenticates, and its message says
what Key Vault holds and which keys the registry accepts. Running it again finishes the rotation
rather than starting a second one.

It also writes the vault entry the portal actually reads. A deployment that names that entry
explicitly — the control instance does — used to get a new key written under a different name
that nothing read.

## Revoking a key

A key that should no longer work can be revoked **where it sits**: the new `RevokeRegistryKey`
instance action reads it from a named Secret in the instance's namespace and hands it to the
registry, which stops accepting it. Nobody has to know or copy the key. The action refuses to revoke
the key the portal currently uses — that one is rotated. A platform administrator on the registry
can also revoke every key of an instance by its id.

The full design, the failure cases and the steps to roll it out are in
[Registry-key rotation](/Doc/Architecture/RegistryKeyRotation).
