---
Name: A parked type stays parked
Category: Fix
Description: A type whose build was refused can no longer briefly un-park itself while the framework retries it.
Icon: Sparkle
Order: -20260825
---

# A parked type stays parked

When a type cannot be built, the portal parks it: the failure is recorded once, shown once, and
nothing retries it until you ask for a build. That containment had a gap. The framework's automatic
"the build inputs changed, take one more attempt" retry used to clear the park first and only
re-apply it after the attempt failed again — so for a moment the broken type looked healthy, and any
trigger arriving in that moment was let through.

The retry no longer clears the park. It asks to be let through once, the park stays in place the
whole time, and everything else still meets it. In practice this means a type refused for a missing
prebuilt assembly reports its refusal consistently instead of flickering, and a broken type can no
longer be woken by a stray trigger that happened to land at the wrong instant.
