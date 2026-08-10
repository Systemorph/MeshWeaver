---
Name: Schema lookups stop filling the log with false errors
Category: Fix
Description: Reading a node type's schema no longer spins up a full node control plane it immediately tears down, so the portal log stays free of the errors that teardown produced.
Icon: Sparkle
Order: -20260810
---

# Schema lookups stop filling the log with false errors

Whenever an agent created or updated a node, the portal checked the content
against the node type's schema. To find out what that schema *is*, it applied
the node type's configuration to a throwaway hub — and that hub was given the
full machinery a real, long-lived node gets: the compile watcher, the release
watcher, the sources watcher, and the compile-state mirror.

None of that had anything to do on a hub that exists for a fraction of a
millisecond. Each watcher would open a connection to the mesh and then fault as
the hub was disposed out from under it, reporting the teardown as a failure and
trying to re-establish. One schema check produced around twenty error and
warning lines — none of them describing a real problem, all of them competing
for attention with the ones that do.

Throwaway lookup hubs are now built without that machinery. They still carry
everything the lookup actually reads, so schema validation and the node type's
data-model view behave exactly as before — they just no longer report their own
shutdown as a fault.

A rejected write also got cheaper: the error message and the schema the agent
needs to correct itself are now read in one pass instead of two.
