---
Name: Update verdicts now say which architecture they checked
Category: Fix
Description: The candidate-image check could pass by testing the wrong architecture of an image; it now checks the one your portal actually runs, and says so.
Icon: Sparkle
Order: -20260827
---

# Update verdicts now say which architecture they checked

Before a portal adopts a new platform image, that image can be checked against the exact set of
plugins the portal actually carries, and the verdict is shown on the **Updates** settings tab.
A published image is not one thing, though — it holds a separate build for each processor
architecture, and those builds are genuinely different.

The check did not say which of them it had looked at. It quietly tested whichever architecture
the machine running it happened to use, then reported a result for the image as a whole. Run
from a laptop with a different processor than the servers, it could report a clean pass for an
image whose actual server build it had never started.

The check now always looks at the architecture your portal runs, and records that architecture
alongside the result. When it cannot start that build at all, it now says the question could
not be answered instead of reporting a pass — a plain "we do not know" rather than false
reassurance.
