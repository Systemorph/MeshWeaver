---
Name: A local portal serves every plugin repo you have checked out
Category: Feature
Description: A local install now discovers the node repos beside your platform checkout and serves all of them, instead of only the one named MeshWeaver.Plugins.
Icon: Sparkle
Order: -20260831
---

# A local portal serves every plugin repo you have checked out

A local install acts as its own plugin registry and reads plugin repositories straight off your
disk. It used to look for exactly one directory — the one literally named `MeshWeaver.Plugins` —
and everything else had to be listed in an environment variable, on every single command.

The effect was quiet and easy to misread. With the course repository checked out right next to the
platform, the portal still could not serve a single course, and nothing said why: the Store simply
listed less than you expected. Setting the variable fixed it, until the next update typed without
it, at which point the courses disappeared again.

Now every repository beside your checkout that actually declares packages is discovered and served.
Clone one next to the others and it appears on the next update — no configuration, and nothing to
remember a second time.

Discovery goes by what a repository *contains* rather than what it is called, so the platform
checkout and its worktrees leave themselves out, and a repository added or renamed later is picked
up with no change. `MEMEX_PLUGIN_REPOS` still works when you want to serve fewer repositories than
you have checked out.
