---
Name: New modules from a plugin repo can now appear by themselves
Category: Feature
Description: Turn on auto-discovery for a plugin repository and every module it ships is listed here — with the missing ones set up automatically when you ask for that.
Icon: Sparkle
Order: -20260808
---

# New modules from a plugin repo can now appear by themselves

Modules reach an instance through a per-space sync entry, and until now somebody had
to make that entry by hand for every single module. Nothing told you when one was
missing: a module the repository had shipped simply was not here, and the page you
would have looked at did not exist either. Two modules sat absent from a portal for
weeks purely because nobody had made their entries, while nine of their siblings from
the very same repository were present.

Worse, "make the entry by hand" was not really available. A sync entry needs its space
to exist first, and creating a space the ordinary way makes you its administrator — on
a partition the repository owns, which the access rules forbid and which the next sync
undoes anyway. So adding a module had no safe self-service path at all.

**The repository is now the thing you configure, not each space.** A plugin source gets
two settings next to the repository and branch it already had:

- **auto-discover new modules** — the instance lists what the repository ships and
  records, for each module, whether it is here. Nothing is created; absence just stops
  being invisible. Every module gets a status and a reason you can read.
- **auto-sync discovered modules** — a module that is missing is set up for you: its
  space, its access, its sync entry, and the first import.

It runs when the repository's build goes green, so a module merged this morning is here
this morning.

**Everything is created by the platform, never by you.** That is what makes it safe: the
space belongs to the repository from the first moment, so no personal administrator
grant is ever minted on it and there is nothing to retract afterwards.

A few things deliberately do **not** happen automatically. A **priced** module is never
brought in without an entitled administrator — it is listed as refused, with the reason.
A name already taken by an existing space is left alone rather than adopted, because
adopting it would take that space away from its owner. And a module the repository has
**dropped** is flagged as orphaned rather than deleted — your content is never destroyed
on the strength of a listing.

Both settings are off unless you turn them on, so nothing changes for an instance that
does not ask for it.
