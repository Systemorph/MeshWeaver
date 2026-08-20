---
Name: Installed plugins show up in the catalog again
Category: Fix
Description: The list of installed packages is readable again, so the catalog stops looking empty and instances that pull modules from a registry receive them.
Icon: Sparkle
Order: -20260821
---

# Installed plugins show up in the catalog again

Every installed package is recorded in one place, and every surface that asks "what is installed here?" reads that record — the plugin catalog, the admin pages, and the feed a connected instance pulls its modules from.

That record store described who may read it only in memory. The database never learned the rule, so it filtered the whole store out of every search and listing: the catalog looked empty, and an instance asking a registry which packages it could fetch was told "none" — no error, no warning, on either side. Looking a record up by its exact address still worked, which is why it read as missing data rather than a permissions problem.

The rule is now written down where the database can see it, on installation and again at startup, so an instance that was already affected repairs itself on its next restart. Nothing about who may read what has changed — the same rule now simply applies everywhere. A registry that ends up serving an empty list also says so in its log instead of going quiet.
