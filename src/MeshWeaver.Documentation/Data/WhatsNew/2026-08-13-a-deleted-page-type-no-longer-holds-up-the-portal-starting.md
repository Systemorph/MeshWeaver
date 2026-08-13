---
Name: A deleted page type no longer holds up the portal starting
Category: Fix
Description: Deleting the code behind a page type left something behind that the portal treated as newly broken, so it refused to finish starting and blamed code that no longer existed. A type with no code left is now recognised as deleted content, not as a fault in the new version.
Icon: Delete
Order: -20260813
---

# A deleted page type no longer holds up the portal starting

When a new version of the portal starts, it rebuilds every page type before reporting itself
ready. That check is deliberate: if a new version breaks a page that used to work, the portal
should refuse to start rather than serve something broken.

The check could not tell two very different situations apart. One is "this page's code is
still here and the new version broke it" — a genuine problem, and exactly what the check is
for. The other is "this page's code was deleted, so there is nothing left to build" — not a
problem with the new version at all, just content someone removed.

Deleted content was being reported as the first kind. The portal would try to build a page
type whose code was gone, get errors about names that no longer existed, treat that as the new
version breaking something, and hold back readiness. The error message pointed at code that
had already been deleted, which made it look like a puzzle rather than a leftover.

It was worse than it sounds, because of which page types were affected. A page type can either
name where its code lives or just use the standard location — and almost every page type uses
the standard location. Only the first kind was ever recognised as "the code is gone", so for
practically every page type in a portal, deleting its code meant holding up every restart from
then on.

A page type with no code left is now recognised as deleted content, whichever way it finds its
code, and no longer holds back startup. A page type whose code is still there and genuinely
fails to build still does — that check is unchanged, and is the point of having it.
