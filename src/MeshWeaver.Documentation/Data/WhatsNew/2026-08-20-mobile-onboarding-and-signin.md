---
Name: The phone app onboards you properly and signs in to portals
Category: Fix
Description: First launch shows the profile onboarding dialog, remote portals always open their sign-in instead of connecting anonymously, and the chat composer follows dark mode.
Icon: Sparkle
Order: -20260820
---

# The phone app onboards you properly and signs in to portals

Three fixes from first-day use of the phone app. On first launch against your local mesh the app now
opens into an onboarding dialog — enter your name and a line about yourself, and it creates your
device user with that profile, instead of silently creating an empty one that greeted you with
"set up your profile". Picking a remote portal now always opens that portal's own sign-in when you
have no valid session — the app never connects anonymously any more, which used to render as a
blank portal. And the chat composer now follows dark mode instead of staying light.
