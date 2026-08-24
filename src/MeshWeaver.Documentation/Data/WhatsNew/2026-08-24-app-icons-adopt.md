---
Name: App tiles wear their own icon
Category: Fix
Description: An app tile still showing the generic placeholder now picks up the icon of the app it opens.
Icon: Sparkle
Order: -20260824
---

# App tiles wear their own icon

Apps added before their icon was recorded showed a generic placeholder, and a grid of identical
placeholders defeats the point of an icon grid — you should recognise an app before you read its
label.

Each app entry now takes the icon of the app it opens, the first time that entry is used. An app
that already has its own icon keeps it, so nothing you or the Store set is overwritten.
