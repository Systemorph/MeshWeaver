---
Name: In-mesh C# is held to a warning standard
Category: Feature
Description: >-
  The code you write inside the mesh was the only code in the platform that no warnings-as-errors
  build ever checked — the compiler produced the warnings and the build threw them away. It now
  reports them, and two shrink-only baselines keep the debt going down. Three whole classes of
  warning turned out to be the platform's own and are simply gone.
Icon: ExclamationTriangle
Order: -20260916
---

# In-mesh C# is held to a warning standard

Code stored in a mesh node — a NodeType's `Source/*.cs`, a layout area, a test suite — is compiled
by the portal at runtime. That makes it the only C# in the platform that no ordinary
warnings-as-errors build has ever looked at.

It turned out nothing else was looking either. The compiler *did* produce the warnings; the build
step that compiles every NodeType read the list and discarded it. So a build could report every
NodeType green over source carrying unused variables, documentation links pointing at types that no
longer exist, and hundreds of missing XML doc comments.

## What changed

The build now reports those warnings, and holds them to a baseline that can only shrink. A warning
of a kind that is not already recorded fails the build; a recorded one that has been fixed also
fails it, until the line recording it is removed. The debt cannot grow back, and it cannot be
quietly carried after it has been paid.

There are **two separate baselines**, on purpose:

- **Real warnings** — unused code, a `cref` that resolves to nothing, malformed XML in a comment.
  These are latent bugs.
- **Missing doc comments** — their own baseline, shrinking on its own schedule, so an undocumented
  member is never the reason an unrelated change cannot go in.

## Three classes of warning were ours, and are gone

Reading the first real inventory showed that most of the noise was not authored at all — the
platform was emitting warnings into content it does not own, where no author could ever have fixed
them:

- writing `using MeshWeaver.Layout;` at the top of your source earned *"the using directive already
  appeared"*, because the build concatenated every file's imports without de-duplicating them;
- every NodeType in existence carried two "missing XML comment" warnings for a class the build
  itself generates;
- writing `string?` earned a complaint that nullable annotations need a `#nullable` context the
  generated file never had.

All three are fixed at the source. On the sample content that is 850 raw warnings down to 375, and
five distinct warning codes down to two.

## Your NodeTypes still compile

**A missing doc comment can never stop a NodeType compiling.** The runtime compile stays deliberately
lenient — warnings are reported, never promoted to errors — because a NodeType that cannot compile
is one the portal refuses to serve. The standard is applied where it belongs: in the build, before
anything ships.

## Quieter builds

The warning report is folded rather than transcribed. One diagnostic in a source file shared by
eight NodeTypes is one line naming eight, not eight lines. A clean build now says what it measured
in four lines instead of several hundred, and `MW_LOG_LEVEL=Information` still lists every site.
