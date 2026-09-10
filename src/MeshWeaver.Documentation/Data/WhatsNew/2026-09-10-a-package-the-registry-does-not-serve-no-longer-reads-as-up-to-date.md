---
Name: A package the registry does not serve no longer reads as up to date
Category: Fix
Description: An installed package whose module the registry does not offer was invisible to both lanes of the update reconcile — its content kept advancing while its module bytes stayed on the generation the deployment was first seeded with, and every surface read "installed, up to date". The reconcile now records what a registry does NOT deliver on its own ledger, names it in one Warning, and distinguishes "not offered" from "could not tell".
Icon: PlugConnected
Order: -20260910
---

# A package the registry does not serve no longer reads as up to date

On 2026-09-10 at 02:40Z `memex` was running `MeshWeaver.Mail.MicrosoftGraph` from an assembly
written on **2026-08-30** — eleven days earlier — on an image built from the very commit that
shipped that module's 1.5. The package's install record said `version 1.5.0`, `installedAtUtc`
one hour before the measurement, with the 1.5 file hashes; the health check said no module
activation was pending, correctly, because nothing had landed and so nothing was waiting on a
restart. Every surface read *installed, up to date*. An Executive Assistant thread asked for the
Teams tools that shipped in that module and had none.

**Why nothing advanced.** Both lanes of the registry reconcile — the content lane and the module
lane — iterate the packages the **registry serves** and intersect them with this installation's
install records. The registry answered its feed with 45 manifests and `Mail` was not among them:
the package declares `tier: personal` and the instance's catalog grant covers the baseline plan, so
the registry declined it (its own maintenance task says so in as many words — *the registry does
not offer a package 'Mail' to this instance*). A package the registry does not serve is therefore
neither up to date nor failed. It is **absent from both loops**, and absence produced no log line,
no ledger entry and no card state anywhere, while the package's content kept arriving through the
instance's own git source and its install record kept advancing.

**The absence is now an answer.** After the content and module lanes, a full feed pass lists the
install records once and records on the reconcile ledger
(`Plugins/_RegistryReconcileLedger`) which installed packages declare a module that *this registry
did not offer* — the package, the module whose bytes are not arriving, and the content identity the
record claims. One Warning names them and points at the ledger;
`ModuleDelivery.NotDeliveredByAnyRegistry` intersects the per-registry entries, so on an
installation with several registries a package one of them serves is still delivered.

Three things it deliberately does not do. It does not treat a **failed** listing as an empty one —
that records *not determined*, never *none*. It does not run from the per-package broadcast drain,
whose one named package would make every other installed module look undelivered. And it is not an
entitlement verdict or a fault: a consumer cannot tell "your grant does not cover this" from "this
registry never carried it", and the remedy belongs to whoever runs the registry — grant the
instance the plan, tier the package differently, or point it at a registry that carries it.

The mechanism is written up in
[Plugin Update on Green Build](/Doc/Architecture/PluginUpdateOnGreenBuild) → "A package the
registry does NOT offer is recorded as NOT DELIVERED".
