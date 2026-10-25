---
Name: A Catalog's Loaded Signal
Category: Architecture
Description: Why a standing catalog's "has the first listing arrived?" signal is a lock-free flag plus an AsyncSubject and never a BehaviorSubject<bool>. A BehaviorSubject replays to a new subscriber while holding the gate its Value reader takes, which closed a lock-order deadlock with the zip in InstanceGitHubApp.WhenReadable and hung module discovery's first import.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="11" width="18" height="11" rx="2"/><path d="M7 11V7a5 5 0 0 1 10 0v4"/></svg>
---

# A Catalog's Loaded Signal

The standing catalogs of instance-held records (the Instance Secrets catalog, and every
`InstanceAdminRecordCatalog<T>`: the instance's package sources and its GitHub App metadata) each
answer one question beside their entries: **has the first complete listing arrived?** That answer
separates "nothing is stored" from "nothing has been read yet". It has two readers:

- `WhenLoaded`, a signal a caller waits on before it decides anything;
- `IsLoaded` (on the admin-record catalogs), a synchronous read used by the code that decides.

## The rule

**Two fields: a lock-free flag for the read, and an `AsyncSubject<Unit>` for the signal. Never a
`BehaviorSubject<bool>`.**

```csharp
private readonly AsyncSubject<Unit> loaded = new();
private int isLoaded;

public IObservable<Unit> WhenLoaded => loaded.AsObservable();
public bool IsLoaded => Volatile.Read(ref isLoaded) == 1;

private void MarkLoaded()
{
    if (Interlocked.Exchange(ref isLoaded, 1) != 0)
        return;
    loaded.OnNext(Unit.Default);   // the flag is already true for any subscriber that reads it
    loaded.OnCompleted();
}
```

## Why a BehaviorSubject is wrong here

`BehaviorSubject<T>.Subscribe` delivers the current value to the new observer **while it holds the
subject's gate**, and `BehaviorSubject<T>.Value` takes the **same** gate (System.Reactive 7.0.0).
So every subscriber of `WhenLoaded` ran its continuation under the lock that every `IsLoaded` reader
needs. A continuation that ends up waiting on another lock, held by a thread that is itself reading
`IsLoaded`, closes a cycle.

`AsyncSubject<T>` replays a completed value with no lock held: its `Subscribe` reads the terminal
state through `Volatile.Read` and calls the observer outside any lock.

## The measured deadlock

`InstanceGitHubApp.WhenReadable` zips the secret catalog's `WhenLoaded` with the App catalog's.
`GitHubSyncService.DecideRead` (the GitHub identity decision behind every import) then asks
`GitHubAppTokenService.IsConfigured`, which reads `InstanceGitHubApp.Options`, which reads the App
catalog's `IsLoaded`. The decision runs inside the zip's emission, under the zip's gate. Stacks taken
during the hang showed two threads:

| Thread | Holds | Waits for |
|---|---|---|
| A: subscribing the zip | the App catalog's `BehaviorSubject` gate (replaying `true`) | the zip's gate, for `Take(1)`'s `OnCompleted` |
| B: the secret catalog's first listing | the zip's gate (emitting the zipped value) | the App catalog's gate, for `IsLoaded` |

Neither returned. In MeshWeaver.Plugins, `ModuleAutoDiscoveryTest` hung one test per class run for
240 s. Which test hung depended only on which test's fresh mesh raced the secret catalog's first
listing against the zip's subscription. The activity log stopped after `Ifrs17/_Activity/<id>` was
created and before the import's first line, and teardown reported *1 pooled I/O leaf(s) still
running*: that leaf was the activity, parked in `DecideRead`.

The Instance Secrets catalog had the same signal, and a second victim besides the zip: its own change
handler read `loaded.Value` on every full listing. So a `WhenLoaded` subscriber still inside its
callback froze the catalog's live feed, and no secret, rotation or revocation reached the process
until that callback returned.

## Proof

- `test/MeshWeaver.Hosting.Test/CatalogLoadedSignalLockOrderTest`: a `WhenLoaded` subscriber parks
  in its callback until another thread's `IsLoaded` read returns. Under the old signal, the read
  cannot return during the park, and the test fails in 5 s every time.
- `test/MeshWeaver.Graph.Test/SecretCatalogLoadedSignalLockOrderTest`: the same park, against a full
  listing that the secret catalog's own feed delivers on another thread.

## Applies beyond these two

Any signal that answers "has X happened yet" and also has a synchronous reader should not be a
`BehaviorSubject<bool>` paired with `.Value`. Publish the fact through a field that readers can read
without a lock, and replay it through a subject that emits outside its lock.
