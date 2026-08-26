---
Name: The Northwind sample's dashboard and product pages work again
Category: Fix
Description: Two Northwind page types had stopped loading; both are fixed, and a check now catches the mistake before it ships.
Icon: Sparkle
Order: -20260825
---

# The Northwind sample's dashboard and product pages work again

Two parts of the Northwind sample had stopped drawing: the analytics dashboard, and the pages for
individual products. Both failed the same way — quietly. The page simply did not appear, with
nothing on screen to say why.

The dashboard was pointing at four panels using names they no longer have, and the product pages
needed two pieces of reference data — supplier and category — that were kept somewhere the page
could not reach. Both are corrected, so the dashboard and every product page load normally again.

The reason this went unnoticed is the part worth fixing properly: pages like these are assembled
when a deployment starts up rather than when the software is built, so a broken one is invisible to
every check until someone opens it. A check now looks for exactly this mistake before a change is
accepted — including in the case that used to slip past it, where a page says where to find its
building blocks and that location turns out to be empty.
