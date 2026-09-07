---
Name: A version number now has exactly two shapes
Category: Feature
Description: Every build is X.Y.Z-ci.<n> and every release is a clean X.Y.Z — no rc, no preview, no labelled line ever again, and a guard that reds the build if one comes back.
Icon: Tag
Order: -20260907
---

# A version number now has exactly two shapes

A MeshWeaver version is now one of two things and nothing else: **`X.Y.Z-ci.<n>`** for every
continuous or temporary build, and a clean **`X.Y.Z`** for the release. No `rc`, no `preview`, no
`beta`, no labelled line. The `-ci.<n>` part is a channel marker rather than a version — it says
which publication of the line you are looking at — and the release is a *promotion* of one of those
sealed builds, retagged, never rebuilt. That is what leaves nothing for a "candidate" label to mark.

The rule is written down once, in **Release Process & Versioning**, and enforced where it is
composed: `Directory.Build.props` no longer carries the branch that produced a labelled line's
`.ci.` separator, and a new guard evaluates both the maintained number and the composed version
through real MSBuild on every pull request. It fails if either leaves the two shapes, and it carries
a control arm that feeds it the retired shape so that it can be seen to fail.

The reason is an ordering one. SemVer compares pre-release identifiers as text, so `"ci"` sorts below
`"rc"` and a `3.0.0-ci.<n>` build ranks under `3.0.0-rc8` for every `n`. On 2026-09-07 that held
every self-update candidate on both hosted portals: 42 module packages declared floors naming a
platform that no longer existed — the registry held 1268 image tags, 48 of them `3.0.0-ci.*` and not
one `rc` — while the portals logged a hold and carried on. One label in the whole scheme means there
is no label left to sort against.

One half of the hazard is deliberately still there, and the page says so: a clean `3.0.0` really does
outrank its own `3.0.0-ci.<n>` builds, which is exactly how an install on the Stable channel reaches
the release it is waiting for. Retiring `rc` was never about making pre-release ordering stop
mattering.
