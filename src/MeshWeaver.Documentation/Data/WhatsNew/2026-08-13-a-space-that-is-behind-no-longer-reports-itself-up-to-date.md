---
Name: A Space that is behind no longer reports itself up to date
Category: Fix
Description: GitHub sync stops claiming "Skipped (0 nodes)" for a Space that is genuinely behind, and a forced import no longer deletes release records the repo never carries.
Icon: Sparkle
Order: -20260813
---

# A Space that is behind no longer reports itself up to date

Updating a GitHub-synced Space could report "Imported Skipped (0 node(s))" while the Space was
genuinely behind the repository, and keep reporting it forever — only a forced import brought the
content across. That happened because an incremental sync, which only looks at the files a commit
changed, still recorded that the whole Space matched the repository. Every later update trusted that
record and skipped without checking. An incremental sync now records only what it actually looked
at, so a Space that has fallen behind catches up on the next ordinary update.

Forcing an import was also destructive in a way nobody asked for: it deleted the release records the
platform writes for each compiled type, because those are deliberately never stored in the
repository and the prune read their absence as a deletion. Anything your Space's sync rules exclude
is now left alone, whatever its capitalisation. Finally, if the configured subdirectory matches
nothing in the repository, the import stops and says so instead of quietly treating the Space as
empty — which would previously have removed everything in it.
