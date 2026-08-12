---
Name: Panels and editors now accept the styling you give them
Category: Fix
Description: Ten more parts of the page — the export and import panels, the appearance settings, catalogs, editors, the comment wrapper and the diff viewer — ignored the width, spacing and styling their author set, each one silently.
Icon: Sparkle
Order: -20260813
---

# Panels and editors now accept the styling you give them

Every element placed on a page can be given styling by whoever placed it — a width, a margin, a
colour, or a name that a stylesheet elsewhere can target. Ten parts of the page took that
instruction and dropped it on the floor: the export and import panels, the document export panel,
the appearance settings, catalogs, the markdown editor, the collaborative editor, the content
editor, the comment wrapper and the side-by-side diff viewer.

Each of those has a fixed look built into it — a maximum width, a default height — and that fixed
look was the only thing it would honour. Setting a different width on one did nothing at all: no
warning, no error, no visible sign that the instruction had been discarded. The panels that cap
themselves at a fixed width were the most noticeable, because widening them for a large screen was
exactly the instruction being thrown away.

All ten now carry the styling through, and it is applied **after** their built-in defaults, so a
width or height set by the author replaces the default rather than losing to it. Anything whose
author set no styling looks exactly as it did before.
