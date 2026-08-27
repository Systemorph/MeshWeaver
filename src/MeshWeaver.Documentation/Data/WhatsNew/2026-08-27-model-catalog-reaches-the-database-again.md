---
Name: Model catalog reaches the database again
Category: Fix
Description: On deployed portals the AI model and provider catalog silently stayed in memory — the Provider partition never materialized in the database, so git-governed model management had nothing to sync against. The portal now hands its configuration to the module system at boot, and the catalog lands in the database as configured.
Icon: Sparkle
Order: -20260827
---

# Model catalog reaches the database again

Deployments configure which AI content partitions are served from the database
(`Features:StaticRepoSync:Partitions`), and the model/provider catalog is one of them: serving it
from the database is what makes it durable, queryable, and manageable through git-governed sync.

On deployed portals that configuration was silently ignored. The AI engine activates as a module at
boot, and modules read the deployment's configuration through the mesh builder — but the portal's
boot path never handed its configuration over. The engine saw "nothing configured", fell back to
in-memory serving (the documented answer for an absent key), and skipped registering its content
sources. The result: sixteen models serving happily from memory, zero rows in the `model` schema,
no `provider` schema at all, and nothing in any log naming why — every part behaved correctly for
the inputs it saw.

## What changed

The portal hands its configuration to the module system before any module installs, so module
activations now see exactly what the deployment configured. A regression test pins the hand-over.

## What you will notice

The Provider and Model partitions materialize in the database as configured, which unblocks
git-governed model catalog management (`Provider/_GitSync`) and makes provider/model changes durable
across restarts instead of re-projected from configuration each boot.
