---
Name: Content saved into a synced space can be read back again
Category: Fix
Description: In a space whose built-in definitions are kept in the database, saving a page reported success while every later read returned the built-in placeholder instead. The saved content was never lost — it was unreachable. Reads now answer with what was saved.
Icon: Sparkle
Order: -20260902
---

# Content saved into a synced space can be read back again

Some spaces carry built-in definitions that ship with the platform and are then kept in the
database, so that what you edit is a real, saved record rather than something baked into the
product. In those spaces a save could complete successfully — the confirmation appeared, the
change was recorded in the page's version history, everything reported done — and yet every
later read of that page returned the built-in placeholder instead of what had just been saved.

Reopen the page and your edit was gone. Search did not find it. Nothing reported an error,
because as far as the saving side was concerned nothing had gone wrong: the content really was
written, and it really was still there. It simply could not be reached, because the read went to
the built-in placeholder first and stopped there.

**This is the worst shape a bug can take**, and it is worth saying why. Everything that happens
after a save trusts the confirmation. An import that copies a folder of pages marks each one as
done and never retries it. A tool that hands out a key after saving it hands out a key nobody can
look up afterwards. A person edits, sees "saved", and moves on. A save that confirms without
being readable turns one silent failure into a long tail of them.

The rule is now enforced where it belongs: a built-in definition that has been handed over to the
database is no longer treated as the page at that address by any part of the system. Reads,
existence checks, listings and navigation all agree on the same answer — the saved record.

Nothing you can see today changes: pages whose built-in version is genuinely the one being shown
keep behaving exactly as before. And there is now a standing check that fails the build if a save
is ever again confirmed for content that cannot be read back.
