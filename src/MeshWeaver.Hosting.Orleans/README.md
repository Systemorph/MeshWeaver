# MeshWeaver.Hosting.Orleans

## Overview
MeshWeaver.Hosting.Orleans provides a distributed hosting model for MeshWeaver using Microsoft Orleans. Each message hub is represented as a virtual actor (grain) in the Orleans cluster, enabling automatic distribution, scalability, and fault tolerance.

## How It Works
- Each message hub is mapped to an Orleans grain
- Messages are dispatched through Orleans silos
- Grains are automatically distributed across the cluster
- Virtual actor model ensures hubs are always addressable
- Orleans handles activation/deactivation and placement of hubs

## Usage
```csharp
var builder = WebApplication.CreateBuilder(args);

// Configure Orleans cluster
builder.Host.UseOrleans(orleans =>
{
    orleans.UseLocalhostClustering();
    // Configure other Orleans options
});

// Configure MeshWeaver with Orleans hosting
builder.UseMeshWeaver(
    new MeshAddress(),
    config => config
        .ConfigureWebPortal()
        .ConfigurePortalMesh()
        .UseOrleansMesh()
        .ConfigureServices(services => services.AddArticles())
);

var app = builder.Build();
app.StartPortalApplication();
```

## Features
- Distributed message processing
- Automatic scalability through Orleans clustering
- Fault tolerance and automatic recovery
- Virtual actor model for message hubs
- Transparent hub activation/deactivation
- Location transparency for message routing

## Benefits
- **Scalability**: Automatically scales across multiple servers
- **Reliability**: Built-in fault tolerance through Orleans
- **Persistence**: Optional state persistence for hubs
- **Distribution**: Automatic workload distribution
- **Recovery**: Automatic failure recovery

## Integration
- Built on [MeshWeaver.Hosting](../MeshWeaver.Hosting/README.md)
- Uses [Microsoft Orleans](https://learn.microsoft.com/en-us/dotnet/orleans/overview) for distribution
- Compatible with all mesh message patterns

## Silo departure and cancellation timeouts (#6392, log fingerprint `97b80f6a91dbdb74`)

`Orleans.Runtime.GrainCallCancellationManager` logs `System.TimeoutException` ("Error while cancelling N requests to S...") when a peer's cancellation batch to a silo's `sys.svc.canceler` gets no answer within the 30 s `ResponseTimeout`. An answer, or a refusal because the target is already known `Dead`, would return in milliseconds; a full 30 s timeout means the target was neither answering nor yet known `Dead` to the sender, and every batch queued in that window costs every peer one 30 s timeout.

What the repo shows (read from the code; not yet confirmed by the target silos' own logs):

- A silo stop here chains three independent bounded holds: `RoutingQuiescenceSiloParticipant` (stage `Active`, 30 s), `IoPoolSiloTeardown` (stage `First`, stops last, 30 s) and `MeshTeardownHostedService.StoppedAsync` (30 s). The last one's remarks record 55-61 s shutdowns measured on memex-cloud.
- The deploy gives a graceful stop room, so the grace period is not the likely cut: `deploy/helm/values.yaml` sets `portal.drainSeconds: 1800` with `portal.shutdownMarginSeconds: 120` reserved for the process's own shutdown, and documents the host's `ShutdownTimeout` as 90 s (`Memex.Portal.Distributed` `Program.cs`; stated by the chart, not read in this project). The three 30 s holds can at most add up to that 90 s, so a stop that burns every budget is cut by the host, not by Kubernetes.
- What peers do meanwhile is Orleans' decision, not this repo's: a message to a silo that is not yet `Dead` in the sender's membership view is sent, and waits out the 30 s `ResponseTimeout` if the target no longer answers on `sys.svc.canceler`. The incident's shape fits that window (one silo unreachable from several pods for more than a minute: 2026-10-07 09:54:09 and 09:54:45 against `10.244.2.27`, 2026-10-09 20:39 against `10.244.17.110`), but it fits two causes equally: a silo that is stopping (or was cut) and a silo that is alive but starved. `values.yaml` records the starved case on memex (2026-07-22: membership probes starved into the "I have been told I am dead" loop).

Not yet shown: which of the two it was. The probe, to run on the next rollout (or read from Loki for the two dates above): for the departing pod's silo address, line up (1) its `RoutingQuiescence:` / `IoPoolSiloTeardown:` / `MeshTeardownHostedService:` lines, (2) its Orleans membership lines (`ShuttingDown`, `Stopping`, `Dead`) and (3) the fingerprint's timestamps on the peers. Fingerprint timestamps inside the pod's stop window, before `Dead`: peers sent to a leaving silo - an Orleans-side window, reported upstream rather than patched here. No stop lines at all, or a process cut at `ShutdownTimeout`: the stop did not complete - a hosting defect to fix in this project. Stop lines present but the pod otherwise silent for the window: a starved silo - the thread-pool and grain-scheduler load during the stop is the lead. Until one of those is read from real logs, the cause stays a hypothesis.

**What the silo now writes to make that read possible (`SiloStopTimeline`).** `SiloStopTimeline` (registered by `AddOrleansMeshServices`) puts one marker on each of six silo lifecycle stages - `Active`, `BecomeActive`, `GrainDeactivation`, `RuntimeServices`, `RuntimeInitialize`, `First` - and, as Orleans stops them in descending order, logs one Information line per stage: `SiloStopTimeline: stage X reached N ms into the silo stop (M ms after the previous stage); thread pool T threads, P pending work items; GC pause total G ms; graceful=...`. `M` on a line is how long the stage ABOVE it took, so a stop that spent a minute in one stage shows the minute as the gap in front of the next line. It holds nothing, cancels nothing and changes no behaviour; it is observability, not a remedy, and it does not close the issue.

Reading a departing pod's log with those lines (the three existing hold lines stay as they are):

- The fingerprint's peer timestamps fall AFTER the pod's `RuntimeServices` line and before its last line: peers sent to a silo whose message centre had already stopped, while the process lingered through the pool drain and the mesh drain (up to 30 s each, after the silo has stopped answering). That is an Orleans membership-visibility window; this repo can only shorten how long the process lingers, so the next step would be a measured change to those two holds, or an upstream report.
- The fingerprint timestamps fall BEFORE `BecomeActive` or inside the `Active` hold: the silo was still answering and peers' batches were slow for another reason; look at the thread-pool and GC figures on the lines around it.
- Large `thread pool ... pending work items` or `GC pause total` figures on the lines, or a long gap in front of one stage with no stage line from the stage above's own hold: the process was starved, which is the memex 2026-07-22 shape in `values.yaml`.
- No `SiloStopTimeline` lines at all for the pod, or the last line followed by no further log: the process was cut mid-stop (a hosting defect to fix here) - compare the last line's `ms into the silo stop` with the host's 90 s `ShutdownTimeout`.

Worth noting, not shown: the measured silo stops on memex-cloud (55-61 s, recorded in `MeshTeardownHostedService`) are the same order as the 66 s window the 2026-10-07 samples cover (failures at 09:54:09 and 09:54:45). Until the pod logs for `10.244.2.27` (2026-10-07 about 09:53-09:55 UTC) or `10.244.17.110` (2026-10-09 about 20:38-20:40 UTC) are lined up with these lines, that is a coincidence of magnitudes and not a finding. Rollout check for the fingerprint: after the first roll with this change, read the departing pod's `SiloStopTimeline:` lines in Loki next to any `GrainCallCancellationManager` fingerprint `97b80f6a91dbdb74` lines from the peers for the same minute.

Why this is not a multi-silo test: a `TestCluster`'s silos share one process and one memory store, so an in-process cluster cannot reproduce how OTHER processes see a departing one (the limit `OrleansServerRegistryExtensions` records for the pub-sub store). `SiloStopTimelineTest` pins what the timeline logs (every stage once, in Orleans' stop order, a slow stage visible as the next line's gap) and that the silo registers it.

## See Also
- [Orleans Documentation](https://learn.microsoft.com/en-us/dotnet/orleans/overview) - Learn more about the Orleans virtual actor model
- [Main MeshWeaver Documentation](../../Readme.md) - More about MeshWeaver hosting options
