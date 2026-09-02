---
Name: A new build no longer rebuilds itself before serving you
Category: Fix
Description: Every release ships with its pages, views and reports already built, so a freshly updated portal can serve them immediately. For about a day, that pre-built work was filed under a label the new build did not recognise — so it was ignored, and each portal rebuilt everything from scratch on its first start. The label matches again.
Icon: Timer
Order: -20260902
---

# A new build no longer rebuilds itself before serving you

Much of what you see in the portal — documentation pages, reports, the views behind your spaces — is
written as source and turned into a runnable form before it can be shown. That work is done **once**,
when the release is built, and shipped alongside it. A portal that takes a new build is supposed to
find it already done and simply start serving.

For roughly a day it did not find it, and the reason is worth telling because nothing about it
looked wrong.

The pre-built work is filed under a label that says *which exact build it was made for* — a
deliberate safety rule, not bureaucracy. Content built against one version of the platform must
never be handed to another; the label is what makes that impossible. A portal asks for its own
label and takes only what matches.

Two programs are involved in producing a release: the one that *builds* the pre-made content, and
the one that *runs* it. They are built from the same code, at the same moment, from the same commit
— so they should compute the same label. A small change made a few days earlier, correcting an
unrelated problem where the portal reported the wrong version number on its About page, quietly
caused one of the two to stamp the build number into a place that helps decide that label. The
labels drifted apart. Everything else stayed green: the release was built, the images were
published, the content was baked.

The effect was invisible until a portal started. It asked for its own label, found nothing filed
under it, and did the only correct thing left: rebuilt everything itself. That is minutes of extra
work on the first start after an update, during which pages are slow to appear and some views wait
before they render — and it happened on every pod, on every update, rather than once.

Nothing was lost and nothing was wrong with the content — it was simply filed under a name nobody
asked for.

**The two now agree again**, and the rule that keeps them agreeing is written down rather than
assumed: the build number belongs on the image and in the version you see reported, and it is kept
out of everything the label is computed from. A check that runs on every change proves the two stay
identical, so this cannot drift back quietly.

If you updated recently and the first page after the update felt unusually slow, that was this. It
will not repeat on the next one.
