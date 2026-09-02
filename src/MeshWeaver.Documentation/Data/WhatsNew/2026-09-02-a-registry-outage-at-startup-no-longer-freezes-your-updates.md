---
Name: A registry outage at startup no longer freezes your installed packages
Category: Fix
Description: When the plugin registry stayed unavailable for longer than an installation's startup retry budget, the installation silently stopped checking its packages for updates until its next restart. The skipped check is now recorded, platform admins are told, and it runs automatically the next time anyone opens the catalog or installs a package.
Icon: Sparkle
Order: -20260902
---

# A registry outage at startup no longer freezes your installed packages

An installation checks its installed packages against the plugin registry when it starts. Since
the end of August a registry that answers *temporarily unavailable* is asked again a few times
within that startup window — but a registry that stays down for longer than the window left the
check undone, and nothing said so: the only trace was one error line in one server log, and the
installation did not notice package or grant changes again until somebody restarted it.

Two things are different now.

**You are told.** When the startup check gives up on a registry, platform administrators get a
notification in the bell naming the registry, how many attempts were made and what the registry
last answered. The check is also recorded as *pending* on a small bookkeeping node in the Plugins
partition, so the state can be read rather than inferred from a log.

**It catches up by itself — without a timer.** The next time the installation reaches that
registry for any reason at all — someone opens the plugin catalog, installs a package, or the
Store counts what is available — the check that was skipped at startup runs from that very
answer. There is still no background poll behind this, deliberately: the events the installation
already has are enough, and now they do what the startup log always said they would.
