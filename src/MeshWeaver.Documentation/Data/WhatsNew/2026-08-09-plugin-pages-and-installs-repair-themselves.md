---
Name: Plugin pages show their overview, and installations repair themselves
Category: Fix
Description: A plugin page you have not purchased now shows its cover instead of a permission error, and an installation whose plugins failed to install fixes itself on the next restart instead of staying empty.
Icon: Sparkle
Order: -20260809
---

# Plugin pages show their overview, and installations repair themselves

Opening a plugin you had not purchased could show a raw permission error above the page instead of
the plugin's own cover. Being asked to buy something is a normal state, not a fault, so the page now
simply shows what it is offering.

Installations are also more robust about the plugins they ship with. Previously a deployment whose
plugin configuration was wrong on its very first start would come up looking healthy but with
nothing installed, and correcting the configuration afterwards had no effect — the only route back
was a rebuild from scratch. An installation now completes any plugin it has not yet delivered the
next time it starts, while leaving anything an administrator removed on purpose alone.
