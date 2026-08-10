---
Name: AI settings are no longer written for a phantom account
Category: Fix
Description: Package installs and portal starts no longer try to register AI agent and skill sources for a non-existent account named "User", which failed on every start and filled the logs with database errors.
Icon: Sparkle
Order: -20260810
---

# AI settings are no longer written for a phantom account

When a plugin package is installed — and again on every portal start, as part of the
routine repair pass — the platform registers the package's agents and skills in each
user's AI settings, so new content shows up in everyone's pickers right away.

The list of "each user" accidentally included one entry that is not a user at all: the
built-in definition of the User node type itself. Treated as an account, it produced a
write into a storage area named after it that no portal provisions, which failed every
single time — once per installed package, on every pod, on every start. The failures were
caught and retried on the next start, so nothing a real person entered was ever touched
or lost, but the logs filled with recurring database errors and an incident was filed for
what looked like missing storage.

The phantom entry is now filtered out where the user list is built, and a regression test
pins it there: real accounts keep receiving every package's sources, and the type
definition is never mistaken for an account again.
