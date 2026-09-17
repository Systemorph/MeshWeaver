---
Name: A landed module the image also ships no longer asks for a restart that cannot help
Category: Fix
Description: Eight modules on one portal reported "landed but not yet loaded — a restart activates them" and stayed listed after the restart, because the boot had declined them by comparing two different kinds of identity that can never be equal. The comparison now compares like with like, so a module built by this platform's own build is adopted, and a module that is not says so with the remedy that actually clears it.
Icon: Fingerprint
Order: -20260917
---

# A landed module the image also ships no longer asks for a restart that cannot help

A portal's health page reported this, and kept reporting it:

> **8 module(s) are landed but not yet loaded in this process — a restart activates them:**
> MeshWeaver.AI, MeshWeaver.Blazor.Chat, MeshWeaver.Blazor.EntityViews, MeshWeaver.Blazor.Graph,
> MeshWeaver.Hosting.Instance, MeshWeaver.Markdown.Collaboration, MeshWeaver.Markdown.Export,
> MeshWeaver.Mcp

The deployment was restarted. The new pods listed the same eight modules sixteen minutes later. Two
operators had by then restarted a production portal for nothing, and the boot log was asking for a
second remedy that could not work either — *re-install the module*, which lands the same bytes.

## What was actually happening

A module the image ships its own copy of, and the registry ALSO delivers, has two copies on the
pod. Boot decides between them on the framework build each was compiled against: where the store
copy was built against a different platform build, the image's own copy runs, because that one is
correct for this platform by construction.

The two sides wrote that fact in two different alphabets. A module bundle states the platform's
**commit** identity (`gce971b2c…`) because that is what its producer can read off the platform it
packed against. A portal resolves its own **API-surface** identity (`s4b28366…`) because that is
what decides whether compiled content is stale. Comparing the two as strings answers *different*
for every pair that exists in the fleet — so every store copy of a double-shipped module was
declined on every boot, whatever the registry published, and the activation report (which could not
see the decline) called it "waiting for a restart".

## What changed

- The comparison is **scheme-aware**, and the platform now states **both** of its readings. A module
  packed by the same build that produced the image matches it and is adopted — the convergence the
  rule always promised.
- A module built against another platform build still loses to the image's copy, and the reason
  names both values. Where the two identities cannot be compared at all, it says exactly that
  instead of asserting a difference nothing measured.
- The declined state is now **its own row** on the activation report, not folded into "pending". It
  reads: *the module RUNS, from the image's own copy; a restart does not change this and neither
  does re-installing; it clears when the module is published built against this platform build.*
- The `STALE PACK` line in the boot log stops telling the reader to re-install a module that was
  declined.

Nothing was ever missing on those portals — each of the eight modules was running, from the copy
the image ships. What was broken was every sentence describing it.

The full account, with the measurements: [Two Identity Schemes, One
Comparison](/Doc/Architecture/ModuleIdentitySchemes).
