---
Name: A release now says which packages it can carry
Category: Feature
Description: Every release records the framework identity it resolved, and one shared check answers whether a given set of packages is actually available for it — readable by anything about to roll or build against that release.
Icon: ShieldCheckmark
Order: -20260817
---

# A release now says which packages it can carry

Until now, "this release is newer" was the only thing anyone checked before acting on it. That is
the wrong precondition: a release whose content has not been baked yet is one an environment
recompiles its way through at boot, and a package whose module floor the release does not meet
cannot load on it at all. Both were known before rolling — nothing asked.

Two things landed:

- **Every release records its own framework identity.** The delivery pipeline now writes a marker
  naming the identity each published release resolved. An identity is a property of the binaries an
  image ships, so nothing outside that image could previously work out which one a release would
  resolve — now it is a lookup, and a release with no marker is unambiguously one that published no
  content bake.
- **One shared check answers "is every package available for that release?"** — a sealed content
  bake under the release's identity for content, the declared platform floor for compiled modules.
  It is asked and answered the same way wherever it is consulted, and it is readable from outside
  the portal at `/api/plugins/is-updatable`.

The check refuses safely and says which of the three things happened: a package that genuinely
cannot survive the release, a catalogue that could not be read at all, or a deployment the check
does not apply to. Those are different problems with different fixes, and a verdict that blurred
them would send you to re-bake something that was never broken.

Both halves are deliberately regression checks rather than absolute ones — they ask whether a move
would take away something that works today, never whether everything is ideal. A gate that can
freeze an environment indefinitely is a worse outage than the one it prevents.
