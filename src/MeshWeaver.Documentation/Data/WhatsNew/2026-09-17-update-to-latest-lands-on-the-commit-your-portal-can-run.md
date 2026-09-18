---
Name: Update to latest lands on the commit your portal can run
Category: Fix
Description: For a repository whose compiled modules a portal runs, Update to latest and Re-import now import the commit the portal's modules were built from — or nothing, with the reason — instead of source files the running modules were never built from. A green build lands there too, instead of waiting.
Icon: LockClosed
Order: -20260917
---

# Update to latest lands on the commit your portal can run

A portal runs plugin modules that were compiled from one exact commit of their repository and
**sealed** for the platform build the portal runs. Its content sync keeps the plugin's source files
on that same commit, so the sources and the running modules always describe the same code — a
green build of the repository, the seal arriving, a first import and the plugin install at boot all
followed that rule.

Two buttons did not. **Update to latest** fetched the branch tip, and **Re-import at this commit**
fetched whatever was typed into its field, a branch included. On a plugin repository that put
source files on the portal that its running modules were never built from: the portal then declined
those modules and compiled the types from source, or failed to compile them at all.

Both buttons now ask the seal first:

- **A repository this portal runs no modules of** — a course, a document tree, a deployment
  record — is unchanged: you get exactly the branch or commit you asked for.
- **A plugin repository sealed for this portal** imports the sealed commit. The activity says so in
  a warning that names both the commit you asked for and the one that was imported.
- **A plugin repository whose publication is incomplete** imports nothing. The activity names the
  publication and what will release it: rolling the portal onto its newer platform build when one is
  sealed, otherwise the publishing lane sealing a newer commit.

There is no override that fetches the tip anyway — **Force** still discards local edits, but it no
longer decides which commit arrives. If a Space is held, the way forward is the one the activity
names, not a tip import.

A green build of a plugin repository that is not yet sealed for the portal used to leave its Spaces
waiting until the seal was read again. It now lands them on the sealed commit straight away, the
same commit that later read would have chosen.

The rule and its reasons: [The Sync-Ref Contract](/Doc/Architecture/SyncRefContract).
