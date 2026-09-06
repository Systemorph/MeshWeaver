---
Name: A course index follows the order you gave it
Category: Fix
Description: The left-hand index used to list every plain page before every page that had sub-pages, whatever their order. It now reads top to bottom in the order the course sets, so a lesson can sit between two ordinary pages.
Icon: DocumentBulletList
Order: -20260906
---

# A course index follows the order you gave it

The left-hand index on a course page is built from an order the course itself sets. Until now that
order only held between pages of the *same shape*: every plain page was listed first, and every page
that had sub-pages of its own — a lesson with its exercise, solution and quiz — was pushed below
them, however it was numbered.

For a course that reads *introduction, lesson 1, an exercise round, lesson 2, wrap-up*, that came out
as *introduction, exercise round, wrap-up, lesson 1, lesson 2*. The numbering was already correct;
there was simply no number that could put a lesson above a plain page, because having sub-pages
counted for more than the order did. One live course had four lessons numbered 1 to 4 rendering
tenth to thirteenth.

**The index now reads straight down in the order the course gives, with no regard for whether an
entry has sub-pages.** A lesson can sit between two ordinary pages, exactly where the author put it.

Nothing else about the index changes. A lesson is still a collapsible group holding its own pages —
the fix reorders the index, it does not flatten it — the group you are reading is still the one that
opens, and the page you are on still carries the position marker in place. An index whose entries all
happened to be one shape looked right before and is untouched.

This reaches every module that supplies its own index, not only courses.
