---
Name: A portal on a large module volume can roll again
Category: Fix
Description: Reading which module set the mesh runs no longer opens every historical set record on the shared volume. On a portal whose volume had accumulated hundreds of superseded records, that read took ten seconds inside the health check the startup probe asks every ten seconds with a five-second limit, so no new pod could ever become ready and a rollout stayed stuck for hours.
Icon: Checkmark
Order: -20260908
---

# A portal on a large module volume can roll again

On 2026-09-08 memex.meshweaver.cloud could not complete a rollout, and could not add a pod when
load rose: every new pod failed its startup probe, while the two old pods kept serving. The
migration had completed, the image was fine, and the same image was healthy on memex.systemorph.com.

The difference was the shared module volume. Under `modules/sets` the portal keeps one small record
per module-set proposal and adoption, and memex-cloud's volume had accumulated 687 of them, beside
843 superseded module generations that a separate fault had kept the garbage collector from
removing. The reader that answers "which set does the mesh run" opened every one of those records
on every call — ten seconds on Azure Files — and a health check asks it on every startup probe,
which allows five.

The reader now decides from the record names, which carry the sequence and the set id, and opens
only the records the decision needs: the newest proposal, the newest adopted proposal and its one
adoption record. A record no decision depends on is neither read nor reported, so a duplicate
proposal from months ago is no longer announced on every probe either; the conflict notice names
only the sequences that were actually decided on. The result is the same index as before, from a
handful of reads instead of hundreds.
