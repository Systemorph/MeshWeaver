---
Name: Catalog actions follow the selected package
Category: Fix
Description: Catalog refreshes preserve the package selected by an Install or Update click.
Icon: Apps
Order: -20260911
---

# Catalog actions follow the selected package

An Install or Update click now keeps the identity of the selected package when the catalog
refreshes. Adding or renaming another package, or updating a card's description, cannot redirect
the click to a neighboring card.

If that action is no longer available, the old click has no effect. Orphaned installation cards
also retain their package identity when their list changes.
