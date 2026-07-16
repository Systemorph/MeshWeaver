---
Name: Public pages for logged-out visitors, paywall for the rest
Category: What's New
Description: A logged-out visitor can now open pages with an explicit Anonymous grant; every other page sends them to the partition's configured paywall page, or to sign-in.
Icon: Sparkle
---

# Public pages for logged-out visitors, paywall for the rest

Until now the portal sent every logged-out visitor straight to sign-in — even for pages that were
meant to be public, like a course cover or a catalog. Now a page that carries an explicit
**Anonymous** grant opens directly for logged-out visitors.

For everything else, the partition's **redirect on denied** setting decides where the visitor goes:
if the access policy names a page (for example a course's Subscribe page), the visitor lands there
instead of a dead end — sign-in stays the fallback when nothing is configured. This makes public
course funnels work end to end: the course cover is open to everyone, and every gated lesson leads
to the subscribe page.
