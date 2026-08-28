---
Name: User lookups no longer trip over the User type definition
Category: Fix
Description: A leftover database row that made the "User" type definition answer user searches is now repaired automatically at startup, so identity lookups stop failing in bulk.
Icon: Sparkle
Order: -20260828
---

# User lookups no longer trip over the User type definition

On long-lived installations, the built-in "User" type definition had once been saved to the
database claiming to be a user itself. Every user lookup then received that definition row
alongside real accounts, which degraded identity resolution (display name, time zone, language)
and produced a steady stream of background errors. The platform now detects and corrects such
rows automatically at startup, so the user directory returns only real accounts.
