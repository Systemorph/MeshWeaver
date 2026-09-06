---
Name: A publication refuses to seal over someone else's bytes
Category: Fix
Description: When two builds published add-on content to the same shelf at the same time, the result could be a set that was half one build and half the other — sealed, self-consistent, and wrong. The publisher now checks the bytes before sealing, and refuses.
Icon: ShieldCheckmark
Order: -20260907
---

# A publication refuses to seal over someone else's bytes

Your portal does not compile the add-on content it ships. That content is baked once by CI, put on a
shared shelf, and adopted at boot — which is why a portal starts in seconds instead of minutes.

Several builds write to that shelf, and until now they could write to the *same* place at the same
time. When they did, the result was a set of bundles that was partly from one build and partly from
another, closed with a completeness marker saying the whole thing belonged together. Nothing could
tell. Every check passed: the marker was there, every file it listed existed, and each individual
read was served from something that looked complete.

The failure only showed up later, on a running portal, as views that rendered nothing — because the
add-on code had been compiled against one set of libraries and was being loaded beside a different
one.

## What changes

Every file a build publishes now carries a fingerprint of its exact contents and the name of the
build that put it there. Immediately before closing a publication, the build reads all of them back
and asks one question: *are these the bytes I uploaded?*

- **They are** → the publication is closed as before. Nothing about a normal publish changes.
- **Some are not** → the build **refuses to close it**, says which files were overwritten and by
  which other build, and removes any completeness marker that had been written over the mixture.
  The shelf goes back to the state readers already skip, and the next publish rebuilds it whole.
- **None are** → another build replaced this one entirely. That publication is left exactly as it
  is — it is whole and correct — and this build reports that it shipped nothing, rather than
  claiming success.

The same fingerprints protect the case where a build only recompiles part of the content and carries
the rest forward from what is already published: if the shelf is replaced while it is reading, the
carried files no longer agree and the build stops instead of publishing a blend of two.

## What you will notice

Almost certainly nothing, which is the point. Two builds landing on one shelf at the same moment now
produces a red build and a re-run, in place of a portal that starts cleanly and then shows empty
pages for reasons nothing recorded.

One narrow gap remains, and it is written down rather than glossed over: a build that overwrites a
file in the instant between the final check and the closing marker can still slip under it. Removing
that entirely means changing how publications are laid out on the shelf, which every reader has to
learn first.
