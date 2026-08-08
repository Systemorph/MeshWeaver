---
Name: Button styles now apply
Category: What's New
Description: A style set on a button — or on an icon, badge, checkbox or menu — now reaches the page instead of being silently discarded.
Icon: Sparkle
---

# Button styles now apply

Styling a button used to do nothing. `Hide this button`, `pin it to the corner`,
`push it to the right` — the style was accepted, stored and then dropped on its way
to the page, so the button sat there unhidden and unmoved. The only way around it
was to wrap the button in a styled container.

Buttons now carry their style and their CSS class through to the page, and so do
the other controls that quietly had the same hole: icons, badges, checkboxes,
switches, dropdowns, list boxes, radio groups, search boxes, grid items, splitter
panes and the navigation menu.

The wrapper-container workaround is no longer needed — style the control itself.
