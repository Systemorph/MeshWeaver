---
Name: A plugin that cannot install on this instance now says so, immediately
Category: Fix
Description: Installing a plugin whose content collides with a catalog this instance already serves from memory used to stall for thirty seconds and then fail with nothing to act on — it now stops at once and names the setting that fixes it.
Icon: Sparkle
Order: -20260811
---

# A plugin that cannot install on this instance now says so, immediately

Some catalogs — agents, skills, harnesses, model providers, the documentation — can be served
two ways: straight from the instance's own program, or from its database. Which one an instance
uses is a setting, and it must be the database whenever a plugin is going to put content there.
Get it wrong and the two claim the same place at once: the built-in copy wins every read, and the
plugin's content lands somewhere nothing can see it again.

Until now nothing said any of that. The install simply sat there for thirty seconds and gave up
with a timeout, having written nothing. Repeating it produced the same thirty seconds and the
same empty result, and the message pointed at a wait rather than at the cause — so the only way
to the answer was to already know it.

Now the collision is checked before anything is written. The install stops in under a second and
says exactly which path is contested, which built-in catalog is claiming it, and which setting to
change so the database owns that content instead. Nothing is half-written, so there is nothing to
clean up before retrying once the setting is right.

Instances that serve those catalogs from memory and install no plugin into them are unaffected —
that arrangement is perfectly valid and keeps working exactly as before.
