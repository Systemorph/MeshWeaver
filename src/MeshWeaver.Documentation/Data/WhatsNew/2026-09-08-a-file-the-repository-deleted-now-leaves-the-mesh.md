---
Name: A file the repository deleted now leaves the mesh
Category: Fix
Description: A source file retired upstream could stay in its partition for ever — still compiled, still occupying a NodeType, still refusing every bundle on the source fingerprint. The import's content-addressed marker now records whether the run CONVERGED, and a run that kept nodes back no longer licences the next one to skip reading the partition.
Icon: Broom
Order: -20260908
---

# A file the repository deleted now leaves the mesh

On `memex.systemorph.com`, three `Crm/Source/Mail*` code nodes deleted from `MeshWeaver.Crm` on
2026-09-06 were still in the partition — and still compiled into every Crm type — two days later.
`Edu/Course` had been deleted upstream for **53 days**. Six retired nodes across two of the portal's
fourteen synced partitions; two of them parked at `compilationStatus: Error`, recompiled on every
boot and reported by the pre-production NodeType sweep. Because the source-fingerprint gate hashes
the *live* source set, every `Crm` bundle stayed refused for as long as the three files did.

The import had actually found them. Its own log says so:

```
↩ Kept Crm/Source/Mail (added on the server — commit to sync it back).
Imported 23 node(s), kept 5 local change(s), pruned 0, synced 0 content file(s).
```

Two things had to be true for that to become permanent, and both are now fixed.

**Only a person's write counts as a server edit.** The prune keeps a node changed on the server since
the last sync — but the sync horizon is deliberately held while anything is preserved, so a previous
import's *own* writes sat after it and read as "newer on the server" for ever. Every one of the six
retired nodes was import-written (`system-security`, or no author at all); not one carried a real
user id. `ImportConflictPolicy.IsHumanEdit` is what separates them, and a node a *person* edited is
still preserved exactly as before.

**And a run that kept nodes back no longer licences the next skip.** This is what made the residue
permanent rather than merely slow. The import short-circuits on a content-addressed marker that the
next run reads *instead of* the partition — so the marker is a claim about the partition, and the run
that kept five nodes back stamped it green anyway. The next sync then answered `Skipped`, reported
`Preserved = 0` — a zero nothing measured — and advanced the partition's seen commit to the branch
head, after which every later build answered "already at this commit". The marker now records what
its run left undone (`preserved`, `pruneRefused`); a marker that does not say "converged" — including
every marker written before today, which says nothing — is re-examined once, which is what retires
what is already there. A converged run's marker still short-circuits, at exactly the cost it always
did.

The design is in
[The Import Marker Records Convergence](/Doc/Architecture/ImportMarkerRecordsConvergence).
