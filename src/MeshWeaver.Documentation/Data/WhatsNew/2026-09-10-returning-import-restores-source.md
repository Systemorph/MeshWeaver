---
Name: Returning imports restore the selected source
Category: Fix
Order: -20260910
Description: Re-importing a previously used source no longer skips on an older success marker.
Icon: ArrowSync
---

Returning a synced partition to a previously imported revision restores changed and missing nodes.
The importer verifies its current source manifest before trusting a historical success marker.
Reconciliation also evaluates live drift when Git records the requested commit already, while
preserving protected local edits. An unchanged ordinary repeat remains a zero-content-write skip.

See [Static Repo Import](/Doc/Architecture/StaticRepoImport) for the mechanism and regression evidence.
