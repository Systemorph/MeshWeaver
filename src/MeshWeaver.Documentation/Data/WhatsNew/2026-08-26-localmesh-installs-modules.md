---
Name: The local mesh installs modules
Category: Fix
Description: Listing a module for the local mesh did nothing unless it happened to be built into the sidecar; modules now install there the same way they do on a portal.
Icon: PlugConnected
Order: -20260826
---

# The local mesh installs modules

The local mesh reads a list of modules to install, the same setting a portal uses. On the local
mesh that list was only ever half-connected: a module was picked up if it happened to be compiled
into the sidecar already, and otherwise nothing happened at all — no error, no warning, just a
feature that was quietly absent.

That is now wired properly. Modules are laid out beside the local mesh in their own folders, the
way they are on a portal, and the list is what decides whether one runs. Adding or removing an
entry has a real effect, and the AI engine — which powers chat threads from the mobile and web
shells — arrives through that same route instead of being built in.

Nothing changes about how you use chat on the local mesh. What changes is that the local mesh and
the portals now install modules the same way, so a module that works on one works on the other.
