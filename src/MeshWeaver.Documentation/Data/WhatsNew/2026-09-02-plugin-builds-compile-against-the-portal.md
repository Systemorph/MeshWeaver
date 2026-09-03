---
Name: Plugin builds compile against the portal, not the test runner
Category: Fix
Description: A plugin built with the memex CLI was compiled against the test runner's smaller set of libraries, so content using a library that only the portal ships failed to build — and the error blamed the content. The build now uses the portal's own libraries, and refuses to start if it is not told which portal to use.
Icon: Box
Order: -20260902
---

# Plugin builds compile against the portal, not the test runner

A plugin is built inside a MeshWeaver image, and until now `memex build plugin` used a single one:
the test runner. That image carries only what running the tests needs — a little under half of what
a portal actually ships. Anything a portal has and the test runner does not simply was not there
while the plugin compiled.

For most plugins that made no difference, which is what made it hard to see. For a plugin using one
of the portal-only libraries — maps, for instance — the build stopped with *"the type or namespace
name 'Maps' does not exist"*, pointing at a page that had not been touched in weeks. The message was
accurate about what the compiler could see and completely misleading about the cause: nothing was
wrong with the page, the build was simply looking at the wrong shelf.

The build now takes the portal image as well, and compiles against the portal's libraries while the
test runner does the running. The same page that failed yesterday builds today, unchanged.

Two things follow from that, both deliberate:

- **The portal image has to be named.** `memex build plugin` now takes `--platform-image` alongside
  `--image`, and refuses to start without it rather than quietly falling back to the test runner. A
  build against the wrong set of libraries is not a smaller version of a correct build — it is a
  green result you cannot trust, on every plugin that happens not to touch the missing half.
- **The two images have to belong together.** The build checks that they come from the same release
  before it compiles anything, and says which pieces differ when they do not. Mixing them produced
  packages addressed to a framework no portal ever asks for — packages that sit intact on disk while
  every portal rebuilds everything from scratch at startup, with nothing reporting it.

The repository's own CI pipelines already worked this way. This brings the command-line tool in
line, so a plugin repository gets the same answer whichever route it builds through.
