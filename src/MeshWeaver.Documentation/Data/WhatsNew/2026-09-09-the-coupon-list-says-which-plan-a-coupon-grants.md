---
Name: The coupon list says which plan a coupon grants
Category: Fix
Description: The admin coupon table now shows the plan and term each coupon confers, and says plainly when a coupon grants nothing.
Icon: Ticket
Order: -20260909
---

A coupon grants a subscription plan. The admin coupon list did not have a column for it: it showed the package fields, which only say **where** a code may be redeemed. Once the live coupons were migrated to plans they carry no package list at all, so the staff coupon — which confers all-access — rendered as *"the package it is used on"*. That is not a missing answer but a wrong one, and it is the answer an operator would have acted on.

The table now leads with **Grants**: the plan and its term, as `personal · 90 days` or `dedicated · permanent`. A coupon whose term was left blank shows the ninety days that will actually apply, rather than an empty cell. A coupon that names no plan says *"nothing — no plan set"* — it grants nothing and is refused at redemption, and an empty cell there would read as "no plan needed".

The old column keeps its honest meaning under the title **Redeemable on**. Plan identifiers stay in their wire spelling; the wording around them follows the reader's language.
