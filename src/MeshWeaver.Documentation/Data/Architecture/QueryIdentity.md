---
Name: Query Identity
Category: Architecture
Description: Who a secured read runs behind — why an unstamped query resolves to Anonymous, why that reads as absence rather than denial, and the explicit intents (ForViewer / AsPublicListing / RequireViewer) that make the omission impossible to miss.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="11" cy="11" r="7"/><path d="m21 21-4.35-4.35"/><path d="M11 8v3"/><circle cx="11" cy="14" r="0.5" fill="currentColor"/></svg>
---

# Query Identity

Every secured read runs behind exactly one **viewer**, and row-level security answers for that
viewer. This page is about the one question the query surface used to be unable to ask: *who is
this read for, when nobody said?*

## The failure mode: absence and denial look identical

A read that names no viewer resolves to [`WellKnownUsers.Anonymous`](/Doc/Architecture/AccessControl).
That is the correct, fail-closed answer — it never widens anything. But consider what it *returns*
for a read aimed into somebody's own space:

```text
path:rbuergi/Chess/History  →  evaluated as Anonymous  →  0 rows
```

Zero rows. Not an error, not a denial — an empty list, which the caller renders as **"no recorded
games"** to an owner with six of them. The access system worked perfectly and the user was told
their data does not exist.

That confusion produced **five user-visible bugs in a single day** (MeshWeaver.Plugins #360, #406,
#415, and two in #417). One of them was a "run from your own copy" redirect that had *never once
fired* since it shipped, because the query that looked for the user's copy always ran as Anonymous
and always found nothing.

The root of it is that `UserId = null` had to mean two incompatible things at once:

| The author meant | What the read did |
|---|---|
| "show this user their own content" (forgot to stamp) | returned the Anonymous view — looks like absence |
| "list what anyone may see" (a public catalog) | returned the Anonymous view — exactly right |

One input, two intents, no way to tell them apart — so no diagnostic could distinguish a bug from
correct code, and the bug class regenerated every time someone wrote a query.

## Say which one you mean

Four named intents on `MeshQueryRequest` — three for application reads, one for framework plumbing.
All additive; the default is unchanged behaviour.

```csharp
// 1. A read about a specific user — the always-correct form. Does not depend on the caller's
//    ambient context surviving whatever scheduler or pool hop lies between here and storage.
mesh.Query<MeshNode>(MeshQueryRequest.FromQuery(q).ForViewer(userId));
mesh.Query<MeshNode>(MeshQueryRequest.FromQuery(q, userId));      // same thing, older spelling

// 2. A genuine mesh-wide PUBLIC listing — Anonymous is the intended viewer, not a fallback.
mesh.Query<MeshNode>(MeshQueryRequest.FromQuery("nodeType:Course").AsPublicListing());

// 3. A read whose empty result would be reported to a human as absence. Fails closed with
//    QueryIdentityUnresolvedException rather than answering as Anonymous.
mesh.Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{userHome}/Exercise").RequireViewer());

// 4. Framework plumbing that must not be filtered at all.
mesh.Query<MeshNode>(MeshQueryRequest.FromQuery(q).AsSystem());
```

🚨 **`AsPublicListing()` is a decision, not a way to silence a warning.** A catalog of published
courses is the real case — and stamping the visitor there is actively wrong: it folds each viewer's
own private copies into the public list as duplicate cards (that was #415). But if the read is
supposed to show the caller *their* content, `AsPublicListing()` makes the bug permanent. Use
`RequireViewer()` for those.

## When nothing names a viewer

`QueryIdentityResolver.Resolve` is the one definition of the rule:

| Request | Resolves to | Source |
|---|---|---|
| `UserId = WellKnownUsers.System` | System — row-level security bypassed | `Request` |
| `UserId` non-empty | that viewer | `Request` |
| `UserId = ""` (empty, not null) | Anonymous, explicitly | `Request` |
| `IdentityFallback = PublicListing` | Anonymous, by intent | `PublicListing` |
| `UserId = null`, ambient context present | the ambient viewer | `Ambient` |
| `UserId = null`, nothing ambient | Anonymous | **`Unresolved`** |

`Unresolved` is the new fact. The verdict is unchanged (still Anonymous, still never widened) but it
is no longer silent:

- `RequireViewer()` → throws `QueryIdentityUnresolvedException` naming the query.
- otherwise, if the query aims at a **named partition** → a warning naming the query and the
  remedies.
- otherwise (an unscoped, mesh-wide read) → nothing. An unscoped read answering as Anonymous
  returns the mesh's public subset, which is a sensible answer and the shape a real catalog has.

That last row is what keeps the diagnostic honest rather than noisy. Every one of the five known
instances was a **scoped** read; a mesh-wide listing is the case that is legitimately unstamped.

### Why `Unresolved` is a strong signal, not a guess

Every entry point in the platform that serves a logged-out caller stamps an **explicit** Anonymous
`AccessContext` — the HTTP middleware, the Blazor circuit handler, SignalR, gRPC and the MCP
surface all do it. So a genuine anonymous visitor resolves as `Ambient`, never as `Unresolved`.

Reaching `Unresolved` therefore means the read is running somewhere **no entry point established
identity at all**: a hub action block, an Rx continuation, an `IIoPool` worker, a background
service. `AccessService.CircuitContext` deliberately returns null there — see
[AsyncLocal across hops](/Doc/Architecture/AsyncLocalAcrossHops) — so that identity resolution fails
closed instead of answering as whichever user touched the process last. On those threads the
identity is genuinely gone and only the call site can supply it.

## Resolve at the boundary, never in the provider

The rule used to live in **five** copies — `StorageAdapterMeshQueryProvider`, `PostgreSqlMeshQuery`,
`PostgreSqlPartitionedMeshQuery`, `SnowflakeMeshQuery`, `SnowflakePartitionedMeshQuery` — and they
had drifted. An explicit `UserId = ""` meant "the anonymous visitor" in the first and "ignore that,
go look at the ambient context" in the other four, so **the same request answered differently
depending on which storage backend served it**. The System bypass had two spellings and one provider
did not special-case it at all.

Worse than the drift was the *timing*. Those providers are singletons holding the root
`AccessService`, and they resolved identity at **subscribe** time — after the read had typically
hopped a scheduler, a pool or a change feed. Whether the caller's ambient `AsyncLocal` was still
theirs depended on Rx plumbing rather than on anything the author wrote: the same code returned the
caller's rows in a test and the Anonymous view in production.

So identity is captured as **early as it can be known**, and reported where it becomes **final**:

```text
caller (ambient context is still theirs)
   │
   ├─ MeshService.Query  ──► QueryIdentityResolver.Resolve(request, ambient)
   │                          • stamps request.UserId when a viewer WAS resolved
   │                          • leaves it null otherwise — see below
   │
   └─ provider           ──► QueryIdentityResolver.ResolveAndReport(request, ambient, logger)
                              • the last word: warns / throws on Unresolved
```

🚨 **The boundary stamps what it resolved, never the fallback.** Pinning the Anonymous fallback at
the boundary would make it the last word, and it is not: a caller whose ambient context is empty at
*call* time can still have one at *subscribe* time — the plugin installer constructs its queries
outside the `ImpersonateAsSystem` scope it subscribes them in. An unconditional stamp froze those
reads as Anonymous and the Store package installed **0 nodes**. Leaving `UserId` null preserves the
provider's late resolution byte-for-byte, which is why this change is additive rather than a
behaviour swap.

That is also why the diagnostic lives in the provider: it is the point at which "nobody named a
viewer" is finally true, so it can neither cry wolf nor miss.

## The same rule applies to single-node reads

This is one instance of a general contract, not a query-only quirk:

> **Identity is captured eagerly, at the call site, where the ambient context is still the
> caller's — and the absence of an identity is never reported as the absence of data.**

`meshService.CreateNode` already worked this way (it captures before its `Observable.Defer` and pins
the result onto the request as `CreatedBy`). `GetMeshNode` is the sibling that still resolves its
posting identity ambiently at subscribe; because it re-issues a read from inside an `OnError`
callback on another thread, any ambient scope is long gone by then and the re-probe posts
unattributed — landing on `null`, which the caller again cannot tell apart from "not found". The fix
there is the same shape: eager `CaptureContext()` plus `o.WithAccessContext(captured)`. Wrapping
individual call sites in an impersonation scope treats the symptom, because the re-issue escapes the
scope.

See [Access Context Propagation](/Doc/Architecture/AccessContextPropagation) for the write-side
contract and [CQRS and Content Access](/Doc/Architecture/CqrsAndContentAccess) for what queries are
and are not for.

## Checklist for a new read

1. Is this read **about a specific user**? → `ForViewer(userId)`, or `RequireViewer()` if the caller
   is expected to be that user.
2. Is it a **mesh-wide public catalog**? → `AsPublicListing()`.
3. Is it **framework plumbing** that must not be filtered? → `AsSystem()`.
4. None of the above and the answer is "the caller, whoever that is"? → leave it unstamped; the
   ambient context resolves it, and you will hear about it if it ever cannot.

Never reach for `AsPublicListing()` or `AsSystem()` to make a warning go away. The warning is
telling you the read is answering as somebody other than the person it is for.
