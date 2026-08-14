---
Name: Plugin updates reach installations that install from a registry
Category: Fix
Description: An installation that gets its plugins from a registry now finds out when an installed plugin changed, instead of needing someone to notice and click.
Icon: Sparkle
Order: -20260813
---

# Plugin updates reach installations that install from a registry

Until now, "a plugin changed" only ever reached an installation that received GitHub build notifications for the plugin's own repository. An installation that gets its plugins from a registry over HTTP — the setup that deliberately holds no GitHub credentials — had no way to find out at all: its plugins stayed at the version they were installed at until an administrator noticed and pressed Provision.

Such an installation now checks the registry it already installs from, on startup, and compares what the registry is serving with what it has. A plugin that opted into automatic updates is brought up to date; anything else raises a notification on the plugin so an administrator can decide. A plugin whose content has not changed costs nothing, and an installation with no registry configured is unaffected.
