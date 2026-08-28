---
Name: Open pages recover when their hub restarts
Category: Fix
Description: A page you already have open now reloads itself when the thing serving it restarts, instead of sitting on the "compiling" placeholder until you refresh.
Icon: Sparkle
Order: -20260828
---

# Open pages recover when their hub restarts

When a page's code is still being compiled, the portal shows a short "compiling" placeholder and
then restarts the page's server side so it picks up the finished build. Anyone who arrived after
that restart got the real page. Anyone who was already looking at the placeholder did not: their
page was never told the restart had happened, so it kept showing the placeholder — with no error and
no spinner — until they reloaded the browser.

That was easiest to hit right after an upgrade, which recompiles every page's code at once and so
opens the window on every open page in the portal at the same time.

A restart now tells the pages it was serving, and they re-connect on their own as soon as it
finishes. The page you were already looking at fills in by itself.
