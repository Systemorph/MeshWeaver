---
Name: An operator can force a rebuild when the hosts changed
Category: Fix
Description: The portal hosts live in a different repository from the one continuous delivery keys on, so a change to a file that ships inside the image was invisible to delivery until an unrelated commit happened to rebuild — and dispatching the workflow by hand did nothing, because it took the same "already complete" path.
Icon: ArrowClockwise
Order: -20260830
---

# An operator can force a rebuild when the hosts changed

Continuous delivery decides whether to build by asking whether the current commit already has a
complete set of images. That question is keyed on **this** repository's commit — but the portal
hosts live in `MeshWeaver.Plugins`. A merge there that edits a file shipping *inside* the image, such
as the host's `appsettings.json`, changes what the image should contain and changes nothing this
check can see.

The consequence was a change that had merged and could not ship: the newest image predated the fix,
and no producer existed that would ever rebuild — delivery simply had nothing to notice. It waited
for an unrelated commit in this repository to happen along.

Dispatching the workflow by hand did not help, and that is the part worth being precise about: a
manual run took the same reconcile path, found the image set complete for the same commit, and
decided to re-assert the content bake without building. It was a **structural no-op** for exactly
the case an operator would reach for it.

Running the workflow now takes a **Force rebuild** option, which builds the full image set for the
current commit even when its set is already complete.

## This is the escape hatch, not the fix

The durable repair is to make the image's identity honest — record which `MeshWeaver.Plugins` commit
it was built from, and have the completeness check compare that against the current one — so the
**reconciler notices on its own** rather than waiting for a human who happens to know. That is still
open.

What changes today is that someone who *does* know now has a way to say so. Before, they did not.
