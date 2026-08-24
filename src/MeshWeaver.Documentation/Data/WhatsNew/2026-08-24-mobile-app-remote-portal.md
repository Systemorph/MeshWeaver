---
Name: Mobile app: pages render against remote portals, and the home shows your apps
Category: Fix
Description: The mobile/web-lite client now renders markdown pages, file listings and the tabbed apps home against a signed-in portal, and node-bound editors show and save their values.
Icon: Sparkle
Order: -20260824
---

# Mobile app: pages render against remote portals, and the home shows your apps

Signing the mobile app into a portal now works end to end. Markdown pages render properly instead
of showing their raw source, file listings fill in, and breadcrumbs show real page names — a defect
in the shared REST client had silently failed every one of those calls in the browser.

The home screen now matches the portal: the Apps icon grid, the scope tabs (Pinned, Apps, Spaces,
All) and tapping an app to open it all work, instead of falling back to the old flat catalog.

Editors that bind straight to a mesh node — the Data view, profile fields, content editors — now
display the node's values and save edits back, field by field.

The local-first sidecar (Memex.LocalMesh) also bakes a fresh copy of the app into every release
publish, so a shipped sidecar can no longer serve a stale interface.
