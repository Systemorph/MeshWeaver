---
Name: Shutdown no longer cuts itself short
Category: Fix
Description: A package or space that takes a while to shut down is now allowed to finish, instead of being force-torn-down after eight seconds.
Icon: Sparkle
Order: -20260822
---

# Shutdown no longer cuts itself short

Restarting or recycling something in the mesh — a package root, a space, a node type being rebuilt —
tears down a whole tree of internal parts, one level at a time. A safety net was watching that
teardown and stepping in if it had not finished after eight seconds.

The net measured the wrong thing. It counted total elapsed time, so a teardown that was going
perfectly well but simply had several levels to work through was cut off and forced apart halfway.
Anything reading that package during the window then saw it as unavailable, and a rebuild started in
that window could fail outright.

The net now watches for a teardown that has actually *stopped moving*: as long as the shutdown keeps
making progress it is left alone, however long it takes, and it is still stepped in on the moment it
genuinely stalls.
