---
Name: An installed pack is what loads, even when the platform ships one too
Category: Fix
Description: A module you installed from the registry now wins over a same-named copy that ships with the platform — and if the installed one cannot load, the platform copy still keeps the feature working.
Icon: Bug
Order: -20260827
---

# An installed pack is what loads, even when the platform ships one too

When a module was both listed by the platform and installed from the registry, the platform's copy won and the installed one was unreachable. Installing or upgrading such a module appeared to succeed and changed nothing.

On a **brand-new mesh** the same rule had a much louder effect. Two of the view packs that draw the interface had recently moved out of the platform image and into the registry, so on a fresh instance nothing supplied them — and every view fell back to printing its own description instead of rendering. The instance reported itself healthy while showing raw text where the interface should be. Existing installs were unaffected: they already had the packs from before the move.

Now the installed copy is the one that loads.

**Two things it deliberately does not do:**

- **A copy that cannot load does not take the feature down with it.** If the installed module is missing its files, or is built for a newer platform than this instance runs, the platform's own copy keeps working and the reason is reported. A refused upgrade should never turn into a missing feature.
- **Disabling a module returns it to the platform's copy** rather than removing it.

Nothing changes for an instance where only one copy exists — the same modules load, in the same order.

**Still open:** an instance with no registry configured at all — a local development mesh — has nowhere to get these packs from, and is unaffected by this fix. That is tracked separately.
