---
Name: Skills look like themselves again
Category: Fix
Description: Every skill in the Store showed the same sparkle, whatever icon it had been given — and a few showed a broken image. Skills now carry the icon they declare.
Icon: Sparkle
Order: -20260809
---

# Skills look like themselves again

Open the Store, look at the skills, and they were a wall of identical sparkles.
Not because nobody chose icons for them — most of them *had* an icon chosen, a
padlock, a map pin, a clock, a puzzle piece — but none of it showed. A handful
were worse than identical: they showed a broken image.

Two things were going on.

Icons can be written in a few different ways: a picture file, a drawing embedded
in the page, an emoji, or the *name* of an icon from the standard set. That last
form is what most skills used, because it is what the sidebar and the chat
composer understand. But the part of the platform that produces an icon for a
card needs a picture to point at, and it had no way to turn a name into one — so
it quietly gave up and fell back to the generic icon for "this is a skill". Every
skill, the same sparkle. Names are now matched against the icons the platform
already ships, so a skill that asked for a clock gets a clock.

Some of those names had no matching picture at all, and a few skills pointed
directly at pictures that had never actually been drawn — which is where the
broken images came from. Those are drawn now, in the same style as the rest of
the set: a map pin, a padlock, a book, a library, a target, a clock, a phone, a
layout, a puzzle piece, a cloud upload, a bug, and a plus.

A skill that asks for an icon nobody has drawn still falls back to the sparkle,
so a card can never come up empty — it just no longer happens to almost all of
them.
