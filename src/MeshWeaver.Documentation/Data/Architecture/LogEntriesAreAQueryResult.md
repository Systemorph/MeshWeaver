---
Name: Log Entries Are a Query Result, Not a Feed
Category: Architecture
Description: "Hosting/LogEntry nodes are the output of one Logs InstanceAction — a LogQL query somebody asked. Nothing ingests logs continuously. So reading them as a feed answers 'what did the last person ask for', and the 2026-09-10 reading of that as 'the ingest omits the content route' was wrong in a way the data itself could settle."
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 6h16"/><path d="M4 12h10"/><path d="M4 18h6"/><circle cx="17.5" cy="16.5" r="3.5"/><path d="m21 20-1.9-1.9"/></svg>
---

# Log entries are a query result, not a feed

> **`Hosting/LogEntry` nodes under `Ops/Logs` are the OUTPUT of one `Logs` `Hosting/InstanceAction`
> — a LogQL query a person wrote, over a window they chose, capped at a limit they set. Nothing on
> this platform ingests logs continuously.** So `search 'nodeType:Hosting/LogEntry'` answers *"what
> did the last person ask for"*. It never answers *"what does this portal log"*, and an absence in
> it is not evidence of anything.

That distinction is not pedantic. On 2026-09-10 it produced a confident, wrong conclusion about a
live incident, and the data carried its own refutation the whole time.

## The measured case

[#3931](https://github.com/Systemorph/MeshWeaver/issues/3931) — every cold `/api/content` read on
`memex.meshweaver.cloud` 503ing after ~10.2 s — needed the discriminator from
[The /api/content 503](../ContentRoute503). The issue reports:

> `search 'nodeType:Hosting/LogEntry'` on that portal returns only `Received
> '$type':'LearningJourneyContent' which is NOT registered in this (receiving) hub` entries, every
> one stamped `2026-09-10T12:36:35Z` … **LogWatch's ingest does not carry the lines the documented
> discriminator depends on.**

Every observation there is correct. The inference is not. Those 13 rows are the complete answer to
this node, `Ops/AssessmentRoutingLogs20260909`, created the previous evening and executed at
12:36:35Z:

```json
{ "$type": "InstanceActionContent",
  "deployment": "Deployments/memex-cloud",
  "query": "SelfAssessment|LearningJourney|OrleansMeshChangeFeed",
  "sinceMinutes": 30, "limit": 100,
  "logQl": "{namespace=\"memex-cloud\"} |~ \"(?i)SelfAssessment|LearningJourney|OrleansMeshChangeFeed\"",
  "entryCount": 13, "truncated": false, "state": "Done" }
```

One message class because the query asked for three tokens and one of them matched. One timestamp
because `lastModified` on a `LogEntry` is **when the action ran**, not when the line was emitted
(the line's own clock is `content.timestamp` — 12:09:10Z here, half an hour earlier). Nothing from
the content route because nobody asked for the content route. Nothing from the 14:46Z incident
window because the run ended two hours before it.

🚨 **The entry itself says so.** `LogEntryContent.Selector` exists for exactly this — *"kept so an
entry can always be traced back to the query that produced it — and so a bad selector is diagnosable
from the data it wrote"* — and every one of the 13 rows carries
`{namespace="memex-cloud"} |~ "(?i)SelfAssessment|LearningJourney|OrleansMeshChangeFeed"`. A
regex naming three unrelated subsystems is not an ingest rule. **Read `content.selector` before
reading a `Hosting/LogEntry` population as evidence**; it is the denominator, printed on every row.

### The denominator, both portals, 2026-09-10

| Portal | `Ops/Logs` rows | Produced by |
|---|---|---|
| memex.meshweaver.cloud | **13** | ONE `Logs` action (above): `entryCount: 13`, `truncated: false` — a complete answer to a question about assessment routing |
| memex.systemorph.com (control instance) | **2 498** | many runs — three of them the `Hosting/Script/ingest-logs` script, recognisable because its bursts are exactly 120 × namespaces (360 on 08-12, 240 on 09-03, 240 on 09-08); the rest `Logs` actions |

**Every one of the 30 actions named `Logs …` on the control instance targets `memex` — 24 of them
that same day, and not one has ever targeted `memex-cloud`.** That is the whole reason the incident
portal's `Ops/Logs` looked empty of everything: the instrument is in daily use, and it had simply
never been pointed there.

## The three log paths, and what each is for

| Path | Trigger | Selection rule | Writes |
|---|---|---|---|
| **LogWatch** (`LogWatch__*`, [Red-Log Watching & Ticketing](../LogWatchTriage)) | continuous, from `mw-log-watcher` in the `monitoring` namespace | reads every line in a namespace, then detects red **client-side** and groups a burst per pod; fingerprints it | `Admin/_LogIncident/{fingerprint}` — one node per distinct defect, folded on recurrence |
| **`Logs` `Hosting/InstanceAction`** (Plugins#1521) | on demand, one node | the LogQL **you** write | `Hosting/LogEntry` under `Ops/Logs`, plus `logQl` / `entryCount` / `truncated` on the run |
| **`Hosting/Script/ingest-logs`** | a person presses Run | bare `{namespace="…"}`, newest 120 lines per recorded deployment, last hour | the same `Hosting/LogEntry` nodes |

🚨 **LogWatch never writes a `Hosting/LogEntry`.** The `LogWatch__DefaultRepository` /
`LogWatch__Routes__0__Prefix` keys on a `Deployments/*` record are log-**category** prefixes that
choose which GitHub repository an incident is filed into — they select a ticket's destination, not
what is ingested. And LogWatch tickets what a portal reports as **red**: a fault that logs at
`warn:` is invisible to it by design, which covers the whole content-route family (`Content read
timed out for {Path}` is `LogWarning`). So "LogWatch's ingest omits X" is not a statement that can
be true or false — there is no such ingest to omit from.

## Nothing is level-filtered — but the level is on a different node

Neither the `Logs` action nor the script filters by level: a blank `query` lands every line. That
makes every diagnostic line **eligible**. It does not make it **findable**, because of how a .NET
console record reaches the store.

The default console formatter writes a record as several physical lines — the `warn: Category[0]`
header, then the indented message, then the exception and its frames — and the CRI log format
stamps each line separately, so Loki holds each as its own entry and the projection makes each its
own node. **Only the header carries `level` and `category`.** Measured on the control instance,
two adjacent nodes 68 µs apart:

| Node | `content.level` | `content.category` | `content.message` |
|---|---|---|---|
| `Ops/Logs/memex-1789041621188149431-fd958f-b2z6v` | `Information` | `MeshWeaver.Mesh.CreateNode` | `info: MeshWeaver.Mesh.CreateNode[0]` |
| `Ops/Logs/memex-1789041621188217895-fd958f-b2z6v` | *(empty)* | *(empty)* | `      Node created at Admin/_Notification/… by system-security` |

Three consequences, all of them things a reader will otherwise get wrong:

- **`content.level:Error` finds headers, not messages.** It returns rows whose whole text is
  `fail: SomeCategory[0]`.
- **A free-text search for the message finds a row with no level, no category and no link back to
  its header.** The two nodes are related only by pod and adjacent timestamp.
- **A multi-line record is never one node.** `[STALE-CALLBACK]`'s handler-side fate block is
  several lines, so it lands as several unrelated nodes.

`LogEntryContent` states the boundary itself: *"Nothing here is authoritative. Loki is."*

## Asking for a line: filter on the line that CARRIES the answer

A `|~` filter selects **physical lines**, and this repo has already paid for forgetting that. The
watcher's own query was `{namespace="x"} |~ "^(fail|crit):"` until 2026-08-08, and it returned
headers **alone** — every incident lost its exception and top frame, and every fault in a category
collapsed onto one fingerprint. `LokiQuery.ForNamespace` is deliberately unfiltered for that
reason.

So the rule for a `Logs` action is:

> **Filter on the text of the line you want to READ, not on the record you want to find.**

Worked example — the `/api/content` 503 discriminator. The line that decides Cause A from Cause B
is the `HubUnreachableException` message built by `ReadBudget.Unreachable`, and it is a **single
physical line** because `MessageHub.GetPendingRequestDiagnostics` is a single-line snapshot by
contract:

```text
MeshWeaver.Mesh.HubUnreachableException: Reading content collection config from '…' gave up
after 10s — … Reader: Hub portal/reads-… RunLevel=Started Queue(buffer=0,deferred=0,drainsInFlight=0)
PendingCallbacks=1[…=GetDataRequest@…(10003ms)]
```

Ask for that line:

```json
{ "id": "memex-cloud-logs-contentroute", "namespace": "Ops/Actions",
  "name": "Logs memex-cloud — the /api/content read budget lapse and its Reader snapshot",
  "nodeType": "Hosting/InstanceAction",
  "content": { "$type": "InstanceActionContent",
    "deployment": "Deployments/memex-cloud",
    "requestedAction": "Logs",
    "query": "Reading content collection config from",
    "sinceMinutes": 240, "limit": 200,
    "reason": "Read-only. Adjudicate ContentRoute503 Cause A vs Cause B for the 14:46Z probe failure." } }
```

Then read `logQl`, `entryCount` and `truncated` back off the same node, and the matched lines under
`Ops/Logs`. `requestedAction` is cleared to `None` when the run finishes, so a completed node's
JSON no longer shows it — the run's `logQl` is what tells you what was asked.

🚨 **The obvious query is the wrong one.** `query: "Content read timed out for"` matches the
`LogWarning`'s *message* line and returns it **without** the exception line underneath — the 503
without the discriminator. Same trap, same mechanism, as the watcher's `^(fail|crit):`.

| Parameter | Behaviour |
|---|---|
| `query` | blank ⇒ every line; starting with `\|` ⇒ a verbatim LogQL pipeline; otherwise a case-insensitive regex line filter, `\|~ "(?i)<query>"` |
| `sinceMinutes` | default **60** |
| `limit` | default **200**, hard cap **1000**; `truncated` is `lines >= limit` |
| `pod` | adds `pod="…"` to the stream selector |

Newest first (`direction=backward`), so a truncated answer is the **most recent** N matches, not
the first N in the window.

## Which portal is the instrument on

`Logs` runs against the deployment its `deployment` field names, from whichever portal hosts the
action node — and the two are routinely different. `memex` → memex.systemorph.com (the control
instance), `meshweaver` → memex.meshweaver.cloud; the record names and the namespaces are crossed
on this fleet, so never infer one from the other. `Ops/Logs` on a portal holds only what somebody
ran **there**, which is why the same query answers 2 498 rows on one and 13 on the other. Confirm
the portal before drawing any conclusion from a read — see
[Operating from the portal, not the cluster](../OperatingFromThePortal).

## Before you read an absence

[Measuring a Live Portal Read-Only](../MeasuringALivePortalReadOnly) states the governing rule —
*establish that the instrument COULD have seen the event, before you report that it did not*. For a
`Hosting/LogEntry` population the coverage facts are all on the data:

1. **`content.selector`** on the rows — does the query even mention what you are looking for?
2. **`entryCount` and `truncated`** on the run — a truncated run is a partial answer wearing a
   complete one's clothes.
3. **The window** — `sinceMinutes` back from `startedAt`, not from now.
4. **Loki's retention** (31 d on this cluster, checked not assumed) — which is the only thing that
   decides whether a *new* `Logs` action can still answer about an old event.

A `Logs` run that lands **zero** entries **with** a `logQl` is a real answer. A `Hosting/LogEntry`
population that does not mention your subject is not.

## Related

- [The /api/content 503](../ContentRoute503) — the discriminator this page's worked example fetches.
- [Red-Log Watching & Ticketing](../LogWatchTriage) — the other log path: `fail:`/`crit:` bursts to one ticket each.
- [Operating from the portal, not the cluster](../OperatingFromThePortal) — the `Logs` and `Sample` action kinds, and which reads are still break-glass.
- [Measuring a Live Portal Read-Only](../MeasuringALivePortalReadOnly) — the break-glass forms, and why an absence needs a coverage fact.
