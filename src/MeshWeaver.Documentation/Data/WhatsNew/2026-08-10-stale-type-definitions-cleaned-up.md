---
Name: Leftover content-type definitions are cleaned up
Category: Fix
Description: Outdated type definitions left behind by earlier renames no longer fail to compile at every start.
Icon: Sparkle
Order: -20260810
---

# Leftover content-type definitions are cleaned up

Two earlier renames moved their source code to a new home but left the old content-type
definitions behind. With no source of their own, those leftovers failed to compile at every
platform start — harmless, but noisy in the logs and in the diagnostics views, and they
could not be removed through the normal delete because parts of them were system-owned.

A repair now removes these leftovers, including their recorded compile state and version
history. Databases that never had them are unaffected, and the surrounding plugin
configuration is left untouched.
