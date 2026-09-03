---
Name: Signing in no longer depends on a mesh-wide query
Category: Fix
Description: On portals built with the newest storage layer every signed-in user was answered "We could not check your account just now" on every page. The sign-in path now reads your roles from the three places they are granted instead of searching the whole mesh, and every other whole-mesh read the platform makes says so explicitly.
Icon: ShieldCheckmark
Order: -20260903
---

# Signing in no longer depends on a mesh-wide query

If your portal was built from the newest platform packages, signing in ended on a 503 for
everyone — *"We could not check your account just now. This is a temporary problem on our side, not
a problem with your account."* — on every page, every time. The message was honest: the portal had
refused to guess at your account rather than tell a signed-in user they had none. But the problem
was not temporary.

**What happened.** Checking who you are involved a query for your role grants that named no
partition — it asked the whole mesh. On a large portal that was a union over every partition schema
on every request (measured the day before at around four seconds each under load), and the storage
layer had just, deliberately, stopped serving such queries: a read that does not say where to look
is now refused rather than silently paid for by everyone else. The sign-in path was the first caller
to meet that refusal, and it met it on every request.

**What changed.**

- Your platform roles are read from the three places they are granted — the platform-wide grants,
  the platform-admin grants, and the grants on your own home — each a single, pinned read. The
  result is the same set of roles; what is gone is the search of every partition to find them.
- Every other read the platform makes across the whole mesh — the list of node types, the menu
  contributions plugins add, the Spaces on your home page, the sitemap, the outbound-mail sender —
  now declares that it is mesh-wide, so the storage layer serves it knowingly instead of refusing
  it. Nothing about what those pages show has changed.
- A test now classifies every such query the platform issues with the storage layer's own rule, so a
  new mesh-wide read cannot arrive unannounced.

If you saw the 503, no action is needed: reload after your portal has been updated.
