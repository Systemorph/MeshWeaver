# Review instructions for MeshWeaver

## 🚨 FLAG EVERY `await`. Every one.

This codebase is `IObservable<T>` end to end. **Any `async`, `await`, `Task<T>`, `Task.Run`,
`TaskCompletionSource`, `.ToTask()`, `.Result`, `.Wait()`, `.GetAwaiter().GetResult()`, or
`Observable.FromAsync` added under `src/` is a defect** — report it, every occurrence, even when the
surrounding file already contains others and even when the change "looks harmless".

Do not treat this as a style preference to mention once and move on. It is the single defect class
that has cost this project the most production time, so the review must be exhaustive rather than
representative: if a diff adds ten `await`s, flag ten.

**Why it is a defect and not a preference.** A hub action block, an Orleans grain turn and a Blazor
circuit are all single-threaded schedulers. An `await` on one of them parks that scheduler, so the
message the continuation is waiting for can never be processed — a deadlock, not a slowdown.
Separately, `AccessService.Context` is an `AsyncLocal`: a continuation resuming on a pool thread
loses the caller's identity, the write then posts with no `AccessContext`, `PostPipeline` fails
closed, the write silently does nothing, and something upstream retries it into a storm. Neither
failure shows up as an exception at the call site, which is exactly why review has to catch it.

**The correct shape** is to return `IObservable<T>` and compose — `.Select`, `.SelectMany`,
`.Where`, `.Timeout` — then `.Subscribe(onNext, onError)`. Dependent work belongs in `.SelectMany`,
never behind an `await`. A genuinely async leaf (database, blob, HTTP, Roslyn, `Process`,
synchronous file IO) goes through `IIoPool`: `pool.Invoke(ct => …Async(ct))`,
`pool.InvokeBlocking(ct => …)`, or `pool.Run(...)` for the promise-cached one-shot.

**External code that only offers an async API is not an exception — it is the case the rule is for.**
A third-party SDK, an HTTP client, a database driver, Roslyn, a `Process` — none of them can be
rewritten to return `IObservable<T>`, and that is precisely why they must be *run somewhere that
owns the async*: either **inside an Activity** (an operation with inputs, progress and an outcome —
see `ActivityControlPlane.md`) or **through `IIoPool`**. Awaiting such a call directly from a hub
handler, a layout area, a grain turn or a Blazor component is the defect, no matter that the library
gave you no synchronous overload. Flag it and name the boundary it should have crossed.

**The only places `await` is legitimate — do not report these:**

- **Inside `IoPool` itself** (`src/MeshWeaver.Mesh.Contract/Threading/IoPool.cs`). It *is* the
  sanctioned boundary between the turn-based schedulers and real async IO, and it is the one place
  a `SemaphoreSlim` is allowed to live. New `await`s added *there* are in scope for ordinary
  correctness review, but not for this rule.
- **Test projects** (`test/**`). `await …FirstAsync().ToTask()` is the accepted bridge.
- **A one-line SDK/MCP surface adapter** whose body is reactive, e.g.
  `public Task<string> Patch(...) => ops.Patch(...).FirstAsync().ToTask();`
- **Framework APIs whose signature is not ours** — `IHostedService.StartAsync`,
  `Grain.OnActivateAsync`/`OnDeactivateAsync`, ASP.NET middleware. Flag anything *inside* them that
  could have been reactive, but not the override itself.

When you flag one, say which of the two failures it risks (parked scheduler, or lost identity), and
name the reactive replacement. "Consider using async/await consistently" is the opposite of the
right advice here.

## Also always worth flagging

These are the other rules the project treats as absolute. Report violations with the same rigour.

- **`SemaphoreSlim`, `lock`-around-`await`, `ManualResetEventSlim`, `TaskCompletionSource`-as-a-gate**
  anywhere outside `IoPool` — hand-woven async gates deadlock turn-based schedulers. Serialisation
  belongs on a hub (`Subject<T>` + `.Select(Run).Concat()`); bounded concurrency and run-once belong
  on `IIoPool` with `PromiseCache`/`PromiseSlot`.
- **`Observable.Using(() => access.ImpersonateAsSystem(), …)`** and the `ImpersonateAsHub` /
  `SwitchAccessContext` variants. Impersonation is an AsyncLocal store/restore pair; Rx runs the
  resource factory on the subscribing thread and disposes it when the inner observable *terminates*,
  so the two land on different threads and the subscriber is left latched as the impersonated
  identity. The sanctioned forms are `RunAsSystem` / `RunAsHub` / `RunAs` in
  `ImpersonationScopeExtensions`. There is a ratchet-guard test enforcing this.
- **A cold write that is never subscribed.** `GetMeshNodeStream(path).Update(...)` and the
  `meshService.Create/Update/Delete` family are cold — the side effect runs on `Subscribe`. A
  composed write with no `.Subscribe(...)` silently does nothing.
- **`static` collections or caches** — `static ConcurrentDictionary`, `static MemoryCache`,
  `[ThreadStatic]`, `static Lazy<…>` of mutable state. They survive mesh disposal and bleed across
  tests and users. A `Clear()` method "for test isolation" is the tell. Instance state on a
  mesh-scoped singleton instead. Immutable read-only lookups are fine.
- **Mutable collections in models** — prefer `System.Collections.Immutable`.
- **Casting an `object` payload** — `node.Content as T` / `payload is T`. Polymorphic
  deserialization degrades an unresolvable `$type` to a raw `JsonElement`, so the cast yields a
  silent null. Use `.As<T>(options, logger)` / `.ContentAs<T>(options)`.
- **A bespoke `XxxRequest`/`XxxResponse` pair to mutate state.** The only mutation API is
  `GetMeshNodeStream(path).Update(...)`; a state machine uses a `RequestedX` field the owning hub
  watches.
- **Hard-coded user-visible strings.** The portal ships English and German; a literal renders
  English for everyone. Use `[Translation]` next to `[Description]` on a declaration, or a key in
  `strings.{en,de}.json` read via `Localize`. A new key must also be mirrored into
  `clients/react/src/i18n/` or the drift-guard test fails.
- **Resolving culture from `CultureInfo.CurrentUICulture`/`CurrentCulture`** — including for date
  and number formatting. On Blazor Server that is the container's culture, identical for every
  simultaneous viewer. Resolve explicitly from `AccessContext.Locale`.
