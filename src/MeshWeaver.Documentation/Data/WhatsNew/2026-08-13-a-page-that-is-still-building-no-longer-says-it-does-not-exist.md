---
Name: A page that is still building no longer says it does not exist
Category: Fix
Description: While an item's code was being rebuilt, only its main view said so — open any other view of it and you were told that view did not exist. Every view now shows the same live build progress, and returns you to the view you asked for once the build finishes.
Icon: Sparkle
Order: -20260813
---

# A page that is still building no longer says it does not exist

Items in the portal get their views from code, and that code is compiled while the portal runs.
Most of the time you never notice: a build takes a moment and the page opens. But after a platform
update every item's code is rebuilt at once, and for a short while an item you open is genuinely
waiting on its own build.

The portal already had an answer for that. Open such an item and, instead of a page that hangs, you
get a live progress view: what is building, how far along the whole queue is, and a link into the
build log — and the moment the build finishes it takes you to the real page.

The problem was that this answer only covered an item's **main** view. Open one of its other views
— a specific chart, a table, a section you had bookmarked — and you got something much worse than a
wait:

> **Area not found** — No renderer is registered for area `KeyMetrics`.

That reads like a verdict. Nothing is coming, you followed a dead link, the thing you bookmarked is
gone. In fact nothing was wrong at all: the view's code was thirty seconds from being ready. The
silence the progress view was built to remove had not been removed, only moved off the front page
and onto every other one.

Now every view of an item whose code is building shows that same live progress — and when the build
lands it returns you to **the view you asked for**, not to the item's front page. A bookmark to a
particular chart survives the wait instead of being answered with "that chart does not exist".

The genuine case still answers honestly: ask for a view that really does not exist and you are
still told so, plainly. The two situations were only ever distinguishable by reading the wording,
which is why so much of the portal treated them the same — they now carry a machine-readable mark,
so anything waiting on a view knows the difference between *not here* and *not yet*.
