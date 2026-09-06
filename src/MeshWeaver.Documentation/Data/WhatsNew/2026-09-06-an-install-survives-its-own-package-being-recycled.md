---
Name: An install survives its own package being recycled
Category: Fix
Description: While a package was installing, the platform could legitimately restart that package's own hub — and every write already in flight underneath it was then reported as failed, even though it had already been applied. The install gave up on work it could simply have redone. Those writes are now retried against the restarted package, and the install finishes.
Icon: ArrowSync
Order: -20260906
---

# An install survives its own package being recycled

Installing a package writes a lot of small things underneath it — its policy, its access entries,
the compile state of each type it brings. While that is happening the platform can decide, for
perfectly good reasons, to restart the package's own hub: its type changed, a fresh build of that
type was published, or the installer itself asked for the restart so the package comes back bound to
what it now is.

Restarting is fine. What was not fine is what happened to the writes that were in flight at that
moment. Each of them had already been *applied* — the change had landed and everything watching the
node had seen it — and only the final "and it is safely stored" step was still outstanding. When the
hub went away underneath that step, the platform told the writer the write had failed, in the one
way that means *do not try again*. So the installer stopped, and the whole package install ended in a
timeout it could have avoided entirely.

That is not what a restart means. A component going away says nothing about whether your change was
acceptable — it says the address will be back in a moment. The platform already has a word for
exactly that, and already knows what to do when it hears it: reapply the change against the restarted
component, comparing against the freshest state each time, so a change that did land costs nothing
and one that was lost is put back.

Those writes now carry that word. A package install rides out a restart of its own package instead
of failing on it, and nothing waits any longer than before: no timeout, retry budget or install
deadline changed. A write that genuinely cannot be made — one you are not allowed to do, or one the
node refuses — is still refused immediately, and still says why.

The same correction applies everywhere a node's owner answers a change, not just during installs:
saving from a page while its hub is being recycled now gets the retry it deserves instead of an
error card.
