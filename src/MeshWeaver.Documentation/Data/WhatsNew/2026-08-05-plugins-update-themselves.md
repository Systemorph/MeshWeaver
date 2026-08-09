---
Name: Plugins can update themselves
Category: Feature
Description: Opt a plugin (or your whole deployment) into automatic updates — green builds install themselves, and your own added or claimed nodes are never touched.
Icon: Sparkle
Order: -20260805
---

Installed plugins can now stay current on their own. When a plugin's repository passes CI, an
installation that opted in installs the change automatically — only the files that actually
changed, usually within minutes. No catalog visit, no Update button. On the MeshWeaver cloud
portals this is on for every plugin.

By default nothing installs unattended: you get an "Update available" notification on the
installed package and choose when to apply it. Opt in per package on its install record, or for a
whole deployment with one setting (`PluginCatalog:AutoUpdateByDefault`) so everything installed
from then on tracks its repository.

Your content is safe either way: an update only ever touches nodes the plugin itself ships.
Anything you added alongside them is invisible to it, and any shipped node you modified and claimed
(set its sync behavior to excluded) stays yours — updates skip it entirely, forever.
