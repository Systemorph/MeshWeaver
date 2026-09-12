---
Name: The Self-Update Registry Credential
Category: Architecture
Description: Which plugin-registry instance key the self-updater may present to a container registry, why host equality was the wrong rule for a fleet whose registry validates at another portal, and why the replacement is a declared pairing rather than a resemblance.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="11" width="18" height="10" rx="2"/><path d="M7 11V7a5 5 0 0 1 10 0v4"/><path d="M12 15v2"/></svg>
---

# The Self-Update Registry Credential

**An installation on a non-ACR registry lists its platform tags with the `mwi_` instance key it
already holds for a plugin registry. WHICH plugin registry's key is a disclosure decision, and it is
decided by DECLARED PAIRINGS — never by host equality, never by name resemblance, and never by "there
is only one key, so use it".**

The rule, in `OciTagLister.ResolveCredential`:

| The key is presented to… | …because |
|---|---|
| the container registry's **own host**, when a plugin registry is configured there | the operator already gave this installation a key for that exact host — a portal serving its images from the `/v2` mirror it also serves its catalog from |
| the host named by **`SelfUpdate:RegistryValidationUrl`**, when a plugin registry is configured there | the operator declared that portal as the one that *validates* this installation's key at that registry, AND gave this installation a key for it |
| **nothing else, ever** | an absent declaration is a refusal, not permission |

Both rows need TWO explicit statements. A declaration alone grants nothing (there is still no key for
that host); a key alone grants nothing (nothing says that registry trusts that portal).

## Why host equality was wrong

The fleet's registry deliberately does **not** hold credentials. `cr.meshweaver.cloud` decides a pull
by forwarding the caller's password to a portal — `docker_auth`'s `ext_auth` hook `POST`s it to
`registry.validationUrl`, by default `https://memex.meshweaver.cloud/api/instances/token`, and grants
`pull` on a 200 ([A Container Registry in Memex](../ContainerRegistryInMemex)). That is what makes ONE
`mwi_` key serve both the plugin catalog and the image pull, exactly as
`RegistrySpec.ValidationUrl` says it does:

> The registry's own pull is decided by the portal at `ValidationUrl` — the same instance-key gate
> the plugin registry uses — **so a consumer holds ONE credential for both.**

So the image registry and the plugin registry are **two different hosts by design**. A rule that
presents the key only to a host that is *itself* a plugin registry can therefore never authenticate
on the fleet's own shape. Measured 2026-09-12 on the first boot of `Deployments/build`
([#4093](https://github.com/Systemorph/MeshWeaver/issues/4093)): the kubelet pulled the image in six
seconds with that very key, and the self-updater in the same pod refused to use it —

```
[SelfUpdate] check (Startup): check FAILED: InvalidOperationException: SelfUpdate:Registry is
'cr.meshweaver.cloud' … but no plugin registry … is configured on that host
```

Every instance provisioned on the fleet registry booted fine and then never self-updated. `Reconcile`
and `Roll` from the control instance still worked, which is why the state is easy to miss: the
instance is healthy, it is merely frozen.

## Why the obvious fix is the wrong fix

The guard is a **credential-disclosure control**. `SelfUpdate:Registry` names a host; without the
check, whatever that host happens to be gets handed this installation's instance key. So none of
these is acceptable:

- **Drop the check** — any `SelfUpdate:Registry` value now receives the key.
- **"When exactly one consumer mount carries a key, present it"** — the number of mounts an
  installation has is not a statement about which registries may hold its key. It is the same rule as
  dropping the check for the common case of one mount, which is most of the fleet.
- **Same registrable domain / a suffix match** (`*.meshweaver.cloud`) — a coincidence of naming, not
  a grant. Whoever can obtain a name under the suffix obtains the key.

What makes the pairing safe is that the registry **declares** which portal it trusts with the key.
That declaration is already data — `RegistrySpec.ValidationUrl` — so the fix restates it on the
consuming side rather than inventing a new concept.

## Where the running portal learns the pairing

🚨 **It does not learn it by itself, and that is deliberate.** `RegistrySpec` lives on the record of
the instance that **HOSTS** the registry (`Deployments/memex-cloud`), on the control instance. A
CONSUMER's record (`build`, `pearl`) has no `registry` block at all, `HelmValues` renders
`registry.validationUrl` only for the hosting record, and reaching across instances to read it would
put a network dependency inside the credential path — a lookup that fails open, or fails the update.

So the pairing is an **explicit declaration the operator sets**, in the consumer's own configuration:

| where | what |
|---|---|
| `SelfUpdate:RegistryValidationUrl` | the config key the portal binds (`SelfUpdateOptions.RegistryValidationUrl`) |
| `selfUpdate.registryValidationUrl` in the chart | renders `SelfUpdate__RegistryValidationUrl` into the portal ConfigMap |
| a record's `extraPortalConfig` / an overlay's `config.memex_portal` | renders the same key — this is how an existing instance is fixed without a chart change |

The value is the registry record's `validationUrl`, copied verbatim
(`https://memex.meshweaver.cloud/api/instances/token`); a bare host means the same thing. **Only the
host is ever read**, and it is read whole: a non-default port is part of it, and a value carrying
userinfo (`https://memex.meshweaver.cloud@evil.example`) declares NOTHING rather than a pairing with
the host a human would not have read. An empty value — the default — declares nothing and refuses.

## What the refusal says

The message names the hosts and the key that would declare the pairing. It never names, echoes,
lengths or logs the credential itself. The one new log line, at `Information` on the paired path
only, names the plugin registry, the container registry and the declared validator — so "who did this
installation hand its key to" is answerable from Loki, and a pairing nobody declared can never
produce a line.

## The negative control

`OciTagListerTest` pins both directions, and the negatives are what prove the guard still guards:

- **paired** — the container registry on its own host, the validator declared: the listing proceeds,
  the key presented is the one held for the *validator*, and the only host contacted is the
  container registry.
- **the same registry, nothing declared** — refused, no request leaves, no key presented. Deleting or
  loosening the host comparison turns this green, which is exactly the point.
- **a declared validator this installation holds no key for** — refused. A declaration alone is not a
  grant.

## See also

- [A Container Registry in Memex](../ContainerRegistryInMemex) — the registry, `docker_auth`, and the
  validation hook that makes one key serve both
- [Self-Update Target Selection](../SelfUpdateTargetSelection) — what the listing is then used for
- [Release & Self-Update Strategy](../ReleaseStrategy)
