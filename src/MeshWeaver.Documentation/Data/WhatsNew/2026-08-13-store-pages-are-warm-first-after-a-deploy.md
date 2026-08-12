---
Name: Store and paywall pages are warm first after a deploy
Category: Fix
Description: The types behind every store cover and the subscribe flow used to be compiled LAST after an update, so the first visitor sat through a chain of slow redirects. They are now compiled first.
Icon: Sparkle
Order: -20260813
---

# Store and paywall pages are warm first after a deploy

Every platform update recompiles the code behind your content types, and until a
type is compiled the first person to open a page built on it waits for the
compiler. That work is done in dependency order — a type that borrows code from
another has to wait for it.

Three of the store's types borrow code from each other in a loop, and a loop has
no order. The old rule dealt with that by putting the whole loop at the very
**end** of the queue — and behind it everything that depends on it: the store
catalogue, enrolments, installs, provisioning. So the most-visited pages on the
portal were the last to become fast, and opening `Subscribe` shortly after an
update meant waiting through several slow redirects.

A loop only means "these three have no order **between them**". It says nothing
about when they should be built relative to anything else. They are now compiled
as soon as everything they genuinely wait for is ready — which, for the store, is
nothing at all, so they go first — and each dependent follows immediately behind
the thing it was waiting on, keeping the whole store chain together at the front
of the queue.
