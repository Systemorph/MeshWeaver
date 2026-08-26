---
Name: A module whose registration fails no longer crashes the whole portal
Category: Fix
Description: A module that loaded fine but failed while registering its own features against the running platform used to abort the entire portal at boot. It is now isolated to that one module — skipped, reported, and the portal keeps serving.
Icon: Sparkle
Order: -20260826
---

# A module whose registration fails no longer crashes the whole portal

Yesterday's fix (2026-08-25) stopped a module that fails to LOAD against the running platform from
taking the whole portal down with it — that module is skipped, reported, and every other module still
installs. It left one door open: a module can load successfully and still fail later, while it is
registering its own features, if that step calls into a signature the running platform no longer has.
That is exactly what happened twice — once building the crash that prompted yesterday's fix, and again
the next morning on a different portal and a different module.

## What changed

A module's feature registration is now isolated the same way its loading already was: if it throws, that
one module contributes nothing and is reported as incompatible, but the portal boots and every other
module installs normally. Nothing before it is torn down, and nothing after it is blocked.

## What you will notice

A module that no longer matches the running platform shows up as a named, reported problem — never a
portal that fails to start. The remedy is unchanged: a module and the platform it runs on have to move
together.
