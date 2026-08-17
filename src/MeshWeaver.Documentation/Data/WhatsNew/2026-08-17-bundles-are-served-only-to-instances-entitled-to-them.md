---
Name: Bundles are served only to instances entitled to them
Category: Fix
Description: The bundle download route checked that a caller was a registered instance but never checked which packages it was granted, so any registered installation could download every installed package's compiled assemblies — paid content included. It now authorizes per package, and a refusal is indistinguishable from a bundle that does not exist.
Icon: ShieldCheckmark
Order: -20260817
---

# Bundles are served only to instances entitled to them

A registry portal hands other installations the compiled assemblies for the packages they have —
that is what lets a consumer skip a full compile at install time. The route asked callers for a
registered instance key and refused everyone without one. What it never asked was the second half of
the question: *which packages is this caller entitled to?*

So an installation could download the bundle for **any** package the registry had installed, not
only its own — 900 CHF course content included. It needed a valid instance key, so this was never
open to the internet; but an instance key is issued to every installation that registers, which
makes the population "everyone who ever registered", not "someone who broke in". The route's own
documentation described the entitlement check as though it were running. It was not.

Now it runs, on both routes, using the grant that already exists — the per-source, per-package
authorization an administrator issues to each installation, the same one the catalog listing and the
file download have always honoured. There is no second, bundle-specific notion of entitlement to
keep in step with what purchases are recorded against.

Two things were deliberate in how the refusal behaves:

- **A refusal looks exactly like "there is no such bundle."** Same status, same empty body, same
  headers — and an ungranted package is simply absent from the index. Bundle URLs are predictable,
  so an answer that distinguished "you may not have this" from "this does not exist" would let any
  installation enumerate the entire catalogue and its versions without being entitled to a single
  item of it. Which of the two it actually was is written to the registry's log, where the caller
  cannot read it.
- **It fails closed.** An install record that does not say which source it came from is served to
  nobody, rather than being served to everybody. A consumer treats a missing bundle the way it
  always has — it compiles the package itself — so the cost of a refusal is time, never a failed
  install.

Alongside it, the source recorded on an install record now survives a re-install. An automatic
update rebuilds that record from the catalogue entry, and not every catalogue stamps the source, so
the first unattended update could quietly erase the very field the new check reads.
