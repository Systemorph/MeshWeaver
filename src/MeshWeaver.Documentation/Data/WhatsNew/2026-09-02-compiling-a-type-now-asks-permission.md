---
Name: Compiling a type now asks permission
Category: Fix
Description: Anyone who could open a NodeType could also trigger a build of it, even with read-only access. Compiling now requires the Compile permission — the one editors already have and viewers do not.
Icon: ShieldTask
Order: -20260902
---

# Compiling a type now asks permission

Most operations on a node check what you are allowed to do with it before doing it. Recycling one
needs write access. Exporting one needs export access.

**Compiling one checked nothing at all.** Anyone who could read a type could ask the platform to
build it — which schedules a real compilation and records an activity under the node, both of them
things a read-only visitor has no business starting.

The odd part is that the permission for this already existed and was already set up correctly.
*Compile* is exactly the entitlement an editor has and a viewer does not — it is what "may ship a
release" means. It was simply never consulted. So this is less a new rule than a rule that was
written down and never read.

Compiling now requires that permission. In practice nothing changes for the people who do it:
editors, owners and administrators all carry it already. A read-only visitor gets a refusal that
names what to ask for instead of a build nobody authorised.

**A refusal and a failed check are kept apart**, which matters more than it sounds. If the platform
cannot *establish* your permissions — a momentary hiccup rather than a decision — you are told to try
again, not told you lack access. Telling someone they need a permission they already hold sends them
off to request something they own, and that mistake is easy to make and hard to notice.
