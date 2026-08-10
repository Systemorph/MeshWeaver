---
Name: Change-Feed Isolation
Category: Architecture
Description: Every IStorageAdapter publishes its change feed through IsolatedChangeFeed, never a plain Subject. Subject.OnNext delivers synchronously in subscription order, so one throwing observer aborts delivery to every observer after it — and the catch that used to wrap the publish turned that into silence.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 12h4"/><path d="M12 4v16"/><path d="M12 8h8"/><path d="M12 16h8"/><circle cx="4" cy="12" r="2"/><circle cx="21" cy="8" r="1.6"/><circle cx="21" cy="16" r="1.6"/></svg>
---

`IStorageAdapter.Changes` is the in-process broadcast every subscriber inside the silo depends on to
learn that storage moved: per-node hubs reconcile their cached workspace state from it, and **every
live synced query** folds it into the result set it is keeping current. A view that databinds to a
children listing, the `$security-access` query the permission evaluator folds, an agent watching a
namespace — all of them are downstream of this one feed.

So the feed has a contract, and it is stronger than "it is an `IObservable`":

> **Delivery to one subscriber is independent of every other subscriber.** A subscriber that throws
> misses *that* notification and nothing more. It never costs another subscriber a notification, it
> never costs the publishing write, and it is never silent.

The class that enforces it is `IsolatedChangeFeed` (`MeshWeaver.Mesh.Contract`, namespace
`MeshWeaver.Mesh.Services`). It lives next to the interface it enforces, and **every adapter
publishes through it** — there is no adapter-local variant.

## Why a plain `Subject<T>` is the wrong primitive

`Subject<T>.OnNext` delivers to its observers **synchronously, in subscription order, on the caller's
thread.** There is no isolation in that loop: the first observer that throws unwinds out of
`OnNext`, and every observer registered *after* it never sees the notification at all. Which
subscribers are starved is decided by subscription order — an ordering nobody controls, chose, or
can even observe.

Adapters used to compound that by wrapping the publish:

```csharp
// ❌ The shape this contract exists to forbid.
try { _changes.OnNext(new DataChangeNotification(path, …)); }
catch { /* best-effort — never throw out of a write */ }
```

The `catch` is the amplifier. Without it the write would at least fail loudly. With it: no error, no
log line, **and the write reports success** — while an arbitrary suffix of the subscriber list
silently missed a change it will now never be told about.

### The thrower is not hypothetical

Every live synced query owns a `persistence.Changes → changeBuffer` pipeline in
`StorageAdapterMeshQueryProvider`. Teardown disposes that pipeline's `CompositeDisposable`, and a
pipeline caught mid-teardown has a **disposed `changeBuffer` while its feed subscription is still
delivering into it** — an `ObjectDisposedException` straight into the fan-out.

That window is not rare on any backend. One-shot queries — `IMeshService.QueryAsync`, autocomplete,
path resolution — open and tear down one of these pipelines constantly, so on a busy mesh the window
is hit whenever a write lands during a teardown. It needs no parallelism and no unusual load, which
is precisely why the resulting failures read as intermittent.

The two symptoms this produced, on the two backends where it was found:

| Backend | What the surviving subscribers lost | How it looked |
|---|---|---|
| Postgres (#889) | the `$security-access:{scope}` query the permission fold reads; its `Replay(1)` cache stayed frozen at the pre-write snapshot | reads evaluated against **stale permissions** until something re-triggered the query; in tests, `PaywallRealGateShapeTests` timing out on its fold barrier |
| In-memory (#1053) | a live `scope:children` listing | a children query that **silently stopped re-emitting** after a create that completed successfully — 30 s timeout, write present in storage, nothing wrong anywhere |

Note what both have in common: the *write* succeeded, the *notification* was published, and the
notification was relevant. Nothing upstream of delivery is at fault. Chasing "did the write publish?"
or "does `PathMatcher.ShouldNotify` agree?" is chasing the wrong half — the drop happens **after**
relevance matching, in the fan-out.

## What `IsolatedChangeFeed` guarantees

```csharp
public sealed class IsolatedChangeFeed
    : IObservable<DataChangeNotification>, IObserver<DataChangeNotification>, IDisposable
{
    public IsolatedChangeFeed(ILogger? logger, string adapter);
}
```

It is both the observable the adapter exposes and the observer the adapter publishes into, so an
adapter holds exactly one field and needs no fan-out code of its own.

**1. Delivery walks an immutable snapshot.** The observer list is an `ImmutableList<…>` read once,
before delivery starts, with no lock held for the loop. A subscriber that subscribes or disposes
*during* a publish cannot disturb the fan-out in flight.

**2. Each observer is isolated, and the two failure modes are treated differently.**

```csharp
foreach (var observer in observers)
{
    try { observer.OnNext(value); }
    catch (ObjectDisposedException ex)
    {
        Remove(observer);          // provably dead — every later notification would throw again
        _logger?.LogWarning(ex, "…was disposed while still subscribed…; dropped from the feed…");
    }
    catch (Exception ex)
    {
        // isolated but KEPT — see below
        _logger?.LogWarning(ex, "…threw on {Path}… and MISSED that notification; it remains subscribed…");
    }
}
```

- A **disposed** observer is dropped. Its sink is gone; every future notification would throw
  identically, so keeping it buys nothing and costs a warning per write.
- **Any other throw is isolated but the subscriber is kept.** This asymmetry is deliberate:
  permanently unsubscribing a live subscriber over a transient fault would starve it of every future
  change — which is *this very bug*, just relocated. Only a disposed sink is provably unrecoverable.

**3. Both arms log at `Warning`, and each names the adapter, the path, and how many other observers
were unaffected.** Not `Debug` — a subscriber that missed a change is holding a stale view of the
mesh, and on the security-fold path that means stale permissions. A swallowed fault on that path is
exactly what made the Postgres instance take three CI runs to see.

**4. `OnError` and `OnCompleted` are isolated the same way**, so a terminal signal cannot be lost to
one bad observer either.

**5. `Dispose()` detaches every observer and refuses further subscriptions** (later `Subscribe` calls
return `Disposable.Empty`), so a torn-down adapter cannot resurrect a feed.

## Writing or reviewing an adapter

Hold one `IsolatedChangeFeed` field, expose it as `Changes`, publish into it directly, and **do not
wrap the publish in `catch`**:

```csharp
private readonly IsolatedChangeFeed _changes;

public InMemoryStorageAdapter(ILogger<InMemoryStorageAdapter>? logger = null)
{
    _changes = new IsolatedChangeFeed(logger, "in-memory");
}

public IObservable<DataChangeNotification> Changes => _changes;
```

Three rules that go with it:

- **Pass a real logger.** A null logger restores exactly the silence the class exists to end. (The
  Postgres per-schema feeds were once constructed with a null logger; the Snowflake partition
  provider grew the same internal `Logger` accessor its Postgres twin has so the router's feed can
  report an isolated fault instead.)
- **Name the adapter.** The string is what attributes a warning to the feed it came from — a Postgres
  schema name, `"path-router"`, `"in-memory"`, `"sqlite"`, `"cosmos"`.
- **A refused write must not publish.** Nothing changed, and a notification carrying the losing node
  would hand every subscriber the stale state the store just rejected. See the version-conditional
  write contract on `IStorageAdapter.Write`.

Current publishers, all on the same class:

| Adapter | Feed name |
|---|---|
| `InMemoryStorageAdapter` | `in-memory` |
| `SqliteStorageAdapter` | `sqlite` |
| `CosmosStorageAdapter` | `cosmos` |
| `PostgreSqlStorageAdapter` | the partition's schema (or `public`) |
| `SnowflakeStorageAdapter` | the partition's schema (or `public`) |
| `PostgreSqlPathRoutingAdapter` / `SnowflakePathRoutingAdapter` | `path-router` |

## Reading the symptom

If a live view, query, or watcher **stops updating while writes keep succeeding**, this is one of the
two mechanisms to check (the other is a `.Take(1)` on a stream feeding a live binding — see
[Asynchronous Calls](/Doc/Architecture/AsynchronousCalls) rule 8).

Search the log for the feed's own warnings before hypothesising anything upstream:

```
Change-feed observer <Observer> was disposed while still subscribed (<adapter>); dropped from
the feed at <path>. Delivery to the other N observer(s) was NOT affected.
```

That line is the whole diagnosis: it names the adapter, the path, and the observer type. Its
*absence* is informative too — no isolated fault means the fan-out was clean and the gap is genuinely
elsewhere (publication, relevance matching, or the synced-query fold).

## Pinned by

| Test | What it holds |
|---|---|
| `InMemoryChangeFeedFanoutIsolationTest` (`test/MeshWeaver.Hosting.Test`) | the fan-out contract at the adapter |
| `IsolatedChangeFeedTests`, `MergedFeedFanoutIsolationTests` (`test/MeshWeaver.Hosting.PostgreSql.Test`) | the same contract on the merged per-schema feeds |
| `SyncedQueryChangeFeedStarvationTest` (`test/MeshWeaver.Hosting.Test`) | the end-to-end symptom: a live children query re-emits even while a torn-down pipeline sits on the feed |

The starvation test was written with a [negative control](/Doc/Architecture/NegativeControls): with
only the adapter's feed reverted it fails in 30 s with the reported signature verbatim, and passes in
0.5 s with the feed restored. That control is also what ruled out the two competing explanations —
it holds publication and relevance matching fixed, so only the fan-out differs between red and green.

## See also

- [Storage Adapter Implementation](/Doc/Architecture/StorageAdapterImplementation) — the rest of the `IStorageAdapter` contract.
- [Silent Completion](/Doc/Architecture/SilentCompletion) — the sibling failure shape: a chain that terminates with nothing and reports nothing.
- [Error Propagation & Wedges](/Doc/Architecture/ErrorPropagationAndWedges) — why a `catch { }` on a fan-out is never the fix.
- [CQRS — Queries vs. Content Access](/Doc/Architecture/CqrsAndContentAccess) — what the synced queries downstream of this feed guarantee.
