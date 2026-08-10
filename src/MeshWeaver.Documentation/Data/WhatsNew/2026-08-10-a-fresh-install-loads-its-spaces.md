---
Name: A fresh installation loads its spaces instead of waiting forever
Category: Fix
Description: On a newly created installation some spaces never finished loading and their pages sat on "Subscribing…" indefinitely.
Icon: Sparkle
Order: -20260810
---

# A fresh installation loads its spaces instead of waiting forever

On an installation created from scratch, several spaces never finished starting up. Their pages sat
on "Subscribing…" and never showed anything, and the content that ships with those spaces was never
imported.

The cause was a compatibility step that upgrades user areas created by much older versions. On a new
installation there is nothing old to upgrade, but the check for it was treated as a failure rather
than as "nothing to do" — and because it ran for every space, it stopped all of them from loading.
A new installation now recognises that there is nothing to upgrade and starts normally.
