---
Name: Adopting .NET 11 — Measured
Category: Architecture
Description: The measurements taken on the v4 line before adopting any .NET 11 runtime or library feature — what each one showed, the two defects it exposed and fixed (the thread-pool census and the runtime compiler's language version), and what was adopted, deferred or refused, with the numbers.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 3v18h18"/><path d="m7 15 4-4 3 3 5-6"/></svg>
---

# Adopting .NET 11 — measured

The v4 line runs on `net11.0` ([Platform 4 on .NET 11](../Platform4Net11)). This page records what
was MEASURED on that line before any .NET 11 feature was adopted, and what each measurement decided.
The rule is: no adoption without a before/after number or a correctness argument. Every number below
was taken on an Apple M-series host (arm64), SDK 11.0.100, runtime 11.0.0 (and 10.0.11 for the
comparison column).

## What .NET 11 offers, from the official pages

| Feature | Opt-in? | Source |
|---|---|---|
| Runtime Async: the runtime, not the compiler, generates the async state machine | **Opt-in per project** (`<Features>$(Features);runtime-async=on</Features>`). The runtime libraries themselves are already compiled with it. | [Runtime](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/runtime) |
| Continuations skip `ExecutionContext` capture/restore when there is nothing to restore | Free | [Runtime](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/runtime) |
| JIT: more bounds-check elimination, generic virtual method devirtualization, constant-folded `SequenceEqual`, redundant branch elimination | Free | [Runtime](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/runtime), [Performance Improvements in .NET 11](https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-11/) |
| R2R specialises `Comparer<T>.Default`/`EqualityComparer<T>.Default` | Free | [Runtime](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/runtime) |
| GC: dependent handles age with their referents | Free | [Performance Improvements in .NET 11](https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-11/) |
| No GC opt-in switch is introduced by the runtime page | — | [Runtime](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/runtime) |
| `ZstandardStream`/`ZstandardEncoder`/`ZstandardDictionary` in `System.IO.Compression`; span-based `DeflateEncoder`/`ZLibEncoder`/`GZipEncoder` | New API | [Libraries](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/libraries) |
| ZIP entries validate CRC32 on read (breaking) | Free, behavioural | [Breaking change](https://learn.microsoft.com/en-us/dotnet/core/compatibility/core-libraries/11/ziparchive-entry-crc32-validation) |
| System.Text.Json: JSON Lines output (`SerializeAsyncEnumerable(…, topLevelValues: true)`), `GetTypeInfo<T>`/`TryGetTypeInfo<T>`, `Utf8JsonWriter.Reset(…, JsonWriterOptions)`, unions | New API | [Libraries](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/libraries) |
| LINQ `LeftJoin`/`RightJoin`/`FullJoin`, tuple-returning `Join`/`GroupJoin` | New API | [Libraries](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/libraries) |
| `EqualityComparer<T>.Create`, `BitArray` span constructors and `PopCount` | New API | [Libraries](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/libraries) |
| `ReadOnlyMemoryStream`, `ReadOnlySequenceStream`, `StringStream`; `Base64.EncodeToString`/`DecodeFromChars`; `Utf8`/`Utf16.IndexOfInvalidSubsequence`; `StringBuilder.MoveChunks` | New API | [Libraries](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/libraries) |
| `MemoryExtensions.ContainsAnyWhiteSpace`/`IndexOfAnyWhiteSpace`; faster ASCII `string.Split`; `ImmutableArray.Create` via `Array.Copy` | New API / free | [Performance Improvements in .NET 11](https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-11/) |
| `SearchValues`, `Frozen*`, hashing (`XxHash*`, SHA) | **No new API found** on the libraries page or the read parts of the performance post | — |

## (a) The inline-resumption bridge under Runtime Async

`test/MeshWeaver.Messaging.Hub.Test/InlineResumptionMechanismTest` measures which thread an awaiter
resumes on. It was run on the same commit with the test assembly compiled both ways (the switch
verified by the assembly: 1,412,096 bytes with state machines, 1,138,688 bytes without them, and no
`>d__` state machine left for the test methods).

| Run | Result |
|---|---|
| runtime-async off | 5 of 5 passed |
| runtime-async on (test assembly) | 5 of 5 passed |
| whole project (799 tests), runtime-async on | 799 of 799 passed, 312 s wall |
| whole project, runtime-async off | 799 of 799 passed, 323 s wall |

The 3 % wall-clock difference is one run each on a host that was also building, so it is noise, not
a gain: this suite's time is spent waiting on hubs, not in state machines.

So `.ToTask()` and `await observable` still resume inline on the signalling thread, and `.Await(ct)`
/`.ObserveCompletion(…)` still do not. Runtime Async changes how `await` is generated, not where a
custom awaiter's continuation runs. 🚨 Toggling `Features` on an already-built project is NOT always
picked up by an incremental build; measured on a scratch project, a `-p:` switch left the old
state-machine assembly in place. Verify the switch on the assembly, never on the command line.

## (b) The thread-pool census was silently wrong on .NET 11 — fixed

`ThreadPoolQueueCensus` keys each queued work item by the code it will run. On .NET 11 the runtime
libraries are compiled with Runtime Async, so a BCL async method's task is an
`AsyncHelpers+RuntimeAsyncTask<T>` whose delegate is the runtime's own `DispatchContinuations`.
Measured with every pool worker parked and continuations queued behind them:

| Queued item | Census key before the fix | After |
|---|---|---|
| 4 × `Stream.CopyToAsync` continuations | `` …RuntimeAsyncTask`1[VoidTaskResult].DispatchContinuations `` | `System.IO.Stream.<CopyToAsync>g__Core\|30_0` |
| 2 × `StreamReader.ReadToEndAsync` | `` …RuntimeAsyncTask`1[String].DispatchContinuations `` | `System.IO.StreamReader.ReadBufferAsync` |
| 5 × our own method compiled runtime-async | `` …RuntimeAsyncTask`1[VoidTaskResult].DispatchContinuations `` (merged with the four above) | the method's own name |

Every BCL async continuation in a `[POOLSTALL]` capture therefore collapsed into one group per RESULT
type. Nothing failed, so nothing said so. The fix reads the task's continuation chain
(`m_stateObject`, then `Continuation.Next`), takes each link's `ResumeInfo.DiagnosticIP`, and maps it
back with the runtime's `StackFrame.GetMethodFromNativeIP` — the address the debugger and SOS use.
All of it is internal and reached by reflection; a runtime without it keys the item as
`runtime-async continuation (method unresolved: <what is missing>)`. Two traps found on the way:
`GetType()` on a continuation object throws `NotSupportedException`, so the chain is walked through
the base type's fields only; and the first link (the task's own) carries no diagnostic address.

Tests: `ThreadPoolQueueCensusTest.ARuntimeAsyncBclContinuation_IsKeyedByTheMethodThatResumes_NotTheDispatcher`
and `…OnTheRealThreadPool_QueuedRuntimeAsyncContinuations_AreNamedByTheirMethod` (200 queued
continuations). Negative control: with the runtime-async branch disabled both fail — the first with
`` Expected "…RuntimeAsyncTask`1[System.Threading.Tasks.VoidTaskResult].DispatchContinuations" to start
with "System.IO.Stream.<CopyToAsync>" ``, the second with `Expected value to be 200 … but found 0`.

## (c) The portal's runtime compiler does not speak C# 15

`EmitPipeline.CreateParseOptions` sets no language version, so in-mesh C# compiles at the default of
the `Microsoft.CodeAnalysis.CSharp` package pinned in `Directory.Packages.props`. Measured:

| Compiler | Version | Default language | `closed` hierarchy | `union` |
|---|---|---|---|---|
| SDK 11.0.100 (builds `src/`) | 5.12.0-1.26529.118 | C# 15 | compiles | compiles |
| Runtime compiler (package) | 5.9.0 | **C# 14** | **CS8652**: "currently in Preview and *unsupported*" | does not parse |
| Package 5.12.0 (on nuget.org) | 5.12.0 | C# 15 | compiles | compiles |

So `src/` and in-mesh source speak different languages on v4. Until the package moves, no C# 15
feature may appear on a surface in-mesh code compiles against, nor in in-mesh source. Moving it is
NOT done here: it changes what every NodeType compiles to on every replica, so it belongs in its own
change, with the in-mesh compile sweep and the bake as its evidence.
`TheRuntimeCompilerLanguageVersionTest` pins today's state (C# 14, `closed` refused, `union` refused)
and turns red on the bump, so the change cannot happen unnoticed. (A `closed` type is implicitly
abstract in C# 15: `closed abstract` is CS9384.)

## (d) ZIP CRC32 on read — a corrupt bundle is a reported refusal

Measured: an entry whose recorded CRC32 no longer matches its bytes now throws
`InvalidDataException` ("CRC"), where .NET 10 read it silently. Each reader was checked for where
that exception lands:

| Reader | Where `InvalidDataException` lands |
|---|---|
| `BundleReader.ReadAll` (every bundle read) | thrown as `InvalidDataException`, the type its existing size checks already throw |
| `ServedModuleBytes.FromSealed` (`Inflate`) | caught (`IOException or InvalidDataException or BadImageFormatException`), logged as "unreadable — it cannot supply …", the next candidate is tried |
| `PluginBundleClient` (registry download → landing) | the read runs in `IIoPool.InvokeBlocking`, so it is the observable's error arm; `LandingFaultOutcome` records it as `landing failed — the module is unchanged` |
| `MeshOperations.ParseExportZip` (node import) | runs in `IIoPool.InvokeBlocking`; the error reaches the import's subscriber |

No reader crashes. `BundleReaderTest.AnEntryWhoseChecksumDoesNotMatch_IsRefusedAsInvalidData_NeverReadSilently`
pins the type and the message; its control is the same bundle with its checksum intact, read back
byte for byte. **Not changed, and stated:** `TransientRegistryFailure.IsTransient` treats
`InvalidDataException` as a crash (`_ => true`), so a registry bundle that is corrupt AT REST is
re-armed and re-fetched rather than recorded as a final `Failed`. A truncated transfer raises the same
exception type and is genuinely transient, so splitting the two needs more than the type and is
left as a follow-up.

## Hot paths: what the retarget bought, and what a new API would add

A BenchmarkDotNet run (in-process, short run) of the platform's hot-path primitives, the same code
on net10.0 and net11.0:

| Operation | net10.0 (10.0.11) | net11.0 (11.0.0) | Allocated (both) |
|---|---|---|---|
| Serialize a polymorphic hub message (`$type`, `ImmutableDictionary` content) | 394 ns | 362 ns | 960 B |
| Deserialize it | 924 ns | 895 ns | 2,896 B |
| `JsonDocument.Parse` → cloned `RootElement` | 363 ns | 351 ns | 1,576 B |
| `ImmutableDictionary` lookup | 3.65 ns | 2.76 ns | 0 |
| `SHA256.HashData`, 64 KB (content hashing) | 19.3 µs | 19.9 µs | 56 B |
| `path.Split('/')`, 7 segments | 42.9 ns | 43.8 ns | 336 B |
| `MeshNode.Segments` shape (`Split` + `Append` + `ToArray`) | 58.7 ns | 56.9 ns | 432 B |
| `token.Any(char.IsWhiteSpace)`, 70 chars, none found | 83.0 ns | 86.4 ns | 0 |
| `token.AsSpan().ContainsAnyWhiteSpace()` (new in 11) | — | 2.7 ns | 0 |

The ShortRun error bars are wide (Serialize ±165 ns on 11), so only two rows are clearly outside
them: the `ImmutableDictionary` lookup (−25 %) and the new whitespace helper (32× the LINQ form).
JSON gains 3–8 % for free; hashing and splitting are unchanged; no allocation moved.

**Adopted: nothing in `src/` for these.** The whitespace helper is the only new API with a large
win, and every `Any(char.IsWhiteSpace)` in `src/` is a one-shot validation (a module name, a
repository, a framework MVID, a path literal), never a per-message path, so a 32× win on an 85 ns
check is not a measurable gain for the platform. Use it in new code. `Utf8JsonWriter.Reset` would
need a pooled writer, and a pool is mesh-scoped state that has to buy more than writer construction
costs; no measurement shows that. `FullJoin`, JSON Lines output and Zstandard have no caller (see
the adoption analysis).

## Runtime Async: decision

The same three-deep async chain, on net11.0, compiled both ways:

| Chain | state machines | runtime-async |
|---|---|---|
| completes synchronously (the `IIoPool` leaf fast path) | 7.79 ns, 0 B | 5.14 ns, 0 B |
| one real suspension (`Task.Yield`) | 1,316 ns, 279 B | 1,231 ns (±834), 304 B |

A synchronous chain is a third faster. A chain that really suspends, which is what an I/O leaf
does, is within the error bar and allocates 9 % more.

**Hang dumps.** A test-shaped hang (an async chain that suspends, resumes, then blocks a thread)
was dumped live both ways. Off: 10 frames, the innermost method plus five `AsyncStateMachineBox` /
`ExecutionContext` frames. On: 5 frames, the innermost method plus the pool's dispatch loop. The
logical callers (the test method and its arrange step) are on NEITHER stack, because after a real
suspension they are continuations, not frames. So Runtime Async removes noise from a hang dump; it
does not recover the causal chain. The official 13 → 5 example is a chain that never suspends.

**Decision.**

- **`test/`: enabled in `MeshWeaver.Messaging.Hub.Test`, the project it was trialled on.** Its
  bridge tests behave identically, all 799 tests pass, and its hang dumps lose the state-machine
  frames. The next projects follow one at a time, each with its own before/after run.
- **`src/`: off.** The async left in `src/` sits at I/O leaves behind `IIoPool`, where the chain
  suspends; there the measured gain is inside the noise and the allocation is higher. The one place
  Runtime Async already reaches production, the runtime libraries, is now read correctly by the
  thread-pool census (b). Revisit when a profile of a running portal shows a synchronous async chain
  on a hot path.
- **Never a reason to relax a rule.** Runtime Async changes how `await` is generated, not where it is
  allowed: the no-async rules for hub and UI code stand unchanged ([Asynchronous Calls](../AsynchronousCalls)).

See also: [Platform 4 on .NET 11](../Platform4Net11), [Which Kind of Silence](../WhichKindOfSilence),
[Asynchronous Calls](../AsynchronousCalls), [Node Type Compilation](../NodeTypeCompilation).
