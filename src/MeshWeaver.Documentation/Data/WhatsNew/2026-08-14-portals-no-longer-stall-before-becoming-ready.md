---
Name: Portals no longer stall before becoming ready
Category: Fix
Description: A database write the build coordinator makes on every pass could fail outright, leaving a portal stuck preparing its content types and never finishing startup.
Icon: Sparkle
Order: -20260814
---

# Portals no longer stall before becoming ready

Before a portal starts serving, one of its instances has to volunteer to prepare the content types
everything else depends on. The instances agree on who does the work by writing a small coordination
record to the database.

That write could fail outright — not because of anything about the data, but because of how the
write was described to PostgreSQL. Once the coordination record existed, every later attempt to
update it was rejected, so nobody was ever chosen to do the work. The portal reported "preparing
content types" and stayed there indefinitely: pages never loaded and the instance never became
healthy.

The write is now described precisely enough that PostgreSQL accepts it in every case, so the
coordination proceeds, the content types get prepared, and the portal comes up. The problem only
affected records the platform writes for itself, which is why it surfaced as a startup stall rather
than as an error anyone could see while using the portal.
