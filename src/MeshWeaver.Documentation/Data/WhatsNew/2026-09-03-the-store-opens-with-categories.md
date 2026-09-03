---
Name: The Store opens with its categories and loads plugins per category
Category: Feature
Description: The plugin catalog no longer loads every package's install record before it can show anything — it opens on the list of categories and reads only the category you open.
Icon: Sparkle
Order: -20260903
---

# The Store opens with its categories and loads plugins per category

The plugin catalog used to open on one long list: every package the source offers, each joined
against its install record before a single card could appear. On an installation with many packages
that meant waiting for reads nobody had asked for yet.

It now opens on its **categories** — one tile per category with the number of packages in it, plus
an *All packages* entry — and that page is built from the source's package listing alone. Opening a
category shows just that category's cards and reads only those packages' install records; the
Install, Update and Installed states, and the restart note for a package whose module is still
activating, are unchanged. The full flat list is still there behind *All packages*, which is also
where install records the source no longer offers can be removed.

Packages that declare no category are grouped under *Uncategorized*. Every label on the page is
translated, so the catalog reads the same in German as in English.
