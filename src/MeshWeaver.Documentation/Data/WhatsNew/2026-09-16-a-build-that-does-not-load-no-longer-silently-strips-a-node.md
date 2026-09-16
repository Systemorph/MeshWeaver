---
Name: A build that does not load no longer silently strips a node of its type
Category: Fix
Description: When a node type's compiled build failed to load on a portal replica, an instance activating at that moment kept only the generic pages — none of its type's views, handlers or background watchers — until the next restart, while the type still reported a healthy compile. It now rebuilds once and, if the build still does not load, shows a diagnosis naming the reason.
Icon: Warning
Order: -20260916
---

# A build that does not load no longer silently strips a node of its type

Every node's hub takes its configuration from its type's compiled build **once**, when it
activates. If that build could not be loaded on the replica at that moment — the file gone, stale,
or unreadable — the hub used to activate anyway with only the generic configuration: the
**Overview**, **Settings** and **Search** pages, but none of the views, message handlers or
background watchers its type declares. Nothing said so, the type's own record kept reading
*compiled OK*, and the hub stayed that way until something re-activated it.

On the fleet's control instance that is what stopped the platform-build inbox for twenty hours on
2026-09-16: a transient load failure two minutes after a pod started caught the one hub that is kept
activated for the life of the portal, and with it the webhook inbox, the fleet watch, the build
queue, triage intake and self-update routing
([#4471](https://github.com/Systemorph/MeshWeaver/issues/4471)). A restart drained 303 waiting
deliveries at once.

Now the activation reads the loader's verdict before it binds anything. A build that does not load
is rebuilt once and the node binds the fresh build; if that still fails, the node shows the
*assembly unavailable* diagnosis with the loader's reason, answers requests with a clear failure
instead of ignoring them, and recovers by itself on the type's next successful compile. A build that
loads is bound exactly as before.

What was measured, and the two explanations it ruled out, are on
[An Unloadable Build Is Never A Silent Default](/Doc/Architecture/AnUnloadableBuildIsNeverASilentDefault).
