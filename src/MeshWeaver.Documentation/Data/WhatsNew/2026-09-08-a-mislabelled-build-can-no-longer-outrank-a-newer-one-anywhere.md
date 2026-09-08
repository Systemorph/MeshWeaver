---
Name: A mislabelled build can no longer outrank a newer one, anywhere
Category: Fix
Description: A build published under the wrong version label used to look newer than every build that came after it — forever. That was fixed for the self-updater; three more places still ranked platform builds by their label, including the pass that decides which precompiled bundles to delete. All four now rank by the build that published them.
Icon: ArrowSortDownLines
Order: -20260908
---

# A mislabelled build can no longer outrank a newer one, anywhere

A platform build carries two things: a **version label** a person maintains by hand, and the
**build number** of the delivery run that published it. Only the second one is produced by the
machine, and only the second one always increases.

On 2026-09-05 the label was briefly wrong — ten builds went out labelled `3.1.0` on a line that was
still `3.0.0`, and were withdrawn. Ranked by the label those ten outrank every later build for good,
so on 2026-09-07 two portals updated themselves onto one of them, three days behind, and then
reported "nothing newer" on every check for the rest of the day. Nothing is ever newer than the
highest label. Removing the bad labels did not help either: the ranking simply moved to the next
oddly-labelled build, an older one from a retired naming scheme.

**The self-updater was taught to rank by the build number instead.** What this change adds is the
rest of the story: three more places ranked platform builds by their label, and they read the same
labels one layer down — the marker files the delivery pipeline writes beside the precompiled
content bundles, one per publication, each named after the version that publication carried.

- Which platform line a set of precompiled bundles belongs to.
- Which set of bundles an installation adopts when it has been rolled onto a newer platform than the
  one those bundles were built for.
- **Which sets the clean-up pass keeps, and which it deletes.** This is the one that mattered most:
  ranked by the label, the withdrawn build's bundles were the ones protected, and the newest ones —
  the sets the running portal actually uses — were the ones removed.

All four now share a single ranking: a build the delivery pipeline published wins on its build
number, whatever line it is labelled with; a shipped release, which has no build number of its own
because it is a re-tag of a build that does, is ranked among the other releases by version. Nothing
in this ranking can be moved by editing a label.

Nothing changes for an installation on a correctly labelled line, which is every installation today
— the ranking is identical when the labels agree with the build numbers. It changes what happens the
next time they do not.

See [Self-Update Target Selection](/Doc/Architecture/SelfUpdateTargetSelection) for the incident, the
ranking and the one question it deliberately does not answer.
