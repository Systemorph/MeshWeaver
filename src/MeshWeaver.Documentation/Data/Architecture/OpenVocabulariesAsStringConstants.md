---
Name: Open Vocabularies Are String Constants
Category: Architecture
Description: "Why a persisted, serialised or module-extended vocabulary is a static class of const string named exactly as the enum would have been — never a C# enum — what that buys, how to migrate one in place, and the narrow cases where an enum is still right."
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 7V4h16v3"/><path d="M9 20h6"/><path d="M12 4v16"/></svg>
---

# Open Vocabularies Are String Constants

> **The rule in one sentence:** a set of named values that is **persisted, serialised, or extended by
> a module** is a `static class` of `const string`, **named exactly as the enum would have been** —
> never a C# `enum` — and it **stays open for anyone else to extend with their own constants**.
> Policy `open-vocabulary-string-constants` ([Policy Not Prose](/Doc/Architecture/PolicyNotProse)).

```csharp
// ✅ the shape
public static class TransportKind
{
    public const string InApp    = "InApp";
    public const string Email    = "Email";
    public const string Teams    = "Teams";
    public const string WhatsApp = "WhatsApp";
}

public record Participant
{
    /// <summary>A <see cref="TransportKind"/> constant.</summary>
    public string Transport { get; init; } = TransportKind.InApp;
}
```

The value is the member name, spelled identically. `TransportKind.Email` reads at a call site
exactly as the enum member did, which is what makes converting an existing enum a drop-in.

## 🚨 The vocabulary stays OPEN — that is the whole point

**The platform's constants class is a starting set, never the permitted set.** Any other party — a
module, a plugin, a satellite repo, a customer deployment, another agent — declares **its own**
`static class` of `const string` and uses those values in the same field. Nothing registers them,
nothing validates against an allow-list, and the platform neither knows nor needs to know.

```csharp
// In someone else's module. No coordination with the platform, no PR to core.
public static class AcmeTransportKind
{
    public const string Signal   = "Signal";
    public const string PagerDuty = "PagerDuty";
}

participant with { Transport = AcmeTransportKind.PagerDuty }
```

That value now flows through the platform's records, storage, queries and UI untouched. The
platform's own code does not recognise it, routes it to the handler that does, and says so where it
cannot.

Two obligations follow, and they are the price of the openness:

- 🚨 **Never validate a value against the platform's own constants.** A check of the form *"is this
  one of the values I know?"* re-closes the vocabulary and silently breaks every extension. Validate
  shape if you must — non-empty, no whitespace — never membership.
- 🚨 **Never let an unknown value take a default branch that means something.** Coercing an
  unrecognised transport to `InApp`, or an unrecognised kind to `Person`, is the enum zero-member bug
  wearing different clothes. Unknown must stay unknown: skip it, route it to whoever declared it, and
  log it by name.

A vocabulary that a third party cannot extend without a change to core is a vocabulary that should
have been an enum. If this one could be closed, it would not need this shape.

## 🚨 Resolution is a CHAIN OF RULES, never a switch

An open vocabulary and a centralised `switch` are contradictory: the moment anyone can add a value,
no single site can know them all. So **the values are open and the dispatch is a chain** — the two
halves of one design, and shipping the first without the second gives you an extensible vocabulary
nothing can act on.

Each party registers a **rule** for the values it owns. Resolution walks the chain in order and the
first rule that **claims** the value handles it.

### 🚨 The chain is DURABLE — the rules are mesh nodes, not DI registrations

**A rule is a node.** It is written, read, versioned, queried and edited like everything else; it
survives a restart, and adding one is a node write rather than a redeploy. A chain assembled from
compiled registrations would be invisible (you cannot ask a running system what its chain is),
unversioned, and unchangeable without shipping a build — and holding it in a `static` list is
forbidden outright ([No Static State](/Doc/Architecture/NoStaticState)).

```csharp
public record DispatchRule
{
    [Key] public string Id { get; init; } = Guid.NewGuid().ToString();

    /// <summary>The values this rule claims — ITS OWN constants, never the platform's set.</summary>
    public ImmutableArray<string> Claims { get; init; } = [];

    /// <summary>Lower runs first. Explicit and DURABLE — never registration or load order.</summary>
    public int Order { get; init; }

    /// <summary>The handler that acts: a node path, resolved when the rule fires.</summary>
    [MeshNode] public string Handler { get; init; } = string.Empty;

    public bool Enabled { get; init; } = true;
}
```

Three things follow from the chain being nodes:

- **Resolution reads the chain LIVE**, from the rule nodes' own stream, ordered by `Order`. It is
  not captured into the hub's configuration at activation — a rule added while the portal runs takes
  effect on the next dispatch, with no recycle. 🚨 That is a deliberate exception to the usual
  binding: anything a hub reads *once* at activation keeps serving what it holds until a
  `DisposeRequest` reaches it ([Stale State Until Recycle](/Doc/Architecture/StaleStateUntilRecycle)),
  and a dispatch chain is precisely the thing that must not need a recycle to change.
- **The chain is queryable and auditable.** *"What handles `Signal`?"* and *"why did this message go
  nowhere?"* are reads against rule nodes, not an exercise in reasoning about DI order.
- **It is ordinary data, so it obeys the ordinary rules** — a module ships its rules as nodes, a
  deployment can add or disable one without a build, and access to a rule is the partition's access.

This is the shipped precedent, not a new invention: `NotificationRule` already lives at
`{user}/_NotificationRule/{id}` as a durable, user-authored node resolved by explicit `order`
precedence.

### The handler can be a Code node — compiled by the backend, cached

`Handler` is a node path, and that node may be **in-mesh C#**: a `Code` node the backend compiles
and executes, with the compiled assembly **cached** — keyed by path and version, so it is compiled
once and reused across dispatches, not per message.

That is what takes the openness to its conclusion: **a new transport can be added with no
deployment at all.** Its constants, its rule node and its handler source are all written into the
mesh, and the chain picks it up live. Nothing is rebuilt, nothing is redeployed, and core never
learns the value exists.

🚨 Four consequences, all of which bite in production:

- **A handler's source is invisible to CI.** Every `.cs` stored in a mesh node compiles at RUNTIME
  in the portal and **never** in `dotnet build`
  ([NodeType Compilation](/Doc/Architecture/NodeTypeCompilation)). A handler that does not compile is
  green everywhere in the repo and broken on the pod, and it shows up as `compilationStatus: Error`
  on its own node — which is the field to sweep before a deploy, not the build log.
- **A rule whose handler will not compile must fail LOUDLY, as unclaimed.** It must never be
  silently skipped: that turns a compile error into a message that vanishes, which is the failure
  mode this whole chain exists to prevent.
- **A recompile mints a NEW assembly in a new collectible load context**, so a type from the
  previous one will not cast even with an identical name. Payloads crossing that boundary are read
  with `.As<T>()` / `.ContentAs<T>()`, never a direct cast — the cast yields a silent null
  (`src/MeshWeaver.Mesh.Contract/ObjectAsExtensions.cs`).
- **The cache is keyed by version, so editing the source is what invalidates it** — a new version is
  a new entry. An instance already holding the old assembly keeps serving it until the address is
  recycled; the operator's "rebuild from what is there now" is a forced recycle of the type
  ([Stale State Until Recycle](/Doc/Architecture/StaleStateUntilRecycle)).

In-mesh handler source is held to the same warning standard as the rest of the mesh's C# — see
[In-Mesh Warning Standard](/Doc/Architecture/InMeshWarningStandard).

### What makes the chain correct

Four properties, and each is a way it goes wrong:

- **The platform's own handlers are ordinary entries.** Email and Teams sit in the chain exactly as
  a third party's Signal rule does, with no privileged position and no earlier pass reserved for
  them. A built-in fast path that runs before the chain is how extensions get silently pre-empted.
- **Order is explicit and declared** — an `Order` field, not registration order, not assembly load
  order, not the declaration order of constants (which a string vocabulary does not have anyway).
  Implicit ordering turns a module's behaviour into a function of when it happened to load.
- **A rule claims by its OWN constants.** `Claims` compares against the class the rule's author
  declared. A rule that asks "is this one of the platform's values?" has re-closed the vocabulary
  from inside the chain.
- **The chain ends in an explicit terminal, and the terminal is loud.** When nothing claims a value,
  that is a *reportable fact* — named, logged, surfaced — never a silent drop and never a fall
  through to a default handler that means something. An unclaimed value is the signal that a module
  is missing or misconfigured, and it is the only way anyone finds out.

This is the shape the codebase already uses where user intent must beat built-in behaviour:
`NotificationRule` resolves by explicit `order` precedence, and the Settings page composes itself
from contributed tabs rather than a hard-coded list
([Settings Page](/Doc/GUI/SettingsPage)).

🚨 **A chain resolves BEHAVIOUR, not validity.** It decides who acts on a value; it never decides
whether the value is allowed. Reintroducing membership validation as "no rule claims it, so reject
it" is the closed vocabulary again — the unclaimed value must still store, query and render.

## Why

### 1. A new member must not break code the compiler cannot see

Widening a `public` enum makes every exhaustive `switch` over it non-exhaustive. Under
`-warnaserror` that is a build failure — and not only in `src/`. **Every `.cs` stored in a mesh node
compiles at RUNTIME in the portal, never in CI** ([NodeType Compilation](/Doc/Architecture/NodeTypeCompilation)),
so a widened enum can leave a NodeType that no `dotnet build` ever type-checked failing to compile
on a pod, discovered only when somebody opens the page. Adding a `const string` breaks nobody,
anywhere.

For a vocabulary that exists *in order to be extended* — transports, participant kinds, channel
types, anything a module contributes to — that alone settles it.

### 2. The zero member is a silent wrong answer

An enum deserialising a value it does not know either throws or, far worse, yields the **zero
member** — and the zero member is almost always a real, meaningful value (`InApp`, `Person`,
`Running`). A message from a newer peer, a hand-authored node JSON, or a module this build has never
heard of then reads as a plausible wrong value with nothing logged. A string round-trips: unknown
stays unknown, visible, and reportable.

This matters more here than in most codebases, because node content is JSON that outlives the
assembly that wrote it and is frequently authored by hand.

### 3. It is already a string on the wire

These values serialise as strings either way. Modelling them as strings removes a conversion that
can fail rather than adding one.

### 4. Migration is a drop-in

Because the static class keeps the enum's **name** and its members keep their **spelling**, an
existing enum converts in place: change the declaration and the field's type: every
`TransportKind.Email` at every call site is untouched. That is what makes converting a shipped enum
a mechanical change rather than a sweep.

## How consumers read one

**Compare; never exhaust.**

```csharp
// ✅
if (participant.Transport == TransportKind.Email)
    …
else
    logger.LogWarning("Unknown transport {Transport} on {Path} — skipped", participant.Transport, path);
```

```csharp
// ❌ a switch with no default over an open vocabulary is the bug this shape prevents
switch (participant.Transport) { case TransportKind.Email: …; case TransportKind.Teams: …; }
```

🚨 **Always carry the unknown branch, and make it say so.** The whole gain is that an unrecognised
value arrives intact instead of being coerced; throwing it away silently spends that gain.

Ordering, where a vocabulary needs one, is an explicit `order` field or a lookup table — never the
declaration order of the members, which a string vocabulary does not have.

## When an enum is still right

An `enum` remains correct for a vocabulary that is **all three** of:

- **never persisted and never serialised** — it exists only inside a process;
- **closed by nature, not by convenience** — no module will ever add a member;
- **exhaustively handled in one assembly**, where a new member *should* break the switches.

A local state machine private to one class is the typical case. `LogLevel`-style severity, a
parser's token kind, a comparison operator — fine. The moment a value is written to a node, crosses
the wire, or is contributed by a module, it is a string.

## What this does NOT mean

- **Not "use raw string literals".** A bare `"Email"` scattered across call sites is exactly what
  the constants prevent. The value is always cited through the class.
- **Not a licence to leave the vocabulary undocumented.** The static class is the vocabulary's
  definition and carries the XML docs the enum members would have carried.
- **Not a sweep of every existing enum.** This is forward-looking: it governs new vocabularies, and
  existing ones when they are touched for another reason or when one needs widening — the point at
  which the enum's cost is actually paid.

## Cross-references

- [Policy Not Prose](/Doc/Architecture/PolicyNotProse) — the register entry `open-vocabulary-string-constants`.
- [NodeType Compilation](/Doc/Architecture/NodeTypeCompilation) — why a widened enum can break code no build type-checks.
- [The Communication Hub](/Doc/Architecture/CommunicationHub) — the design this rule was drawn from; its participant kinds and transports are the worked example.
- [No Static State](/Doc/Architecture/NoStaticState) — the neighbouring absolute; a `static class` of `const string` is immutable and is explicitly allowed there.
