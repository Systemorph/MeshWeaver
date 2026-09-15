---
Name: A Space no longer rewrites itself on every import
Category: Fix
Description: >-
  A Space declared in a content repository was re-written — and its version bumped — on every single
  import, because it stamped a fresh creation timestamp each time it was read. Nothing was wrong
  with the result, which is why nobody noticed.
Icon: ArrowSync
Order: -20260915
---

# A Space no longer rewrites itself on every import

A **Space** is a tenant container — a company, a team, an organisational unit with its own
partition. Like most things on the mesh, one can be declared in a content repository and imported.

Every such Space was being **written again on every import**, forever, with a new version each time.

## Why

The Space's content carried a "created at" timestamp whose default value was *the moment it was
read*. A declaring file does not write that field — there is nothing sensible for an author to put
there — so every import invented a new one. The incoming content therefore never matched the stored
content, and the "nothing changed, skip it" step could never fire.

## Why nobody noticed

The re-written node was **correct**. The Space kept its name, description, logo and every other
field; only an invisible timestamp moved. There was no error, no warning and no visible difference —
just a version number climbing with no author behind it, and an import doing work it did not need
to do.

It surfaced only when a package happened to declare a Space and met the install gate that asks the
one question nobody else asks: *would this write have happened if nothing had changed?*

## What changed

The timestamp is no longer invented. A declaration that does not carry one now stays without one,
so the same file imports to the same content every time — and a Space that genuinely records its
creation time keeps it.

When a node was first created is already recorded in its own version history, which is the reading
to use.
