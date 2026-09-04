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
                          │ fingerprint  =  hash(WHERE, WHAT, WHICH)
                          │     WHERE = top app frame, or (category, event id) with no frame
                          │     WHAT  = exception type
                          │     WHICH = masked exception message, or masked log message with no exception
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

## Line → burst: reconstruction is PER POD, and a bodyless burst is never a fault

A .NET console error is several *lines* — the `fail:` header, the message, then the stack trace —
and the CRI log format stamps **each line** with its own timestamp. The query is deliberately
namespace-wide (a line filter would return headers without their stack traces, `LokiQuery`), so what
comes back is every replica's output **merged by timestamp**. A burst whose header and trace fall in
different milliseconds therefore has any other pod's line from in between sorted right into the
middle of it.

🚨 **So the burst grouper reconstructs per pod, never over the merged sequence.** It used to end a
burst at the first line that came from somewhere else, and that is how production filed two
unactionable tickets in a week:

| Where the cut landed | What the incident carried | Filed as |
|---|---|---|
| after the header | nothing at all | #2222 — "an Error with no message, exception, or stack" |
| after the message | message, no exception, no frame | #2153 — "logs a bare Unexpected error with no exception attached" |

#2153 is worth reading twice: the call site *does* pass its exception to `LogError(ex, …)` and
always did. The exception was lost in the READER. `RedLogBurstReconstructionTest` pins both shapes on
the incidents' own lines.

**A burst that arrives bodyless anyway is not fingerprinted.** With no message, no exception and no
frame, its only possible identity is `(category, event id)` — a token that names a component and no
defect, into which every later bodyless capture from that site would fold. `BurstAggregator` keeps
those out of the reports and surfaces them on `BurstAggregation.HeaderOnly` instead, and the watcher:

- **recovers the recoverable ones** — a burst still open at the window's edge (`AtWindowEdge`) has
  its body on the other side of `end`, where the grouper has no header to attach it to. The cursor
  resumes **at that header** so the next poll reads the burst whole. Terminating by construction: the
  rewind lands the cursor *on* the header, so a burst that is still bodyless next time is genuinely
  bodyless and falls through;
- **reports the rest once per namespace**, naming the categories
  (`LogPipelineGap.HeaderOnlyReport`) — the same pattern as the truncated-window and lost-window
  findings. Nothing is dropped silently; what changes is that the finding is about the capture, which
  is what it actually is.

## One fault, one ticket

The fingerprint (`StructuralLogIncidentIdentity.Compute`) identifies **the fault, not the reporter**.
It is a `sha256` truncated to **16 hex characters** (the first 8 bytes) over three parts — *where*
the fault is, *what* it is, and *which* one it is:

| Part | Value | Why |
|---|---|---|
| **WHERE** | the top application stack frame — or `(category, eventId)` when the burst names no frame | the frame names the exact method that faulted; with no frame, the log site the code assigns is the only locator |
| **WHAT** | the exception type, by simple name | the same method can fail two ways, and folding those hides the second bug behind the first one's ticket |
| **WHICH** | the **masked exception message** — or the masked log message when there is no exception | inside one site there is nothing else left that says which fault this is |

- **🚨 The reporting category is NOT in the identity when a frame is present.** The category names
  the class that *caught and printed* the fault, which is not where the fault is. One exception
  unwinding through two catch sites is one defect however many of them log it — production
  2026-08-10 filed issues #1170 and #1171 for a single `ObjectDisposedException` raised at
  `SynchronizationStream<T>.OnCompleted()` during a single hub teardown, on one pod, at one instant,
  because `MessageHub` and `HostedHubsCollection` each logged it on the way out.
- **🚨 The discriminating text is the EXCEPTION's message, never the reporter's prose** — and that
  is what keeps #1170/#1171 folded while still splitting real defects. The two reporters worded
  their own messages differently ("Error during shutdown of hub …" vs "Hub … disposal faulted") and
  quoted the *same* exception message ("Cannot access a disposed object."). Only a burst carrying no
  exception at all falls back to the logged message, because then it is the only text there is.
- **🚨 Everything volatile is masked before hashing** (`LogLineParser.Normalize`): guids, timestamps,
  paths, quoted literals, hex blobs, labelled identifiers, bare numbers — **and any token the
  message itself uses as a path segment**. That last rule is what makes prose safe to hash at all:
  a message's subject can sit anywhere (`target: Claims` after a label, `[PluginGating] Chess:`
  before a colon), and no masking rule anticipates the next position. So this one does not guess —
  it reads the subject *out of* the message, from the paths the message already spells out. `Chess`
  is a path segment in `Chess/_Access/Public_Access`, therefore `Chess` anywhere in that message is
  an identifier.
- **The exception type is compared on its simple name**: `ex.ToString()` prints
  `System.ObjectDisposedException` while a message interpolating `ex.GetType().Name` prints
  `ObjectDisposedException`, and one fault must not fork on the caller's formatting. For the same
  reason the parser recovers the type *and its message* from the message text when the call site
  formatted the exception into it instead of passing it to the logger.
- **The top frame excludes framework code** (`System.` / `Microsoft.` / `Npgsql.` / `Orleans.`
  prefixes are skipped) and drops its `in /path/file.cs:line NNN` suffix, so an unrelated edit above
  the faulting line does not fork the fingerprint into a second ticket.
- **Namespace and pod are NOT in the fingerprint.** The same defect on two pods is one ticket; the
  pods are recorded on the incident instead — and so a defect every tenant hits opens one ticket
  rather than one per tenant.

### The two cases this has to get right

Both are measured, both from `memex-cloud` on 2026-08-17 (#1787), and
`ProdRedLogFixtureTest` pins them on verbatim production lines:

| Input | Result | Why |
|---|---|---|
| **3,894 lines of the SAME error** | **one** incident, `Occurrences = 3894` | everything that varies per occurrence — node paths, guids, counts, elapsed times — is masked out before hashing |
| **13 lines of 13 DIFFERENT errors** | **one incident per distinct failure shape** | thirteen NodeTypes parked at `CompileError` share a category, an event id, an exception type *and* a top frame; only the compiler diagnostics differ, and those are now in the key. Two nodes failing *identically* still share one ticket and list each other in its evidence — that is one defect with two instances. |

Before this, "same frame + same exception type" was the whole key, so all thirteen were **one**
fingerprint and none of them was ticketed; #1786 had to be filed by hand.

### The floor under "too fine": the per-site variant budget

Masking cannot anticipate every message shape, and when it misses, one defect fans out into one
ticket per subject — 2026-08-09 produced ~50 that way. So a log site that opens more than
`MaxVariantsPerSite` (default **20**) distinct fingerprints *in one window* stops being N incidents
and becomes **one**, keyed by `StructuralLogIncidentIdentity.ComputeSiteFold` and carrying
`Variants = N`. The ticket then says "this site produced N shapes and the masking rule needs a case"
instead of burying a human in tickets.

The default sits deliberately between the two numbers production has produced: **13 stays 13**
(each parked NodeType needs its own fix), **~50 folds**. The fold is per window and every
fingerprint it produces is stable, so recurrences still deduplicate.

The remaining trade is unchanged in direction: an under-split incident is one ticket a human can
split, an over-split one is fifty nobody reads. What identity must *never* do is discard the fault
site to make unrelated reporters agree — issues #1183 and #1184 (one logger, one event id, one
exception type, two different health checks) are two code sites and stay two incidents.

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

## The watcher tickets its own blind spots

🚨 **A watcher that cannot see is worse than no watcher, because it still reports "all quiet".** So
every way this one can fail to read a window is itself a `LogIncidentReport` travelling the normal
ingest path — landing in Postgres, which survives Loki being gone. All three dedup per namespace and
carry no timestamps in their fingerprint, so a repeat raises an occurrence count instead of opening
another ticket.

| Condition | Detected by | Severity | What it means |
|---|---|---|---|
| Loki answered a long, continuously-watched window with **zero lines** | `LogPipelineGap.IsLostWindow` | Critical | the store lost that stretch — the query is unfiltered, so a running portal cannot be that quiet |
| The query came back **at `QueryLimit`** | `LogPipelineGap.IsTruncated` | Error | the window was NOT fully read; the remainder is **deferred**, and while it lasts a noisy source crowds quieter errors out of the prefix that gets read |
| The cursor was **floored by `MaxCatchUp`** | `WatcherState.CursorFor` returns the skipped stretch | Critical | the only path that LOSES evidence outright — that stretch will never be read |

**🚨 Raising `QueryLimit` is not the fix for truncation.** The number in the watcher's log is a *cap*,
not a count: on 2026-08-17 several consecutive `memex-cloud` windows reported exactly `5000` and
nothing said so anywhere a verdict is read. A higher cap moves the ceiling; the finding is that one
namespace out-talks its watcher, and the actionable number is the **backlog** the report carries —
because a backlog that keeps growing ends at the `MaxCatchUp` floor, which is the row above that
loses data for good.

The per-window summary distinguishes the counts that used to be conflated:

```
memex-cloud: 5 distinct fingerprint(s) from 7 red burst(s) (2934 line(s) read)
memex-cloud: 3 distinct fingerprint(s) from 41 red burst(s) (5000 line(s) read — TRUNCATED at the query limit)
```

The old line read `"1 distinct fingerprint(s) from 5000 red line(s)"` with `5000` bound to the
**total** line count — the query returns every severity — so it looked like 5000 errors collapsing
onto one ticket when it was 5000 lines of mostly `info:`.

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
| `QueryLimit` | Max entries one Loki query may return. Default 5000. Hitting it is **reported as an incident** — see "The watcher tickets its own blind spots". Raising it is not the fix. |
| `MaxVariantsPerSite` | How many distinct fingerprints one log site may open from one window before they fold onto a single site-level incident. Default 20 — above 13 (the parked-NodeType case, which must stay 13 tickets) and below ~50 (the 2026-08-09 fan-out, which must fold). `0` disables the fold. |
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
# Expect: "Loki: N line(s) in <ns> …", then
#         "<ns>: F distinct fingerprint(s) from B red burst(s) (N line(s) read)",
#         then "Reported <fingerprint> (<category>) — 200".
# 🚨 If that line ever says "TRUNCATED at the query limit", the window was not fully read — read
#    the log-query-truncated-<ns> incident rather than raising QueryLimit.
```

Then browse `Admin/_LogIncident` in the portal — every incident links to the ticket it opened and
to the triage thread that wrote it.

## What belongs at `fail:` — a cancellation and a disconnect do not

Everything red becomes a ticket, so **the level a call site chooses is a ticketing decision**, not a
verbosity knob (AGENTS.md: never edit a level to dial a debugging session up or down; fix it
permanently, with the reason, or leave it). Three outcomes are routinely mistaken for faults, and
each produced a steady ticket stream:

| Outcome | Why it is not a fault | Where |
|---|---|---|
| **Cooperative cancellation** — the caller went away, a partition cleanup cascaded, the host is shutting down, the I/O pool drained | Nothing failed and nothing was written. `MeshWeaver.Mesh.CreateNode` logged 491 of these in three days (#2152); `MeshWeaver.Mesh.MeshNode` logged `[DeleteNode] unexpected … partial-deleted=0` (#2182) — a counter that says the node was never touched. | `CancellationClassifier.IsCooperativeCancellation` |
| **A client that disconnected mid-stream** | gRPC finalises the request and answers the next write with `InvalidOperationException("Can't write the message because the request is complete.")`; the client is already gone (#2138 / #2139). | `MeshGrpcService.WritePumpAsync` |
| **Hosted-hub creation that raced pod teardown** | A hub activates while its pod is stopping and the Autofac scope dies UNDER the build (`LifetimeScope.ThrowDisposedException`). Nothing failed, nothing was written, and the next access re-activates the node on a live host (#3243). | `HostedHubsCollection.CreateHub` → `HostedHubOutcome.HostShuttingDown` |

🚨 **The rule is narrow on purpose, and the exceptions to it are the point:**

- **`catch (OperationCanceledException)` is NOT the rule.** A **timeout** raised on a token is the
  same CLR type and IS a fault. .NET marks it by hanging a `TimeoutException` off the cancellation
  (an `HttpClient` timeout is verbatim `TaskCanceledException(…, new TimeoutException())`), and
  `CancellationClassifier` refuses to call that benign. Classifying by the CONDITION rather than the
  type is what keeps "cancellation is benign" from silently becoming "storage timeouts are benign".
- **A cancellation that arrives AFTER work landed stays LOUD.** A delete cancelled with
  `partial-deleted > 0` really did leave the subtree torn — that is the case the old wording was
  borrowing its urgency from, and it keeps `Error` plus the count.
- **The caller is still answered, and answered accurately.** Both handlers reply with the
  `Unavailable` rejection reason ("not evaluated; retrying is meaningful"), never `Unknown`, and an
  error string that says *cancelled* rather than *unexpected error*. Downgrading a level is never
  licence to swallow an outcome.
- **The exception still rides along.** The benign line is `LogDebug(ex, …)`, and it states the token
  state that made it benign rather than asserting it (`CancellationClassifier.Describe`).
- **A caller cannot classify what it cannot see, so the condition TRAVELS.** `GetHostedHub` answered
  `null` for a shutdown and for a configuration that threw alike, so `MessageHubGrain` logged one
  fail-level sentence listing both and committing to neither — a message that was *honest about its
  own ignorance* and ticketed a pod rollout every time. `HostedHubResult` carries the outcome from
  the collection that owns the container to the grain that writes the line (#3243); re-deriving it
  at the caller would have been a guess.
- **And the benign verdict is MEASURED.** An `ObjectDisposedException` out of hub construction is
  only a shutdown if the container really is gone, so the container is asked — directly — whether it
  can still resolve. A live scope that threw that type for its own reasons still reads as a fault,
  and a probe that cannot answer leaves the outcome LOUD.

`CancellationIsNotAFaultTest` pins the cancellation directions, including the timeout impostor;
`HubCreationDuringTeardownIsNotAFaultTest` and `HubConstructionOutcomeReportingTest` pin the
hub-creation ones, including the configuration-throw that must stay red.

## What this is not

- **Not an alerting system.** A provisioned Grafana rule covers the "tell a human now" case
  (the rule is bound to a specific Grafana and set of namespaces, so it lives with the
  deployment it describes, not here). Ticketing
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
