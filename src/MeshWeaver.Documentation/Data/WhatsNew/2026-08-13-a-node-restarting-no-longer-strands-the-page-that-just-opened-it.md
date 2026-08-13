---
Name: A node restarting no longer strands the page that just opened it
Category: Fix
Description: Opening or saving something at the exact moment its node restarts no longer hangs for 30 seconds and then fails.
Icon: Sparkle
Order: -20260813
---

# A node restarting no longer strands the page that just opened it

Nodes restart routinely — after a republish, an idle deactivation, or a self-heal — and that
normally passes unnoticed. But if you happened to open or save something in the split second a
node was restarting, and nothing on your screen had loaded from it yet, the connection was quietly
abandoned: the view sat empty, or the save spun for thirty seconds and then reported a timeout.
Trying again worked, which is why this mostly looked like a random hiccup.

The connection now waits for the restart to finish before asking again, so the work simply lands a
moment later instead of failing. Installs are the most visible beneficiary — one could previously
report zero items written when a node it was writing to restarted mid-run.
