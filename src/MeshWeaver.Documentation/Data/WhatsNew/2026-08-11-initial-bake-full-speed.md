---
Name: Platform updates prepare in about two minutes instead of twenty
Category: Feature
Description: The preparation step of a platform update now runs at full speed, since nobody is being served from the updating side while it runs.
Icon: Sparkle
Order: -20260811
---

# Platform updates prepare in about two minutes instead of twenty

When the platform updates itself, it first rebuilds every dynamic content type against the new
version. That preparation deliberately paced itself so a busy portal would not slow down — but
during an update the preparing side isn't serving anyone yet, so the pacing only made updates
take longer. Measurements showed the actual rebuild work takes under a minute; the pacing
accounted for most of the wait.

Preparation now runs at full speed during an update and keeps its gentle pacing only when a
running portal warms itself in the background. Updates get ready in a couple of minutes, and
day-to-day editing and compiling of individual types is unchanged.
