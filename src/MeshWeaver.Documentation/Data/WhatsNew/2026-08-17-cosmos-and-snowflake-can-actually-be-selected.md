---
Name: Cosmos and Snowflake can actually be selected
Category: Fix
Description: The Cosmos and Snowflake storage backends now ship with the image, so a deployment that sets Graph:Storage:Type to either one can actually start on it.
Icon: Database
Order: -20260817
---

# Cosmos and Snowflake can actually be selected

Both alternative storage backends were fully built, tested and documented as selectable through
`Graph:Storage:Type` — and neither one reached a shipped deployment. Nothing referenced them
except their own test projects, and they were absent from the module publish layout, so the
assemblies existed on no image and the documented selection could not be turned on at all.

They now ship under `modules/`, exactly as the AI provider packs and the other backends-by-listing
do. Selecting one is an appsettings edit in the deployment that wants it: add the DLL to
`Modules:Assemblies` and set `Graph:Storage:Type`. Nothing changes for anyone else — no portal
lists them, and a module that is present but unlisted is never loaded.

They ship with the image rather than as an installable Store package on purpose: persistence
selection happens during boot, so a storage backend is not something a running mesh can install
for itself.
