---
Name: Store-installed modules load again, and a missing one says why
Category: Fix
Description: Modules installed from the Store no longer disappear when several are published at once or when a pod restarts, and a module that is missing now says which of "waiting for a restart", "re-install it" or "never installed" it is — instead of stalling a release or silently 404ing.
Icon: PuzzlePiece
Order: -20260825
---

# Store-installed modules load again, and a missing one says why

Modules installed from the Store — charts, maps, analysis views, voice, the MCP tool surface —
could go missing in three different ways, each of which looked like nothing had gone wrong.

**Publishing several modules at once could lose some of them.** Every portal replica shares one
volume, and the list of installed modules was a single file that each install rewrote from end to
end. Two installs landing at the same moment overwrote each other's entries, and a restart could
erase a module a sibling replica had just installed. Installing a module now writes only that
module's own record, so two installs can never collide and nothing can be lost.

**A brief hiccup reading that file demoted a whole portal.** If the file could not be read for even
a moment — which is exactly what happens while another replica is replacing it — the portal started
with *none* of its Store modules and stayed that way until someone restarted it. One unreadable
record now costs only its own module, reported by name, and everything else loads normally.

**A module that could not load said nothing at all.** A module whose files had gone missing still
showed as installed, so anything it provided — including whole web endpoints — simply answered "not
found" for as long as the portal ran. That state is now reported at startup and on the health page,
and it is clearly separated from the ordinary "installed, restarts on the next update" case: one
says a restart activates it, the other says to re-install it.

## Releases no longer stall waiting for a module the release itself delivers

A portal can declare modules it must not run without. That check used to treat every absence the
same way and hold the release — including for modules that only ever arrive from the Store, which
no held release could deliver. Two production updates stalled that way with no way out but editing
live configuration.

The check now tells the two apart. A feature the build itself was supposed to include and lost
still stops the release, exactly as before — the previous version keeps serving, which is the point.
A Store-delivered module that has not arrived yet is reported clearly, named, with what it is
waiting for, and the release proceeds. Nothing is hidden: the health page lists both kinds
separately, so an operator can always see the difference.
