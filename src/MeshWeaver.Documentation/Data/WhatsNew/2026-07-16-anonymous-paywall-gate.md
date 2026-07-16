---
Name: Public covers for logged-out visitors, sign-in first everywhere else
Category: What's New
Description: A logged-out visitor can open pages with an explicit Anonymous grant (course covers, catalogs); every other page asks them to sign in first — and after sign-in, gated content leads to the partition's configured paywall page.
Icon: Sparkle
---

# Public covers for logged-out visitors, sign-in first everywhere else

Until now the portal sent every logged-out visitor straight to sign-in — even for pages that were
meant to be public, like a course cover or a catalog. Now a page that carries an explicit
**Anonymous** grant opens directly for logged-out visitors.

Every other page keeps asking for sign-in first, and returns the visitor to the page they wanted
afterwards. If that page is gated content in a partition with a **redirect on denied** setting
(for example a course's Subscribe page), the signed-in visitor lands there instead of an error —
so a public course funnel works end to end: open cover, sign in, subscribe, learn.

The user home also became extensible: the tab row on your home page (Spaces, My Items, …) now
accepts tabs published as mesh nodes, so a plugin can add its own tab — like a Courses tab with a
"+" that opens the course catalog — without any platform change.
