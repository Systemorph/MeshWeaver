---
Name: Reading a Silo Stop
Category: Architecture
Description: How a departing silo's own SiloStopTimeline lines decide whether peers' GrainCallCancellationManager errors (the 30 s timeout of issue 6392 and the connect timeout of issue 7094) came from a stopping, cut or starved silo, and why that read was impossible in production until the category was pinned.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="10"/><path d="M12 6v6l4 2"/></svg>
---

# Reading a Silo Stop

Silo departure and cancellation timeouts (#6392, log fingerprint `97b80f6a91dbdb74`). See also
[A Departed Silo Is Not a Delivery Defect](../ADepartedSiloIsNotADeliveryDefect) and
[Reading a Silo Eviction](../ReadingASiloEviction).

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

## The connect-timeout variant, and why the read above was never possible (#7094)

The same logger has a second shape: `Error while cancelling 1 requests to S10.244.6.253:11111:151775954` with `OrleansMessageRejectionException` wrapping `ConnectionFailedException: Connection attempt to endpoint … timed out after 00:00:05` (memex-cloud, 2026-10-23 18:43:44Z, fingerprint `e578fd62e6a46b11`). Where the 30 s shape means "a connection exists and nothing answers in time", this one means **no usable connection was established within `OpenConnectionTimeout`**. In Orleans 10.4.0's `ConnectionManager.ConnectAsync`, that one deadline covers both opening the transport connection AND awaiting `connection.Initialized` (the handshake). So the exception is equally consistent with a pod that is gone, a network failure, and a live pod whose connection initialization stalled. Pod departure is a hypothesis here, not a reading.

**It is Orleans behaving as designed, and nothing in this repository issues the cancellation.** Read from Orleans 10.4.0's `GrainCallCancellationManager`:

- A cancellation for a call to silo S goes into a per-silo channel, and a pump sends each batch to S's `sys.svc.canceler`.
- On a failed send the pump logs `Error while cancelling {Count} requests to {SiloAddress}`, keeps the failed batch, waits 5 s, and returns to `WaitToReadAsync`.
- It does **not** resend on a timer. The retained batch goes out again only when ANOTHER cancellation for S arrives, so a single failure followed by silence establishes neither a recovery nor a membership transition. The batch may simply never have been resent.
- The pump ends when membership reports S `Dead` (or `None`), or when the local silo shuts down: the worker's channel is completed and its token cancelled. A cancelled pump exits without logging.

No grain interface declared in core (`IMessageHubGrain`, `IPodHubGrain`, `IRoutingGrain`, `ISiloDrainNoticeGrain`) takes a `CancellationToken`, so these cancellations are Orleans' own: a call to S that timed out, cancelled by Orleans' cancel-on-timeout messaging behaviour.

What the logs show for that instant (read through `Logs` actions `Ops/Actions/i7094-*` on the control instance):

- A roll of memex-cloud was in progress (`Ops/Actions/selfupdate-roll-memex-cloud-3-1-11453-d951f25e`, 18:29:40Z–18:53:25Z). The reporting pod `…-7455dc7c55-npq89` was a NEW pod of that roll and was joining the cluster.
- At 18:43:44Z the same pod logged `Did not receive a probe response from silo S10.244.6.253:11111:151775954 in timeout 00:00:15` and `Failed to get ping responses from 1 of 5 active silos. Newly joining silos validate connectivity with all active silos before joining the cluster.` Orleans' join check (`MembershipAgent.ValidateInitialConnectivity`) picks the `Active` entries from the joining silo's cached `CurrentSnapshot` BEFORE it awaits the probes, and refreshes the table only between attempts. So this warning says the joining silo's snapshot listed the address as `Active` when that attempt began. It does not establish the persisted row's status at 18:43:44Z.
- About two minutes earlier, at 18:41:40Z, a peer (`…-85dfc86d59-mz4z6`) already logged `The target silo became unavailable for message: 'Request [S10.244.6.253:11111:151775954 …]'`.
- A graceful Orleans stop writes `ShuttingDown` to the table when the `BecomeActive` stage stops, which is before `GrainDeactivation`, and writes `Dead` when `RuntimeInitialize` stops. Because the reading above comes from a cached snapshot, it does NOT show that the departing silo failed to write `ShuttingDown`: the row may already have changed while the joining silo was still using its earlier view. A stop that never reached Orleans' lifecycle (or was cut) remains one hypothesis among those above, not a finding.

Not established: which pod had that address, and what the persisted row read at that instant. One old pod, `…-85dfc86d59-lk9l4`, logged `Application is shutting down...` at 18:41:22Z and `'Orleans.Runtime.Silo' stopped in stage 'GrainDeactivation (19998)'` at 18:41:40Z, the same second the peer lost 6.253. That timing is suggestive and not proof. What would separate the hypotheses:

- the departing pod's own stop timeline, below;
- a corroborated membership-update outcome for generation `151775954` (a peer's status-change line, or the table row itself), as opposed to one silo's cached view.

The timeline, at least, is a gap of ours:

**`SiloStopTimeline` never reached Loki.** It logs at Information under `MeshWeaver.Hosting.Orleans.SiloStopTimeline`, and the Distributed portal's `"MeshWeaver": "Warning"` catch-all dropped every line before stdout. Measured on memex-cloud: 0 `SiloStopTimeline` lines over 2026-10-23 12:00–21:00Z. In the same window and on the same instrument, departing pods logged dozens of `Application is shutting down...` and `SiloLifecycleSubject` lines (the negative control: the query and the window were not empty). So every read this page prescribes above was impossible in production since the timeline shipped. MeshWeaver.Plugins pins the category to Information in `Memex.Portal.Distributed/appsettings.json`, held by `ShippedLoggingLevelsGuard`. That is what makes the next occurrence readable.

What this page does NOT recommend: demoting Orleans' line with a category filter. `Orleans.Runtime.GrainCallCancellationManager` also logs `Error processing cluster membership updates` through the same category, and a silo that peers still treat as live while it does not answer is the condition #6432 investigates. That condition is worth a red line until the stop timeline shows it to be an ordinary roll.
