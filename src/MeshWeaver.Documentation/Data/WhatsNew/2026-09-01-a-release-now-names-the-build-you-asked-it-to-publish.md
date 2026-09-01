---
Name: A release now names the build you asked it to publish
Category: Fix
Description: Asking a type for a release could be recorded as done while the newest release still pointed at an older build — so instances went on serving the previous assembly under a green status. The request is no longer counted as answered until a release for that exact build exists.
Icon: TagMultiple
Order: -20260901
---

# A release now names the build you asked it to publish

Every dynamic type on the mesh carries two facts side by side: **the build it last compiled**, and
**the release that build was published as**. Almost everything a reader does with a type follows the
second one — a portal deciding which assembly to bind, the "→ release" link on the Configuration
pane, an installation adopting a published build.

The two could drift apart, and nothing said so.

A release request was recorded as *handled* the moment it was picked up, not when a release actually
existed. If the release could not then be cut — the write did not land inside its budget, or was
refused — the compile still succeeded and the type still settled **Ok**, over current sources, with a
current assembly. The release beside it went on naming the **previous** build. Every individual field
read healthy; only holding the release up against the build revealed it.

That state was stable, and it was silent. It was found the way this kind of defect is always found:
somebody noticed that a merged fix was not live. A slide deck kept showing pre-fix behaviour for a
day while its type reported a clean compile of the fixed source.

Worse, it could not be repaired by asking again. A second, ordinary release request was answered
"you already have this build" — true about the bytes, and the reason the request was consumed a
second time without producing anything. Only forcing a rebuild got out of it, and only if you knew
to look.

**Three things changed.**

- **A request is not answered until a release for that build exists.** The check is exact rather than
  chronological: a release version is minted from the build's own storage identity, so the platform
  can ask "does this release name these bytes?" from the type's record alone, with no lookup.
- **When the bytes are already right, the missing release is cut from them** — no rebuild. A type
  whose build arrived prepackaged, or was produced by another replica, still gets a proper release
  without paying for a compile it does not need. Absence of a release is treated as absence, never as
  staleness, so installations that legitimately have no release yet behave exactly as before.
- **A release that cannot be cut now says so, loudly.** Giving up used to be indistinguishable from
  never having been asked. It is now an error in the log naming the type and the release that was
  attempted, so the next occurrence is one search away instead of a comparison between two
  timestamps.

A forced rebuild is unchanged, and so is everything about how compiles are scheduled. What changed is
that "published" is now a statement the platform will not make until it is true.
