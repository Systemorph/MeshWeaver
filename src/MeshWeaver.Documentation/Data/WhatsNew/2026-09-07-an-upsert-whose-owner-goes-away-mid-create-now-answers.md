---
Category: Fix
---

# An upsert whose owner goes away mid-create now answers

Creating a node through the single-verb upsert could hang for the caller's whole budget in complete
silence. The install of a plugin package retypes its root and then recycles it — which happens on
every plugin install — and a create that was in flight when that happened simply never received a
response. Nothing was logged, nothing failed, and the caller waited.

The create now answers either way: if the response cannot arrive, the caller gets a refusal that says
the create was **not** applied and is safe to retry, and names the recycle as the usual cause.
