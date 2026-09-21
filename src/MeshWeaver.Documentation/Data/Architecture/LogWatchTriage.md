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

🚨 **Which means `namespace` on an incident is FIRST-SEEN, while `pods[]`, `occurrences`, `lastSeen`
and `samples[]` keep folding — across DEPLOYMENTS.** The auto-filed issue prints that stale
`namespace` in its evidence table as though it described every occurrence, and it is the first thing
a reader uses to pick which portal to go and look at. Measured on the control instance,
2026-09-11 — `Admin/_LogIncident/c0b1424c7beb28e0`, `content.namespace: "memex"`, `occurrences: 62`:

| pod, from that incident's `pods[]` | actually in namespace | established by |
|---|---|---|
| `memex-portal-deployment-7f74766b8d-rltnt` | **`memex`** | `Ops/Logs/memex-1789108640040221110-766b8d-rltnt` — `deployment: memex`, selector `{namespace="memex"}` |
| `memex-portal-deployment-6d7497cb58-jc296` | **`memex-cloud`** | `Ops/Logs/memex-cloud-1789104559148633219-97cb58-jc296` — `deployment: memex-cloud`, selector `{namespace="memex-cloud"}` |

Both portals, one incident, one namespace label. And because the normaliser masks the varying node
path (`Failed to compile assembly for node '{value}'`), that incident's ten most recent `samples[]`
name `MeshWeaver/samples/Graph/Data/Type/article`, `…/Northwind/Product` and `Hosting/InstanceAction`
— **not** the `BinaryClickerV2/BinaryToggle` the issue it opened ([#3883](https://github.com/Systemorph/MeshWeaver/issues/3883))
is titled after and whose last recorded occurrence is still 2026-09-10 02:30:43Z.

> **So `occurrences` counts the FINGERPRINT, not the defect named in the title, and `lastSeen` is
> the last time ANY member of the group fired on ANY portal.** A rising counter on an auto-filed
> issue is not evidence that its headline defect is still live; read `samples[]` — the unmasked node
> paths are in there — and resolve each `pods[]` entry to a deployment before naming a portal.

### 🚨 A REOPEN is not a recurrence — read `samples[]` before you believe it

`ReopenOnRecurrence` reopens a **closed** issue when its incident fires again. On a folded
fingerprint that sentence is doing something the reader does not expect: the thing that fired again
may be **a different call site**, and the issue it reopens is the one that happened to be filed from
that fingerprint first. The reopen comment then prints recent log lines — from the *group*, not from
the headline defect — and reads, convincingly, as *"your fix did not work"*.

Measured 2026-09-13/14, two issues reopened the same minute by the same watcher, neither of them
about what reopened it:

| issue | its own subject | what the reopen actually carried |
|---|---|---|
| [#2387](https://github.com/Systemorph/MeshWeaver/issues/2387) — category `MeshWeaver.PluginCatalog.InstanceAutoRegistrationService` | `[DefaultInstall] reconciled with FAILURES … FAILED: [Import]` — last seen **2026-09-02T07:43:52Z** | `Package Feedback … 1 of 14 declared node(s) are ABSENT … REPAIRED rather than skipped` — a **different method** in the same class |
| [#1840](https://github.com/Systemorph/MeshWeaver/issues/1840) — category `MeshWeaver.Graph.Configuration.MeshNodeCompilationService` | `…/Northwind/AnalyticsCatalog` — a node that **no longer exists** on that portal | `rbuergi/OperationRequest`, `Hosting/InstanceAction` — unrelated NodeTypes |

🚨 **The line that reopened #2387 was read as a SUCCESS on 2026-09-14 — and that reading was wrong.**
`Feedback/Feedback/Source/FeedbackHandover` was named ABSENT at 22:02:37.818Z and its `lastModified`
in the mesh was 22:02:45.036Z, eight seconds later. The write landed; the repair did not **hold**.
Re-measured on memex.meshweaver.cloud 2026-09-16, the same node, at the same module version
(`caffd87567c65b4f`), was named ABSENT on **eleven boots** — 2026-09-13T22:02Z through
2026-09-16T21:33Z, each matched one-to-one by a `Plugins/Feedback` record version (v18…v28) — and at 22:05Z,
thirty minutes after the last "repair", `get` answered `Not found`. What removed it each time is the
every-boot flap [the Sync-Ref Contract](../SyncRefContract) predicted for two unattended writers of
one partition: `Feedback/_GitSync` imports the partition at the sealed Plugins commit `627fb3cd`,
whose tree has no `FeedbackHandover.cs`, and prunes it (imports at 21:31:58Z, 21:32:01Z and
21:54:40Z that day); the boot default install lists `Plugins@main` (`installedFromRef: main`) and
writes it back (record v28, 21:33:24Z). The control instance, which has no `Feedback/_GitSync`, holds
the node. The portal ran core `c84c6c05` (2026-09-12), which carries neither the seal-pinned boot
install (#4259, PR #4292) nor the one-bookkeeping rule (#4355, PR #4364) — so every one of those
reopens was a delivery gap, not a recurrence, and not a self-heal either.

The emitter was changed on the strength of the first reading (#4257: the detection is now a Warning
and the Error moved onto an outcome read taken right after the install), and the rule it teaches
stands — but only with the second half that this measurement adds:

> **Never assert a severity on a DETECTION when a remedy is about to run.** A line that says "X is
> missing, repairing" is an `Error` about a condition that is often gone by the time anyone reads
> it. Log the detection at Warning, re-observe after the remedy, and put the `Error` on the outcome.
>
> 🚨 **But an outcome read taken right after the remedy cannot see a writer that undoes it later.**
> It proves the write landed, never that it held. The signature of a remedy that did not hold is the
> DETECTION REPEATING at an unchanged version on consecutive runs — and once the detection is a
> Warning and the outcome reads "landed whole", nothing ships that signature to an **incident**: the
> Warning is still in the pod log and still readable through a `Logs` action, but this pipeline
> ingests `fail:`/`crit:` only, so nothing files it, folds it or reopens on it. On an image carrying
> #4257 without #4292 the flap above would have kept running with no ticket anywhere. Before calling
> a repair successful, look for the same detection on the NEXT run.

🚨 **That severity difference is also how you attribute an occurrence to an IMAGE.** The pre-#4257
line is `fail:` and ends *"…so the install is being REPAIRED rather than skipped (MeshWeaver#3485)."*;
the post-#4257 line is a Warning and ends *"…Whether the repair worked is reported separately, once
it has (MeshWeaver#3485)."* So a sample carrying the first sentence was emitted by an image that does
not contain `83cb0dd932`, whatever its pod name says — measured on this incident 2026-09-16: all ten
retained samples (occurrences 54→63, through 22:54:35Z) carry it, and the only fleet portal without
that commit was memex.meshweaver.cloud on core `c84c6c05`. **Read the LINE for the image, the
`namespace`/`pods` fields for neither** — both portals name their deployment
`memex-portal-deployment`, so the pod suffix discriminates nothing.

**So the procedure on a reopened auto-filed issue is:**

1. Read the incident node's `samples[]` — **not** the issue body's evidence table, which was written
   when the issue was filed and may never have been updated (see the stuck-syncer note above).
2. Ask whether the newest samples are the **same call site** as the title. On a category fold they
   often are not.
3. Only if they are, treat it as a recurrence. Otherwise the reopen is routing traffic, and the
   right move is to say so on the thread, close on the headline defect's own evidence, and file the
   traffic where it belongs.

🚨 **Fixing the identity function does not clear this.** Changing how fingerprints are computed
re-fingerprints **future** occurrences only; existing `Admin/_LogIncident/*` nodes keep the identity
they were minted under, and nothing recomputes them. So after an identity fix, folded traffic keeps
arriving on the same old threads until that backlog is migrated — which is its own change.

> **That change has since landed** (MeshWeaver.Plugins#1796, 2026-09-18) and the backlog is being
> re-addressed on every fold. The paragraph above is the pre-migration world; read
> *The corpus RE-ADDRESSES itself now* below before acting on it, because whether a node has been
> migrated yet decides whether closing its issue holds.

Re-measured 2026-09-11T09:1xZ, same incident, and the spread had widened rather than settled:
`occurrences: 62`, **26** entries in `pods[]`, `firstSeen` 2026-09-10T02:26:49Z,
`lastSeen` 2026-09-11T05:22:40Z. All **ten** retained `samples[]` are one of three node paths —
`MeshWeaver/samples/Graph/Data/Type/article` (CS0246 `Article`, *Matched Code nodes (0)*),
`MeshWeaver/samples/Graph/Data/Northwind/Product` (CS0246 `Supplier`/`Category`) and
`Hosting/InstanceAction` (CS0103 `ObserverExpiryTests` ×1307) — and **none** is the
`BinaryClickerV2/BinaryToggle` the issue is titled after. Note the three carry *different* Roslyn
error codes from the CS1929 the normaliser recorded: the masked template keeps `CS1{n} Error … does
not contain a definition for …`, yet CS0246 and CS0103 lines fold into it anyway, so the fingerprint
is broader than its own `normalizedMessage` reads.

🚨 **And the issue's evidence table does not track the incident, because the sync is erroring.** That
incident carries `occurrencesAtLastComment: 2` beside `error: "Resource not accessible by
integration"` — the GitHub write has been failing since the issue was filed, so #3883 still prints
*Occurrences 2 / Last seen 2026-09-10 02:30:43Z*. Here that staleness is a mercy: those two really
were the only `BinaryClickerV2/BinaryToggle` compiles in the record. Had the sync worked, the issue
would now claim 62 occurrences across 26 pods and two portals for a node that has not been seen
since. **A stuck syncer and an accurate counter are indistinguishable from the issue page** — check
`error` and `occurrencesAtLastComment` on the incident before quoting either number.

### 🚨 When a fingerprint OUTLIVES its issue: repoint the link, never `Suppress`

Step 3 above ends "file the traffic where it belongs". When the traffic already *has* a home, that
is still not the end of it: the incident goes on carrying the **original** issue's number, so every
future fold reopens the wrong ticket — indefinitely, and with a comment that reads as *"your fix did
not work"*.

Measured on [#1134](https://github.com/Systemorph/MeshWeaver/issues/1134), fingerprint
`9ca334c1e8dad9ca` (category `Polly`). The issue was filed on timeout events that could not be
attributed to any call path — `Source: '-standard//Standard-AttemptTimeout'`, the empty client name
of the ONE shared `ConfigureHttpClientDefaults` pipeline. #1133/#1137 fixed that by re-registering
the registry clients by name (`ServiceDefaults.AddServiceDefaults`). `samples[]` is a ROLLING window
— it holds the last `MaxSamples` lines, not the history — so it can say what the events carry NOW,
never that every occurrence since a fix was attributed: read 2026-09-15, all ten name a pipeline
(`plugin-registry-standard//…`, one `plugin-registry-bundles-standard//…`) and none carries the
unattributed form. What
keeps folding onto the fingerprint is the registry LATENCY *behind* those timeouts —
[#4222](https://github.com/Systemorph/MeshWeaver/issues/4222)'s subject, not #1134's, because the
normalizer masks the `Source:` value and every Polly `OnTimeout` on every pipeline shares one
identity. The issue was closed on its own evidence on 09-13 and again on 09-14; the watcher reopened
it both times within hours. Three sessions re-derived that before anyone changed the field.

**The redirect is two fields on the incident**, and the watcher's own code is what makes it safe:

| Code | Why the repoint is safe |
|---|---|
| `NextRequest`: `{ IssueNumber: not null } => Comment` | the link is read from the LIVE node on every fold, never cached — a new value takes effect on the next recurrence |
| `ClaimRequest`: a `File` on a ticketed incident is granted as `Comment` | repointing can never mint a second issue, whatever the status says |
| `LogIncidentFiler.Comment` → `Reopen` | reads the TARGET issue and reopens it when closed **while `ReopenOnRecurrence` is on** (the default), so the traffic arrives as a reopen of the ticket that owns it — the notification you actually want. With the option off the recurrence still comments on the new target; it simply does not reopen it, so the redirect lands either way |
| `OccurrencesAtLastComment` / `LastCommentedAt` are not touched | the first comment on the new target continues the count instead of restarting it |

```jsonc
// patch @Admin/_LogIncident/9ca334c1e8dad9ca   — applied 2026-09-15, v1641 → v1642
{ "content": { "issueNumber": 4222,
               "issueUrl": "https://github.com/Systemorph/MeshWeaver/issues/4222" } }
```

🚨 **`Suppress` is the wrong tool here and the damage is silent.** It stops tickets while occurrences
keep counting — which destroys the very instrument the receiving issue's closing condition names
("this incident's `occurrences` stops advancing over a week with the consumer still polling").
Suppress is for a line that should never have minted an incident. A fault that still fires and still
matters wants an issue — just not *that* one.

**What the redirect is not.** It does not re-fingerprint anything (the paragraph above holds: the
node keeps the identity it was minted under), and it does not merge the two histories — the old
issue keeps its own evidence table and its own close. Say what you did on **both** threads: the
incident node is the only place the link lives, and a reader who finds the new ticket reopening from
a fingerprint filed under an old title has no other way to know why.

### 🚨 The corpus RE-ADDRESSES itself now — so read `status` before deciding a close will hold

The paragraph above ("Fixing the identity function does not clear this") described the state of the
world until 2026-09-18. It no longer holds, and the difference decides whether closing a folded
bucket is durable or theatre. **MeshWeaver.Plugins#1795** narrowed the identity function and
**#1796** — *"existing `Admin/_LogIncident/*` nodes carry a fingerprint from an older identity
function, so a corrected identity cannot close the issues it fixes"* — added the migration that
carries the history across. Both are closed (#1795 on 09-13, #1796 `completed` on 09-18), and the
re-addressing starts as soon as a portal runs an identity newer than the deployed watcher's.

**It is keyed on the reporter's own fingerprint, never on a recomputed one.** The watcher's report
says which id *its* generation computed (`reporterFingerprint`); the portal computes its own; when
they differ, `LogIncidentCorpusMigration.Fold` carries the legacy node's counts, window, pods,
evidence and shape ledger onto the successor and `Supersede` marks the legacy
`Status = Superseded`, `SupersededBy = <successor>`. Recomputing a legacy node's identity from its
stored fields was rejected deliberately: mint-time masking and the retained fields have drifted
apart, so the stored fields no longer reproduce what was hashed. **The migration therefore runs on a
FOLD** — it needs a live burst to carry the exact id — so a bucket that has gone quiet keeps its old
identity until it fires again.

🚨 **A superseded node is INERT, and that is what makes a hand-close stick.**
`LogIncidentIngestService.NextRequest` answers `{ Status: Superseded } => LogIncidentRequest.None`
before every other rule: no triage, no comment, and **no `ReopenOnRecurrence`**, ever again. So the
three-month "closed on evidence → reopened by the watcher → closed again" cycle that #1134, #1840 and
#2387 each ran is ended by the supersede, not by a better closing argument.

**Two outcomes, and they mean opposite things for the old issue.** The ticket moves **only into an
empty seat** (`target.IssueNumber is null`), and it brings the link, the comment budget, the draft
and the triage thread with it:

| After the fold | The legacy issue | What a later reopen means |
|---|---|---|
| successor had **no** issue ⇒ it **inherits** the legacy's | still the fault's one ticket, now addressed by a NARROW identity | genuinely actionable — one shape, not bucket traffic |
| successor **already** had one (it filed its own, or a second bucket split onto it) | **orphaned but inert** — it keeps `issueNumber` and will never speak again | impossible; the issue must be closed BY HAND |

The second row is the one that reads wrong. `Admin/_LogIncident/c0b1424c7beb28e0` (2733
occurrences, the bucket behind [#3883](https://github.com/Systemorph/MeshWeaver/issues/3883)) was
superseded by `e60160b647aa00b4` at 2026-09-19T13:02:25Z; the successor had already filed
[#4876](https://github.com/Systemorph/MeshWeaver/issues/4876), so #3883 kept its link and went
silent. Nothing announces that on the thread — the issue simply stops being reopened — and a reader
who takes continued silence for "the watcher agrees it is fixed" has it backwards: the watcher is no
longer looking at that node at all.

**So before closing an auto-filed issue, read the incident's `status`:**

- `Filed` and still folding ⇒ the close WILL be reverted on the next recurrence. Either wait for the
  supersede, or repoint the link by hand as the section above prescribes.
- `Superseded` ⇒ the close holds. Say so on the thread and name `supersededBy`, because the
  successor is where the traffic now lives.

The wave is not hypothetical and it is not slow: measured on the control instance 2026-09-19,
`search 'namespace:Admin/_LogIncident scope:children nodeType:LogIncident content.status:Superseded'`
returned **48**, `truncated: false`, every one re-addressed that day between 11:41Z and 18:48Z —
including `9ca334c1e8dad9ca`, the Polly bucket the redirect section above is written about.

🚨 **And `shapes[]` is now the instrument `samples[]` could never be.** `RecordShape` keeps a
per-defect ledger — what the CURRENT identity computes for each burst alone, with its own
`firstSeen`, `lastSeen` and `occurrences`, the `MaxShapes` (12) most recent kept and the rest counted
in `ShapesEvicted`. That answers the question a bucketed incident previously could not: *has THIS
shape been seen since the fix?* `samples[]` is a rolling evidence window and can only say what the
last few bursts carried; a shape row says when that particular fault was last seen. Read `shapes[]`
first and fall back to `samples[]` only for a node minted before the ledger existed.

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
every way this one can fail to read a window — or to deliver what it read — is itself a
`LogIncidentReport` travelling the normal ingest path, landing in Postgres, which survives Loki being
gone. All of them dedup per namespace and carry no timestamps in their fingerprint, so a repeat
raises an occurrence count instead of opening another ticket.

| Condition | Detected by | Severity | What it means |
|---|---|---|---|
| Loki answered a long, continuously-watched window with **zero lines** | `LogPipelineGap.IsLostWindow` | Critical | the store lost that stretch — the query is unfiltered, so a running portal cannot be that quiet |
| The query came back **at `QueryLimit`** | `LogPipelineGap.IsTruncated` | Error | the window was NOT fully read; the remainder is **deferred**, and while it lasts a noisy source crowds quieter errors out of the prefix that gets read |
| The cursor was **floored by `MaxCatchUp`** | `WatcherState.CursorFor` returns the skipped stretch | Critical | one of the two paths that LOSE evidence outright — that stretch will never be read |
| Red bursts arrived as a console **header and nothing else** | `BurstAggregator` → `LogPipelineGap.HeaderOnlyReport` | Error | a capture with no body; never fingerprinted, because `(category, eventId)` names a component and no defect |
| The portal **permanently rejected** a report | `LogIncidentDelivery.IsPermanent` → `LogPipelineGap.RejectedReport` | Critical | the other path that loses outright — that report is gone from the queue and its red log will never be ticketed |
| …and the **portal's own** record of the same refusal | `LogIncidentEndpoints.PermanentRefusal` → `LogIncidentDelivery.RefusalReport` | Critical | written by the ingest endpoint, not the watcher — the only one that survives a contract skew |

### 🚨 A rejected report is DESTROYED, so the destruction is a finding

`LogWatchWorker.Deliver` removes a permanently-rejected report from the durable queue exactly as it
removes an accepted one. That is correct — a payload the portal will not take cannot be fixed by
resending it, and keeping it would block every report behind it forever. What was **not** correct is
what the removal used to leave behind: one `LogError` in the watcher's own pod, in namespace
`monitoring`, which this watcher does not read, which no `Hosting/InstanceAction` can target (there
is no `Deployments/mw-log-watcher` record — see the vintage section below), and which nothing else in
the fleet reads either. **A red log that reached the pipeline and then fell out of it produced the
same observable result as a red log that never happened.** That is this subsystem's worst failure
shape — *the absence of a ticket reading as the absence of a fault* — sitting on the one path whose
job is to make faults visible.

Three properties make the finding sound, and each is pinned by `RejectedReportIsTicketedTest`:

- **It is not subject to the cause it reports.** It copies none of the rejected payload's samples
  forward (so a 413 cannot recur), and it carries a fingerprint and a category by construction (so
  the 400 for a missing one cannot recur).
- **It stops at one.** `LogPipelineGap.IsRejectionFinding` is a guard, not a budget: a finding about
  a rejected finding would be refused for the same reason and mint its own successor, growing the
  queue fastest exactly when the portal refuses everything.
- **The swap is ONE durable write** (`WatcherState.Replace`). Removing the report and appending the
  finding as two persists leaves a gap holding neither, and a crash there loses the red log *and* the
  record that it was lost — this defect, one level down.

### 🚨 …and the portal files its OWN record, because a log line cannot escape a contract skew

The finding above covers one bad payload. It cannot cover the case worth catching. The watcher ships
as a separate image on its own cadence — [#2681](https://github.com/Systemorph/MeshWeaver/issues/2681)
ran **35 days** behind the portal it reported to — so once its report *shape* has drifted from
`MeshWeaver.Observability.Contract`, it can POST nothing the portal will take, its own finding
included.

Leaving the fact in a log line does not rescue it either, and this is worth spelling out because it
looks like it should: the portal's log **is** what the watcher reads, so the obvious move is to log
loudly and let the pipeline pick it up. Follow it through and it is a loop — the watcher reads the
line, builds a report from it, and has *that* refused too, forever, persisting nothing.

So `LogIncidentEndpoints.PermanentRefusal` **writes the incident itself**, through the
`ILogIncidentIngest` seam it has already resolved, on a path that does not travel through the watcher
at all. It names no field of the refused payload, so it cannot be refused for the reason it is
reporting, and a failed write degrades to a Warning rather than turning a 400 into a 500. The Error
log stays — as what a reader of the portal's log sees, not as the mechanism.

🚨 **Its fingerprint is `log-ingest-refused-{ns}`, deliberately NOT the watcher's
`log-report-rejected-{ns}`.** They are two facts with two observers: the watcher knows *which* report
it lost, the portal knows it *refused* one, and only the second is obtainable when the watcher cannot
serialise anything the portal accepts. Folding them would also hand whichever arrived first the
incident's `normalizedMessage` — the swallowing this subsystem already has a production instance of
(see the section above).

Both findings state their scope conditionally, for the same reason: **one refusal proves that one
report is gone and nothing about the rest.** A single occurrence is a payload the portal could not
take; an occurrence count that keeps climbing is the drift, and red-log ticketing is down for that
namespace until the images agree. The per-namespace, timestamp-free fingerprint is what puts that
discriminator in front of the responder.

🚨 **The permanence rule itself lives in the contract** (`LogIncidentDelivery.IsPermanent`), not on
either side. The consequence of a status is a *joint* fact: the portal picks the number, the watcher
decides from it whether to keep the report or destroy it. Two copies drift in the one direction that
costs data — and since the watcher is a separately shipped image with its own cadence, that drift is
this subsystem's normal operating condition, not a hypothetical. Same argument as
`LogIncidentReportSanity`: a classification only one side enforces is not enforced.

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

## Reading which watcher VINTAGE is running, without a cluster

🚨 **The running watcher's image tag is not readable from the portal, and the capture-gap incident
answers the question better anyway.** Measured 2026-09-13, all three portal instruments refuse for
the same structural reason — they are scoped to an *instance*, and the watcher is not one:

- there is no `Deployments/mw-log-watcher` record, so `Sample` (which is what reports per-replica
  images) has nothing to target;
- `Logs` builds its stream selector from the deployment record's own namespace
  (`LokiQuery.ForNamespace`; every executed action's `logQl` field reads `{namespace="memex"} …`) and
  the `query` field is only the pipeline appended after it — the watcher runs in `monitoring`;
- `Audit` is likewise scoped to the instance's own helm release.

So a tag read is `kubectl`, i.e. break-glass. Do not reach for it: the per-namespace capture-gap
incident carries the answer, and carries it as a fact about the binary that is actually reporting
rather than about the image a Deployment names.

**The discriminator is textual, and the two sides cannot forge each other's wording** — they are
emitted by two different assemblies:

| on `Admin/_LogIncident/log-burst-header-only-{namespace}` | a CURRENT watcher (`LogPipelineGap.HeaderOnlyReport`) | the PORTAL refusing a bodyless report (`LogIncidentReportSanity.AsCaptureGap`) |
|---|---|---|
| `content.severity` | always `Error` | `max(reported, Error)` — so `Critical` is possible |
| `content.normalizedMessage` ends | `…being dropped between the pod and the log store.` | `…and the log store — or an out-of-date log watcher is still fingerprinting headers.` |
| `content.samples` | `"N bodyless red burst(s) in [start, end) — categories: …"` | the raw header lines **plus** a `refused undiagnosable report <fp> for category <cat> — no message, no exception, no stack frame` line |

A fold carrying the portal's wording proves the reporting binary predates the `BurstAggregator`
header-only holdback, because a current watcher never POSTs a bodyless report at all. That is the
reading [#2681](https://github.com/Systemorph/MeshWeaver/issues/2681) turns on.

🚨 **A `lastSeen` that merely stops advancing is NOT the same reading.** It is equally "the watcher is
current" and "the watcher stopped reporting" — and telling those apart is the entire point of a
capture-gap incident. Require the message-ending AND the sample shape together; either one alone can
be produced by a stale node nobody has folded into recently.

### 🚨 The exact instrument: RECOMPUTE the fingerprint, and the binary dates itself

The textual discriminator above says *which side* filed the fold. It does not say *how old* the
watcher is, and it only works on a capture-gap incident. **The fingerprint does both, exactly**, and
it works on any incident that carries one.

`StructuralLogIncidentIdentity.Compute` is a pure function of the burst, so its payload is a dated
artefact of the binary that computed it. Take the fingerprint out of a refused report's evidence line
— `refused undiagnosable report <fp> for category <cat> …` — and test the candidate payloads.

The 2026-08-09 payload is `{category}\n{eventId}\n{discriminator}`, and the discriminator switches on
the PAIR `(exceptionType, topFrame)` — four branches, all four of which occur in the live record:

| payload hashed (`sha256`, first 8 bytes, lower hex) | in force |
|---|---|
| `{category}\n{eventId}\n` — neither present | 2026-08-09 (core `8115e39425`, "Stop deriving incident identity from prose") |
| `{category}\n{eventId}\n{exceptionType}` | same change, exception but no application frame |
| `{category}\n{eventId}\n{topFrame}` | same change, **frame but no exception type** |
| `{category}\n{eventId}\n{exceptionType}\|{topFrame}` | same change, both |
| `site\n{category}\n{eventId}\n{fault}\n{detail}` — or `frame\n{frame}\n{fault}\n{detail}` | current |

🚨 **The frame-only row is easy to miss and it addresses one of the busiest ids in the record.**
Measured 2026-09-19, `Admin/_LogIncident/92c0e9442e275f65` ([#3110](https://github.com/Systemorph/MeshWeaver/issues/3110)):

```
SHA256("MeshWeaver.Messaging.MessageService\n0\n"
     + "MeshWeaver.Messaging.MessageHub.HandleMessageAsync(IMessageDelivery delivery, "
     + "AsyncDelivery[] ruleChain, CancellationToken cancellationToken)")[..8]  = 92c0e9442e275f65
```

No exception type in the key at all — so that one id addresses **every fault of every type that
surfaces inside `MessageHub.HandleMessageAsync`**, the most-travelled method in the mesh. An issue
filed from such an id is titled after whichever fault arrived first, and two occurrences of it can
share nothing but the method they died in.

Measured 2026-09-14 against `Admin/_LogIncident/log-burst-header-only-{memex,memex-cloud}` on the
control instance, every refused fingerprint on both namespaces reproduced from the **first** row:

```
SHA256("Orleans.Messaging\n100071\n")[..8]                                       = ce8d2e8715bf9aa0
SHA256("MeshWeaver.Hosting.PostgreSql.PostgreSqlPartitionedMeshQuery\n0\n")[..8]  = 5d52ad4396af9a59
SHA256("MeshWeaver.Hosting.PostgreSql.PostgreSqlChangeListener\n0\n")[..8]        = fc4f22eb5cffabd0
SHA256("MeshWeaver.Hosting.Orleans.RoutingGrain\n0\n")[..8]                       = e4a97855ab595beb
```

4 of 4, across two namespaces and three weeks — while the current payload would have produced
`199e5a2f690b9c89`, `4e297103d4d86c81`, `bddb036580fac948`, `4ec310c5270ddb0e`, none of which appears
anywhere. **So the binary that COMPUTED these fingerprints predates every identity change since
2026-08-09, and therefore predates the 2026-08-25 header-only holdback** — from data the portal
already holds, with no cluster read and no image tag.

🚨 **That dates the PRODUCER, not by itself the running image, and the difference is this watcher's
own design.** Reports are fingerprinted and queued to disk *before* delivery — deliberately, so a
crash costs a redelivery rather than an un-ticketed error ("Delivery guarantees" above) — so a
current watcher draining a pre-update backlog delivers old-format fingerprints while running new
code. That is one of the three alternatives
[#2681](https://github.com/Systemorph/MeshWeaver/issues/2681) itself lists, and the hash cannot
distinguish it.

**What closes the gap is the report's own timestamps, because a report is fingerprinted at QUEUE
time.** A window is read *after* the lines in it exist, never before, so the binary that fingerprinted
a report was running **at or after** that report's `lastSeen`.

🚨 That is a **one-sided** bound, and one-sided is all the delivery guarantees support. `IngestLag`
is the margin the watcher subtracts when choosing a window's upper bound — not a promise that
Promtail and Loki have delivered by then — and a store backlog or watcher downtime pushes processing
arbitrarily later, which is exactly what `MaxCatchUp` and the skipped-window finding exist for. So do
not read it as "processed within a poll interval of the line". One-sided is enough here, because
later only strengthens the conclusion: whenever that window was read, an old-format binary was doing
the reading, and that cannot have been before the lines existed.

Measured 2026-09-14: `log-burst-header-only-memex` carries the 2026-08-09 payload over lines stamped
`14:24:01Z`–`14:24:18Z` that same day — so an old-format binary was running **on or after** that
instant, about 18 hours after the holdback image was published. A queued backlog cannot account for
it either: a report cannot be older than the lines it contains.

> **So the instrument is the PAIR — the fingerprint's payload format and the report's own
> `lastSeen` — never the fingerprint alone.** With a stale `lastSeen` the honest reading stops at
> "whatever produced this predates 2026-08-09", and dating the running image then needs rollout
> evidence or the watcher's queue.

Two things this is good for beyond dating:

- **Settling a reopen without re-investigating.** An auto-filed incident issue reopens on every fold.
  If the newest sample's fingerprint still reproduces from an *old* payload, the reopen is the stale
  binary and there is nothing to investigate. If it does **not**, that is a genuine regression.
- **Telling a fingerprint FOLD from a recurrence** — the same job the
  [reopen section](#-a-reopen-is-not-a-recurrence--read-samples-before-you-believe-it) describes, done
  arithmetically instead of by eye.

### 🚨 One log SITE holds TWO buckets, and the second one's TITLE reads like a per-caller ticket

Because the discriminator switches on the pair, a site that emits *both* shapes — some lines with an
exception body attached, some bodyless — takes **two different branches of the table above** and mints
**two** fingerprints, each of which then files its own GitHub issue about the same defect class.
Measured 2026-09-19 on memex.systemorph.com,
`MeshWeaver.Hosting.PostgreSql.PostgreSqlPartitionedMeshQuery` has exactly two, both reproducible
locally (the site logs no application frame either way, so these are the exception-only and
neither-present rows):

| fingerprint | payload (branch) | issue | what folds onto it |
|---|---|---|---|
| `d4c8f6f74ecfa422` | `{category}\n0\n{UnanchoredQueryException}` — exception, no frame | [#3545](https://github.com/Systemorph/MeshWeaver/issues/3545), titled `nodeType:*Post` | every unanchored query **with** the exception body, whatever the caller |
| `5d52ad4396af9a59` | `{category}\n0\n` — neither present | [#4443](https://github.com/Systemorph/MeshWeaver/issues/4443), titled `nodeType:Skill` | every **bodyless** one, whatever the caller |

`search 'namespace:Admin/_LogIncident nodeType:LogIncident content.category:*PartitionedMeshQuery* select:name,lastModified limit:50'`
→ `count: 2`, `truncated: false`, `coverage.partitions: ["admin"]`. Two buckets, two tickets, one site.

🚨 **So a per-caller-looking TITLE is not evidence that a per-caller identity shipped.** #4443 names
`nodeType:Skill` because that is the sample it happened to be filed from; its bucket is the whole
site. #3545's thread had been waiting since 2026-09-13 for the identity that keeps a `nodeType:`
term (`{types:…}`), and the check that settles it is arithmetic: the per-caller payloads for that
very sample — `{category}\n0\n{types:Skill}` → `45d3d07500163cd1`, `{category}\n0\nnodeType:Skill` →
`ed08112634d8759d` — appear **nowhere**, while the bare payload reproduces `5d52ad4396af9a59` byte
for byte. Control on a second site, same day: `SHA256("Polly\n0\n")[..8] = 9ca334c1e8dad9ca`, which
is `Admin/_LogIncident/833c5f2e6337f4d3`'s `reporterFingerprint` verbatim. The **reporter** still
runs the 2026-08-09 function.

### 🚨 The portal RE-ADDRESSES a reported fingerprint — so a frozen `lastSeen` is NOT a stopped fault

Since 2026-09-19 the portal recomputes the identity of every report it accepts and keeps its own
node, folding the reporter's id into it. The new fields are the tell — measured on nodes created
`11:41:32Z` (`833c5f2e6337f4d3`) and `11:51:04Z` (`6b49ccecb9615d8f`):

```jsonc
{ "fingerprint": "6b49ccecb9615d8f",          // the PORTAL's id — the node's own path
  "reporterFingerprint": "1b405a782123de6b",  // what the watcher computed (2026-08-09 payload)
  "foldedFrom": ["1b405a782123de6b"],
  "shapes": [ { "key": "6b49ccecb9615d8f", "detail": "Failed to deliver to {path}", "occurrences": 2 } ] }
```

The fold comment says it in words — *"Re-addressed by the current identity function: this incident
inherited `1b405a782123de6b`. Those nodes are superseded and will not fold, file or comment again"* —
and the successor **inherits the old node's issue link**, so the churn moves rather than ending.

Two consequences for a triager, and the first one is a trap:

- **The predecessor node goes quiet whether or not the fault did.** After a re-addressing its
  `occurrences`/`lastSeen` freeze by construction, exactly as they would if the caller had been
  fixed. `lastSeen` alone cannot tell those apart — the same warning the capture-gap section makes
  about a stale `lastSeen`, now with a second cause.
- **The successor sweep is by CATEGORY.** 🚨 `content.foldedFrom:<id>` does **not** match an array
  member: the positive control `content.foldedFrom:1b405a782123de6b` returns `count: 0` on the very
  node whose `foldedFrom` holds that value, so a zero there is a broken instrument, not an answer.
  Sweep `content.category:*<Category>*` instead and read `count` against `truncated` and
  `coverage.partitions`.

🚨 **And the recompute instrument above dates the REPORTER only.** The portal's payload is not
reproducible from the fields its node carries — `{category}\n{eventId}\n` combined with the exception
type, the `normalizedMessage`, the shape `detail`, or any pairing of them, hashes to none of
`6b49ccecb9615d8f`/`833c5f2e6337f4d3` (tried 2026-09-19). Date the watcher with the hash; do not try
to date the portal with it.

**Proving a fault has STOPPED across a re-addressing boundary takes two mechanisms plus a liveness
control**, because neither covers the whole window:

1. *Before* the boundary the predecessor node would have advanced — so its `lastSeen` bounds the
   fault's end on that side.
2. *After* it a successor node with that category would exist — so the category sweep covers the
   other side.
3. The ingest must be shown reading that namespace **across** the window, and the evidence for that
   is a line CAPTURED from it inside the window (another incident's `lastSeen`), never the watcher's
   own health.

Worked example, the one that closed #3545: `d4c8f6f74ecfa422` froze at `2026-09-19T05:15:30Z`,
re-addressing went live at `11:41Z`, the category sweep finds no successor, and memex-cloud lines
were captured at `06:30:42Z` and `08:54:16Z` with a memex-cloud incident still updating at
`19:00:24Z`. Zero unanchored fan-out lines on that portal after 05:15:30Z, with no hole in the
window.

### 🚨 A bodyless capture can SWALLOW diagnosable bursts — `occurrences` is not a count of bodyless lines

Under the 2026-08-09 payload the message is not in the key, so **every** burst from one category
shares a fingerprint whether it has a body or not. The report then takes its `normalizedMessage` from
whichever burst came *first*; if that one was bodyless, the whole report is undiagnosable and the
portal refuses all of it — bodies included.

That is not a theory. `log-burst-header-only-memex` (2026-09-14) carries `occurrences: 4` whose
samples are **one** bare `crit: …RoutingGrain[0]` header and **three** full `[ROUTE] Routing
back-pressure` bodies: three diagnosable red logs that got no ticket of their own. The
`memex-cloud` sibling shows the same shape from one pod 33 ms apart on 2026-09-08.

🚨 **Be precise about which half of that the evidence settles, because both issues were filed on the
other half.** They read the samples as multi-pod interleaving cutting a burst up. Interleaving is a
perfectly good explanation of why the first burst is **bodyless** — in a merged stream another pod's
line can land between a header and its body, and a global grouper closes the burst there and orphans
the rest; that is exactly #2153/#2222, and a same-pod pair does *not* rule it out, because the
orphaned body is simply dropped and never appears in the samples. What interleaving cannot explain is
the **fold**: why a bodyless burst and a diagnosable one end up in ONE report under ONE fingerprint.
Grouping decides where bursts begin and end; it has no say in which bursts share an identity. That is
the identity function, and the arithmetic above names which one.

> **Grouping explains bodylessness; identity explains the fold.** Ruling a cause in or out needs the
> matching instrument, and a capture-gap incident carries evidence for the second, not the first.

> **So read a capture-gap incident's `samples[]` before quoting its `occurrences` as a count of
> bodyless captures.** Some of them may be fully-formed faults that were folded into it, and those
> are the ones still owed a ticket. Under the current identity this cannot happen — the message is in
> the key and a bodyless burst is never fingerprinted at all — and
> `RedLogBurstReconstructionTest.A_bodyless_burst_never_swallows_a_diagnosable_one_from_the_same_pod`
> pins it on that incident's own lines.

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
  closed its ticket is exactly what should notify — but when the fold is no longer about that
  ticket's own defect, the fix is to
  [repoint the link](#-when-a-fingerprint-outlives-its-issue-repoint-the-link-never-suppress),
  never to suppress the incident.
- 🚨 **A close is a DECISION, and `State` + `ClosedAt` do not carry it.** "Fixed", "won't do" and
  "this is tracked on another issue" are three different statements about the same closed ticket, and
  a predicate that reads only the close TIME treats them identically — which is how a deliberate
  consolidation is undone by a fault that is, by construction, still firing
  (Systemorph/MeshWeaver.Plugins#2177: 35 tickets closed as `duplicate` onto 9 roots, two of them
  reopened within 11 and 14 minutes). `GitHubIssue.StateReason` carries GitHub's `state_reason` so the
  decision is readable: `Completed`, `NotPlanned`, `Duplicate`, `Reopened`, or **`Unknown`**, which
  means *the reason was not established* — an open issue carries none, GitHub omits it for anything
  closed before the field existed, and a list read never asks. `Unknown` is never a synonym for
  `Completed`.
  🚨 It is parsed from the RAW wire token, not through Octokit's `StringEnum<ItemStateReason>.Value`:
  that enum has three members and no `duplicate`, so `.Value` throws `ArgumentException` on exactly
  the value this exists to read (measured against Octokit 14.0.0).
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

### A refused update is loud — once

Every leg that talks to GitHub can be refused, and a refusal used to live in exactly one place: the
incident's own `Status: Failed` / `Error` fields, which nobody opens unless they already suspect
something. The issue thread shows nothing — no error, no gap marker — so **a thread that went quiet
because the commenter was refused reads exactly like a fault that stopped firing.** It fails toward
"fine".

Measured on `memex.systemorph.com` on 2026-09-11
([#4022](https://github.com/Systemorph/MeshWeaver/issues/4022)): `Admin/_LogIncident/af1ee515fdf60bd1`
stood at `occurrences: 7`, `occurrencesAtLastComment: 5`, `status: Failed`,
`error: "Resource not accessible by integration"` against issue #3876 — two occurrences that never
reached the ticket. It was not one incident. The App (`meshweaver-cloud`, installation `144517285`)
had **opened 261 issues and commented on none of them** (its 39 comments are all on pull requests),
because its permission set, read from the App itself, is `contents:write, emails:write,
metadata:read, pull_requests:write` — no `issues` at all, at either the App or the installation.
Every recurrence comment since the watcher's first live run on 2026-08-10 was refused, and the only
record was a field on each node.

A refused `File` or `Comment` therefore rings the **platform bell** — `Admin/_Notification`, whose
read scope is exactly `hub.IsGlobalAdmin()` — as a `System` notification whose row opens the
incident:

| Leg | Bell title (English) | The message names |
|---|---|---|
| `Comment` | `Incident {fingerprint}: updates are not reaching {owner/repo}#{n}` | the refusal, how many occurrences never reached the issue, the total, and the incident path |
| `File` | `Incident {fingerprint} could not be filed` | the refusal, the occurrence count, and the incident path |

The same moment logs one Warning, `[IssueRefused] Incident {path}: {leg} to {issue} was refused —
{reason}`, naming both sides — the precedent is `ConfigsTargeting`'s zero-match Warning
([Sync Ref Contract](../SyncRefContract)): "I could not do the thing you think I did" is said out
loud, once, where it can be found.

**Once per refusal, not once per recurrence.** A ticketed incident requests a `Comment` on every
report, and after filing `LastCommentedAt` is `null`, so the comment rate limit never engages while
the post keeps failing — a refused incident retries on **every** report. A bell per attempt would
turn one missing permission into one notification per occurrence. So `RefusalAnnouncedAt` is
stamped once the bell has been written, and a post that lands clears it: one refusal episode, one
bell, and the next refusal after a landed post rings again. The marker cannot be `Error` — the
claim clears `Error` before every attempt, so a marker keyed on it would announce the same refusal
every time.

**Bell first, marker second — and the bell row has an identity.** The two writes are not one
transaction, so their order decides what a crash between them costs. Marker first leaves a marker
with no bell: every later refusal reads "already announced" and the episode stays silent for good.
So the bell is written first, under a deterministic identity — the fingerprint, the leg, and the
`occurrencesAtLastComment` the episode started from, which nothing moves until a post lands — and
upserted; the marker is written second. A crash between them costs one re-announcement into the
SAME row, refreshed and unread: never a silent episode, never a duplicate.

A refusal is information, not a transient: nothing retries it and nothing swallows it. If the bell
itself cannot be written, that is logged at Error — which the watcher tickets like any other red
line — and the marker stays unset, so the next refusal announces again, into the same row.

The bell's text is platform-owned and lives in the catalog (`logIncident.refused.*`, English and
German). A notification row stores its title and message **verbatim**, and the platform addressee
is a group of operators rather than one viewer — so the writer resolves the keys **explicitly in the
platform default language (English)**, never in whatever locale the writing context happens to
carry, which would store one operator's language for all of them. The German message is phrased
count-neutrally, because the counts are runtime values that can be `1`.

#### Is `occurrencesAtLastComment` lagging `occurrences` enough on its own?

It is stored, it is truthful, and the bell quotes it — but as a detector it cannot stand alone:

- **Lag is normal.** `CommentInterval` (6 h) means a healthy, continuously firing incident lags for
  up to six hours by design. Lag alone cannot tell "rate-limited, will post" from "refused, never
  will".
- **The lag's age is not stored.** After filing, `LastCommentedAt` is `null`, so "how long has it
  lagged" has no timestamp to read.
- **It cannot see the `File` leg.** An incident that was never filed has no issue to lag behind.
- **Suppressed incidents lag by design** — occurrences keep counting and tickets stop.
- **It is passive.** Like `Error`, it answers only someone who already thought to ask.

So the lag stays what it is good for — an audit query and the number in the bell — and the
announcement is the bell.

#### Granting the App what the commenter needs

An App's permissions are not writable through any API, and a new permission takes effect only when
the installation accepts it. This is an **organization-owner** action:

1. `github.com/organizations/Systemorph/settings/apps/meshweaver-cloud/permissions` →
   **Repository permissions** → **Issues: Read and write** → **Save changes**.
2. `github.com/organizations/Systemorph/settings/installations/144517285` → **Review request** →
   **Accept new permissions**. Until the installation accepts, its tokens still carry no `issues`.
3. Verify from GitHub, never from the absence of an error: an App-JWT `GET /app/installations`
   lists `"issues": "write"` on installation `144517285`; then the next recurrence comment lands,
   the bot's first comment appears on the issue, and that incident's `occurrencesAtLastComment`
   advances. Never print the key or a token while doing it — permission names only.

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

🚨 **Opening an issue is not the same grant as updating one.** A recurrence comment, and the reopen
that precedes it on a closed issue, need the App's **Issues: Read and write** repository permission.
Without it GitHub answers every one with `Resource not accessible by integration` — while the issue
itself still opens, so the pipeline looks healthy from the one place anyone checks. What that looked
like in production, and how a refusal is now surfaced, is under "A refused update is loud — once"
below.

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

> The log watcher is cluster infrastructure in the `monitoring` namespace, described by no
> `Deployments/<name>` record — so its deploy is a cluster operation with no action kind yet. It
> is break-glass by construction; write it up as such ([OperatingFromThePortal](/Doc/Architecture/OperatingFromThePortal)).

```bash
# 1. One shared secret, both sides.
TOKEN=$(openssl rand -hex 32)
az aks command invoke -g <aks-resource-group> -n <aks-cluster> --command \
  "kubectl -n monitoring create secret generic mw-log-watcher --from-literal=ingest-token=$TOKEN"
#    …and set LogWatch__IngestToken to the same value on the portal (via its KeyVault secret).

# 2. Pick a PUBLISHED watcher image — never hand-build one. The watcher's source is
#    MeshWeaver.Plugins (src/MeshWeaver.LogWatcher; it left core on 2026-08-27, #2276), and that
#    repo's `log-watcher image` lane pushes meshweaver.azurecr.io/memex-log-watcher:<version>,
#    :<sha7> and :latest on every change to the watcher or its contract (Plugins#823/#992).
#    A tag you did not read from the registry is not a tag you can deploy.
az acr repository show-tags -n meshweaver --repository memex-log-watcher -o tsv

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

- 🚨 **Classify EVERY reporter of the fault, or the ticket does not stop — and this is the identity
  rule above working as designed, not a bug.** Because the fingerprint identifies the fault and not
  the reporter (the category is excluded once a frame is present, and the discriminating text is the
  *exception's* message rather than the reporter's prose), downgrading one site leaves the incident
  firing through every other site that prints the same exception. Measured on #3243: the missing-hub
  line was classified on 2026-09-04, and the incident reopened on 2026-09-18 carrying
  `[ACTIVATE] Grain Admin/_Notification/…: activation faulted for …` — the *activation chain's*
  error arm, a few lines away in the same grain, still an unconditional `LogError`. The same
  `ObjectDisposedException`, a different `catch`, one fingerprint. So when you downgrade an expected
  condition, grep the component for its SIBLING arms first: the thing that keeps a ticket alive is
  the one you did not classify. `MessageHubGrain.ActivationFaultLevel` is that arm's classifier and
  shares the two existing predicates — `HubDisposingException.IsHubDisposal` (the hub announced its
  own disposal) and `IsDisposedContainer` (a scope closed underneath live work) — rather than
  re-deriving the fact a third time.

`CancellationIsNotAFaultTest` pins the cancellation directions, including the timeout impostor;
`HubCreationDuringTeardownIsNotAFaultTest` and `HubConstructionOutcomeReportingTest` pin the
hub-creation ones, including the configuration-throw that must stay red and the activation arm's
own control — a `TimeoutException` or an unresolvable node stays red, and a null fault is an unknown
rather than a shutdown.

## Before you CLOSE one: two things the fingerprint does not assert

Measured 2026-09-19 while consolidating the 53 bot-filed issues then open on this repository — 34 of
them were duplicates of 9 roots. Both traps below produce a closure that *reads* like a pass.

### 1. An empty `normalizedDetail` makes the fingerprint SITE-ONLY, so "the titled fault is fixed" licenses nothing

The identity's third term is the normalized **detail** — the exception's own message when there is one,
else the logged message. It is the parser property `NormalizedDetail`, and it lands on the incident node
as the JSON field `normalizedDetail`; the JSON spelling is used from here on, because everything this
section tells you to read is read off the node with `get @Admin/_LogIncident/<fingerprint>`. A burst that
carries no exception type *and* whose parse leaves that field empty contributes nothing to the third
term, and the identity collapses to the log **site**. Every `Error` from that category then folds onto
one incident, with `variants: 1` — so nothing on the ticket says it happened.

[#4597](https://github.com/Systemorph/MeshWeaver/issues/4597) is the worked example.
`Admin/_LogIncident/253b5feaa9ed755e` carries `normalizedDetail: ""`, a title naming a content-cast
failure, and five samples of which **four are a different fault**:

```
normalizedMessage:  As<MarkdownContent> for {path}: value is EmailContent (…), not convertible
 2026-09-05 12:26:06Z  As<MarkdownContent> … not convertible                      <- the titled fault
 2026-09-17 08:57:24Z  As<InstanceActionContent> could not recover value: JsonException
 2026-09-17 13:05:50Z  As<MarkdownContent>       could not recover value: JsonException
 2026-09-17 15:22:21Z  As<MarkdownContent>       could not recover value: JsonException
 2026-09-18 12:39:09Z  As<MarkdownContent>       could not recover value: JsonException
```

The titled fault *is* fixed: `NodeUpdatePipeline.WithExistingContentTyped` now asks
`ContentDiscriminator.Admits` first and logs at `Warning` instead of reporting a failed `As<T>`
recovery at `Error` (`3c36d05ade`, 2026-09-18 07:19 +0200) — and that commit is an ancestor of the
commit each production portal reports at `/api/version`. A closure reading *"fixed, and live, proved by
`merge-base --is-ancestor`"* would therefore have passed every check a careful reader applies, while
closing a fault that fired at **12:39Z that same day** from `ObjectAsExtensions.LogRecoveryFailure` —
a different call site, still `LogError`, untouched by that commit.

🚨 **The discriminator costs one read: the incident node's `samples`, not its title.** When the samples
disagree with `normalizedMessage`, the fingerprint is site-only and the ticket covers more than it
says — so a fix for the titled fault is a comment, never a close.

### 2. A `MeshWeaver.`-prefixed category can belong to a Plugins assembly, and a transfer does not stop the re-file

Routing is by category prefix (above), and **a prefix does not name an assembly**.
`MeshWeaver.SelfUpdate.Aks.*` and `Memex.Portal.Distributed.*` are MeshWeaver.Plugins projects; a
framework category such as `Microsoft.Extensions.Diagnostics.HealthChecks.DefaultHealthCheckService`
carries no hint of who registered the failing check at all. Each of those files onto core.

When a human then transfers the ticket the redirect keeps working —
`repos/Systemorph/MeshWeaver/issues/1897` resolves to `MeshWeaver.Plugins#2154` — but the same
fingerprint was measured open **twice, in two repositories**, three times over:

| fingerprint | open in MeshWeaver.Plugins | open again on MeshWeaver (core) |
|---|---|---|
| `93710ed097873d0b` | [Plugins#2132](https://github.com/Systemorph/MeshWeaver.Plugins/issues/2132) | [core#4767](https://github.com/Systemorph/MeshWeaver/issues/4767) |
| `34928a1851aa5217` | [Plugins#2153](https://github.com/Systemorph/MeshWeaver.Plugins/issues/2153) | [core#4794](https://github.com/Systemorph/MeshWeaver/issues/4794) |
| `cd48b16db4d9809b` | [Plugins#2154](https://github.com/Systemorph/MeshWeaver.Plugins/issues/2154) | [core#4795](https://github.com/Systemorph/MeshWeaver/issues/4795) |

`Admin/_LogIncident/93710ed097873d0b` shows the shape: `"issueNumber": 4767` alongside
`"supersededIssueUrl": ".../issues/4611"` and `"status": "Superseded"` — the incident was superseded and
filed a fresh ticket, into the repository the route still points at. So **"one fault, one ticket" holds
per incident node, not per fault**, across a supersession or a transfer.

🚨 Before opening *or* closing one, search the owning repository by **fingerprint** as well as by title;
and when the code is not in this repository's `src/`, the ticket wants a transfer rather than an
analysis.

### And a fan-out is read from the log line, not the title

Two clusters in that sweep were one condition each, reported once per shard and once per grain
activation. The discriminators were already in the evidence: the memory-stream tickets differ only in
the per-queue log **category** (`…Memory.memory-0` … `memory-7`, 8 registered queues, one provider),
and the `[ROUTE] Routing back-pressure` tickets are split by a field the line itself explains —
`deepest per-destination queue` ≥ 1 is head-of-line blocking, `0` is load. Consolidating on the title
instead would have merged the two readings and lost the only thing that tells them apart.

## What this is not

- **Not an alerting system.** A provisioned Grafana rule covers the "tell a human now" case
  (the rule is bound to a specific Grafana and set of namespaces, so it lives with the
  deployment it describes, not here). Ticketing
  hangs off the watcher's cursor instead, because an alert notification that fires while its
  receiver is down is simply lost — acceptable for a nudge, not for "every distinct error gets a
  ticket".
- **Not a substitute for reading logs.** It tickets what a portal reports as red. A fault that logs
  at `warn:` or does not log at all is invisible to it.
- 🚨 **Not the thing that writes `Hosting/LogEntry`.** This subsystem's only mesh output is
  `Admin/_LogIncident/{fingerprint}`. `Hosting/LogEntry` nodes under `Ops/Logs` come from a `Logs`
  `Hosting/InstanceAction` — one LogQL query somebody asked — and the `LogWatch__*` keys on a
  `Deployments/*` record choose which GitHub repository an incident is FILED into, not what is
  ingested. Reading `Ops/Logs` as "LogWatch's ingest" and its gaps as omissions produced a wrong
  conclusion on [#3931](https://github.com/Systemorph/MeshWeaver/issues/3931); see
  [Log Entries Are a Query Result, Not a Feed](../LogEntriesAreAQueryResult).

## Related

- [Controlled I/O Pooling](../ControlledIoPooling) — every HTTP and file leaf here runs on an `IIoPool`.
- [Activity Control Plane](../ActivityControlPlane) — the `Status` / `RequestedStatus` pattern.
- [Access Context Propagation](../AccessContextPropagation) — why ingest writes under `ImpersonateAsSystem`.
- [CQRS — Queries vs. Content Access](../CqrsAndContentAccess) — why ingest reads the node stream, not a query.
