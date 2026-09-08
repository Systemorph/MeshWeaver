---
Name: A module version that ships is in use within minutes
Category: Feature
Description: When a module is published to the registry your installation consumes, your installation now lands it within minutes and restarts itself to load it — instead of waiting for its next reboot, which could be days away. A module that could not load on your platform is re-examined every time a new build of it appears, and a half-hourly safety net catches anything the notification missed.
Icon: ArrowSync
Order: -20260908
---

# A module version that ships is in use within minutes

Until now, an installation learned that a module had a new version at exactly one moment: when it
**booted**. The registry's feed was read once per process start, and if a module had shipped since,
the new bundle landed and waited for the *next* restart to load. On an installation that only
restarts when its platform image rolls, "the module shipped" and "the module is running" could be
days apart — and a module that had landed but could not load on your platform was never looked at
again until a reboot happened to follow a rebuild.

Three things change, and together they make the maintainer's rule true: **as soon as a new module
version ships, the installation starts using it.**

## The registry tells you

The moment a module bundle is published to a registry, the registry sends a short notification to
every installation registered with it — to the same webhook inbox every other event reaches your
portal through. Your installation reads nothing off that notification but *which package* to look
at: it then reads the registry's own index, decides exactly as the boot would, and lands the
bundle if it is newer or built for your platform. A notification that arrives while your portal is
down is consumed at the next boot; one that is stale, duplicated or forged costs one authenticated
index read and changes nothing.

To receive it, allowlist the inbox target on the consuming installation:

```
WebhookInbox__Targets__0=Plugins/_RegistryReconcileLedger
```

and make sure the installation recorded its public URL when it registered
(`PluginCatalog:HomeUrl`). An installation that allowlists the target *with* a `SecretConfigKey`
must share that secret with the registry (`Plugins:Registry:BroadcastSecret`); without one the
delivery is unsigned, which is safe for the reason above.

## A safety net, every 30 minutes

A notification reaches you over a chain nobody re-verifies — the URL you registered, the allowlist
slot, the ingress in between — and every joint of it fails silently. So every
`PluginCatalog:ReconcileSafetyNetInterval` (default 30 minutes; zero disables it) your installation
reconciles its installed packages against each registry's feed regardless. This is not what drives
the update — the notification does — it is the longest a lost notification can hide.

## The restart happens

A module never swaps inside a running process: a landed generation loads at the next restart.
That restart is now taken by the self-updater itself. After every check that finds nothing newer to
roll to, it reads the module activation record and, when a landed generation is waiting, rolls the
portal workloads **on the image they already run** — paced by the same `SelfUpdate:MinRollInterval`
floor as any other roll, so publication frequency never becomes restart frequency. An installation
that cannot restart itself says so on the Updates tab (`RestartUnavailable`), naming the move.

> On Kubernetes the restart is issued by the `MeshWeaver.SelfUpdate.Aks` module; an updater that
> predates it reports the pending restart rather than taking it.

## A fallback is re-examined

When the newest landed generation of a module cannot be loaded on your platform and the previous
one keeps running, every reconcile now checks whether the registry serves a *different* build of
that version than the one that would not load — and lands it when it does. The catalog's decision
names the state (`SkipUnloadable`) instead of reporting the module as "already landed".

Full reference: [Plugin Update on Green Build](/Doc/Architecture/PluginUpdateOnGreenBuild) and
[Modules](/Doc/Architecture/Modules).
