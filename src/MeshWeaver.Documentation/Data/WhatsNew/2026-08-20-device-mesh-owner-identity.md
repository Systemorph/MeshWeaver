---
Name: Your local mesh knows it's you
Category: Fix
Description: On your own device mesh the app now acts as you, so your page shows your personal dashboard instead of the visitor view.
Icon: Sparkle
Order: -20260820
---

# Your local mesh knows it's you

The phone app connects to your local mesh without a token, and the mesh treated that connection as
an anonymous visitor — so your own page greeted you with the visitor profile ("hasn't set up their
profile yet") instead of your personal dashboard. A single-user device mesh now declares that a
token-less connection is its device user: your page renders the owner view, and everything you
create is attributed to you. Portals are unchanged — connecting to them without signing in remains
anonymous, and an invalid token still counts for nothing.
