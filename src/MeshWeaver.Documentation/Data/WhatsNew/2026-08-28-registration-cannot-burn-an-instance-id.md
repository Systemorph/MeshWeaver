---
Name: Registering an instance can no longer burn its id
Category: Fix
Description: A first-startup registration that could not store its issued key used to take the instance id permanently and lose the key, with no way back.
Icon: Sparkle
Order: -20260828
---

# Registering an instance can no longer burn its id

When an installation registered itself with a registry for the first time, it asked for its
instance key before checking that it could actually store one. If storing then failed, the result
was the worst of both worlds: the registry had taken the instance id for good, and the key — issued
exactly once — was gone. Retrying could not help, and the message blamed the bootstrap key, which
had worked perfectly.

The check now happens first. If the installation cannot store a key, it declines to register at all
and says so, leaving the id and the bootstrap key unspent so a corrected setup still works. And if
storing does fail after a successful registration, the message now says exactly that, instead of
sending you to re-check a credential that was never the problem.
