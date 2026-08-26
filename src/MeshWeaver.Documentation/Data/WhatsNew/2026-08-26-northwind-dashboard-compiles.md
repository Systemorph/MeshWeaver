---
Name: Northwind dashboard renders again
Category: Fix
Description: The Northwind analytics dashboard failed to compile and showed nothing; its tiles are back.
Icon: Sparkle
Order: -20260826
---

# Northwind dashboard renders again

The Northwind sample's analytics dashboard referred to four view classes by an old name, so the
page failed to compile and its tiles did not appear. The references now use the current names and
the dashboard renders as intended. The views themselves are unchanged.
