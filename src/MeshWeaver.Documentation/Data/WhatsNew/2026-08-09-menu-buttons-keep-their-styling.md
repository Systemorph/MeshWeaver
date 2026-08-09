---
Name: Dropdown buttons keep the styling they were given
Category: Fix
Description: A button with a dropdown now honours the appearance its page asked for, instead of quietly falling back to the default look.
Icon: Sparkle
---

# Dropdown buttons keep the styling they were given

A button that carries a dropdown menu ignored the styling its page had asked for and always
rendered in the default look. On pages that style their buttons deliberately — a product page whose
call to action uses the page's own colours, for instance — this showed up as one button suddenly
looking unlike the others next to it, and changing appearance depending on what state the page was
in.

Those buttons now look the way the page intended, in every one of their forms: with a dropdown
alone, with a main action plus a dropdown, and as a plain entry inside an open menu.
