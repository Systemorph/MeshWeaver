---
Name: The About page names the build the portal is actually running
Category: Fix
Description: Settings → About reported Version 1.0.0 and "no build commit recorded" on every deployed portal, and the header build chip was stuck offering a refresh because the install could never see itself as up to date.
Icon: Info
Order: -20260901
---

# The About page names the build the portal is actually running

Settings → **About** exists to answer one question: which build is this, and is it current. Since
25 August it had been answering the first half wrongly on every deployed portal — `Version: 1.0.0`,
and `Build commit: not recorded for this build` — while `/api/version` on the very same host
answered correctly. Two surfaces, one process, two different builds named.

The consequences ran further than the page. The portal compares that same number against the
release registry to decide whether it is current, so a version pinned at `1.0.0` made **every**
published build look newer, permanently. An install could never reach "up to date"; it re-armed a
roll on every check interval; and the build chip in the header — the small icon beside the alerts
bell — stayed in its update-available state, where clicking it reloads the page instead of opening
About. The one route from the header to the build identity was closed by the very defect you would
have gone there to read about.

The cause was a build stamp, not the page. A portal learns its own identity from two places: the
commit and base version are compiled into the assemblies, and the full run-numbered version rides
the container image as an environment variable. Both were being read off the *entry assembly* —
the executable — which stopped being part of the platform build when the portal hosts moved to
their own repository, and which therefore carried the toolchain's default `1.0.0` and no commit at
all. `/api/version` was unaffected because it already refused an unstamped host and fell back to an
assembly the build really did stamp.

That refusal is now the platform's, not one endpoint's: both surfaces resolve the same build
assembly through one place, so they cannot name different builds again. The delivery pipeline also
bakes the run-numbered version into the image it publishes rather than only tagging it with one,
and — because a tag says nothing about the bytes underneath it — the pipeline now reads the version
back out of the published image, on every architecture, before that image is given a tag anyone can
roll to. An image that misreports itself no longer reaches a portal.
