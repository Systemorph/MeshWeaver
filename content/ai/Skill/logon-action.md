---
nodeType: Skill
name: /logon-action
description: "Make something happen for every user the next time they log on — once per user ever (a migration: re-pin their home, seed a record, flip a preference) or on every logon (a repair that must keep catching new work). Covers the once-vs-every decision, the copy-paste declaration for both the data route and the code route, the identity rule (runs as the USER; RunAs*, never Observable.Using(ImpersonateAsSystem)), how to verify it actually ran for a given user, and how to keep it deployment-specific so a portal that lacks the target nodes is a clean no-op instead of a dangling pin."
icon: ArrowEnter
category: Skills
order: 13
---

You need something to happen **for each user, at logon** — most often because an existing user is
missing something a new user gets. Do **not** write a database migration that loops partition schemas
patching `mesh_nodes`. Declare a **logon action**.

Full treatment: [Logon Actions](/Doc/Architecture/LogonActions). This page is the operational one.

# 1. Decide the mode first

| | `RunOnce` | `EveryLogon` |
|---|---|---|
| Runs | at most once per user, ever | every logon |
| Ledger | `User.CompletedLogonActions[actionId]` | none |
| Use for | a one-time change the user then OWNS | a repair that must keep catching new work |
| Example | swap their pinned items | adopt icons for apps installed since |

**The test:** *can new work arrive after the first run?*

- **No** → `RunOnce`. Re-running would clobber whatever the user has curated since. That is the whole
  reason the ledger is durable rather than a process flag.
- **Yes** → `EveryLogon`, and it **must** carry a cheap "nothing to do" check. One query, zero writes
  in the steady state. An every-logon action without that check is a per-logon storm.

# 2. Declare it — pick the route by whether every portal should have it

## Data route (deployment-specific — the default for anything naming content)

Create a node at `Admin/_LogonAction/{id}`. No code, no image roll. **The node id is the ledger key.**

```
create @Admin/_LogonAction/docs-to-courses
{
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

`enabled: false` parks it without deleting it and **keeps the ledger**, so re-enabling does not re-run
it for users who already had it.

## Code route (platform behaviour, every portal, no configuration)

```csharp
public sealed class MyLogonAction : ILogonAction
{
    public string Id => "platform.my-action";      // stable — changing it re-runs for EVERYONE
    public LogonActionMode Mode => LogonActionMode.RunOnce;
    public int Order => 10;

    public IObservable<LogonActionOutcome> Run(LogonActionContext context) =>
        SomethingReactive(context)                 // side work on other nodes goes HERE
            .Select(_ => LogonActionOutcome.Profile(user => user with { /* pure profile change */ }));
}

builder.AddLogonAction<MeshBuilder, MyLogonAction>();
```

The returned `ProfileChange` must be **pure** — the framework re-runs it when the owning hub rebases a
stale patch. `LogonActionOutcome.Nothing` means "ran, changed nothing", and for a run-once action that
still counts as having run.

# 3. Keep it deployment-specific

🚨 **Never hard-code a content path in a code-declared action.** Code ships to every portal; the path
may not exist there. `memex.meshweaver.cloud` has the agentic-engineering courses and
`systemorph.com` does not — a shipped course path writes a dead tile onto every user's home on the
portal that lacks it.

The data route handles this for you: `pinPaths` are **existence-checked** before they are written, so
a portal missing the targets pins nothing, records the action as done, and carries on. `unpinPaths`
are deliberately **not** checked — unpinning a path that has gone away is usually the point. Set
`requireTargetsExist: false` only for a path that resolves but is not query-visible at logon.

If you write a code action that touches named content, do the same existence check yourself.

# 4. Identity — it runs as the USER

A logon action touches the user's own nodes, so it runs under **their** identity. At logon a real
identity exists; thread it through rather than impersonating.

```csharp
// ✅ the sanctioned scope (ImpersonationScopeExtensions)
access.RunAs(identity, () => work)

// ❌ BANNED — a ratchet-guard test fails the build at any new site
Observable.Using(() => access.ImpersonateAsSystem(), _ => work)
```

`Observable.Using` opens the `AsyncLocal` scope on the **subscribing** thread and disposes it when the
inner observable **terminates** — for a cross-hub write, the owning hub's response thread. The two
halves land on different threads and the subscriber stays latched as the impersonated identity.

The only sanctioned System use in this framework is the runner's own read of the declarations from the
`Admin` partition (an RLS-filtered read there returns **empty, not denied**, which would silently
disable the framework). Everything an action *does* runs as the user.

# 5. Verify it ran

```
get @{user}      → content.completedLogonActions
```

| What you see | What it means |
|---|---|
| key present, with a timestamp | it ran, then |
| key absent, action declared | not yet — it runs on their next logon |
| key absent **after** a logon | the action FAILED and was deliberately not recorded, so it retries |

For the failure case, look for `Logon action {Action} failed for {User}` at Warning. A failing action
is logged, skipped, left unrecorded, and does **not** stop the others in the run.

An `EveryLogon` action never appears in the ledger — that is the point. Verify it by its effect.

# 6. Where it fires, and what that costs

`UserContextMiddleware` — the one place cookie/OAuth and Bearer both land with a resolved mesh
identity — behind the same five-minute session dedup as login tracking. Fire-and-forget, with the
runner's own 30-second budget: **authentication never waits on a migration**. Anonymous callers run
nothing.

# Anti-patterns

- A new `IMigration` looping partition schemas to patch user content. Migrations are for **schema**.
- A `RunOnce` action whose effect is on nodes *other* than the profile without its own idempotency —
  the atomic effect-plus-ledger patch only covers the profile; anything else is at-least-once.
- An `EveryLogon` action with no cheap "nothing to do" check.
- Reusing an id for different work: users who ran the old work never get the new.
- `async` / `await` / `Task<T>` anywhere in the action — compose `IObservable<T>` and let the runner
  subscribe ([Asynchronous Calls](/Doc/Architecture/AsynchronousCalls)).
