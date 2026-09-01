---
Name: A page opened a moment too early now finds its views
Category: Fix
Description: Opening a page in the instant before its type had even started building used to leave it stuck with only the generic views for as long as the portal ran — it now shows the build in progress and switches to the real views by itself.
Icon: ArrowSyncCheckmark
Order: -20260902
---

# A page opened a moment too early now finds its views

Types you define in the mesh bring their own views with them. A type that describes a data explorer
also declares the *Explorer* page you look at it through, and both come out of the same act of
building the type — you either have both or you have neither.

There is a brief moment, just after a type is loaded and before its build has been asked for, when it
has no build to report. A page opened in that moment was treated as though it belonged to a type that
was never going to be built at all — a reasonable thing to assume about some types, and quite wrong
about this one. It got the generic pages every node has (overview, settings, search) and none of its
own. Asking for one of its own then produced a flat **"area not found"**, which is the answer for a
view that does not exist, rather than "still building", which is the answer for a view that is on its
way.

The unlucky part was that it never wore off. That decision is made once, when the page's workspace
starts up, and nothing re-opened the question: not the build finishing, not the type becoming
available. Reloading the page fixed it — which is precisely why it read as a random glitch instead of
a race with a permanent loser.

**A type that has code to build is no longer mistaken for one that hasn't.** A page opened in that
window now shows the same build-in-progress view you get when a build is genuinely running — every
view answers, so nothing reports "area not found" — and switches itself onto the real pages the
moment the build lands. Same tab, no reload.

This is the other half of a fix shipped alongside it, and they had to be separate. That one made
*content* readable again once a type became known; this one is about the *views*, and re-reading the
content could never have brought a missing view back, because the two arrive together or not at all.

Two things deliberately did not change:

- **A type with nothing to build still gets the generic pages, immediately.** Types that only name a
  shape, and types on installations with no builder at all, are unaffected — showing them a
  build-in-progress page would be a page about something that is never going to happen.
- **Nothing polls and nothing is retried.** The page waits for the build to announce itself, exactly
  as a page opened one moment later already did.
