---
Name: Per-Process Infrastructure Hubs
Category: Architecture
Description: "A hub that exists once per PROCESS must be addressed once per process, under a type the router carries to that process. Why the activity-tracking hub moved from activity/_tracking to portal/tracking-{meshId}."
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="4" width="7" height="16" rx="1"/><rect x="14" y="4" width="7" height="16" rx="1"/><path d="M10 12h4"/></svg>
---

# Per-Process Infrastructure Hubs

> **The rule:** a hub that exists once per PROCESS — hosted under the mesh root, not backed by a
> node — is addressed `portal/{role}-{meshRootId}`. The mesh id makes the address unique to the
> process. The `portal` type is stream-routed, so the router carries a delivery from any silo to the
> process that hosts the hub, by the pod-hub transport or the stream. If that process has gone, the
> router refuses the delivery loudly.

| Hub | Address | Role |
|---|---|---|
| Node-operation execution | `portal/nodeops-{meshId}` | runs node CRUD off the router (`MeshExtensions.NodeOperationExecutionHub`) |
| Read issuing | `portal/reads-{meshId}` | issues bounded node reads (`MeshExtensions.ReadIssuingHub`) |
| Activity tracking | `portal/tracking-{meshId}` | originates user-activity writes (`ActivityTrackingHub.GetActivityTrackingHub`) |

## Why the address carries both halves (#6605)

The activity-tracking hub used to live at `activity/_tracking`, and that address got both halves
wrong:

- **One address on every silo.** A sender stamps `activity/_tracking~mesh/{rootId}`, but routing
  normalises a `mesh` host away, so all silos' tracking hubs look the same. On a silo that hosts its
  own tracking hub, a delivery meant for another silo's hub short-circuited onto the LOCAL route.
  That is a misdelivery. Every silo also subscribed the same memory stream and claimed the same
  pod-hub key.
- **A type the router cannot carry.** `activity` is neither stream-routed nor node-backed. From
  another silo, `RoutingGrain` read the address as a node path and found nothing. The NACK back to
  the tracking hub had no carrier either: before #6603 it was a stream publish nobody was
  guaranteed to hear, and after #6603 it was logged at Error and dropped.

The tracking hub is per process ON PURPOSE. Activity writes must originate from a stable,
registered hub whose cache is the process's shared `cache/{meshRootId}`, never a transient
per-connection hub (see `ActivityTrackingHub`). So the fix keeps one hub per process and only
gives it the address its siblings already had.

## Where it is pinned

`ActivityTrackingHubCrossSiloTest` (MeshWeaver.Hosting.Orleans.Test) runs on two silos:

- each silo's tracking hub has its own address of a stream-routed type;
- a request from silo B to silo A's tracking hub is answered there, and silo B holds no local route
  that could answer it instead;
- negative control: a route registered on silo A under the old `activity/…` shape is never reached
  from silo B, and the sender receives a `DeliveryFailure` rather than silence.

On the old address, the first two fail and the third passes.
