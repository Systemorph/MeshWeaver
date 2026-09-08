---
Name: Volume capacity is a property of the Deployment record
Category: Feature
Description: Growing an instance's data share no longer needs a cluster credential. The size of every persistent volume is a field on the Hosting/Deployment record; the hosting operator's new hosting-pv-resize grows the claim to it (never shrinks, never creates, refuses a class that cannot expand), the Provision and Reconcile actions apply it, and the Audit reports a claim that has fallen below what its record declares.
Icon: HardDrive
Order: -20260908
---

# Volume capacity is a property of the Deployment record

On 2026-09-08 at 13:51Z the `/data` share of `memex.systemorph.com` measured **16 GiB with 3 MiB
free** — full — while its sibling on `memex.meshweaver.cloud` ran 128 GiB. The size of that share
was written in three places (the Deployment record, the rendered values file, a hand-applied PVC
capture) and none of them could change it: on this fleet the portal's claims were applied by hand
once and are not managed by helm, so a bigger `size` on the record re-rendered a bigger number
into the values file and left the cluster exactly as it was. Growing the share meant `kubectl
patch pvc` from a laptop — the break-glass write the same day's rule
([OperatingFromThePortal](/Doc/Architecture/OperatingFromThePortal)) retires.

**Capacity is now a record property, applied by the operator.** `volumes[].size` on the
`Hosting/Deployment` record is what the claim holds, and three pieces make that true:

- **`hosting-pv-resize --namespace <ns> --claim <pvc> --size <Gi>`** in the operator image grows
  ONE claim to the declared size and reads the capacity BACK from the claim's status before it
  reports. It never shrinks (a record that declares less than the claim holds is a wrong record,
  and the refusal says to correct it), never creates (a new claim is the chart's job), and refuses
  a storage class without `allowVolumeExpansion` before writing anything. Azure Files expands
  online; a block volume whose filesystem resize waits for a pod is reported as pending, on its
  own `::hosting::` line, rather than treated as done. A claim already at size is a successful
  no-op, so the step is safe on every run.
- **`Provision` and `Reconcile` apply it.** Both plans carry one *Ensure volume capacity* step per
  declared claim, and Reconcile orders it FIRST — a full `/data` blocks the rollout the re-apply
  then waits on. Editing `size` on the record and running the action you already have is the whole
  path.
- **`Audit` reports a claim below its record** as a finding of its own
  (`volumeCapacityBelowRecord`, with the claim and both sizes) — the one record-versus-live
  comparison in the audit, because the manifest cannot show it.

The measured numbers are corrected in the same change: `memex`'s data volume is declared at
128Gi, and `memex-cloud`'s stale 16Gi overlay entry now states the 128Gi its share has held all
along. The runbook is in [DeploymentAKS](/Doc/Architecture/DeploymentAKS) → "Volume capacity is a
record property".
