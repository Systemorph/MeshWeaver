---
Name: Updated view packs look right straight away
Category: Fix
Description: A newly installed or updated view pack's styling now reaches the browser on the next page load instead of waiting for a cache to expire.
Icon: ArrowSync
Order: -20260825
---

# Updated view packs look right straight away

View packs — the packages that draw maps, charts and the everyday controls — ship their own
styling. Until now the browser was told nothing about how long it could keep a copy of those files,
so it guessed. A pack that had just been installed or updated could go on being drawn with the
previous version's styling for as long as the guess lasted, and a reload did not necessarily help.

Two things changed. Each pack's stylesheet is now published at an address that changes whenever its
contents change, so an update is picked up on the very next page load and never mistaken for the
old one. And because that address can only ever mean one thing, the browser keeps it for good: a
deployment that has not changed a pack now costs no extra requests at all where it used to re-check
every file on every page.

Everything else a pack ships is now explicitly marked as needing a quick check before use, so the
newest version always wins.
