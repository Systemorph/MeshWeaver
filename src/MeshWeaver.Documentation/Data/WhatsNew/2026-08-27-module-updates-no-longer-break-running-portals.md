---
Name: Module updates no longer break running portals
Category: Fix
Description: A Store-module auto-update could break the copy a running portal was still using — chat via an updated AI provider failed with a missing-file error until the pod restarted. Portals now load modules from their own process-local copy, and the boot-time cleanup of old module folders can no longer delete or half-delete anything a pod still needs.
Icon: Sparkle
Order: -20260827
---

# Module updates no longer break running portals

When a Store module auto-updates, its new files land beside the old ones and pods pick the new
version up at their next restart. Until then, a running pod keeps using the version it loaded at
boot — and parts of that version load lazily, on first use, possibly hours later.

The boot-time cleanup that reclaims old module folders did not know that. Once the update had made
the old folder "no longer current", any pod that happened to boot would clean it up — out from
under the sibling pods still running it. The first lazy use after that failed with a missing-file
error: on 2026-08-27 that was chat through an updated AI provider answering
`Could not load file or assembly 'OpenAI'` on every model, until the pod was restarted.

Two rarer variants of the same cleanup could leave a module folder half-deleted (its main file
present, its dependencies gone) or delete a module's current folder outright after a transient
read glitch on the shared volume — both leaving the module marked installed with broken or missing
files behind it.

## What changed

- Each pod now copies the modules it loads to its own process-local storage at boot, so cleanup of
  the shared folders can never reach into a running pod.
- Cleanup now removes a folder in one atomic step — a folder is either fully there or fully gone,
  never half-deleted.
- When the installed-module list cannot be read completely, cleanup skips reclaiming module folders
  that pass instead of treating the unreadable ones as unused.

## What you will notice

Store-module auto-updates roll out without breaking the pods still running the previous version —
no more sudden missing-file errors between an update landing and the restart that applies it.
