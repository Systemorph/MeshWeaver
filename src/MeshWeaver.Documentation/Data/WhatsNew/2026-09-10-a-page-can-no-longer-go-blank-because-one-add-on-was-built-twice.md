---
Name: A page can no longer go blank because one add-on was built twice
Category: Fix
Description: A release could ship two different builds of the same shared component. Whichever one a server happened to load first won, everything compiled against the other was rejected, and the affected pages rendered nothing until the next release. Releases carrying that mixture are now refused when they are built.
Icon: Checkmark
Order: -20260910
---

# A page can no longer go blank because one add-on was built twice

Some add-ons share a component with other add-ons. Until now, a release could be assembled out of
several separate compilations, and each one produced its **own copy** of that shared component. The
copies were built from identical source, so nothing looked wrong — but they were not the same file,
and a server can only ever use one of them.

Whichever copy the server loaded first won. Everything that had been compiled against one of the
others was then rejected as "built against something else", and the pages behind it were left with
no views to render. On a portal running two servers, each one could pick a different copy, and they
took turns rebuilding and rejecting each other's work indefinitely.

The visible result was a page that simply rendered **nothing** — no error, no warning, no banner.
Reloading did not help. Restarting the affected area did not help. Publishing the missing content
did not help either, because the content was never the problem.

## What changes

**A release that carries two different builds of one shared component is now refused as it is
assembled.** The check happens where every copy is together for the first time, which is also the
last moment before the mixture starts being written into everything else. Nothing ships in that
state any more.

**The refusal says which two producers to look at.** It names the component, both builds, and for
each one whether it arrived as the add-on that *owns* that component or as a copy riding along with
another add-on. That is what tells whoever is publishing which half to change.

**Sharing a component is still completely normal.** More than half of the add-ons in the catalog
carry a copy of something another add-on owns, and that stays exactly as it was. Only *disagreeing*
copies are refused; identical ones pass, and the check reports how many it compared so that a check
that quietly looked at nothing cannot pass for a clean one.

## What this does not change

**Nothing about how you install or update add-ons.** This is a check on how a release is built. If
you never saw a blank page, nothing you do changes.

**A portal that already carries the mixture still needs a new release.** The check prevents new ones
from being published; an installation that already picked one up recovers when it takes the next
release that passes.
