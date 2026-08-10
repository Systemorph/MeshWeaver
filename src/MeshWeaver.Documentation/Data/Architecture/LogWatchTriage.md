# Red-log watching and automatic ticketing

Every `fail:` or `crit:` line a portal emits in production is a defect somebody should see. This
subsystem makes sure one gets a ticket — exactly one, no matter how many times it fires — with an
agent-written description of what is probably broken, filed in the repository that owns the code.

## The shape

```
  pod stdout ──▶ Promtail ──▶ Loki
                                │  query_range from a persisted cursor, every minute
                                ▼
                       mw-log-watcher            (ns monitoring, its own PVC)
                          │ group lines into bursts
                          │ fingerprint  =  hash(top app frame, exception)  — or hash(log site) with no frame
                          │ queue to disk
                          ▼  POST /api/log-incidents   (Bearer, in-cluster)
                       Portal
                          │ first sighting  ──▶  MeshNode  Admin/_LogIncident/{fingerprint}
                          │                       Status=New, RequestedStatus=Triage
                          │ repeat          ──▶  fold: +occurrences, +pods, +samples
                          ▼
                    LogIncidentControlPlane
                          │ Triage  ──▶ LogTriage agent thread (MainNode = the incident)
                          │                └─ writes draft + RequestedStatus=File
                          │ File    ──▶ GitHub App ──▶ issue in the routed repo
                          │ Comment ──▶ "still happening" (rate-limited), reopen if closed
                          ▼
                    GitHub issue  ◀── the ticket
```

## Why the detector is not in the portal

The component that notices "the portal is throwing errors" must not be hosted by the portal.
`mw-log-watcher` is a separate Deployment in the `monitoring` namespace with its own volume; when
the portal wedges, the watcher keeps reading Loki and queues reports to disk until the portal
answers again. Nothing is lost — delivery is just delayed.

The portal owns everything *after* detection, because that is where the mesh, the agents and the
GitHub App credential already live.

## One fault, one ticket

The fingerprint (`StructuralLogIncidentIdentity.Compute`) identifies **the fault, not the reporter**.
It is a `sha256` truncated to **16 hex characters** (the first 8 bytes) over one of two payloads,
chosen by the burst itself:

| The burst names… | The identity is | Why |
|---|---|---|
| an application stack frame | `("frame", topFrame, exceptionType)` | the frame names the exact method that faulted — the most specific locator available |
| no application frame | `("site", category, eventId, exceptionType)` | there is no location to key on, so the log site the code assigns is the locator |

- **🚨 The reporting category is NOT in the identity when a frame is present.** The category names
  the class that *caught and printed* the fault, which is not where the fault is. One exception
  unwinding through two catch sites is one defect however many of them log it — production
  2026-08-10 filed issues #1170 and #1171 for a single `ObjectDisposedException` raised at
  `SynchronizationStream<T>.OnCompleted()` during a single hub teardown, on one pod, at one instant,
  because `MessageHub` and `HostedHubsCollection` each logged it on the way out.
- **🚨 The message text is NOT hashed, in either branch.** Earlier revisions folded a *normalized*
  message (guids, timestamps, paths, quoted literals, hex blobs and bare numbers masked) into the
  identity. That still fanned out: the varying subject sits in an arbitrary position that no masking
  rule reliably anticipates, and one defect produced ~50 incidents. The message is still normalized
  and *carried* on the report (`NormalizedMessage`), it just does not contribute to identity.
- **The exception type is part of the identity in both branches** — the same method can fail two
  ways, and folding those together would hide the second bug behind the first one's ticket. It is
  compared on its **simple** name: `ex.ToString()` prints `System.ObjectDisposedException` while a
  message interpolating `ex.GetType().Name` prints `ObjectDisposedException`, and one fault must not
  fork on the caller's formatting. For the same reason the parser recovers the type from the message
  text when the call site formatted the exception *into* it instead of passing it to the logger.
- **The top frame excludes framework code** (`System.` / `Microsoft.` / `Npgsql.` / `Orleans.`
  prefixes are skipped) and drops its `in /path/file.cs:line NNN` suffix, so an unrelated edit above
  the faulting line does not fork the fingerprint into a second ticket.
- **Namespace and pod are NOT in the fingerprint.** The same defect on two pods is one ticket; the
  pods are recorded on the incident instead — and so a defect every tenant hits opens one ticket
  rather than one per tenant.

The direction of the remaining trade is deliberate: two genuinely different faults raised at the
same frame with the same exception type collapse into one incident. An under-split incident is one
ticket a human can split; an over-split one is fifty tickets nobody reads, and fifty is what
production actually produced. What identity must *never* do is discard the fault site to make
unrelated reporters agree — issues #1183 and #1184 (one logger, one event id, one exception type,
two different health checks) are two code sites and stay two incidents.

The fingerprint is also the incident's node id, so redelivery is idempotent by construction. That
is what lets the watcher retry freely.

## Delivery guarantees

At-least-once, and deliberately so:

| Step | Order | Why |
|---|---|---|
| Read window | `[cursor, now − IngestLag)` | Promtail ships with a delay; reading to `now` would step the cursor past lines that had not landed yet, losing them for good. |
| Queue reports | **before** delivery | A crash between detection and delivery costs a redelivery, not an un-ticketed error. |
| Advance cursor | **after** queueing | Same reason, one level up. |
| Deliver | oldest first, stop at first retryable failure | A wedged portal is retried next tick, not hammered once per queued report. |

A `4xx` other than `429` is permanent — a malformed or unauthorized report will not become valid by
being resent — so it is dropped with an ERROR log rather than retried until the disk fills.

## The incident lifecycle

Incidents are `LogIncident` nodes at `Admin/_LogIncident/{fingerprint}` — Admin-scoped, because a
red log is platform state and carries message text from every partition.

The lifecycle runs on the `Status` / `RequestedStatus` control-plane pair
([Activity Control Plane](../ActivityControlPlane)), never on a bespoke request message:

| `RequestedStatus` | Set by | The control plane does |
|---|---|---|
| `Triage` | ingest (first sighting, or a repeat of an un-ticketed `New`/`Failed` incident) | marks `Triaging`, starts a LogTriage thread with the incident as `MainNode` |
| `File` | the triage agent | marks `Filing`, resolves the repository, opens the issue as the GitHub App, records `IssueNumber`/`IssueUrl` |
| `Comment` | ingest (repeat of a ticketed incident, at most once per `CommentInterval`) | comments the recurrence; reopens the issue first if it had been closed |
| `Suppress` | the triage agent, or an admin | marks `Suppressed` — occurrences keep counting, tickets stop |

Because the state lives in the mesh, a portal restart mid-triage strands nothing: the node still
says what it is waiting for and the next query emission picks it up.

### One fault, one ticket — for the fault's whole life

Deduplicating at the *fingerprint* is only half the promise. The other half is that the incident is
**filed at most once**, and the control plane enforces it in two places.

**The claim.** Every transition consumes `RequestedStatus` in a write against the incident's LIVE
content, and that write lands *before* the GitHub call it guards (the
claim-the-guard-before-the-mutation rule). A second work item for the same incident finds the
request already cleared and stands down. Without it the only guards are in-process: the incident
query is eventually consistent, so it re-emits the pre-write snapshot after the in-flight set has
been released — which is how two issues were opened for one fault inside the same second on the
watcher's first live run.

**The issue link outranks the status.** `NextRequest` checks `IssueNumber` *before* the
`New`/`Failed` retry rule, and a `File` request that arrives on an already-ticketed incident is
granted as a `Comment` instead. Both close the same chain: a recurrence parks a ticketed incident at
`Failed` (a comment that errored, or the stranded-triage reconcile marking it retryable), ingest
re-triages it, the agent drafts again and asks to `File` — and a second issue appears. That chain
filed `ROUTER_TRAFFIC` eight times in seven minutes.

A recurrence is therefore always folded onto the ticket that exists:

- One comment per `CommentInterval` (default 6 h), whatever asked for it — a `File` request landing
  on a ticketed incident obeys the same bound a `Comment` request does, so a continuously-firing
  fault cannot turn its own issue into a feed.
- A **closed** issue is reopened first (`ReopenOnRecurrence`). A defect that returns after someone
  closed its ticket is exactly what should notify.
- A comment that lands writes the incident back to `Filed`, clearing a stale `Failed` — leaving it
  is what let ingest re-triage a ticketed incident in the first place.

The claim keys on `RequestedStatus`, never on the status, so the in-flight statuses (`Triaging`,
`Filing`) are not dead ends: a crash between the claim and the write-back parks the incident there,
and asking again is honoured. **Something has to do the asking**, though, and that differs by status:

- `Triaging` — the stranded-triage reconcile below, which asks the thread whether the round is over.
  Only the thread can tell "still running" from "died", so nothing may re-request on a timer.
- `Filing` **with no `IssueNumber`** — the next recurrence re-requests `File` (`NextRequest`). The
  claim was taken and nothing came back, and unlike `Triaging` there is no in-mesh authority to ask:
  the only record of whether the issue was opened is GitHub. Re-asking is safe because the claim
  itself is the guard — if the incident has since been ticketed, the request is granted as a
  `Comment`, never as a second issue.
- `Filing` **with** an `IssueNumber` is not in-flight at all: the write-back lands the link, so that
  state is a completed file whose status simply settled, and the issue-link rule keeps it quiet.

### The one state nothing requests: `Triaging`

`Triaging` is entered by the control plane and is supposed to be left by the **agent**, which writes
its draft and asks to `File`. Nothing in the table above can leave it, and the ingest path's retry
rule re-triages `New` and `Failed` but never `Triaging` — it cannot tell "the round is running"
from "the round died". So a round that ends without writing back parks the incident permanently:
invisible, un-ticketed, and still parked after the cause is fixed. That is what a missing triage
agent looks like — nineteen incidents accumulated that way on `memex.systemorph.com` while
`LogTriage` was absent from the served agent catalog.

The control plane therefore **reconciles** a `Triaging` incident against the thread it is waiting
on. This is not a timer or a retry watchdog: it fires only on a query emission that already carries
a stranded-looking incident, and it asks the only authority there is — the thread node.

- Round still running (`Executing`, or queued input not yet drained) ⇒ nothing happens.
- Round over and the incident still `Triaging` with nothing requested ⇒ `Failed`, with an `Error`
  naming the configured agent and the thread. `Failed` is re-triaged on the next recurrence, so the
  incident heals itself once the missing dependency is back.
- The incident moved on in the meantime (a draft landed, `RequestedStatus` now asks to `File`) ⇒
  nothing happens. The status is re-read at write time, so a successful triage the reconcile merely
  raced is never overwritten.

### Where the triage agent comes from

`TriageAgent` is a **name**, resolved against the mesh's agent catalog at run time — a name with no
agent behind it fails inside the thread, not at startup. On a portal that catalog is the `Agent`
partition, served from the database and filled by the pre-installed **`Agent` plugin**
(`MeshWeaver.Plugins/Agent/`); the framework's `content/ai/Agent` copy serves only the in-memory
hosts (tests, monolith, MAUI). An agent added to one copy and not the other therefore resolves in
every test host and in no deployment — the parity gate `scripts/check-agent-parity.py` in
MeshWeaver.Plugins exists to make that impossible.

## Repository routing

Configured category prefixes decide, longest prefix first, so a specific route beats a catch-all:

```json
"LogWatch": {
  "DefaultRepository": "Systemorph/MeshWeaver",
  "Routes": [
    { "Prefix": "MeshWeaver.", "Repository": "Systemorph/MeshWeaver" },
    { "Prefix": "Memex.",      "Repository": "Systemorph/Memex" }
  ]
}
```

The triage agent may override the route when the stack trace positively blames someone else, but
**only into a repository the deployment already routes to** (or one listed in
`AllowedRepositories`). An LLM-chosen destination is a write target, so it is allowlisted; a refused
override is logged and recorded on the ticket rather than silently ignored.

Issues are opened as the **GitHub App** — the same machine identity the plugin registry uses — never
a user's OAuth token, because a red log at 03:00 must not depend on whose credential happens to be
stored.

## Configuration

**Portal** (`LogWatch` section — see `LogWatchOptions`):

| Key | Meaning |
|---|---|
| `IngestToken` | Shared secret the watcher presents. **Unset ⇒ `/api/log-incidents` is not mapped at all** — reaching it spends model budget and opens issues, so absence means off, never open. |
| `DefaultRepository`, `Routes`, `AllowedRepositories` | Where tickets go (above). |
| `TriageAgent` | Agent name; defaults to `LogTriage`. |
| `CommentInterval` | Minimum gap between recurrence comments. Default 6 h. |
| `ReopenOnRecurrence` | Reopen a closed issue when its cause returns. Default on. |
| `MaxSamples` | Evidence lines kept per incident. Default 10. |

**Watcher** (`LogWatcher` section, i.e. `LogWatcher__*` env vars — see `LogWatcherOptions`):

| Key | Meaning |
|---|---|
| `LokiUrl`, `Namespaces` | What to read. |
| `PortalUrl`, `IngestToken` | Where to report. Must match the portal's token. |
| `PollInterval` | Default 1 min. |
| `ColdStartLookback` | How far back a cursor-less start reads. Default 15 min — deliberately short, so a fresh install starts ticketing what happens next rather than replaying history into a hundred issues. |
| `MaxCatchUp` | Cap on how far back the cursor may be dragged. Default 6 h. |
| `IngestLag` | How far the window trails `now`. Default 30 s. |
| `StateDirectory` | **Must be a persistent volume.** On an `emptyDir` a restart replays the lookback window. |
| `IgnoreCategories` | Category prefixes never ticketed. Prefer suppressing the incident in the portal, which keeps counting occurrences; this drops the lines entirely. |

## Deploying

```bash
# 1. One shared secret, both sides.
TOKEN=$(openssl rand -hex 32)
az aks command invoke -g <aks-resource-group> -n <aks-cluster> --command \
  "kubectl -n monitoring create secret generic mw-log-watcher --from-literal=ingest-token=$TOKEN"
#    …and set LogWatch__IngestToken to the same value on the portal (via its KeyVault secret).

# 2. Build and push the watcher image.
dotnet publish tools/MeshWeaver.LogWatcher/MeshWeaver.LogWatcher.csproj -c Release \
  -t:PublishContainer -p:ContainerRegistry=meshweaver.azurecr.io \
  -p:ContainerRepository=memex-log-watcher -p:ContainerImageTag=<tag>

# 3. Apply the Deployment + PVC.
az aks command invoke -g <aks-resource-group> -n <aks-cluster> \
  --command "kubectl apply -f log-watcher.yaml" \
  --file deploy/aks/manifests/observability/log-watcher.yaml
```

Verify end to end:

```bash
az aks command invoke -g <aks-resource-group> -n <aks-cluster> --command \
  "kubectl -n monitoring logs deploy/mw-log-watcher --tail=50"
# Expect: "Loki: N red line(s) …" then "Reported <fingerprint> (<category>) — 200".
```

Then browse `Admin/_LogIncident` in the portal — every incident links to the ticket it opened and
to the triage thread that wrote it.

## What this is not

- **Not an alerting system.** A provisioned Grafana rule
  (`deploy/aks/dashboards/memex-red-log-alerts.yaml`) covers the "tell a human now" case. Ticketing
  hangs off the watcher's cursor instead, because an alert notification that fires while its
  receiver is down is simply lost — acceptable for a nudge, not for "every distinct error gets a
  ticket".
- **Not a substitute for reading logs.** It tickets what a portal reports as red. A fault that logs
  at `warn:` or does not log at all is invisible to it.

## Related

- [Controlled I/O Pooling](../ControlledIoPooling) — every HTTP and file leaf here runs on an `IIoPool`.
- [Activity Control Plane](../ActivityControlPlane) — the `Status` / `RequestedStatus` pattern.
- [Access Context Propagation](../AccessContextPropagation) — why ingest writes under `ImpersonateAsSystem`.
- [CQRS — Queries vs. Content Access](../CqrsAndContentAccess) — why ingest reads the node stream, not a query.
