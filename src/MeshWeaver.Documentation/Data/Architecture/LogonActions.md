---
Name: Logon Actions
Category: Documentation
Description: Work that runs for a user when they log on — once per user, ever, or on every logon. The per-user sibling of INodePostCreationHandler, and the way to give an EXISTING user something new without a SQL backfill.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M15 3h4a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2h-4"/><path d="m10 17 5-5-5-5"/><path d="M15 12H3"/></svg>
---

# Logon Actions

A **logon action** is work the platform runs *for one user, at logon, as that user*. It declares
whether it runs **once per user, ever** or **on every logon**, and the framework guarantees the
first of those is idempotent across restarts, replicas, tabs and races.

> **The one-line rule.** If you are about to write a SQL backfill that loops over every partition
> schema patching `mesh_nodes`, you want a logon action instead.

---

## Why the framework exists

The platform could already give a **new** user things: `INodePostCreationHandler` fires when the
`User` node is created and seeds their access grant, their chat composer, their AI settings. It
fires exactly once, at account creation, and it can **never fire again**.

So every "existing users need this too" became a hand-written database migration:
`V29_PinDocsForExistingUsers` walks every partition schema and `UPDATE`s `content->'pinnedPaths'`;
`V33_SeedChatInputForExistingUsers` does the same for a missing satellite. Those migrations work,
and they are the wrong tool three times over:

| The migration does | Why that is wrong |
|---|---|
| Raw `UPDATE` against `mesh_nodes` | Bypasses the workspace cache — a running portal keeps serving the old value until it restarts |
| Runs once per **deployment** | There is no per-user record, so "did this user get it?" is unanswerable |
| Needs a new `IMigration` class, a registry line and a `DbVersion` bump | Only someone shipping a database version can express one — an admin cannot |

A logon action is the per-user sibling of the post-creation handler: same idea, different moment.

---

## The two modes, and how to choose

```
RunOnce     ── a migration. Runs at most once per user, ever.
               Ledger: User.CompletedLogonActions[actionId]
EveryLogon  ── a repair. Runs each logon; decides for itself, cheaply, that there is nothing to do.
               No ledger — the ledger is what would stop it.
```

**Choose `RunOnce` when the work is a one-time change of state the user then owns.** Replacing
someone's pinned items is the archetype: after it runs, those pins are *theirs*. Running it again
next month would clobber whatever they have curated since, which is precisely why it must never run
again — and why the ledger is durable rather than a process-lifetime flag.

**Choose `EveryLogon` when new work can arrive later.** The shipped example is app-icon adoption: a
run-once action would repair whatever apps the user had installed the day it first ran, record
itself as done, and leave every app installed afterwards on the generic placeholder forever. The
cost of running every time is bounded by a **cheap check**, not by a ledger:

```csharp
// AppIconAdoptionLogonAction.Run — the check that makes EveryLogon affordable
var needy = records.Where(AppIconAdoption.NeedsIcon)
    .Where(record => AppIconAdoption.TargetOf(record) is not null)
    .ToArray();
if (needy.Length == 0)
    return Observable.Return(LogonActionOutcome.Nothing);   // one query, zero writes
```

That shape is the requirement, not a nicety: an every-logon action that cannot cheaply answer
"nothing to do" is a per-logon storm.

---

## The ledger, and why idempotency is safe under concurrency

The run-once ledger is a single field on the user's own profile:

```csharp
// User.cs
public IReadOnlyDictionary<string, DateTimeOffset> CompletedLogonActions { get; init; }
```

Three properties fall out of putting it there, and all three are load-bearing.

**It is durable.** It is part of the profile, persisted with it, replicated with it. A process
restart, a pod roll, a second silo — none of them can lose it, because none of them own it.

**It is a dictionary, not a list.** A cross-hub `stream.Update` ships an RFC 7396 merge patch, under
which a dict `SetItem` is merge-safe and a list append is not — the patch for a list is the *whole
list*, so two writers each appending a different id merge to whichever landed last, silently
dropping the other. See [Request via Stream Update](/Doc/Architecture/RequestViaStreamUpdate) →
"Cross-Hub Patch Semantics".

**The effect and the record land in ONE patch.** This is the guarantee. `LogonActionRunner.Commit`
applies the action's profile change *and* writes the ledger key in a single `stream.Update`:

```csharp
// LogonActionRunner.Commit
return access.RunAs(context.Identity, () => hub.GetWorkspace()
    .GetMeshNodeStream(context.UserPath)
    .Update(node =>
    {
        var user = node.ContentAs<User>(options, logger);
        if (node.Content is not null && user is null)
            return node;                      // bad data is left ALONE, never replaced
        user ??= new User();
        if (once && user.CompletedLogonActions.ContainsKey(action.Id))
            return node;                      // ← the guard

        var updated = outcome.ProfileChange?.Invoke(user) ?? user;
        if (once)
            updated = updated with
            {
                CompletedLogonActions = updated.CompletedLogonActions
                    .ToImmutableDictionary().SetItem(action.Id, ranAt),
            };
        return ReferenceEquals(updated, user) ? node : node with { Content = updated };
    }));
```

Split across two writes there would be a window where the change landed and the record did not (a
restart re-applies the migration over the user's later edits) or the record landed and the change
did not (the migration is skipped forever). One patch has neither.

### The race

Two tabs, or two replicas, log the same user on at the same instant. Both runs read an empty ledger
in the cheap `IsPending` pre-check and both reach `Commit`. What separates them:

1. The owning hub serialises the two patches through its single-threaded action block.
2. The first one applies.
3. The second carries base values the owner has since moved past, so the owner **refuses it as
   stale** and answers `Conflict`.
4. `stream.Update` rebases: it re-reads fresher state and **re-runs the lambda**.
5. The re-run sees `CompletedLogonActions.ContainsKey(action.Id)` and returns the node untouched.

This is why the ledger check is *inside* the lambda and not only in `IsPending` — the outer check is
the cheap fast path, the inner one is the guard. It is the same "claim before you mutate" shape as
`ThreadSubmissionServer`'s `Idle → StartingExecution` flip, with the ledger key as the claim.

> **Where the guard is honest about its limits.** The profile lambda may be invoked more than once
> (that is what a rebase does), so it **must be pure** — the framework relies on re-running it, not
> on running it once. An action whose real effect is on *other* nodes cannot be part of this atomic
> patch; the runner writes its ledger entry after the effect, which makes it at-least-once. Such an
> action must be idempotent in its own right. `AppIconAdoptionLogonAction` is exactly that case, and
> it is `EveryLogon` precisely so no ledger is involved.

---

## Identity: a logon action runs as the USER

A logon action acts on the user's own nodes — their pins, their app records. It therefore runs under
**that user's** identity, never `system-security` and never a hub address. At logon a real identity
exists, so it is threaded through rather than impersonated:

```csharp
// LogonActionRunner.RunFor
return access.RunAs(identity, () => resolved.SelectMany(...));
```

🚨 **Never `Observable.Using(() => access.ImpersonateAsSystem(), _ => work)`.** Impersonation is an
`AsyncLocal` store/restore pair. Rx runs `Using`'s resource factory on the **subscribing** thread and
disposes the resource when the inner observable **terminates** — for a cross-hub write, the owning
hub's response thread. The two halves land on different threads, and the subscriber is left latched
as the impersonated identity. The sanctioned forms live in `ImpersonationScopeExtensions` —
`RunAsSystem`, `RunAsHub`, and the two `RunAs` overloads — which open the scope at Subscribe and
close it on the way back out of that same Subscribe. A ratchet-guard test fails the build at any new
`Observable.Using` impersonation site.

Because `Concat` subscribes a later action on whichever thread the previous one completed on, the
identity is re-established **at the write** as well as around the run. A write with no identity
fails *closed* in the post pipeline — see [Access Context
Propagation](/Doc/Architecture/AccessContextPropagation).

**The one deliberate exception** is reading the declarations. They live in the `Admin` partition,
where an ordinary user has no standing grant, and an RLS-filtered read comes back **empty rather
than denied** — so reading them as the user would silently disable the whole framework for exactly
the users it exists to serve. `ReadDeclaredActions` therefore uses `RunAsSystem`, and it is a read of
platform configuration, never a write and never a touch of user data.

---

## How to contribute one

### As DATA — the deployment-specific route

This is what you want for anything naming content only some portals carry. Create a `LogonAction`
node at `Admin/_LogonAction/{id}`. No code, no image roll; the id is the ledger key.

```json
{
  "id": "docs-to-courses",
  "path": "Admin/_LogonAction/docs-to-courses",
  "nodeType": "LogonAction",
  "name": "Swap the documentation pins for the courses",
  "state": "Active",
  "content": {
    "$type": "LogonAction",
    "description": "Existing users pinned the four doc sections; pin the courses instead.",
    "mode": "RunOnce",
    "order": 0,
    "enabled": true,
    "unpinPaths": ["Doc/Architecture", "Doc/DataMesh", "Doc/GUI", "Doc/AI"],
    "pinPaths": ["AgenticPrimer", "AgenticEngineering", "AgenticBusiness"]
  }
}
```

🚨 **Zero action nodes ship with the framework.** A portal that declares none runs none. That is the
whole reason the pin targets are data: `memex.meshweaver.cloud` carries the agentic-engineering
courses and `systemorph.com` does not, and a hard-coded course path would write a dangling pin onto
every user's home on every portal that lacks it.

> 🚨 **A node's TYPE is not a path prefix, and this one has already been written wrong.** The three
> courses above are top-level nodes whose `nodeType` happens to be `Store/Plugin` — so
> `Store/AgenticPrimer` looks plausible and **does not resolve**. On each of them
> `path == id == mainNode == "AgenticPrimer"`. Write a path by reading it back off the node
> (`search 'path:AgenticPrimer select:path'`), never by composing it from the type.
>
> The failure is quiet by design: the existence check skips the unresolvable path, the migration
> pins nothing, records itself as done, and **never runs again**. Nothing errors. Confirm the paths
> before creating the node, because a run-once action gets one attempt per user.

**Pins are existence-checked; unpins are not.** The asymmetry is deliberate. Unpinning a path that
has gone away is exactly right — that is often *why* it is being unpinned. Pinning one that does not
exist is a dead tile. So a deployment missing the targets pins **nothing**, records the action as
done, and carries on. Set `requireTargetsExist: false` only for a path that is legitimately
resolvable but not visible to the index at logon time.

### As CODE — the platform-behaviour route

For behaviour that should run on **every** portal and needs no configuration. Implement
`ILogonAction` and register it as a singleton:

```csharp
public sealed class MyLogonAction : ILogonAction
{
    public string Id => "platform.my-action";          // stable — changing it re-runs for everyone
    public LogonActionMode Mode => LogonActionMode.RunOnce;
    public int Order => 10;

    public IObservable<LogonActionOutcome> Run(LogonActionContext context) =>
        SomethingReactive(context)
            .Select(_ => LogonActionOutcome.Profile(user => user with { /* … */ }));
}

builder.AddLogonAction<MeshBuilder, MyLogonAction>();
```

`Run` emits one `LogonActionOutcome`. Side work on other nodes happens inside `Run`; what the
outcome carries is the **pure** profile change the runner commits together with the ledger entry.
`LogonActionOutcome.Nothing` means "ran, changed nothing about the profile" — and for a run-once
action that still counts as having run.

---

## Where it fires

`UserContextMiddleware` — the one place cookie/OAuth **and** Bearer both land with a fully-resolved
mesh identity. It already carries a five-minute, session-shaped dedup gate for login tracking, and
the logon actions ride the **same** gate: this middleware runs on every HTTP request, so an ungated
call would re-run the platform's actions per page load, per API call, per SSE frame.

```csharp
if (TrackLogin(userContext, hub))
    RunLogonActions(userContext, hub);
```

The Blazor circuit handler was the obvious alternative and is worse: it fires per *tab* and misses
the API surface entirely.

The run is fire-and-forget and bounded on the runner's side — its own 30-second budget, its own
`Catch`. **Authentication never waits on a migration**, and a mesh that cannot answer costs a missed
migration rather than a failed login. Anonymous callers run nothing:
`WellKnownUsers.IsAuthenticated` is the predicate, because "Anonymous" is a perfectly non-empty
string and running a migration for it would write a `User` node for a visitor.

---

## Verifying that it ran

The ledger is on the profile, so the question is answerable with a plain read:

```
get @{user}                       → content.completedLogonActions
```

- **key present** → it ran, and the value is when.
- **key absent, action declared** → it has not run yet; it will on their next logon.
- **key absent after a logon** → the action *failed* and was deliberately not recorded, so it is
  retried next time. Look for `Logon action {Action} failed for {User}` at Warning.

A failing action is logged, skipped, left unrecorded, and does **not** stop the others in the run.

---

## Anti-patterns

- **A new `IMigration` that loops partition schemas patching user content.** That is this page's
  reason for existing. Database migrations remain right for *schema* — tables, indexes, triggers.
- **A `RunOnce` action whose effect is on nodes other than the profile, without its own idempotency.**
  The atomic effect-plus-ledger patch only covers the profile; anything else is at-least-once.
- **An `EveryLogon` action with no cheap "nothing to do" check.** That is a per-logon storm.
- **`Observable.Using(access.ImpersonateAsSystem, …)`** anywhere in the action. Use `RunAs*`.
- **Hard-coding a content path in a code-declared action.** Code ships everywhere; the path may not
  exist there. Declare it as data.
- **Reusing an id for different work.** The id is the ledger key: users who ran the old work will
  never get the new.

---

## See also

- [Request via Stream Update](/Doc/Architecture/RequestViaStreamUpdate) — the mutation API the runner
  uses, and the merge-patch semantics the dictionary ledger depends on
- [Activity Control Plane](/Doc/Architecture/ActivityControlPlane) — the `Status`/`RequestedStatus`
  pattern, and the claim-before-mutate shape the ledger guard mirrors
- [Access Context Propagation](/Doc/Architecture/AccessContextPropagation) — why a write with no
  identity fails closed, and how identity survives reactive hops
- [Asynchronous Calls](/Doc/Architecture/AsynchronousCalls) — the no-`async` rule every action obeys
- [No Static State](/Doc/Architecture/NoStaticState) — why the runner is a mesh-scoped singleton and
  the ledger is not a process cache
- [Apps Home](/Doc/Architecture/AppsHome) — the installed-app records the icon-adoption action
  repairs
