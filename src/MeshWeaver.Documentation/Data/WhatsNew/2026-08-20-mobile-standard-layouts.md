---
Name: The phone app renders every node's standard layout
Category: Fix
Description: Navigation asks each node for its declared default layout — your own page opens on your activities — and breadcrumbs show node names instead of raw path segments.
Icon: Sparkle
Order: -20260820
---

# The phone app renders every node's standard layout

Navigating in the phone app used to hardcode a layout area name, so tapping your own entry in the
breadcrumb showed your profile instead of your activities — and the breadcrumb itself displayed the
raw path segment ("device-user") rather than your name. The app now asks the mesh for each node's
declared default layout, exactly the way the web portal resolves it, and the breadcrumb shows every
node's display name.
