---
Name: An instance on the fleet registry can self-update again
Category: Fix
Description: An installation pulling its images from cr.meshweaver.cloud booted normally and then never updated itself, because the self-updater would only present its instance key to a registry that was also its plugin registry. It now presents it to the portal the registry DECLARES as its validator — and still refuses every host nobody declared.
Icon: RefreshCcw
Order: -20260912
---

# An instance on the fleet registry can self-update again

An installation that pulls its platform images from the fleet's own registry
(`cr.meshweaver.cloud`) started, served, pulled its image in seconds — and then never updated itself.
Every check failed the same way, whatever its update policy said:

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

**It is a grant, and nothing else is one.** The key still goes only to a host that is a configured
plugin registry, or to a registry whose declared validator is one. An absent declaration **refuses**,
exactly as before — it is never read as permission — and hosts are compared whole: no suffix, no
"same domain", no "there is only one key so use it". Two statements are needed, both explicit.

Nothing changes for an installation on ACR, for a portal that serves its own `/v2` mirror, or for any
instance that declares nothing: those behave precisely as they did.

The rule, the measurement behind it and the tests that keep the guard honest are in
[The Self-Update Registry Credential](/Doc/Architecture/SelfUpdateRegistryCredential).
