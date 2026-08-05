---
Name: Plugins update themselves
Category: What's New
Description: Installed plugins now pick up their repo's green builds automatically — your own added or claimed nodes are never touched.
Icon: Sparkle
---

Installed plugins now stay current on their own. When a plugin's repository passes CI, every
installation that uses it installs the change automatically — only the files that actually changed,
usually within minutes. No catalog visit, no Update button.

Your content is safe from this: the update only ever touches nodes the plugin itself ships. Anything
you added alongside them is invisible to it, and any shipped node you modified and claimed (set its
sync behavior to excluded) stays yours — updates skip it entirely, forever.

Prefer to review updates yourself? Opt a package out on its install record and you get an
"Update available" notification instead — nothing installs until you choose.
