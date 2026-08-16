---
Name: Local installs auto-update from the registry
Category: Feature
Description: memex-local autoroll --acr keeps a local install tracking every promoted build — images, modules, and plugin content roll in automatically.
Icon: Sparkle
Order: -20260816
---

# Local installs auto-update from the registry

A local (Colima/k3s) install can now stay current without manual updates: `memex-local autoroll
up --acr` makes the existing auto-roll watcher also track the container registry's promoted
`main` tag. Every merge that passes CI rolls into the local portal automatically — the migration
runs first, then the portal restarts on the new image, exactly like the cloud portals' self-update.

The same mode keeps the mounted plugin repository on its main branch (only when it is clean and
parked there — a working copy with changes is never touched), so Store plugins and modules stay
current too. Plain `memex-local autoroll up` returns to the build-only watcher for the local
development loop.
