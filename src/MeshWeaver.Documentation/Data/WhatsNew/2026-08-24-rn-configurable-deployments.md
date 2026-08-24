---
Name: Configurable mobile-app deployments
Category: Feature
Description: A deployment manifest now composes the mobile app: its portal, its branding, and the client modules bundled into it — one JSON file per deployment, no code change.
Icon: Sparkle
Order: -20260824
---

# Configurable mobile-app deployments

The mobile app is now composed per deployment from a single manifest: which mesh it connects to,
its display name and brand colors, and which client modules ship in its bundle. A client portal
gets its own branded app by writing one JSON file — no code change.

Most module interfaces never needed an app change to begin with: pages and controls declared on
the mesh render through the standard app. The manifest covers the rest — bespoke visual pieces a
module brings along (a game board, a special chart) join the app at build time, and the local-mesh
sidecar bakes exactly the declared variant into its release.
