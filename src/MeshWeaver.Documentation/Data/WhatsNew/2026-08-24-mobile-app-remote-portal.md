---
Name: Mobile app: pages render against remote portals
Category: Fix
Description: The mobile/web-lite client now renders markdown pages and file listings against a signed-in portal, and node-bound editors show and save their values.
Icon: Sparkle
Order: -20260824
---

# Mobile app: pages render against remote portals

Signing the mobile app into a portal now works end to end. Markdown pages render properly instead
of showing their raw source, file listings fill in, and breadcrumbs show real page names — a defect
in the shared REST client had silently failed every one of those calls in the browser.

Editors that bind straight to a mesh node — the Data view, profile fields, content editors — now
display the node's values and save edits back, field by field.

The home got its polish on mobile too: app icons actually render (including the SVG node icons),
Hosting sits on the Apps grid of the local device mesh like any other app, the chat composer sends
with a compact arrow button, and the sections breathe instead of sitting flush.

The local-first sidecar (Memex.LocalMesh) also bakes a fresh copy of the app into every release
publish, so a shipped sidecar can no longer serve a stale interface.
