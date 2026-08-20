---
Name: Installed modules load again after a restart
Category: Fix
Description: Modules installed from the store are found at startup again, instead of being silently skipped so their features disappeared.
Icon: Sparkle
Order: -20260820
---

# Installed modules load again after a restart

Installing a module writes its files into a fresh folder each time, so an update can never disturb the copy a running instance is using. Startup, however, was still looking for the older folder name — so it found nothing, skipped every installed module, and came up as if none had ever been installed.

The effect was invisible until you looked for the feature: chat could not find a model to answer with, the MCP endpoint was not there, and each restart quietly re-downloaded the same modules it had just decided were missing.

Startup now follows the pointer each installation records, so a module is loaded from exactly the folder it was installed into. Modules that were installed before this fix load again on the next restart, with nothing to re-install. Files a module ships for the browser — styles and scripts — are found the same way, so its pages render properly too.
