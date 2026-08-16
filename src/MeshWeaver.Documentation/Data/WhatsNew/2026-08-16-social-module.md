---
Name: LinkedIn publishing rides the module lane
Category: Feature
Description: The LinkedIn integration (connect, publish, page sync, node-menu actions) is now the MeshWeaver.Social module — one Modules:Assemblies line turns it on or off per deployment.
Icon: Sparkle
Order: -20260816
---

# LinkedIn publishing rides the module lane

The LinkedIn integration — connecting your account, publishing posts, refreshing engagement, and
company-Page sync — now ships as the `MeshWeaver.Social` module instead of being hard-wired into
the portal. A deployment turns the whole feature on or off with one `Modules:Assemblies` line: its
HTTP routes and menu actions appear when the module is listed and disappear cleanly when it is not.
Nothing changes for users on deployments that keep it on — the routes, menus, and stored
credentials work exactly as before.
