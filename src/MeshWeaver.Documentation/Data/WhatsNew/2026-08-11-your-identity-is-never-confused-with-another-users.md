---
Name: Your identity is never confused with another user's
Category: Fix
Description: Closed a cross-user identity leak that could let one person's session decide what another person — or an anonymous visitor — was allowed to see.
Icon: Sparkle
Order: -20260811
---

# Your identity is never confused with another user's

On a busy portal, the server could briefly fall back to "whoever signed in most recently" when it
could not otherwise tell who was making a request. That fallback was shared by everyone in the
process, so a page render, a search, or a background save could be attributed to the wrong person —
including an anonymous visitor inheriting a signed-in user's view of the data.

Identity is now scoped to the session it belongs to, and anything that genuinely cannot tell who is
asking is refused instead of guessed. Content you are not entitled to see can no longer be returned
because someone else was active at the same moment, and saved changes are always recorded against
the person who actually made them.
