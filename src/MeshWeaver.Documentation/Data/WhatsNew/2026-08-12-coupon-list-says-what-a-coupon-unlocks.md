---
Name: The coupon list says what a coupon really unlocks
Category: Fix
Description: A coupon with no package list was labelled "any plugin", which read as if it opened the whole store. It never did.
Icon: TicketDiagonal
Order: -20260812
---

# The coupon list says what a coupon really unlocks

The **Coupons** tab in Settings labelled a coupon with no package list as unlocking **"any
plugin"**. That reads as "this code opens the whole store", and it never did: a coupon without a
list can be *entered* on any package, but redeeming it unlocks only the package it was used on.
An administrator reading the list could reasonably have believed a code was far more generous
than it is — and the one code that was actually meant to open everything looked identical to a
code that opens one thing.

The column now says what happens. A coupon with no list reads **"the package it is used on"**. A
coupon that genuinely covers everything — the Store's new *grants all* flag — reads
**"everything"**, and says so even when it also names a list, because that flag is the thing that
unlocks more than the list does. A coupon that names packages still lists them, unchanged.

Both labels are translated, so a German administrator reads them in German.

The flag itself, and the editor that lets an administrator change a coupon's package list again,
ship with the Store package.
