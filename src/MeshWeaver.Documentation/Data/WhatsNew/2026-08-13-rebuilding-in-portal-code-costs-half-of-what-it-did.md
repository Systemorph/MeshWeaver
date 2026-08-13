---
Name: Rebuilding in-portal code costs half of what it did
Category: Fix
Description: Every time something subscribed to a page, the portal built a private copy of an internal lookup it could have shared — and never gave it back. Those copies are now shared, halving what a rebuild of in-portal code leaves behind.
Icon: Sparkle
Order: -20260813
---

# Rebuilding in-portal code costs half of what it did

The portal reads a page through a chain of narrowing views: the whole store, then one collection
inside it, then the single item you asked for. Each link in that chain is real machinery — it keeps
itself in step with the one above it, and it costs memory for as long as it exists.

Only the last link belongs to whoever asked. The middle one is anonymous: it exists solely so the
last link has something to narrow, and nobody ever holds it, names it, or closes it. It is owned by
the link above, which for the store at the root of a page lives as long as the page is being served.

The portal was building a fresh middle link every time anything subscribed — a view opening, a
background watcher attaching, a save routed through. The last link was cleaned up properly when the
subscriber went away. The anonymous middle one was not, because nothing had ever been made
responsible for it. So every subscription left one behind, permanently.

That was quiet on a page read a handful of times, and loud on anything the portal writes to
constantly. Rebuilding a piece of in-portal code writes to its status record dozens of times, and
the status record was where these accumulated fastest — it accounted for the single largest share of
what a rebuild never gave back.

Anonymous middle links are now shared. Asking for the same one twice returns the same instance, and
it lives and dies with the link above it. Links that a caller genuinely owns — the ones a view
closes when you navigate away — are untouched and still private, because sharing those would let one
view's cleanup break another's.

Measured on a rebuild loop: what a rebuild leaves behind dropped from about thirteen pieces of
internal machinery to six, and from seven megabytes to four. Two of the three places that were
growing now grow by nothing at all. The remaining share belongs to the per-rebuild record itself,
which is a different mechanism, and the measurement that found this is tightened again so it cannot
quietly get worse.
