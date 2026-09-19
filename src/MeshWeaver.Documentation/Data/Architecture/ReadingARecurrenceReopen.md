---
Name: Reading a Recurrence Reopen
Category: Architecture
Description: What a bot reopen does and does not assert, measured over the 2026-09-17 wave. Two predicates are needed, not one — the fault must have been seen AFTER the close, and it must have been seen RECENTLY — and the residue of that wave passes the first on all 51 counts while failing the second on 41. The honest triple for the fleet, the per-issue evidence behind it, and why 15 of the 51 point at a third predicate nobody has written yet. Plus the prior question both predicates assume away — whether this issue is still the thing the evidence lands on — after an identity change re-addressed three of core's oldest bot tickets on 2026-09-19.
Icon: ArrowSync
---

# Reading a Recurrence Reopen

The recurrence bot reopens a closed incident issue and writes **"Reopened — this fault is happening
again."** That sentence carries two claims, and they fail independently:

1. **the fault was seen after the close** — otherwise the reopen reverses a person's judgement on
   evidence that person already had;
2. **the fault was seen recently** — otherwise "again" is a statement about a counter, not about the
   system.

Only the first has a fix in flight
([MeshWeaver.Plugins#2014](https://github.com/Systemorph/MeshWeaver.Plugins/issues/2014):
`issue.ClosedAt is { } closedAt && incident.LastSeen > closedAt`). The second is unwritten, and it is
the one that mattered on 2026-09-17.

## The wave, and its residue

Between **07:51:00Z and 07:53:59Z on 2026-09-17** the bot reopened a burst of closed core incident
issues. It is filed twice — [#4577](https://github.com/Systemorph/MeshWeaver/issues/4577) (~130
across a wider 07:45–07:55Z window, backlog 43 → 164) and
[#4610](https://github.com/Systemorph/MeshWeaver/issues/4610) (~51 in the narrow one, backlog
46 → 97).

🚨 **Those are ONE burst measured at two times, not two waves — do not add them.** #4577 was written
at 08:54Z and #4610 at 15:37Z, and #4610's window sits inside #4577's; the backlog figures differ
because re-closing had been going on in between. A third reading at ~17:30Z finds 102 open and 51
from the burst — which matches #4610's count exactly, by a different instrument (it counted
`updated_at` inside 07:40–08:15Z; this counts `reopened` events). Reading the two tickets as ~181
issues of damage overstates it by roughly threefold.

**Denominator, measured 2026-09-17T~17:30Z:** of core's 102 open issues, **51** carry a `reopened`
event inside that three-minute burst. Every one of the 51 was closed **before** 2026-09-17 — 50 by
`rbuergi`, 1 by `github-actions[bot]` — so all 51 are in scope, and the rest of the wave has already
been unwound by earlier sessions.

Eight further issues carry a 2026-09-17 reopen **outside** the burst (#2387 05:33, #4222 06:42,
#4326 08:54, #3712 09:13, #1198 11:04, #1840 13:55, #1561 16:04, #1140 16:06). #1198 was reopened by
a **human**, not the bot. None of the eight is part of the wave and none was touched.

## The finding: the residue passes the first predicate, 51 out of 51

Taking, per issue, the bot's own `Last seen:` from the very comment it posted alongside the reopen,
and the `closed` event that reopen reverted:

| | count |
|---|---|
| newest occurrence **at or before** the close — the reopen was wrong | **0** |
| newest occurrence **after** the close — the reopen was, on that test, right | **51** |

So the residue of this wave is already clean by Plugins#2014's rule, which is the only rule that
authorises re-closing. **Nothing here may be re-closed on the evidence available**, and nothing was.
That is not the same as saying the 51 are live.

## The second axis: how old is the evidence?

Measuring the newest occurrence any comment on the issue reports, against the reopen instant:

| bucket | count | what it means |
|---|---|---|
| **seen today**, after the reopen itself | **3** | genuinely live — the fault is happening now |
| seen within 7 days of the reopen | 7 | recently live; quiet for 5–7 days |
| **quiet for 8–37 days** at the reopen | **41** | "again" is a statement about a counter |

The oldest is **37.4 days** (#1112, #1135 — last seen 2026-08-10, reopened 2026-09-17). That is the
dishonesty in the count, and no close predicate anybody has proposed reaches it: these faults *were*
seen after their close, and then stopped.

## The honest triple

| number | value | basis |
|---|---|---|
| **wrongly reopened and re-closed** | **0** | 0 of 51 have a newest occurrence at or before their close |
| **genuinely recurring** | **3** (+7 within a week) | newest occurrence 2026-09-17, on the running pod |
| **awaiting delivery** | **0 in this wave** | every close in the wave predates both running images |

**Awaiting delivery is empty here by construction.** The newest close in the population is
2026-09-08T21:04Z, while memex.systemorph.com runs `afde4ea` (2026-09-15 21:50Z) and
memex.meshweaver.cloud runs `c84c6c0` (2026-09-12 09:30Z). Every fix that closed a wave issue is in
both running images by at least four days, so **no wave issue can be excused as delivery lag**.
(Whether a given close carried a *fix* at all is a separate question, answered per issue below; the
delivery argument only establishes that if there was one, it is running.) The genuine
awaiting-delivery cases on 2026-09-17 are the two reopened *outside* the wave whose fixes merged on
2026-09-16, after both images: #4616 and #4464.

### The three that are live

Each has an occurrence on **2026-09-17**, reported from `memex-portal-deployment-6c967c7c4c-*` — the
currently-running replica. 🚨 **What each close actually asserted differs, and that decides what the
recurrence means** — so the three are listed with their closing verdict rather than lumped together:

| issue | closed | seen today at | what the close asserted | so a recurrence today means |
|---|---|---|---|---|
| [#1549](https://github.com/Systemorph/MeshWeaver/issues/1549) | 2026-08-21 21:12:33Z | 2026-09-17 11:11:00Z | *"the defect this issue converged on is fixed and deployed"*, residual named | a delivered fix did not hold, or the named residual is what is firing |
| [#2299](https://github.com/Systemorph/MeshWeaver/issues/2299) | 2026-08-26 07:22:28Z | 2026-09-17 11:28:58Z | **nothing — a bare close, no comment, no linked fix** | the fault was never established as fixed; this is the first evidence either way |
| [#2765](https://github.com/Systemorph/MeshWeaver/issues/2765) | 2026-08-30 18:43:43Z | 2026-09-17 14:25:47Z | *"a single transient with no code defect identified, and the retry model worked"* | the "single transient" premise is falsified — the record now stands at 1,069 |

These three are the wave's entire signal. Reading the reopen wave as noise loses them; reading it as
130 regressions buries them.

🚨 **And "recurring against its own fix" is a claim to check per issue, not a property of the
bucket.** Only #1549's close carried a stated, deployed fix; #2765's close explicitly identified no
code defect, and #2299's carried no verdict at all. A live occurrence is a live occurrence either
way — but only one of these three is evidence that a *fix* failed.

## The third predicate nobody has written: the close races the roll

🚨 **15 of the 51 have their ONLY post-close evidence within 17 hours of the close**, and nothing in
the 8–37 days since:

#2152 (+0.0 d) · #3466 (+0.0 d) · #2171 (+0.1 d) · #2182 (+0.1 d) · #2679 (+0.1 d) · #3051 (+0.2 d) ·
#2444 (+0.3 d) · #1112 (+0.4 d) · #1135 (+0.4 d) · #1175 (+0.4 d) · #2233 (+0.5 d) · #3647 (+0.5 d) ·
#2153 (+0.6 d) · #2876 (+0.6 d) · #3126 (+0.7 d)

**Measured:** the gap, and the silence after it. **Inferred, not measured:** that these are the old
replicas still emitting between the merge that closed the issue and the roll that delivered it — a
fix closes its issue when it merges, while the pods keep running the previous image for hours.
Confirming it needs a pod→image mapping the incident comments do not carry.

If that reading is right, the predicate is not *"was it seen after the close"* but ***"was it seen on
an image that contains the fix"*** — and it would take these 15 out of the wave without weakening
the rule for the three that are live. It also explains why Plugins#2014's predicate, which is
correct, leaves a residue that still reads wrong: a close and a delivery are different events, and
the reopen rule compares against the earlier one.

## The mirror image: dormancy is only evidence if the trigger happened

The section above is about a close that races the *delivery* — post-close lines coming from replicas
still running the old image. This is its inverse, measured on 2026-09-19, and it bites the closing
side rather than the reopening one.

**Three teardown faults were closed partly on dormancy and all three reopened within a day:**

| issue | closed | reopened | newest occurrence |
|---|---|---|---|
| #1540 | 09-18 05:58:27 | 09-19 05:08:26 | 09-19 05:07:01Z |
| #1547 | 09-18 05:57:08 | 09-19 05:07:26 | 09-19 05:10:25Z |
| #1548 | 09-18 05:57:09 | 09-19 05:05:27 | 09-19 05:12:06Z |

Every reopen is **correct** by the post-close predicate — each occurrence genuinely postdates its
close. The mistake is upstream, in the reasoning that closed them: one carried the words *"15 days
with no occurrence"*.

🚨 **All three fired inside one seven-minute window, 05:05–05:12Z, and that window is a roll.** Their
subjects are teardown by construction — `ObjectDisposedException` unregistering a grain from the
directory, a mesh hub resolving from a disposed Autofac scope. They fire when pods stop, and they
are silent when nothing is stopping them. Fifteen quiet days meant fifteen days without a roll, not
fifteen days of health.

🚨 **Correlation with a roll window is NOT the same finding as the mechanism.** Two more issues
reopened across the same period — #1422 (Release-snapshot cleanup, whose reopen is a cascade racing
a concurrent delete) and #1449 (a PostgreSQL `CreateNode` Unicode failure) — and it is tempting to
sweep them in. Their records identify neither as teardown- or boot-shaped, and #1449's occurrence
(09-18 15:35:29Z) is not in the window at all. Firing *during* a roll is evidence; needing a roll in
order to fire is the claim, and only the three above carry it. An earlier draft of this section
asserted all five, which is the very over-reach the page exists to warn about.

**The predicate:** silence is evidence only over a window in which the fault's trigger actually
occurred. For a roll-triggered fault, a dormancy argument has to name the rolls it survived. Without
that it is the same shape as [a sweep's zero with no denominator](../SearchCoverageAndRefusal) — a
number that reads like an answer and measured nothing.

Classifying by the timestamp is cheap and splits the backlog usefully. Of fifteen old incident
issues checked the same day, three more sit in the roll window (#1126, #1840, #2833 — two compile-at-
boot, one shutdown quiescence), while four fired at 08:54–09:03Z, after the image then running was
built (#1246, #2307, #2480, #3045).

🚨 **But "fired after the image was built" does NOT imply "fires on that image", and for a teardown
fault it implies close to the opposite.** The pod that emits a teardown fault is the pod being
REPLACED — it is running the image the roll is replacing, by construction. #2480 is exactly that
shape: its subject is the mesh drain *at silo shutdown*, and its 08:59Z occurrence sits right after
an 08:32Z roll, so the likeliest reading is the old replica tearing down, not the new one failing.
The timestamp separates "during a roll" from "between rolls"; deciding WHICH image was running still
needs the pod→image mapping, which the incident comments do not carry.

So the classification gives three buckets, not two: silent-between-rolls, fires-during-normal-
operation, and fires-at-teardown-on-the-outgoing-image — and only the middle one is evidence about
the image now serving.

## A fourth check that is not a predicate: the fingerprint may have been RE-ADDRESSED under the issue

The three checks above all ask *what does the evidence on this issue mean*. This one asks the prior
question — **is this issue still the thing the evidence lands on** — and on 2026-09-19 the answer
changed for three of core's oldest bot tickets at once.

Between **11:41:37Z and 11:44:05Z on 2026-09-19** a new incident-identity function
([MeshWeaver.Plugins#1796](https://github.com/Systemorph/MeshWeaver.Plugins/issues/1796))
re-addressed the fleet's high-volume fingerprints: a mega-fingerprint that had been folding several
different log SHAPES was split per shape, the old node was marked `Superseded`, and each surviving
shape either inherited the old issue or filed its own. Read off the nodes on the control instance
the same afternoon:

| incident node | carries issue | `status` | newest `lastSeen` | `namespace` |
|---|---|---|---|---|
| `d1cd36f53a5f3a6c` | #1246 | **Superseded** → `c3a4225a446594c7` | 2026-09-19 05:20:00Z | memex-cloud |
| `465a6677047a571b` (Feedback gating, 2 paths) | **#1246**, inherited | Filed | 2026-09-19 08:59:06Z | memex-cloud |
| `c3a4225a446594c7` (Feedback gating, 1 path) | **#4814**, new (already closed) | Filed | 2026-09-19 07:49:05Z | memex-cloud |
| `4ff70ec1124b1b3c` (Feedback gating, 3 paths) | **#4806**, new | Filed | 2026-09-19 09:40:44Z | memex |
| `b03482717d5ba39a` | #3659 | superseded (its own fold comment says so) | 2026-09-19 07:41:12Z | memex-cloud |
| `fc3bfaea16374d50` (`DefaultViews` absence) | **#3659**, inherited | Filed | 2026-09-19 07:41:12Z | **memex** |
| `c93238020fe2f0b5` (`ClaimsDeepfield` absence) | **#4812**, new | Filed | 2026-09-19 09:09:51Z | memex-cloud |

Three consequences, each of which reverses a reading a triage pass would otherwise make.

1. **"It will just be reopened" stops being true for a superseded node.** The fold comment says it in
   so many words — *"Those nodes are superseded and will not fold, file or comment again."* The
   recurrence pressure a session declines to close against may already be gone, and the only place
   that is written is `content.status` / `content.supersededBy` on the node. The issue's own history
   cannot show it: the last fold comment looks exactly like every earlier one.
2. **The residue that held a ticket open may have MOVED to another ticket.** #3659 was held open on
   2026-09-17 for one operator item — `ClaimsDeepfield`, 82 of 83 declared nodes absent on
   memex-cloud, unhealed for three weeks. That shape is now its own fingerprint with its own,
   correctly-titled issue (**#4812**), still folding as of 09:09:51Z. Closing #3659 no longer buries
   it. So check every successor's `issueNumber` before concluding a ticket still owns its residue —
   and before concluding that closing it would lose something.
3. **Inheriting an issue and filing a new one are indistinguishable from GitHub.** Both arrive as an
   ordinary bot comment on some issue. The binding is written only on the node
   (`issueNumber`, `reporterFingerprint`, `foldedFrom`).

### The fingerprint's `namespace` is not the deployment its NEWEST samples came from

`content.namespace` is one value on a node whose samples accumulate for weeks, and a split can hand
a successor a namespace that no longer matches the sample you are reading. It is worse than stale:
**one fingerprint can span deployments while that field holds a single value.** `4ff70ec1124b1b3c`
carries `namespace: memex` and its pod list contains both `…-6cd5d8f887-2kcwk` (in
`Ops/Status/memex`'s roster, `3.0.0-ci.8968`) and `…-69956b6dbc-{v5r29,s246c}` (in
`Ops/Status/memex-cloud`'s, `3.0.0-ci.8969`). No single value can be right for that node. **Pod names do not
discriminate either:** on 2026-09-19 both portals ran a deployment called
`memex-portal-deployment` and both produced the replicaset hash `69956b6dbc` (same image, same pod
template) with different suffixes, so `…-69956b6dbc-gx6z6` and `…-69956b6dbc-v5r29` are not
necessarily the same cluster.

**The discriminator that works is a record version quoted in the log line.** #3659's 05:29:06Z
sample says `taken over Plugins/DefaultViews v48 (written 2026-09-19T00:24:55Z)`. Read the same hour
on both portals, `Plugins/DefaultViews` is **v31** (written 2026-09-18T21:31:10Z) on
memex.meshweaver.cloud and **v54** (2026-09-19T07:50:02Z) on memex.systemorph.com. memex-cloud never
held a v48, so that sample is the other portal's — and an ancestry argument about *memex-cloud's*
running image says nothing about it. The same trick works on any line that names a node version, a
module version or an `installedAtUtc`.

### The positive control for "no recurrence since the roll" lives on OTHER tickets

A close review at 2026-09-19T10:35Z could not establish *"not seen since the roll"* for twelve
issues, because no incident anywhere in the readable population had folded a sighting since
07:28:51Z — a reading indistinguishable from a stopped watcher, and correctly refused as evidence.
By 15:41Z the same instrument had folded sightings from memex-cloud's three CURRENT replicas (pods
started 08:34:53 / 08:39:58 / 08:39:59Z on `3.0.0-ci.8969`): `465a6677047a571b` at 08:57:30Z and
08:59:06Z from `…-gx6z6`, `c93238020fe2f0b5` at 09:09:51Z from `…-g6bbb`, `4ff70ec1124b1b3c` at
09:40:44Z.

🚨 **The control does not have to be the fingerprint you are judging.** Any fold naming a pod from
the current generation establishes that ingestion is alive for that generation, which is exactly
what converts a silence on your own fingerprint from "unusable" into evidence. Sort the readable
`Admin/_LogIncident` population by `lastModified` descending and read the newest nodes'
`content.lastSeen` and `content.samples[].pod` — a node rewritten without its `occurrences`
advancing is triage bookkeeping and is NOT such a control.

### The pod→image mapping the section above wants DOES exist, on the deployment record

*"Deciding WHICH image was running still needs the pod→image mapping, which the incident comments do
not carry"* — they do not, but `Ops/Status/<deployment>` does, whenever kube-state-metrics is
scraping the namespace (`notScraped: false`; it flips, #4218). Its `replicas[]` carries `pod`,
`image`, `generation`, `commit`, `startedAt` and `restarts` per replica, so a sample's pod name
resolves to an image and a start time in one read. Measured 2026-09-19T18:53:13Z for memex-cloud:
three replicas on `3.0.0-ci.8969` / `c25f86ae85b7…`, started 08:34:53 / 08:39:58 / 08:39:59Z.

That closes the teardown ambiguity in the direction the section above leaves open: a sample whose pod
STARTED before the sample is the incoming replica, not the outgoing one. #1246's 08:57:30Z and
08:59:06Z sightings are from `…-69956b6dbc-gx6z6`, started 08:39:58Z — eighteen minutes earlier — so
they are the NEW replica reporting, and the fault is on the image that roll delivered. 🚨 The same
read also warns you when the node's own `namespace` disagrees with the roster a pod appears in, which
is the mis-attribution above.

## How to read one, until both predicates exist

- A reopen means **"re-read the record"**. It does not mean a fix regressed
  ([#1122](https://github.com/Systemorph/MeshWeaver/issues/1122) says so, and this wave is the
  measurement behind it).
- **Read `Last seen:`, never the counter.** `Still occurring: 705 more since the last update` is a
  comparison of accumulated totals; it stays positive forever once an issue is closed while its
  record keeps history. Three of the 51 say `Last seen: 2026-09-17`; the rest say August or early
  September, with the same headline.
- **Compare it to three timestamps, in this order:** the close (was it even after?), today (is it
  recent?), and the image the reporting pod was running (could the fix have been in it?).
- 🚨 **And before closing one on quiet, ask what the fault needs in order to fire.** A teardown or
  boot fault is silent by construction while nothing rolls, so dormancy across a roll-free window is
  not evidence — see the section above for five issues closed that way and reopened within a day.
- A burn-down or "zero issues" reading across 2026-09-17T07:51Z is comparing two different
  populations. Say which side of the wave a count was taken on.
- **Read the incident NODE, not only the issue.** `content.status`, `content.supersededBy` and
  `content.issueNumber` on `Admin/_LogIncident/{fingerprint}` are the only place it is written
  whether this issue is still what the evidence lands on. A superseded node folds nothing further,
  and a successor may have carried the residue to a new ticket.
- **Name the deployment from a record version in the line**, never from the incident's `namespace`
  or from a pod name — two portals can share a replicaset hash.
- **Take the positive control off a DIFFERENT fingerprint.** "Not seen since the roll" needs one
  fold from a current pod somewhere in the readable population; without it the silence is
  indistinguishable from a stopped watcher.

## Appendix — the 51, one row each

A negative age means the newest occurrence is NEWER than the reopen: the fault carried on
after the bot reopened the issue.

### Live — seen 2026-09-17 (3)

| issue | closed at | `Last seen` the reopen cited | after the close by | newest occurrence on the issue | age of that at the reopen |
|---|---|---|---|---|---|
| #1549 | 2026-08-21 21:12:33 | 2026-09-06 22:39:32 | +16.1 d | 2026-09-17 11:11:00 | -0.1 d |
| #2299 | 2026-08-26 07:22:28 | 2026-09-02 19:26:24 | +7.5 d | 2026-09-17 11:28:58 | -0.2 d |
| #2765 | 2026-08-30 18:43:43 | 2026-09-08 03:29:51 | +8.4 d | 2026-09-17 14:25:47 | -0.3 d |

### Recently live — quiet 5–7 days at the reopen (7)

| issue | closed at | `Last seen` the reopen cited | after the close by | newest occurrence on the issue | age of that at the reopen |
|---|---|---|---|---|---|
| #3556 | 2026-09-08 05:46:47 | 2026-09-10 07:04:06 | +2.1 d | 2026-09-10 07:04:06 | 7.0 d |
| #2357 | 2026-08-29 08:24:29 | 2026-09-10 12:26:14 | +12.2 d | 2026-09-10 12:26:14 | 6.8 d |
| #2321 | 2026-08-30 06:55:28 | 2026-09-10 14:35:02 | +11.3 d | 2026-09-10 14:35:02 | 6.7 d |
| #1109 | 2026-08-10 12:19:23 | 2026-09-10 19:08:27 | +31.3 d | 2026-09-10 19:08:27 | 6.5 d |
| #2231 | 2026-08-25 19:25:54 | 2026-09-11 18:55:35 | +17.0 d | 2026-09-11 18:55:35 | 5.5 d |
| #1449 | 2026-08-13 21:20:28 | 2026-09-12 03:12:14 | +29.2 d | 2026-09-12 03:12:14 | 5.2 d |
| #1910 | 2026-08-21 00:18:50 | 2026-09-12 08:28:48 | +22.3 d | 2026-09-12 08:28:48 | 5.0 d |

### Quiet 8–37 days at the reopen (41)

| issue | closed at | `Last seen` the reopen cited | after the close by | newest occurrence on the issue | age of that at the reopen |
|---|---|---|---|---|---|
| #1112 | 2026-08-10 12:19:30 | 2026-08-10 22:28:20 | +0.4 d | 2026-08-10 22:28:20 | 37.4 d |
| #1135 | 2026-08-10 13:47:00 | 2026-08-10 22:28:57 | +0.4 d | 2026-08-10 22:28:57 | 37.4 d |
| #1175 | 2026-08-10 21:14:36 | 2026-08-11 07:06:33 | +0.4 d | 2026-08-11 07:06:33 | 37.0 d |
| #1127 | 2026-08-10 20:40:22 | 2026-08-13 16:23:01 | +2.8 d | 2026-08-13 16:23:01 | 34.6 d |
| #1541 | 2026-08-14 15:13:23 | 2026-08-17 03:30:19 | +2.5 d | 2026-08-17 03:30:19 | 31.2 d |
| #1310 | 2026-08-12 19:49:55 | 2026-08-23 20:13:56 | +11.0 d | 2026-08-23 20:13:56 | 24.5 d |
| #1142 | 2026-08-10 13:47:02 | 2026-08-24 07:44:21 | +13.7 d | 2026-08-24 07:44:21 | 24.0 d |
| #2152 | 2026-08-25 19:08:18 | 2026-08-25 20:10:27 | +0.0 d | 2026-08-25 20:10:27 | 22.5 d |
| #2182 | 2026-08-25 19:08:39 | 2026-08-25 20:36:24 | +0.1 d | 2026-08-25 20:36:24 | 22.5 d |
| #2171 | 2026-08-25 18:39:49 | 2026-08-25 22:13:09 | +0.1 d | 2026-08-25 22:13:09 | 22.4 d |
| #2233 | 2026-08-25 18:24:25 | 2026-08-26 06:58:49 | +0.5 d | 2026-08-26 06:58:49 | 22.0 d |
| #2153 | 2026-08-25 19:08:33 | 2026-08-26 10:06:46 | +0.6 d | 2026-08-26 10:06:46 | 21.9 d |
| #2444 | 2026-08-28 13:38:36 | 2026-08-28 20:24:59 | +0.3 d | 2026-08-28 20:24:59 | 19.5 d |
| #2381 | 2026-08-26 19:34:25 | 2026-08-29 23:30:08 | +3.2 d | 2026-08-29 23:30:08 | 18.3 d |
| #2679 | 2026-08-30 07:25:13 | 2026-08-30 09:59:10 | +0.1 d | 2026-08-30 09:59:10 | 17.9 d |
| #2358 | 2026-08-26 15:03:44 | 2026-08-30 11:55:23 | +3.9 d | 2026-08-30 11:55:23 | 17.8 d |
| #1300 | 2026-08-12 22:12:58 | 2026-08-30 15:02:37 | +17.7 d | 2026-08-30 15:02:37 | 17.7 d |
| #2633 | 2026-08-30 00:37:42 | 2026-09-01 08:36:17 | +2.3 d | 2026-09-01 08:36:17 | 16.0 d |
| #2322 | 2026-08-30 08:58:51 | 2026-09-02 03:12:15 | +2.8 d | 2026-09-02 03:12:15 | 15.2 d |
| #2884 | 2026-08-31 06:39:52 | 2026-09-02 03:12:45 | +1.9 d | 2026-09-02 03:12:45 | 15.2 d |
| #1203 | 2026-08-12 06:58:08 | 2026-09-02 08:46:16 | +21.1 d | 2026-09-02 08:46:16 | 15.0 d |
| #3051 | 2026-09-02 07:24:03 | 2026-09-02 11:17:43 | +0.2 d | 2026-09-02 11:17:43 | 14.9 d |
| #2876 | 2026-09-02 01:07:34 | 2026-09-02 14:39:36 | +0.6 d | 2026-09-02 14:39:36 | 14.7 d |
| #1284 | 2026-08-21 12:12:43 | 2026-09-03 07:30:08 | +12.8 d | 2026-09-03 07:30:08 | 14.0 d |
| #1547 | 2026-08-14 15:13:25 | 2026-09-03 07:27:47 | +19.7 d | 2026-09-03 07:27:47 | 14.0 d |
| #2307 | 2026-08-26 07:22:28 | 2026-09-03 08:10:17 | +8.0 d | 2026-09-03 08:10:17 | 14.0 d |
| #2228 | 2026-08-25 18:24:23 | 2026-09-03 16:09:15 | +8.9 d | 2026-09-03 16:09:15 | 13.7 d |
| #3126 | 2026-09-02 20:51:58 | 2026-09-03 14:13:23 | +0.7 d | 2026-09-03 14:13:23 | 13.7 d |
| #1540 | 2026-08-14 15:13:22 | 2026-09-03 17:21:25 | +20.1 d | 2026-09-03 17:21:25 | 13.6 d |
| #1548 | 2026-08-14 15:13:22 | 2026-09-03 17:21:23 | +20.1 d | 2026-09-03 17:21:23 | 13.6 d |
| #1897 | 2026-08-19 20:26:47 | 2026-09-03 17:21:23 | +14.9 d | 2026-09-03 17:21:23 | 13.6 d |
| #1899 | 2026-08-19 20:26:48 | 2026-09-03 16:31:06 | +14.8 d | 2026-09-03 16:31:06 | 13.6 d |
| #2132 | 2026-08-25 18:05:12 | 2026-09-03 17:12:34 | +9.0 d | 2026-09-03 17:12:34 | 13.6 d |
| #2480 | 2026-08-28 11:34:25 | 2026-09-03 17:51:10 | +6.3 d | 2026-09-03 17:51:10 | 13.6 d |
| #1200 | 2026-08-12 10:15:12 | 2026-09-04 13:33:26 | +23.1 d | 2026-09-04 13:33:26 | 12.8 d |
| #1253 | 2026-08-12 12:22:46 | 2026-09-06 05:54:48 | +24.7 d | 2026-09-06 05:54:48 | 11.1 d |
| #3235 | 2026-09-04 08:19:19 | 2026-09-06 16:16:53 | +2.3 d | 2026-09-06 16:16:53 | 10.6 d |
| #3466 | 2026-09-06 22:10:21 | 2026-09-06 22:25:37 | +0.0 d | 2026-09-06 22:25:37 | 10.4 d |
| #2833 | 2026-09-01 20:02:08 | 2026-09-08 09:39:42 | +6.6 d | 2026-09-08 09:39:42 | 8.9 d |
| #3647 | 2026-09-08 21:04:20 | 2026-09-09 10:06:06 | +0.5 d | 2026-09-09 10:06:06 | 7.9 d |
| #3516 | 2026-09-08 07:38:47 | 2026-09-10 06:10:52 | +1.9 d | 2026-09-10 06:10:52 | 7.1 d |

## Method

Read-only over the GitHub REST API: `/issues?state=open` (paginated, `pull_request` filtered out),
then per issue `/issues/{n}/events` for the close/reopen pair and `/issues/{n}/comments` for the
bot's own `Last seen:`. `Last seen` is taken from the reopen comment for the first axis and from the
newest comment of any kind for the second, so a fault that resumed after the reopen is counted as
live. Running images read from `/api/version` on both portals. No issue in the wave was closed,
reopened, commented on or labelled.

## See also

- [Reopening on Image Provenance](../ReopeningOnImageProvenance) — the third predicate this
  page's 15-of-51 finding points at, designed: the evidence chain, the one link still missing,
  and the fail-safe for an image that cannot be resolved

- [Operating from the portal, not the cluster](../OperatingFromThePortal) — where the pod and image
  facts come from
- [Router Traffic Detection](../RouterTrafficDetection) — a detector that had the opposite problem:
  it named the rule broken without naming who broke it
