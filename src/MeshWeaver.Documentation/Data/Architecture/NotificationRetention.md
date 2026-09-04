---
Name: Notification Retention
Category: Architecture
Description: Why notifications expire and nothing else on the platform does, the policy that decides it (age to select, a per-run cap to bound), and why the pass is a logon action rather than a startup Job, a timer, or an operations script.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M6 8a6 6 0 0 1 12 0c0 4 1.5 5.5 1.5 5.5h-15S6 12 6 8z"/><path d="M10.5 17.5a1.8 1.8 0 0 0 3 0"/><path d="m3 3 18 18"/></svg>
---

# Notification Retention

**Nothing on this platform expired, and notifications were the reason it had to start.**
`nodeType:Notification` was measured at **4 476 rows** on memex-cloud on 2026-09-03 — every
notification ever raised, versioned, kept, since the platform's first day. There was no retention
pass of any kind, for notifications or for anything else
([#3250](https://github.com/Systemorph/MeshWeaver/issues/3250)).

Two things made notifications the right place to start, and both are properties of the row rather
than of its count:

1. **A notification is the most perishable row we store.** It says something happened; it is read
   within minutes or not at all; nothing links to it; and its subject —
   `Notification.TargetNodePath` — outlives it and carries the durable record. Deleting an expired
   notification loses a pointer, never the thing pointed at. Compare an activity log (evidence of
   what ran) or a thread message (the artefact itself).
2. **[Addressed Notifications](/Doc/Architecture/AddressedNotifications) §6 depends on this
   existing.** That ruling left the pre-addressing rows where they were — neither migrated nor
   deleted — on the explicit understanding that the anchored bell stops reading them and *a general
   retention pass reclaims them later*. Without a retention pass, "they age out" means "never", and
   §6 becomes a decision to keep every legacy row forever.

> **Status: SHIPPED.** §1 is the policy, §2 the trigger and the arguments against the three
> alternatives, §3 how the pass stays bounded, §4 what it deliberately does not do.

## 1. The policy: age selects, a cap bounds

`NotificationRetention` (`src/MeshWeaver.Graph/NotificationRetention.cs`) is the whole rule — a
record with no mesh, no hub and no clock of its own, so the decision can be tested without any of
them.

| Field | Default | Config key |
|---|---|---|
| `Enabled` | `true` | `Notifications:Retention:Enabled` |
| `MaxAge` | 90 days | `Notifications:Retention:MaxAge` |
| `MaxDeletionsPerRun` | 200 | `Notifications:Retention:MaxDeletionsPerRun` |

```csharp
// The single definition of "expired". Everything uncertain resolves to KEEPING the row.
public bool IsExpired(MeshNode? node, DateTimeOffset now)
{
    if (!Enabled || node is null) return false;
    if (!string.Equals(node.NodeType, NotificationNodeType.NodeType, StringComparison.OrdinalIgnoreCase))
        return false;
    if (node.LastModified == default) return false;
    return node.LastModified <= now - MaxAge;
}
```

**Age, not "keep the newest M".** Both shapes were on the table and age wins on idempotence: the
cutoff is a pure function of one instant, so two runs against the same clock select the same set,
and the second finds it already gone. A "keep the newest M per addressee" rule computed over a
*paged* result selects a different set depending on which page it was handed — and the pass has to
be paged, because that is what bounds it. Age is also the rule that actually retires the legacy
tail: every pre-addressing row is old by construction, so the first sweep that reaches one takes it.

**`LastModified`, not `Notification.CreatedAt`, and the reason is that the ORDER and the PREDICATE
must be the same quantity.** The sweep bounds itself by asking the index for the oldest capped
window; if it then judged those rows on a different timestamp, the page it was handed would not be
the page it wants, and a backlog could stop draining. Only `LastModified` can be both — it is a real
column every backend orders on, while `CreatedAt` lives inside the content JSON and the in-memory
adapter's sort silently falls back to `Name` for it. It also reads better: *untouched for 90 days*
keeps a notification you opened yesterday, whatever the event's own date.

**Armed by default**, which is the opposite of `AssemblyCacheRetention` — and the asymmetry is the
point. That pass ships report-only because a wrong answer deletes bytes a running portal is
executing. Here a wrong answer deletes a three-month-old pointer to a node that still exists, while
a retention pass that ships disarmed reproduces exactly the defect it was written for: nobody arms a
knob they have never heard of. A deployment that wants it off says so.

**The window is clamped to a 7-day floor — at the configuration edge, and nowhere else.**
`Notifications__Retention__MaxAge: "0.00:00:00"` is a typo a chart consumer can make, and read
literally it would empty every bell on the platform in one pass. `IsExpired` itself applies `MaxAge`
exactly as given: configuration is untyped data a typo reaches, a directly-constructed policy is
code a compiler and a reviewer reach, and a predicate that silently substitutes a different window
than its own field states is a worse thing to own than the typo it guards.

## 2. The trigger: a logon action

`NotificationRetentionLogonAction` (`src/MeshWeaver.Graph/Logon/NotificationRetentionLogonAction.cs`)
is an `EveryLogon` [logon action](/Doc/Architecture/LogonActions). At sign-in it sweeps the
partitions the signing-in person is the *reader* of — their own, plus `Admin` when
`hub.IsGlobalAdmin(...)` says so — under **that person's own identity**, with no impersonation
anywhere in it.

Three alternatives were considered and each fails on something this shape gets for free.

**A startup `Job`** — the shape a database migration uses — can stop a portal from serving
(`DbVersionGate` refuses to start a portal whose schema is behind). Reclaiming three-month-old
notifications must never be able to do that, and #3250 states the constraint first for that reason.

**A process-wide timer** has no reader to scope it to. It would have to enumerate partitions and
sweep them as `System` — and there is no partition enumeration API on this platform by design
(`IPartitionStorageProvider.PartitionExists` is a point probe; only the storage tree can be walked).
So it becomes a cross-partition pass over 201 schemas, run by nobody's identity: the unbounded shape
the ticket forbids. It is also the shape `RegistryUpdateReconciler` argues against in general —
*a timer answers "how stale am I willing to be", which is a question nobody asked*.

**A Code-MeshNode operation** (form-bound inputs + `RequestedStatus = Running` + live progress) is
the house pattern for work a *person* runs, and it would be the right answer for a one-off audited
purge of a named partition. It is the wrong answer here for one reason: **a tail that only shrinks
when somebody remembers to press Run does not shrink**, and "it ages out" turning into "never" is the
entire defect. Retention has no inputs a person supplies, no progress worth watching and no output
anyone reads.

A logon action, by contrast, already *is* one person, one partition, one identity. It runs off the
authentication path — `UserContextMiddleware` subscribes it and returns — inside
`LogonActionRunner`'s 30-second budget and its catch, so the worst case is a missed sweep, never a
failed login. And because it runs as the person, the only rows it can reach are ones that person
could delete by hand.

**`EveryLogon`, not `RunOnce`.** Notifications keep arriving and keep expiring, so a ledger entry
saying "retention has run for this user" would be false the next day. The cost of running every time
is bounded by a *check*, not by a ledger — the same argument `AppIconAdoptionLogonAction` makes: in
the steady state the pass issues one capped, partition-anchored query per partition and deletes
nothing, because nothing is old enough.

### Deletion goes through the framework

```csharp
meshService.DeleteNode(path)   // routes a DeleteNodeRequest under the caller's identity
```

🚨 **Never a raw `psql DELETE`.** That bypasses the workspace cache, and a portal would keep serving
rows that are no longer in the database ([Postgres Schema
Architecture](/Doc/Architecture/PostgresSchemaArchitecture)). A failure is handled per *row*, not
per run: an already-deleted node — two devices signing in at once, the second run's window still
naming rows the first removed — answers NodeNotFound, and from here that is what idempotence looks
like.

## 3. What makes it bounded

Four independent bounds, and the first is the one that matters because it is structural rather than
a limit somebody remembered to write.

**Nothing enumerates partitions.** The set of partitions a portal sweeps is the set of people who
signed in, one person at a time, each in a separate run. There is no code path on which this becomes
one statement over 201 schemas, whatever the policy is set to.

**One anchored, capped query per partition.** `NotificationService.RetentionQuery` declares it
beside `BellQuery`, because it is the same layout read a third way and the two must move together:

```text
path:{partition} scope:descendants nodeType:Notification sort:LastModified-asc limit:{cap}
```

* `path:` is what the Postgres router pins on, so this is a single-schema read even though it spans
  a whole partition;
* `nodeType:` keeps it on the notifications satellite table;
* `sort:LastModified-asc` puts the *oldest* rows in the window, so a backlog drains monotonically
  instead of the cap re-reading the same young page;
* `limit:` is the bound — a run can never ask for more rows than it is allowed to delete.

🚨 This is one of the few reads that must **not** carry `limit:all`. The truncation defect that rule
exists for (#1216, #1326) bites a pass that must see every item *once*; retention re-runs, and its
window is ordered ascending, so the rows that fall off it are the newest — exactly the ones that are
not expired yet. Ordered descending it would be the #1326 shape, self-reinforcing and invisible.

**Sequential, never `Merge`.** Partitions run through `Concat`, and within a partition the deletions
do too. Retention is background work on a login path: it should cost the mesh a trickle, never a
burst.

**The query bounds the work; the policy decides the deletion.** Two deliberate steps. If a backend
ever ignored `sort:LastModified-asc`, the window would be an arbitrary capped page — the sweep would
then remove fewer rows per run and take longer to drain, which is a liveness cost. It could never
remove a row the policy has not called expired, which would be a correctness cost.

## 4. What it deliberately does not do

**It does not exempt unread notifications.** A notification nobody opened in ninety days is the
definition of noise, and its subject is still there to be found.

**It does not reach partitions nobody signs into.** Spaces, plugin partitions and the `Doc` tree hold
some pre-addressing rows, and no logon will ever sweep them. That is a stated limit, not an
oversight: the alternative is the cross-partition pass §2 rules out. Those rows are already paid
for, nothing reads them, and a one-off purge of a named partition is exactly the audited operation a
Code MeshNode template is for — which is why that pattern is documented above as rejected *for the
recurring pass*, not as rejected outright.

**It is not a fix for the growth RATE.** That was
[#3213](https://github.com/Systemorph/MeshWeaver/issues/3213) — an update reminder is a state told
once per candidate, not an event told once per poll — which removed the dominant producer (124 of
the newest 200 rows). This pass is about the tail already there and the tail every future year adds.

**It has no user-visible strings**, so there is nothing to localize. If a future change surfaces
retention in the UI — an admin tab showing the window, a "cleared N notifications" toast — every
string in it needs a key in both `strings.en.json` and `strings.de.json`
([Localization](/Doc/Architecture/Localization)).

## Deployment

The chart renders all three keys explicitly, with the code defaults repeated as Helm `default`s so
an un-set key and an un-rendered key mean the same thing:

```yaml
config:
  memex_portal:
    Notifications__Retention__Enabled: "true"
    Notifications__Retention__MaxAge: "90.00:00:00"
    Notifications__Retention__MaxDeletionsPerRun: "200"
```

🚨 **The Helm `default`s are not what arms the pass — the code is.** `FromConfiguration` starts from
`NotificationRetention.Default` and overrides a field only when its key *parses*, so an absent or
empty key changes nothing: a values file predating these keys renders `""` for all three and the
portal still runs 90 days / 200 per run, armed. What the `default`s buy is that the **rendered
ConfigMap states the policy an operator is actually running** instead of three empty strings, and
that it can never disagree with the code. Turning retention off is an explicit `"false"`, never an
omission.
