---
Name: An instance can be told which portal validates its registry key
Category: Fix
Description: An installation pulling its images from cr.meshweaver.cloud booted normally and then never updated itself, because the self-updater would only present its instance key to a registry that was also its plugin registry. It now presents it to the portal the registry DECLARES as its validator — and still refuses every host nobody declared.
Icon: ArrowSync
Order: -20260912
---

# An instance can be told which portal validates its registry key

> **This adds the setting; it does not switch anything on.** An affected instance keeps refusing
> until an operator declares the pairing on it, and an instance that declares nothing behaves
> exactly as before.

An installation that pulls its platform images from the fleet's own registry
(`cr.meshweaver.cloud`) starts, serves, pulls its image in seconds — and then never updates itself.
Every check fails the same way, whatever its update policy says:

```
[SelfUpdate] check (Startup): check FAILED: SelfUpdate:Registry is 'cr.meshweaver.cloud' …
but no plugin registry … is configured on that host, so there is no key to present.
```

The self-updater authenticates with the same `mwi_` instance key the installation already holds for
its plugin registry, and it decided which key to present by matching the **host**. But the fleet's
registry deliberately holds no credentials of its own: it decides a pull by forwarding the caller's
key to a portal — `cr.meshweaver.cloud` asks `memex.meshweaver.cloud` whether the key is good. The
image registry and the plugin registry are therefore two **different** hosts by design, and host
matching could never succeed on the shape the fleet actually deploys.

## What changed

The pairing is now **declared**, with the registry's own `validationUrl` restated on the consuming
side: `SelfUpdate:RegistryValidationUrl` (chart: `selfUpdate.registryValidationUrl`; on an existing
instance, a record's `extraPortalConfig`). Set it to the registry record's validation URL — a bare
host works too — and the self-updater presents the key it already holds for that portal.

The key presented to a declared validator is the durable instance key itself — never the short-lived
token the plugin catalog otherwise exchanges it for — so an instance that registered itself at first
boot (`PluginCatalog:BootstrapKey`, no token configured anywhere) is covered exactly like one with a
configured token: the validator is the exchange endpoint, and it refuses a token by design.

`SelfUpdate:Registry` must also be a bare `host` or `host:port`: the same value is used as the
registry half of an image reference, and a value shaped like `user:secret@host` would be a valid URI
naming a different host as the one to hand the instance key to. A declaration that is set but does
not name a host, and a plugin registry whose URL carries its credential, are each refused with a
message that says so — never diagnosed as "nothing declared" or "no registry configured".

**It is a grant, and nothing else is one.** The key still goes only to a host that is a configured
plugin registry, or to a registry whose declared validator is one. An absent declaration **refuses**,
exactly as before — it is never read as permission — and hosts are compared whole: no suffix, no
"same domain", no "there is only one key so use it". Two statements are needed, both explicit.

Nothing changes for an installation on ACR, for a portal that serves its own `/v2` mirror, or for any
instance that declares nothing: those behave precisely as they did.

The rule, the measurement behind it and the tests that keep the guard honest are in
[The Self-Update Registry Credential](/Doc/Architecture/SelfUpdateRegistryCredential).
