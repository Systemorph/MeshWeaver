---
Name: Side menus follow the order you set
Category: What's New
Description: A page's left-hand menu of sub-pages now follows the order you set on each node, and re-installing a course whose only change was the ordering now takes effect.
Icon: ArrowSort
---

# Side menus follow the order you set

The collapsible left-hand menu on a page that has sub-pages now lists them in the order you set on each node — pages with no order sort last, then alphabetically — the same sequence the space navigator already used. Previously that menu sorted by name only, so setting an order changed nothing there.

A second fix had to land with it, because reordering also has to reach the page. Re-installing a course or plugin whose only change was the sequence of its pages now writes that change; before, an order-only change was judged "unchanged" and silently skipped, so an author saw no effect from reordering and no error either.
