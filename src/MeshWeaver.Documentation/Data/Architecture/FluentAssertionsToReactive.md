# Converting Tests to `MeshWeaver.Reactive.Assertions`

This is the **conversion playbook** for migrating a test project off FluentAssertions (FA) onto the
in-house `MeshWeaver.Reactive.Assertions`. It is written to be applied **mechanically, one project at a
time** — sub-agents reference this file directly.

## TL;DR

FA is gone solution-wide. `Should()`, every assertion, and the `Seconds()`/`Milliseconds()` time helpers now
come from `MeshWeaver.Reactive.Assertions`, injected as a **global using + global project reference** in
`test/Directory.Build.props`. The method **names and chaining are FA-identical**, so the vast majority of
call sites compile unchanged once the `using FluentAssertions;` line is gone (already removed).

**Only three things need manual work:**
1. `BeEquivalentTo` now takes a **mandatory `JsonSerializerOptions`** — pass the owning hub's.
2. **No `await` / `FirstAsync()` / `ToTask()` in tests** — assert on the stream directly.
3. FA.Json element asserts resolve from the core now (no change beyond the removed using).

Goal per project: **0 errors AND 0 warnings** from `dotnet build <csproj> --no-dependencies`.

---

## 1. `BeEquivalentTo` — pass the owning hub's `JsonSerializerOptions`

Equivalence serializes both sides with System.Text.Json. For mesh objects the polymorphic `$type`
discriminators must line up, so the options **must come from the owning hub** — this is deliberate
(it forces equivalence to route through the real serializer, not a guessed default).

```csharp
// ❌ before (FA, no options)
actual.Should().BeEquivalentTo(expected);
actual.Should().BeEquivalentTo(expected, o => o.Excluding(x => x.Message));

// ✅ after — options is the 2nd argument; any config lambda shifts to 3rd
actual.Should().BeEquivalentTo(expected, hub.JsonSerializerOptions);
actual.Should().BeEquivalentTo(expected, hub.JsonSerializerOptions, o => o.Excluding(x => x.Message));
```

**Finding the options in scope** (use whichever hub the test already has):
`hub.JsonSerializerOptions`, `client.JsonSerializerOptions`, `Mesh.JsonSerializerOptions`,
`GetHost().JsonSerializerOptions`, `GetClient().JsonSerializerOptions`. `IMessageHub.JsonSerializerOptions`
is the canonical accessor.

**Plain DTOs with no hub anywhere in the test** (no polymorphism, e.g. a parser result record): use the BCL
static `System.Text.Json.JsonSerializerOptions.Default` — do **not** `new()` one up.

The options builder is unchanged from FA's shape:
`Excluding(x => x.Member)`, `Including(...)`, `WithStrictOrdering()`, plus the JSON-flavoured
`ExcludeTypeDiscriminator()`, `ExcludeProperty<TDecl,TProp>(...)`, `UsingJson(...)`. Collections compare
**order-insensitively** unless `WithStrictOrdering()`.

`NotBeEquivalentTo` takes the same `(expectation, options, config?)` shape.

---

## 2. No async in the test body — assert on the stream

A test bridging a stream to a `Task` and `await`-ing it is the pattern we are removing. Assert on the
`IObservable<T>` directly. The reactive surface (all from `MeshWeaver.Reactive.Assertions`):

| Method | Meaning |
|---|---|
| `obs.Should().Emit()` | block (≤ timeout) for the first emission; returns it |
| `obs.Should().Match(x => pred)` | block for the first emission satisfying `pred`; returns it |
| `obs.Should().Be(expected)` | first emission equals `expected` |
| `obs.Should().Complete()` | stream completes within the timeout |
| `obs.Should().NotEmit(within)` | nothing arrives in `within` (the one place a fixed wait is right) |
| `obs.Should().Within(t).…` | override the wait timeout (default 10 s) for this chain |

The blocking wait lives **inside** the assertion (it goes through Rx's task bridge, never captures a
sync-context, cannot deadlock). The test body stays `await`-free.

```csharp
// ❌ before                                              // ✅ after
var v = await obs.FirstAsync().ToTask();                  var v = obs.Should().Emit();
v.Should().Be(x);                                         // └ or directly: obs.Should().Be(x);

var row = await obs.Where(r => r.Count == 2)              var row = obs.Should().Match(r => r.Count == 2);
    .FirstAsync().ToTask();

await obs.FirstAsync().Timeout(5.Seconds()).ToTask();     obs.Should().Within(5.Seconds()).Emit();

var p = await hub.GetEffectivePermissions(path)           var p = hub.GetEffectivePermissions(path)
    .FirstAsync().ToTask();                                   .Should().Match(_ => true);   // or .Be(Permission.Read)
```

### 2a. `await <observable>` is also an await to remove — and the method MUST become `void`

Many mesh APIs **return `IObservable<T>`, not `Task<T>`** — notably `IMeshService.CreateNode` /
`UpdateNode` / `DeleteNode` and `hub.Observe<T>(...)`. C# lets you `await` an observable (Rx's awaiter
returns the last value), so test code is full of `await NodeFactory.CreateNode(node)` that looks like a
Task await but isn't. **Convert these too:**

```csharp
// ❌ before — awaiting an IObservable keeps the method `async`
var created = await NodeFactory.CreateNode(node);
// ✅ after — reactive; the value is the first (and only) emission
var created = NodeFactory.CreateNode(node).Should().Emit();
```

**🚨 This matters for correctness, not just style.** A reactive `.Should()` assertion **blocks** (waits
synchronously for the emission). If even one `await` remains, the test method stays `async Task`, and
xUnit runs it under an async SynchronizationContext. Blocking inside that context can **deadlock** — the
framework's own continuations (the hub delivering your stream's emission) are starved by the block, and
the assertion times out. So: **a test that uses any reactive `.Should()` assertion must be `void`, with
every `await` removed** (stream-waits → `.Should()`, observable-returning calls → `.Should().Emit()`).
Genuinely-async leaves like `await act.Should().ThrowAsync<T>()` keep their own method `async` — but then
that method must NOT also use a blocking reactive assertion.

### 2b. Test-base reactive reads (the `*Async` helpers are going away)

The `MonolithMeshTestBase` async helpers are being removed in favour of the observables they wrapped:

| ❌ before | ✅ after |
|---|---|
| `await ReadNodeAsync(p)` | `ReadNode(p).Should().Emit()` — authoritative owner-hub read; emits the node or `null` on NotFound |
| `await CreateNodeAsync(n)` / `await NodeFactory.CreateNode(n)` | `NodeFactory.CreateNode(n).Should().Emit()` |
| `await MeshQuery.QueryAsync<MeshNode>(q).ToListAsync()` | `MeshQuery.ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(q)).Should().Match(c => c.ChangeType == QueryChangeType.Initial).Items` |
| `await MeshQuery.QueryAsync(req).ToListAsync()` (non-generic) | same `ObserveQuery<MeshNode>(req).Should().Match(c => c.ChangeType == QueryChangeType.Initial).Items` |
| `await QueryAsync<MeshNode>(q, skip, limit).ToListAsync()` | `ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(q) with { Skip = skip, Limit = limit }).Should().Match(c => c.ChangeType == QueryChangeType.Initial).Items` |

`ObserveQuery<T>`'s **first emission is `ChangeType == Initial` carrying the full result set** — that's the
snapshot the old `QueryAsync().ToListAsync()` returned. If a test races eventual-consistency lag (the
Initial set is short and items trickle in as `Added`), accumulate instead: `.Scan(...)` the items and
`.Should().Match(acc => acc.Count == N)`.

A helper that only `await`-ed these (e.g. `SetupMarketingHierarchy()`, `EnsureNodesCreated()`) becomes
`void`; its callers drop the `await`.

**After removing every `await`**, the method is synchronous: change `public async Task Foo()` →
`public void Foo()` and drop `async`. Remove the now-unused `using System.Threading.Tasks;` only if it
becomes redundant (don't churn unrelated usings).

**Never** replace an await with `Task.Delay`/`Thread.Sleep` to "wait for propagation" — fold the wait into
`Should().Match(...)` on the actual stream. (Genuine "nothing happens" negative tests use `NotEmit(within)`.)

If a test genuinely needs the value out-of-band (not just an assertion), `obs.Should().Emit()` /
`.Match(...)` **return** the awaited value, so `var x = obs.Should().Match(...)` replaces
`var x = await obs....ToTask()` one-for-one.

---

## 3. FA.Json element asserts (rare — RawJson / serialized-JSON tests)

These now resolve from the core; only the `using FluentAssertions.Json;` removal was needed:

```csharp
var root = serialized.Should().NotBeNull().And.BeValidJson().Which;   // string -> JsonNode
root.Should().HaveElement("message").Which
    .Should().HaveElement("$type").Which
    .Should().HaveValue(typeof(SubscribeRequest).FullName);
var raw = deserialized.Message.Should().NotBeNull().And.Subject.As<RawJson>();
```

`BeValidJson()` (on a string), `HaveElement(name)` / `HaveValue(text)` (on a `JsonNode`), and
`.As<T>()` are all provided.

---

## 4. What is unchanged (do NOT touch)

Same names, same `.And` / `.Which` chaining, same trailing `because` args:
`Be / NotBe / BeNull / NotBeNull / BeSameAs / NotBeSameAs / BeOfType<T>() / BeAssignableTo<T>`,
`BeTrue / BeFalse`, `Contain / NotContain / StartWith / NotStartWith / EndWith / Match / MatchRegex /
BeEmpty / NotBeNullOrEmpty / NotBeNullOrWhiteSpace / HaveLength`,
`BeGreaterThan(OrEqualTo) / BeLessThan(OrEqualTo) / BeInRange / BePositive / BeAfter / BeBefore`,
`HaveCount(GreaterThan…) / BeEmpty / NotBeEmpty / Contain / ContainSingle / OnlyContain / AllSatisfy /
Equal / BeSubsetOf / OnlyHaveUniqueItems / BeInAscendingOrder`,
`ContainKey / ContainValue`, `Throw<T>().WithMessage(...) / NotThrow / ThrowAsync<T>() / NotThrowAsync`,
`HaveFlag / NotHaveFlag`, and the time helpers `10.Seconds()`, `200.Milliseconds()`, `1.5.Minutes()`.

`AssertionScope` exists as a shim (collects failures, throws on dispose).

---

## 5. Rules when migrating a project (sub-agent contract)

1. **Scope:** edit ONLY `.cs` files under this one test project. Never touch another project.
2. **Never edit the library** (`src/MeshWeaver.Reactive.Assertions`). If an assertion method is genuinely
   missing, **report it** (name + signature + the call site) — do not add it yourself, or parallel agents
   will collide on the shared file.
3. **Build:** `dotnet build <thisProject>.csproj --no-dependencies --nologo` (dependencies are prebuilt;
   `--no-dependencies` avoids rebuilding shared DLLs and the lock contention that causes).
4. **Done = 0 errors AND 0 warnings** for this project. Read the compiler output; fix every `error CS`
   and `warning CS` that originates in this project's files.
5. **Don't** change log levels, add `Task.Delay`, or alter unrelated code. Keep the diff to the migration.
