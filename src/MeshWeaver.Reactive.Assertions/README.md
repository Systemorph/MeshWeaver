# MeshWeaver.Reactive.Assertions

Fluent, **await-free** assertions for `IObservable<T>`. Subscribe, wait (with a timeout) for the
relevant emission, and assert — in one fluent chain. Test bodies stay declarative and free of
`await`.

## Why

Reactive code is best tested reactively. Instead of bridging a stream to a `Task` and `await`-ing
it, assert on the stream directly:

```csharp
using MeshWeaver.Reactive.Assertions;

// Wait for the first snapshot that has two rows:
meshQuery.Query(request).Should().Match(rows => rows.Count == 2);

// Equality on the first emission:
hub.GetEffectivePermissions(path).Should().Be(Permission.Read | Permission.Create);

// Custom timeout for this chain:
stream.Should().Within(TimeSpan.FromSeconds(2)).Emit();

// Negative — nothing should arrive:
stream.Should().NotEmit(within: TimeSpan.FromMilliseconds(200));
```

The blocking wait lives inside the assertion (it goes through `System.Reactive`'s task bridge, so it
never captures a synchronization context and cannot deadlock a test). Your test body never writes
`await`.

## API

| Member | Behaviour |
|---|---|
| `Should()` / `Should(timeout)` | Begin an assertion (default timeout 10 s). |
| `Within(timeout)` | Override the wait for the rest of the chain. |
| `Emit()` | Block for the first emission; return it for further inspection. |
| `Match(predicate)` | Block for the first emission satisfying the predicate; return it. The workhorse — fold the assertion into the predicate. |
| `Be(expected)` | Assert the first emission equals `expected`. |
| `Complete()` | Assert the stream completes within the timeout. |
| `NotEmit(within)` | Assert nothing is emitted within the window. |

Depends only on `System.Reactive`.
