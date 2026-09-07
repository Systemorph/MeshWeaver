---
Name: A deploy that changes a key no longer runs the backfill with the old one
Category: Fix
Description: The database migration read its vault-backed settings from a copy another pod was responsible for refreshing, so a deploy that changed one of them ran with the previous value — and reported success. It now fetches them itself, before it starts.
Icon: Key
Order: -20260907
---

# A deploy that changes a key no longer runs the backfill with the old one

Some settings a deployment needs — an API key for the search index's embedding provider, for
instance — are held in a key vault rather than in the deployment's own configuration. The platform
reads them through a driver that fetches from the vault and hands the values to the pods that ask
for them.

"Ask for them" is the part that mattered. The driver fetches for the pods that **mount** the vault
declaration; a pod that only reads the resulting copy gets whatever the last fetching pod left
behind. The run-once database migration was in the second group, and it starts the moment an
upgrade is applied — before the portal pods that do the fetching have rolled. So on a deploy that
*changed* one of those values, the migration ran with the value from before the change.

The visible symptom was the worst kind: nothing. On 7 September a deploy repointed the embedding
key to a different provider. The migration's backfill authenticated with the previous key, was
refused 1,260 times, wrote every page without an embedding, and finished with "Database migration
completed". The vault was correct all along — a portal pod that started minutes later used the same
setting successfully. Only the migration's copy was stale, and its own report said everything had
gone fine.

The migration now mounts the vault declaration itself. That mount happens before anything in the
pod starts and fetches from the vault at that moment, so the values it reads are current by
construction — not by a retry, a second upgrade, or lucky timing. A check in the build now refuses
any workload that reads one of these settings without fetching it.

One consequence worth knowing: if a declared vault entry does not exist, the migration now stops
and says so, exactly as the portal already did, instead of starting with whatever was left over.
