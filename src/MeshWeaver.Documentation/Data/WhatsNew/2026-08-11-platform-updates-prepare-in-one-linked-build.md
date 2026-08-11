---
Name: Platform updates prepare all content types in one linked build
Category: Feature
Description: The preparation step of a platform update now rebuilds every dynamic content type directly in one batch, so updates get ready in about the time the compiles themselves take.
Icon: Sparkle
Order: -20260811
---

# Platform updates prepare all content types in one linked build

When the platform updates itself, it rebuilds every dynamic content type against the new
version before taking traffic. That preparation used to drive each rebuild through the type's
own live machinery — waking it up, asking it to rebuild itself, and waiting for it to report
back. Measurements showed the actual rebuild work summed to well under a minute, while the
wake-up round-trips accounted for almost all of the wait — and when another server was
unhealthy, a single round-trip could stall for minutes, stretching preparation to hours.

Preparation now works like one linked build: the update gathers every type's source files in a
single pass, rebuilds each type directly in the right order, and records the results — the same
per-type outputs as before, without any of the round-trips. An update's preparation time now
tracks the compile work itself, and an unhealthy neighbouring server can no longer slow it
down. Day-to-day editing and compiling of individual types is unchanged, and operators can
switch back to the previous behaviour with a configuration setting.
