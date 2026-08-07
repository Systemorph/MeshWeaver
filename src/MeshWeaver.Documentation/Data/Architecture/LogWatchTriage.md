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
                          │ fingerprint  =  hash(category, exception, normalized message, top frame)
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

The fingerprint is `sha256(category, exception type, normalized message head, top application
frame)`, truncated to 16 hex characters. Four details make it collapse the right things and only
the right things:

- **The message is normalized** — guids, timestamps, paths (filesystem *and* mesh-node), quoted
  literals, hex blobs and bare numbers are masked. `rbuergi/Foo/7a2f…` and `acme/Bar/91bc…` are one
  fault, so a defect that every tenant hits opens one ticket rather than one per tenant.
- **The exception type is part of the identity.** The same log line can precede different failures;
  folding them together would hide the second bug behind the first one's already-filed ticket.
- **The top frame excludes framework code** and drops its `:line NNN` suffix, so an unrelated edit
  above the faulting line does not fork the fingerprint into a second ticket.
- **Namespace and pod are NOT in the fingerprint.** The same defect on two pods is one ticket; the
  pods are recorded on the incident instead.

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
| `Triage` | ingest (first sighting, or a repeat of a `New`/`Failed` incident) | marks `Triaging`, starts a LogTriage thread with the incident as `MainNode` |
| `File` | the triage agent | resolves the repository, opens the issue as the GitHub App, records `IssueUrl` |
| `Comment` | ingest (repeat of a `Filed` incident, at most once per `CommentInterval`) | comments the recurrence; reopens the issue first if it had been closed |
| `Suppress` | the triage agent, or an admin | marks `Suppressed` — occurrences keep counting, tickets stop |

Because the state lives in the mesh, a portal restart mid-triage strands nothing: the node still
says what it is waiting for and the next query emission picks it up.

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
az aks command invoke -g memex-aks-rg -n memexaks-cluster --command \
  "kubectl -n monitoring create secret generic mw-log-watcher --from-literal=ingest-token=$TOKEN"
#    …and set LogWatch__IngestToken to the same value on the portal (via its KeyVault secret).

# 2. Build and push the watcher image.
dotnet publish tools/MeshWeaver.LogWatcher/MeshWeaver.LogWatcher.csproj -c Release \
  -t:PublishContainer -p:ContainerRegistry=meshweaver.azurecr.io \
  -p:ContainerRepository=memex-log-watcher -p:ContainerImageTag=<tag>

# 3. Apply the Deployment + PVC.
az aks command invoke -g memex-aks-rg -n memexaks-cluster \
  --command "kubectl apply -f log-watcher.yaml" \
  --file deploy/aks/manifests/observability/log-watcher.yaml
```

Verify end to end:

```bash
az aks command invoke -g memex-aks-rg -n memexaks-cluster --command \
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
