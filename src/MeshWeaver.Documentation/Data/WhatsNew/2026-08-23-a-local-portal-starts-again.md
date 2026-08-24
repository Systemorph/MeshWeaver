---
Name: A local portal starts again after the view packs became modules
Category: Fix
Description: Five features moved out of the portal image and became separately installed modules, but the image still insisted on them — so a local install refused to start. It starts again, and the setting that says which modules are mandatory now reaches the portal at all.
Icon: Sparkle
Order: -20260823
---

# A local portal starts again after the view packs became modules

Charts, analysis views, entity views, maps and speech recently stopped being built into the portal
and became **modules** — separately published pieces a deployment installs. That is the intended
direction, and on a hosted deployment nothing changed: the modules are fetched and installed, and
the features are there.

A portal running on someone's own machine has nowhere to fetch them from. Meanwhile the image still
carried a list saying those five pieces were **mandatory**, and the portal checks that list before
it declares itself healthy. The check was right — the pieces genuinely were missing — so the portal
correctly refused to report healthy, and kept refusing. Every attempt to start ended the same way:
several minutes of waiting, then a timeout, with the old portal still serving and the new one never
taking over.

The list is meant to be adjustable per deployment, precisely so an install that cannot provide a
piece can say so. That adjustment never arrived: the setting was written in the deployment's
configuration, and the packaging that hands configuration to the running portal had no line for it,
so it was dropped in transit — silently, with every step reporting success.

Now the setting reaches the portal, and a local install declares those five optional. It starts
normally again; the affected features are simply absent there until a local install can fetch them.

A related setting was worse than silent: the one that limits how often a portal may restart itself
arrived as an empty value rather than a duration, which is not something a duration can be, so the
portal stopped with an error instead of starting. It now carries a real default.

Both belong to the same small family of settings that were written down but never delivered, and a
check now fails the build when a new one joins them.
