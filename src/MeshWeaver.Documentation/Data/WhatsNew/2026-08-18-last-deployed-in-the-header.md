---
Name: The header says when this build was deployed
Category: Feature
Description: The top bar now reads "Last deployed" with the time in your own zone, and the About page opens for every signed-in user.
Icon: Clock
Order: -20260818
---

# The header says when this build was deployed

The build chip in the top bar used to show a version string — up to 48 characters, most of it a
commit hash. It now reads **Last deployed**, followed by the moment this build started serving,
in your own time zone. That is the question the chip is usually asked: not *which* build is this,
but *is it current*.

The exact build identity has not gone anywhere. It is on the chip's tooltip, and in full on the
About page, which now also states when the build was deployed.

The About page itself opens again for every signed-in user. Its address was repaired separately;
this completes the repair, because the page it points at was readable only by administrators —
so an ordinary user following the link arrived at "Access denied" instead of the page.
