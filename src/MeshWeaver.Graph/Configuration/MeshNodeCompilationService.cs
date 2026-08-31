using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Text.RegularExpressions;
using MeshWeaver.Compiler;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using MeshWeaver.NuGet;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Lsp = MeshWeaver.Mesh.Services.LanguageServer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Service that compiles and caches MeshNode assemblies on-demand.
/// Combines code generation (via DynamicMeshNodeAttributeGenerator) with Roslyn compilation.
/// Implements IMeshNodeCompilationService from MeshWeaver.Mesh.Contract.
/// </summary>
internal class MeshNodeCompilationService(
    ICompilationCacheService cacheService,
    IOptions<CompilationCacheOptions> cacheOptions,
    IMessageHub hub,
    INuGetAssemblyResolver nugetResolver,
    ILogger<MeshNodeCompilationService> logger,
    IAssemblyStore? assemblyStore = null)
    : IMeshNodeCompilationService
{
    private readonly IAssemblyStore _assemblyStore = assemblyStore ?? NullAssemblyStore.Instance;
    private readonly CompilationCacheOptions _cacheOptions = cacheOptions.Value ?? new CompilationCacheOptions();

    // Compile pool for the bare-async leaf (CompilationInputs assembly): a plain
    // Observable.FromAsync deadlocks under a blocking subscriber because SubscribeOn
    // only moves the subscribe, not the await continuation. The pool runs the leaf
    // with ConfigureAwait(false) behind a concurrency gate. Falls back to the
    // unbounded pool when no registry is wired (e.g. tests outside DI).
    private readonly IIoPool _ioPool =
        hub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.Compile)
        ?? IoPool.Unbounded;

    // 🚨 Offload the compile to the ThreadPool via Task.Run — NOT the IoPool. Two reasons the
    // IoPool ("the correct abstraction") fails for this leaf:
    //   1. Its SemaphoreSlim gate serialises the compile against itself (activity-driven +
    //      GetCompilationPathRequest-driven compiles both acquire the Compile pool gate; the
    //      synchronous single-flight in-memory compile re-enters / parks on WaitAsync — an idle
    //      wait, the dotnet-stack signature).
    //   2. SubscribeOn + ConfigureAwait(false) hop threads, which DROPS the AccessService
    //      identity the same way Task.Run does — see below.
    // Task.Run schedules on TaskScheduler.Default (ThreadPool): no gate, no re-entrancy, and the
    // continuation never captures the action-block TaskScheduler the way Task.ToObservable() does.
    //
    // 🚨 RE-ESTABLISH THE LOGIN. The handler ran the compile under ImpersonateAsSystem so its
    // source reads + WriteToParent bypass the caller's per-node permissions. Task.Run hops to a
    // fresh ThreadPool thread where the AccessService identity (AsyncLocal) does NOT flow, and the
    // handler's Impersonate scope is long disposed by the time this runs. Without re-impersonating,
    // the source reads run unauthenticated and stall until the next 45s sync-stream heartbeat
    // re-delivers the content — the ~42s freeze. Impersonate INSIDE the async lambda so the scope
    // spans every await of the compile.
    private IObservable<T> OnThreadPool<T>(Func<Task<T>> asyncWork)
        => OnThreadPoolCore(() => Task.Run(async () =>
        {
            var accessService = hub.ServiceProvider.GetService<AccessService>();
            using (accessService?.ImpersonateAsSystem())
                return await asyncWork();
        }));

    // Synchronous heavy work (Roslyn Emit + assembly load + reflection). Run it on a
    // DEDICATED long-running thread via CompileThread.Run — NOT the ThreadPool. A Roslyn Emit
    // is multi-second, CPU-bound, synchronous work; on Task.Run it pins a ThreadPool worker
    // thread for its whole duration, and a burst of compiles starves the reactive continuations
    // (which also run on the ThreadPool, growing only ~1-2 threads/s) that deliver every
    // cross-hub response — the bulk-only "different test times out each run" flake class. The
    // dedicated thread keeps the compile's CPU off the pool the actor/reactive scheduler needs.
    // Same no-gate / no-captured-scheduler / ExecutionContext-flows (AccessService identity)
    // guarantees as Task.Run — see CompileThread.
    private IObservable<T> OnThreadPool<T>(Func<T> syncWork)
        => OnThreadPoolCore(() => CompileThread.Run(() =>
        {
            var accessService = hub.ServiceProvider.GetService<AccessService>();
            using (accessService?.ImpersonateAsSystem())
                return syncWork();
        }));

    // Shared bridge: subscribe a Task<T>-producing leaf into the observable contract, hopping the
    // OnNext/OnError onto the continuation via ExecuteSynchronously on TaskScheduler.Default so the
    // calling hub's action-block scheduler is never captured. See the class-level note above on why
    // this path uses Task.Run and NOT the IoPool (the Compile pool's gate deadlocks the compile
    // against itself; the thread-hop also drops the AccessService identity — re-established inside).
    private static IObservable<T> OnThreadPoolCore<T>(Func<Task<T>> start)
        => Observable.Create<T>(observer =>
        {
            start().ContinueWith(
                t =>
                {
                    if (t.IsFaulted) observer.OnError(t.Exception!.GetBaseException());
                    else if (t.IsCanceled) observer.OnError(new OperationCanceledException());
                    else { observer.OnNext(t.Result); observer.OnCompleted(); }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return Disposable.Empty;
        });

    /// <summary>
    /// 🚨 WALL-CLOCK BOUND on one leg of the compile pipeline, with the leg NAMED in the
    /// failure. Companion to <see cref="SnapshotSources"/>'s
    /// <see cref="CompilationCacheOptions.SourceSnapshotTimeout"/> and there for exactly the
    /// same reason: the compile subscription is the ONLY component that can settle the
    /// <c>CompilationStatus = Compiling</c> its dispatcher just flipped, so a leg that never
    /// completes strands the NodeType at Compiling for the life of the activation — and the
    /// (correct) single-flight status lock then ABSORBS every fresh trigger against it, so
    /// nothing recovers it short of re-activation.
    ///
    /// <para>The totality guard in <c>RunCompile</c> (<c>DefaultIfEmpty</c>) already makes an
    /// EMPTY completion terminal; this makes NO completion terminal. Together they close the
    /// contract "every dispatched compile reaches exactly one terminal status". The bound is
    /// NOT a retry, a watchdog or a supersede: it makes an IO/leaf answer, once, and the
    /// answer propagates through <c>RunCompile</c>'s <c>Catch</c> to a
    /// <c>CompilationStatus = Error</c> write naming the leg that hung.</para>
    ///
    /// <para>🚨 The timeout also CANCELS the leg: <see cref="CancellationDisposable"/> is
    /// disposed when the bound unsubscribes, tripping the token the leaf was started with, so
    /// an abandoned NuGet restore / Roslyn emit stops instead of pinning a compile thread for
    /// the life of the process. That is more than tidiness for the Roslyn leg — the per-node
    /// single-flight entry (<c>_inflightCompiles</c>) evicts only when its Task settles, so
    /// without cancellation every retry would receive the SAME hung Task and re-fail
    /// identically forever. A leg with nothing cancellable (assembly load + reflection: a
    /// running type initializer cannot be interrupted) simply IGNORES the token it is handed
    /// and the bound only settles the state machine — the honest limit of what it can do
    /// there, at the cost of one abandoned load thread.</para>
    /// </summary>
    private static IObservable<T> BoundLeg<T>(
        Func<CancellationToken, IObservable<T>> leg,
        TimeSpan bound,
        string legName,
        string nodePath)
        => Observable
            .Using(() => new CancellationDisposable(), cancel => leg(cancel.Token))
            .Timeout(bound, Observable.Throw<T>(new TimeoutException(
                $"Compile leg '{legName}' for '{nodePath}' did not complete within "
                + $"{bound.TotalSeconds:0}s. The compile fails terminally instead of parking at "
                + "CompilationStatus=Compiling forever (where the single-flight status lock would "
                + "absorb every later trigger); retry via the Compile button / a fresh "
                + "RequestedReleaseAt once the cause is understood.")));

    private JsonSerializerOptions JsonOptions => hub.JsonSerializerOptions;
    private readonly DynamicMeshNodeAttributeGenerator _attributeGenerator = new();

    // Per-nodeName single-flight: when two callers ask to compile the same
    // NodeType concurrently, the first one's Task runs the compile; every
    // other caller receives the SAME Task and awaits its result. No second
    // call to CompileAsyncCore (which the old SemaphoreSlim shape forced —
    // caller 2 waited for the semaphore, then re-entered the cache-check
    // path even though caller 1 had just finished). Entries clear once the
    // task settles so a future re-compile (e.g. after source edit) starts
    // fresh.
    private readonly ConcurrentDictionary<string, Lazy<Task<CompileEmit>>> _inflightCompiles =
        new(StringComparer.Ordinal);

    // Query expansion lives in CodeQueryResolver (MeshWeaver.Compiler) so the NodeType
    // Configuration side menu can evaluate the *same* queries the compiler uses — the Sources /
    // Tests lists displayed in the UI are guaranteed to match the files compiled.
    //
    // The reference-set construction lives in CompileReferences (MeshWeaver.Compiler, #1707) —
    // which assemblies Roslyn can see is part of the compile input. This service only resolves
    // THIS mesh's installed-module composition from its service tree and threads it in. Lazy per
    // service instance: modules install at boot, before any compile runs.
    private IReadOnlyList<MetadataReference> References => meshReferences.Value;

    private readonly Lazy<IReadOnlyList<MetadataReference>> meshReferences = new(() =>
        CompileReferences.ComposeWithModules(
            hub.ServiceProvider.GetServices<InstalledModuleAssembly>().ToArray()));

    /// <summary>
    /// Resolves every <c>@@</c> include via the toolchain's shaping
    /// (<see cref="NodeCompileShaping.ResolveCodeIncludes"/>), supplying the mesh-actor half:
    /// the System-impersonated, bounded include read (<see cref="ReadIncludeNode"/>).
    /// </summary>
    private IObservable<string> ResolveCodeIncludes(
        string code, HashSet<string> resolved, string? anchorPath)
        => NodeCompileShaping.ResolveCodeIncludes(
            code, resolved, anchorPath, ReadIncludeNode, logger);

    /// <summary>
    /// 🚨 EVERY compile-path node read runs under the well-known System identity — the same rule
    /// (and the same <see cref="Observable.Using{TResult,TResource}(Func{TResource},Func{TResource,IObservable{TResult}})"/>
    /// shape) as <see cref="GetSourceCollection"/>. A compile reading the source it was asked to
    /// compile is framework infrastructure, NOT a user-scoped read, and the identity it would
    /// otherwise inherit is gone by the time the read is issued: every one of these reads is
    /// subscribed from a CONTINUATION of the source-snapshot chain, and the ambient
    /// <c>AccessService</c> context is an <c>AsyncLocal</c> that does not survive the hop onto
    /// whichever thread the upstream emitted on.
    ///
    /// <para>Without the scope the post is null-<c>AccessContext</c>, and the
    /// never-null PostPipeline guard REFUSES it (<c>d.Failed(reason)</c>, and
    /// <c>GetDataRequest</c> is not exempt). The single-argument <c>Failed</c> records no
    /// <c>ErrorType</c>, so <c>MeshNodeStreamExtensions.GetMeshNode</c> takes its
    /// non-<c>Unauthorized</c> branch and emits <b>null</b> — indistinguishable from "the node
    /// does not exist". For an <c>@@</c> include that means the directive is left VERBATIM in the
    /// source, Roslyn parses the <c>@@</c> line itself, and the NodeType parks at
    /// <c>CompileError</c> — which refuses portal readiness and holds every instance hub for the
    /// full 60s activation budget. Issue #1253 (memex-cloud 2026-08-12: 22 refused reads in one
    /// millisecond, the five <c>@@</c> targets of <c>FutuRe/LocalAnalysis/Source/ExternalDependencies</c>
    /// in file order).</para>
    ///
    /// <para>The scope must be established at SUBSCRIBE time, not at composition time — hence
    /// <c>Observable.Using</c>, whose resource factory runs inside the subscribe call that posts
    /// the <c>GetDataRequest</c>. Wrapping each read individually (rather than the whole chain)
    /// is deliberate: a chained read — the include fallback below — is subscribed from the FIRST
    /// read's emission, i.e. on another thread again, so an outer scope would not cover it.</para>
    ///
    /// <para>This is the explicit infrastructure opt-in AGENTS.md sanctions
    /// (<c>ImpersonateAsSystem</c>), NOT the "silently stamp hub-self as principal" fallback that
    /// was deleted 2026-05-21: the identity is chosen at a named callsite for a named reason, and
    /// the PostPipeline still fails closed for everything that does not opt in.</para>
    /// </summary>
    private IObservable<MeshNode?> ReadCompileSourceNode(
        string path, ReadTimeoutBehavior onTimeout)
    {
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        return Observable.Using(
            () => accessService?.ImpersonateAsSystem() ?? Disposable.Empty,
            _ => hub.GetMeshNode(path, TimeSpan.FromSeconds(15), onTimeout));
    }

    /// <summary>
    /// Reads one include target — <paramref name="path"/> first, then
    /// <paramref name="fallbackPath"/> when it differs and the first read found the node genuinely
    /// ABSENT — and reports which path actually produced the node so nested includes anchor there.
    ///
    /// <para>🚨 The anchored read uses <see cref="ReadTimeoutBehavior.Throw"/>, not
    /// <c>EmitNull</c>, for one reason: only "absent" justifies spending a second 15s read, and
    /// <c>EmitNull</c> collapses a STALL into the same null — which would let one I/O stall cost
    /// 30s per include instead of 15s. The <see cref="TimeoutException"/> is caught here and
    /// degrades to exactly what <c>EmitNull</c> produced (an unresolved include, warned at the call
    /// site), so a stalled include still costs ONE read. The exception's diagnostics — elapsed
    /// time, the reading hub's in-flight snapshot — are logged rather than swallowed.</para>
    ///
    /// <para>Not a fault either way: this runs on the hub-ACTIVATION path, where turning a
    /// transient stall into a hard fault would park the type.</para>
    /// </summary>
    private IObservable<(MeshNode? Node, string Path)> ReadIncludeNode(
        string path, string? fallbackPath)
        => ReadCompileSourceNode(path, ReadTimeoutBehavior.Throw)
            .SelectMany(node => node is not null || fallbackPath is null
                ? Observable.Return<(MeshNode? Node, string Path)>((node, path))
                // Genuinely absent (no timeout) — the authored path is the other legal reading.
                : ReadCompileSourceNode(fallbackPath, ReadTimeoutBehavior.EmitNull)
                    .Select<MeshNode?, (MeshNode? Node, string Path)>(
                        fallback => (fallback, fallbackPath)))
            .Catch<(MeshNode? Node, string Path), TimeoutException>(ex =>
            {
                logger.LogWarning(ex,
                    "Code include @@{Path} STALLED (not absent) — the include stays unresolved and "
                    + "no fallback read is attempted, so one stall costs one read, not two", path);
                return Observable.Return<(MeshNode? Node, string Path)>((null, path));
            });

    /// <inheritdoc />
    public IObservable<string?> GetAssemblyLocation(MeshNode node)
        => GetAssemblyLocationWithLog(node).Select(t => t.Path);

    /// <summary>
    /// What one Roslyn emit produced: where the bytes landed, and the stage-1 CONTENT KEY digest
    /// of the fully generated input that produced them (#1707 slice 4 — see
    /// <see cref="GeneratedInputIdentity"/>).
    /// </summary>
    /// <param name="Path">Where the assembly landed.</param>
    /// <param name="InputDigest">The generated-input digest the cache key folds.</param>
    /// <param name="Warnings">Diagnostics the compile produced — carried up so they reach the
    /// compile ACTIVITY. The emit deliberately does not log them itself (the log-once contract),
    /// so this is how they travel to the one funnel that reports.</param>
    private readonly record struct CompileEmit(
        string? Path, string? InputDigest, IReadOnlyList<string> Warnings);

    /// <summary>
    /// One compile attempt's outcome: the assembly path (null on failure), the CONTENT KEY digest
    /// of the input that produced it (null when no compile ran — a disk-cache hit; the dependency
    /// record then simply carries no content key), the <see cref="ActivityLog"/>, and — the point
    /// of carrying it — the ONE source snapshot the attempt was taken against. Every downstream
    /// stage (<see cref="DiscoverSourceVersionSnapshot"/>, <see cref="BuildFailureDiagnostics"/>)
    /// reuses <c>Sources</c> instead of re-discovering; see
    /// <see cref="GetAssemblyLocationWithLog"/>.
    /// </summary>
    private readonly record struct CompileAttempt(
        string? Path, string? InputDigest, ActivityLog Log, IReadOnlyList<MeshNode> Sources);

    /// <summary>
    /// Companion to <see cref="GetAssemblyLocation"/> that also surfaces the
    /// <see cref="ActivityLog"/> of the compile attempt — every executed source
    /// query, every matched Code path, the final compile result. The same chain
    /// runs underneath; this method is what <see cref="CompileAndGetConfigurations"/>
    /// uses so the response surfaced through <c>GetCompilationPathResponse.Log</c>
    /// reflects what actually happened (no double-compile to gather diagnostics).
    ///
    /// <para>🚨 ONE SOURCE SNAPSHOT PER COMPILE. The source set is discovered here, exactly
    /// once, and then THREADED — into the cache-key fold, into <see cref="CompileCore"/> (as
    /// its <c>sourcesOverride</c>), and out on <see cref="CompileAttempt.Sources"/> for the
    /// version-snapshot / failure-diagnostics stages. It used to be re-discovered
    /// independently at each of those stages: measured on a Monolith compile,
    /// <b>three</b> full source-set materializations per successful compile
    /// (<c>freshness</c> → <c>compile-core</c> → <c>version-snapshot</c>), each racing a fresh
    /// set of live <see cref="IMeshService.Query"/> discovery reads against the cached synced
    /// query. On memex those reads are cross-partition and measured ~0.25s each (issue #686),
    /// so the multiplication is paid in seconds against a mesh that a compile is already
    /// loading; on a fresh mesh it is ~0ms, which is precisely why the fresh-mesh comparison
    /// never showed it.</para>
    ///
    /// <para>It is also a CORRECTNESS fix, independent of cost: the three snapshots were three
    /// independent races over a LIVE source collection, so they could disagree. The cache key
    /// (<c>effectiveLastModified</c>) could be folded from set A while set B compiled and set C
    /// was recorded into <see cref="NodeTypeDefinition.CompiledSources"/> — and <c>CompiledSources</c>
    /// is what the recompile-needed check compares against, so a set that was never compiled
    /// either suppresses a needed recompile or triggers a spurious one. One snapshot cannot
    /// disagree with itself.</para>
    /// </summary>
    private IObservable<CompileAttempt> GetAssemblyLocationWithLog(
        MeshNode node, IReadOnlyList<MeshNode>? sourcesOverride = null)
    {
        var log = new ActivityLog(ActivityCategory.Compilation)
        {
            HubPath = node.Path,
            AffectedPaths = ImmutableList<string>.Empty.Add(node.Path)
        };

        if (string.IsNullOrEmpty(node.NodeType))
        {
            logger.LogDebug("Node {NodePath} has no NodeType, skipping assembly compilation", node.Path);
            return Observable.Return(new CompileAttempt(null, null,
                AppendInfo(log, $"Skipped — node '{node.Path}' has no NodeType.")
                    .FinishByOutcome((int)hub.Version),
                Array.Empty<MeshNode>()));
        }

        var nodeName = cacheService.SanitizeNodeName(node.Path);
        // GetDllPath returns the flat shorthand ({cacheDir}/{nodeName}.dll) —
        // CompileToDiskAsync actually writes to a timestamped subdir.
        // TryGetLatestCachedDllPath resolves to the newest valid subdir DLL,
        // used below in the cache-hit branch so the load actually finds bytes.
        var dllPath = cacheService.GetDllPath(nodeName);

        // Resolve the owning NodeTypeDefinition once — used for source discovery
        // (Sources / Source convention) and for Configuration / ContentCollections.
        IObservable<NodeTypeDefinition?> resolveDef;
        string selfPath;
        if (node.Content is NodeTypeDefinition selfDef)
        {
            resolveDef = Observable.Return<NodeTypeDefinition?>(selfDef);
            selfPath = node.Path;
        }
        else
        {
            // ⚠️ EmitNull here is a DELIBERATE HOLD, not an endorsement. A stalled read of the
            // NodeType definition currently yields ntDef == null and the compile proceeds
            // against a null definition — a silently wrong compile. Making it throw is the
            // right end state, but this runs on the hub-ACTIVATION path where compile status
            // is cached: a transient stall would flip from "wrong compile" to "PARKED type"
            // (the UWDeepfield outage class), which needs its own retry/park semantics and its
            // own tests. Held at today's behaviour on purpose; the stall is no longer silent
            // (the read logs it at Warning with hub diagnostics). See the report.
            resolveDef = ReadCompileSourceNode(node.NodeType, ReadTimeoutBehavior.EmitNull)
                .Select(typeNode => typeNode.ContentAs<NodeTypeDefinition>(JsonOptions));
            selfPath = node.NodeType;
        }

        // ⏱️ Two clocks, because the 45s has to be SOMEWHERE and every guess so far picked the
        // wrong somewhere: one for resolving the NodeType definition (a bounded 15s read that
        // yields null on stall), one for the source snapshot that follows it. Both feed the
        // compile's ActivityLog so a single `get @{Type}/_Activity/compile-…` shows the split.
        var resolveClock = System.Diagnostics.Stopwatch.StartNew();
        return resolveDef.SelectMany(ntDef =>
        {
            var resolveMs = resolveClock.ElapsedMilliseconds;
            var snapshotClock = System.Diagnostics.Stopwatch.StartNew();
            // 🚨 THE compile's source snapshot — taken here, once, and reused by every stage
            // below (cache-key fold, CompileCore, and the caller's version-snapshot /
            // failure-diagnostics via CompileAttempt.Sources). See the method remarks.
            return SnapshotSources(ntDef, selfPath, sourcesOverride)
                .Select(nodes => nodes as IReadOnlyList<MeshNode> ?? nodes.ToList())
                .SelectMany(sources =>
                {
                    // Source-aware cache check: the LastModified of every source Code node +
                    // the NodeType itself. The cache is valid only if the compiled DLL is
                    // newer than the most recent source change — so the freshness fold and
                    // the compile MUST see the same set, which is why it is one snapshot.
                    var maxSourceLastModified = sources.Aggregate(
                        DateTimeOffset.MinValue,
                        (acc, n) => n.LastModified > acc ? n.LastModified : acc);
                    log = AppendInfo(log,
                        $"⏱ NodeType definition resolved in {resolveMs}ms "
                        + $"({(ntDef is null ? "NULL — the read stalled or the node is absent" : "ok")}); "
                        + $"source snapshot {snapshotClock.ElapsedMilliseconds}ms "
                        + $"({sources.Count} node(s), taken ONCE and reused by every stage).");

                    var effectiveLastModified = node.LastModified > maxSourceLastModified
                        ? node.LastModified
                        : maxSourceLastModified;

                    if (cacheService.IsDiskCacheEnabled)
                    {
                        var cachedDllPath = cacheService.TryGetLatestCachedDllPath(nodeName, effectiveLastModified);
                        if (cachedDllPath is not null)
                        {
                            logger.LogDebug(
                                "Using cached assembly for {NodePath} at {DllPath} (effectiveLastModified={EffectiveLastModified})",
                                node.Path, cachedDllPath, effectiveLastModified);
                            return Observable.Return(new CompileAttempt(
                                cachedDllPath,
                                // No compile ran, so there is no generated input to key on.
                                null,
                                AppendInfo(log,
                                    $"Cache hit — returning {cachedDllPath} (effective LastModified={effectiveLastModified:O}).")
                                    .FinishByOutcome((int)hub.Version),
                                sources));
                        }
                    }

                    // Hand the snapshot down as the override — CompileCore then short-circuits
                    // its own SnapshotSources to this authoritative point-in-time set.
                    return CompileCore(node, ntDef, selfPath, log, sources)
                        .Select(t => new CompileAttempt(t.Path, t.InputDigest, t.Log, sources));
                });
        });
    }

    private static ActivityLog AppendInfo(ActivityLog log, string message)
        => log.Append(new LogMessage(message, LogLevel.Information));

    private static ActivityLog AppendWarning(ActivityLog log, string message)
        => log.Append(new LogMessage(message, LogLevel.Warning));

    private static ActivityLog AppendError(ActivityLog log, string message)
        => log.Append(new LogMessage(message, LogLevel.Error));

    /// <summary>
    /// Source-set discovery via the workspace SyncedQuery registry — one
    /// long-lived, cached, replayed <see cref="IObservable{T}"/> per
    /// <paramref name="selfPath"/>. The first call spins up a single
    /// <see cref="IMeshQueryCore.Query"/> per NodeType-resolved query
    /// (union of <c>Sources</c> + <c>Tests</c>); subsequent compiles for the
    /// same NodeType hit the registry's <c>Replay(1).RefCount()</c> cache and
    /// skip the Initial re-fetch entirely. Live updates flow through too —
    /// when a Source/Test Code node changes, the cached collection re-emits.
    /// </summary>
    private IObservable<IEnumerable<MeshNode>> GetSourceCollection(
        NodeTypeDefinition? ntDef, string selfPath)
    {
        // SHARED with the per-NodeType hub's sources/IsDirty watcher and any
        // layout-area that lists Source/Test children. Same cache id =
        // ONE Replay(1).RefCount() upstream subscription = "what the
        // compile sees" === "what the watcher computes IsDirty against".
        // If the cache keys diverged, IsDirty could disagree with whatever
        // bytes the compile produced (the bug class CodeEditRecompileTest
        // catches). See NodeSources.GetSources / NodeSources.CacheId.
        //
        // 🚨 Run the source GetQuery under System. Source-set discovery is
        // framework infrastructure, NOT a user-scoped read. Under a user-triggered
        // (or thread-hopped) compile the ambient identity can be lost, and the
        // per-source RLS check then routes a CheckPermission back INTO this hub —
        // a self-call that stalls the read until the ~45s sync-stream heartbeat
        // (the ~42s compile freeze; kickoff activity stuck Running at "Invoking
        // compiler…"). Observable.Using keeps the System scope alive for the live
        // GetQuery subscription. Mirrors InstallSourcesWatcher.
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        return Observable.Using(
            () => accessService?.ImpersonateAsSystem()
                  ?? System.Reactive.Disposables.Disposable.Empty,
            _ => NodeSources.GetSources(hub.GetWorkspace(), ntDef, selfPath));
    }

    /// <summary>
    /// Captures <c>{path → MeshNode.LastModified.Ticks}</c> for every source Code/Test
    /// node that feeds a compile of <paramref name="ntDef"/>. Used by the compile watcher
    /// to populate <c>NodeTypeDefinition.CompiledSources</c> on success so a future
    /// recompile-needed check is a data comparison (added/removed/modified)
    /// instead of a max-LastModified timing guess.
    /// <para>
    /// 🚨 On the compile path this is ALWAYS called with the snapshot the compile actually
    /// consumed (<see cref="CompileAttempt.Sources"/>), so it folds rather than re-discovers —
    /// that is what makes <c>CompiledSources</c> a record of what was compiled instead of a
    /// second, independently-raced observation of a live collection. A <c>null</c>
    /// <paramref name="sourcesOverride"/> (external callers) still takes its own snapshot.
    /// </para>
    /// <para>
    /// Uses <c>LastModified.Ticks</c> (not <c>Version</c>) because the framework's
    /// <c>UpdateNodeRequest</c> handler reliably refreshes <c>LastModified</c>, while
    /// <c>MeshNode.Version</c> is bumped only by the local workspace's
    /// <c>MeshNodeTypeSource</c> stamp and may not propagate through the synced
    /// mesh-level query that <c>HandleCreateRelease</c> reads from.
    /// </para>
    /// </summary>
    public IObservable<ImmutableDictionary<string, long>> DiscoverSourceVersionSnapshot(
        NodeTypeDefinition? ntDef, string selfPath,
        IReadOnlyList<MeshNode>? sourcesOverride = null) =>
        SnapshotSources(ntDef, selfPath, sourcesOverride)
            .Select(nodes => nodes
                .Where(n => !string.IsNullOrEmpty(n.Path))
                .Aggregate(
                    ImmutableDictionary<string, long>.Empty,
                    // One rule for both snapshots — see NodeTypeDefinition.SourceVersionOf.
                    // A raw .LastModified.UtcTicks here records 1601 for any source with no
                    // real timestamp, which compares equal across an edit and hides the
                    // change from IsDirty forever (#1836).
                    (acc, n) => acc.SetItem(n.Path, NodeTypeDefinition.SourceVersionOf(n))));

    /// <summary>
    /// Resolves the source set for a compile run. When the caller hands in a
    /// <paramref name="sourcesOverride"/> (the freshly-observed set from
    /// <c>HandleCreateRelease</c>'s uncached <c>IMeshService.Query</c>),
    /// use that — it's the authoritative post-update snapshot the trigger
    /// already evaluated. Otherwise fall back to the cached SyncedQuery.
    /// </summary>
    private IObservable<IEnumerable<MeshNode>> ResolveSources(
        NodeTypeDefinition? ntDef, string selfPath, IReadOnlyList<MeshNode>? sourcesOverride)
    {
        if (sourcesOverride is not null)
            return Observable.Return<IEnumerable<MeshNode>>(sourcesOverride);
        return GetSourceCollection(ntDef, selfPath);
    }

    /// <summary>
    /// 🚨 The ONE-SHOT source snapshot every compile stage reads — <see cref="ResolveSources"/>
    /// bounded to exactly one emission with a hard completion guarantee. The underlying
    /// <c>NodeSources.GetSources</c> synced query is a LIVE, never-completing collection; a bare
    /// <c>.Take(1)</c> on it means "wait for the first emission, potentially forever". When the
    /// query's Initial is genuinely lost (a subscription that raced a source-update burst — the
    /// memex-cloud 2026-07-20 Store/Plugin GitSync burst), that unbounded wait parked the compile
    /// at <c>CompilationStatus=Compiling</c> with NO error, NO terminal write and NO recovery
    /// path: the compile watcher needs a Pending TRANSITION, the release watcher gates on a
    /// SETTLED status, and the recovery kickoff is activation-one-shot — one hung snapshot wedged
    /// the NodeType absorbing-forever (every later trigger no-oped; only a recycle re-rolled the
    /// dice). Bounding the snapshot turns the lost Initial into a LOUD terminal failure: the
    /// timeout propagates through <c>RunCompile</c>'s Catch to a
    /// <c>CompilationStatus=Error</c> write naming the dead query, the park registry counts a
    /// bounded (non-deterministic) failure, and a fresh <c>RequestedReleaseAt</c> trigger — or
    /// the Compile button — retries cleanly. In every healthy state the snapshot is instant
    /// (Replay(1) cache) or one cold storage read, far below the bound.
    /// </summary>
    private IObservable<IEnumerable<MeshNode>> SnapshotSources(
        NodeTypeDefinition? ntDef, string selfPath, IReadOnlyList<MeshNode>? sourcesOverride)
        => SnapshotSourcesCore(ntDef, selfPath, sourcesOverride)
            .SelectMany(snapshot => snapshot.IsEstablished
                ? Observable.Return<IEnumerable<MeshNode>>(snapshot.Sources)
                // 🚨 NEVER COMPILE AGAINST AN UNESTABLISHED SOURCE SET. Handing Roslyn a set
                // that is short because a discovery query errored produces a completely
                // genuine-looking CS0246/CS0103 about the author's code — which the bake
                // readiness gate then cannot tell from a real image regression (issue #1218:
                // 14 of 56 sampled types on memex-cloud, all of which had compiled in ~1s the
                // same day on the same content). Fail with a DISTINGUISHABLE exception instead;
                // the terminal write-back records CompilationStatus.Unavailable, not Error.
                : Observable.Throw<IEnumerable<MeshNode>>(
                    new SourceDiscoveryUnavailableException(
                        $"Source discovery for '{selfPath}' could not be ESTABLISHED, so the compile "
                        + $"was not attempted: {snapshot.UnestablishedReason}. This is an availability "
                        + "failure, NOT a compile error — nothing is known to be wrong with the code, "
                        + "and compiling against the partial set would produce phantom diagnostics. "
                        + "Retry via the Compile button / a fresh RequestedReleaseAt once the mesh "
                        + "answers.")));

    /// <summary>
    /// The snapshot itself, WITH its establishment verdict. Split from
    /// <see cref="SnapshotSources"/> so the "empty because nothing matched" ⇄ "empty because the
    /// query never answered" distinction exists as data rather than being flattened into an
    /// indistinguishable empty list — see <see cref="SourceSnapshot"/>.
    /// </summary>
    private IObservable<SourceSnapshot> SnapshotSourcesCore(
        NodeTypeDefinition? ntDef, string selfPath, IReadOnlyList<MeshNode>? sourcesOverride)
    {
        var bound = _cacheOptions.SourceSnapshotTimeout;

        // A caller-supplied override is already an authoritative point-in-time snapshot.
        if (sourcesOverride is not null)
            return Observable.Return(SourceSnapshot.Established(sourcesOverride));

        // 🚨 DIRECT READ FIRST — measured, not assumed.
        //
        // A compile needs a POINT-IN-TIME snapshot of its sources. It was taking that snapshot from
        // the cached SYNCED query (NodeSources.GetSources → workspace.GetQuery), which exists to
        // deliver LIVE updates — a guarantee a compile does not need and, on a large mesh, pays
        // dearly for. Measured on memex 2026-07-27/28, same box, same data:
        //
        //     the four discovery queries via IMeshService.Query   ~0.25s   (counts 16 / 4 / 1)
        //     the same discovery through the cached synced query   45.20s
        //
        // 45.20s is one SyncStreamOptions.HeartbeatInterval to within 200ms — the synced
        // subscription misses its Initial and idles until the periodic heartbeat re-delivers.
        // Everything else was ruled out by experiment first: the compile path itself is 0ms
        // discovery on a fresh mesh, with ONE silo and with TWO (so not topology), and the queries
        // themselves answer in a quarter second on memex (so not scale, ~90 schemas, or data
        // volume). See issue #686 for the full elimination.
        //
        // So read directly and keep the synced query only as a FALLBACK for the case the direct
        // read finds nothing — never the other way round. An earlier attempt (#682) kept the cache
        // primary and raced a DELAYED probe against it; the probe never fired once in production,
        // which is precisely the failure this ordering removes: the fast path is no longer
        // conditional on losing a race.
        //
        // 🚨 MERGE, not Concat — both start NOW and the first ANSWER wins.
        //
        // Concat is sequential: the synced query is not even subscribed until the probe has
        // COMPLETED, so every compile paid the probe's full latency up front — and the probe cannot
        // answer faster than SourceProbeQuietWindow, because each leg has to watch its chunked
        // Initial go quiet before it may emit. That is a floor of ~1s per compile that the previous
        // code never paid (the probe was DelaySubscription'd and, on a healthy mesh, never ran).
        // Measured on Hosting.Monolith.Test, same box, CompileLeafStabilityTest:
        //
        //     before #690 (cached primary)          4s
        //     #690 as merged (probe, then cached)  52s     ← 13×; pushed the suite past CI's 6m cap
        //     this change (true race)               ~4s
        //
        // Merging keeps #690's win intact — a STALLED synced query no longer costs 45s, because the
        // probe answers in about a read plus the quiet window — while a HEALTHY synced query answers
        // immediately and the probe's latency never lands on the critical path. Neither source is
        // privileged, so the fast path does not depend on winning a race: whichever is healthy
        // answers, and the loser is simply dropped by FirstAsync.
        //
        // The probe's `.Where(count > 0)` is what makes this safe to merge: a probe that finds
        // nothing stays SILENT and completes, so it can never beat the cached query with an empty
        // set (compiling against no sources is worse than the stall this exists to dodge). That
        // filter is also the likeliest reason #682's probe "never fired" — not the delay alone.
        return NodeCompileShaping.RaceSourceSnapshot(
                DirectSourceProbe(ntDef, selfPath, TimeSpan.Zero),
                // 🚨 The cached leg's FAULT is unestablishment, not emptiness. A synced query
                // that errors used to propagate straight out of the snapshot as a raw exception
                // → terminal CompilationStatus.Error → a gating "regression" for a mesh read
                // that never happened. It now reports itself as unestablished, exactly like a
                // failed probe leg, and the race decides.
                Observable.Defer(() => ResolveSources(ntDef, selfPath, null).Take(1))
                    .Select(SourceSnapshot.Established)
                    .Catch((Exception ex) => Observable.Return(SourceSnapshot.Unavailable(
                        $"the synced source query '{NodeSources.CacheId(selfPath)}' faulted "
                        + $"({ex.GetType().Name}: {ex.Message})"))))
            .Timeout(bound)
            // A snapshot that never emits is the SAME condition as one whose queries errored:
            // the source set was not established. It reports as such (Unavailable, retryable)
            // rather than as a compile verdict — but it still SETTLES, so the NodeType can never
            // park at CompilationStatus=Compiling forever.
            .Catch<SourceSnapshot, TimeoutException>(_ => Observable.Return(
                SourceSnapshot.Unavailable(
                    $"no source set was produced within {bound.TotalSeconds:0}s — neither the direct "
                    + "mesh read nor the synced source query "
                    + $"('{NodeSources.CacheId(selfPath)}') answered")));
    }

    /// <summary>
    /// How long the cached synced source query gets before the uncached probe is even subscribed.
    /// Comfortably past a warm cache hit or one cold storage read, so the probe stays dormant in
    /// every healthy compile and only wakes for a genuinely stalled subscription.
    /// </summary>
    private static readonly TimeSpan SourceStallProbeDelay = TimeSpan.FromSeconds(3);

    /// <summary>How long the uncached probe waits for its chunked Initial to go quiet before
    /// treating the accumulated set as complete.</summary>
    private static readonly TimeSpan SourceProbeQuietWindow = TimeSpan.FromSeconds(1);

    /// <summary>One expanded source query's result — the nodes it matched, or the reason it did
    /// not answer. A failed leg is no longer interchangeable with an empty one (issue #1218).</summary>
    private readonly record struct SourceProbeLeg(
        string Query, IReadOnlyCollection<MeshNode> Nodes, string? Failure);

    /// <summary>
    /// The uncached escape hatch behind <see cref="SnapshotSources"/>: the SAME expanded source
    /// queries issued straight at <see cref="IMeshService"/>, bypassing the synced-query cache whose
    /// missed Initial is what idles until the 45s heartbeat. Subscription-delayed, so a healthy
    /// compile never issues it.
    ///
    /// <para>🚨 A FAILED leg no longer degrades to an empty one. It used to
    /// (<c>.Catch(_ =&gt; Return([]))</c>), which made the probe's answer silently PARTIAL whenever
    /// one of several queries errored — and the partial set then won the race and compiled, so a
    /// starved cross-partition read for a <c>shared=</c> source came out of Roslyn as
    /// <c>CS0246: The type or namespace name 'ScopeLibrary' could not be found</c>: a completely
    /// genuine-looking verdict about code that was fine (issue #1218, memex-cloud 2026-08-11). The
    /// leg now REPORTS its failure and the whole probe reports UNESTABLISHED, so the cached query
    /// still gets to answer — and if it cannot either, nothing is compiled.</para>
    /// </summary>
    private IObservable<SourceSnapshot> DirectSourceProbe(
        NodeTypeDefinition? ntDef, string selfPath, TimeSpan? subscriptionDelay = null)
    {
        var queries = CodeQueryResolver
            .ExpandAll(ntDef?.Sources, CodeQueryResolver.DefaultSources, selfPath)
            .Concat(CodeQueryResolver.ExpandAll(ntDef?.Tests, CodeQueryResolver.DefaultTests, selfPath))
            .ToArray();
        if (queries.Length == 0)
            return Observable.Empty<SourceSnapshot>();

        var mesh = hub.ServiceProvider.GetService<IMeshService>();
        if (mesh is null)
            return Observable.Empty<SourceSnapshot>();

        // 🚨 Run the probe under System, exactly as the CACHED leg already does
        // (GetSourceCollection). The two legs of RaceSourceSnapshot must observe the SAME source
        // set — a race whose competitors read under different identities is not a race, it is a
        // coin-flip between two different answers. This leg was the one still reading under
        // whatever ambient identity happened to survive: MeshService.Query stamps
        // request.UserId from AccessService at CALL time, and the call happens inside the Defer
        // below, i.e. at SUBSCRIBE — which DelaySubscription has already moved onto a ThreadPool
        // tick where the AsyncLocal is gone. The query then runs as Anonymous, and RLS answers
        // with the subset Anonymous may see. An empty answer is harmless (filtered by the
        // .Where below, so the cached leg decides), but a PARTIAL one — a cross-partition
        // `shared=` source the user can read and Anonymous cannot — is ESTABLISHED and NON-EMPTY,
        // so it WINS the race and Roslyn is handed a short set: the phantom
        // `CS0246: type or namespace 'X' could not be found` about code that is fine (issue
        // #1218). Reading as System makes the probe see what the cached leg sees.
        var accessService = hub.ServiceProvider.GetService<AccessService>();

        var probe = Observable
            .Defer(() => Observable
                .CombineLatest(queries.Select(q =>
                    // ACCUMULATE, never Take(1) on the raw stream: a query's Initial arrives in
                    // CHUNKS, so the first emission can be a partial set — and a partial source set
                    // compiles WRONG (missing files → phantom errors), which is worse than slow.
                    // Fold every change, then settle on a quiet window.
                    //
                    // 🚨 Defer per LEG — this lambda runs inside CombineLatest's SUBSCRIPTION, and
                    // the probe's subscription is SCHEDULED (DelaySubscription → a ThreadPool tick).
                    // When the mesh is disposed between scheduling and firing, mesh.Query →
                    // CaptureContext → GetService throws SYNCHRONOUSLY on the disposed Autofac
                    // scope — and Rx routes a synchronous throw during a Producer's subscribe to
                    // the SUBSCRIBE CALLER, not to OnError, so neither this leg's .Catch nor the
                    // probe's outer .Catch ever sees it. The caller is the scheduler → unhandled on
                    // the ThreadPool → xUnit v3's AppDomain handler reports "[FATAL ERROR]
                    // ObjectDisposedException", waits for the run to finish, and Environment.Exit(2)s
                    // — the CI "exit=2 with an all-green trx" shard failures (first seen when #690
                    // made this probe run on every compile). Defer's FACTORY exceptions, by
                    // contrast, ARE forwarded to OnError — so the throw lands in this leg's .Catch
                    // below and the leg reports itself FAILED, exactly like any other faulted leg.
                    // 🚨 .AsSystem() — source-set discovery is framework infrastructure, not a
                    // user-scoped read (same reasoning as the cached SnapshotSources path, which
                    // wraps its GetQuery in ImpersonateAsSystem). The declaration has to ride on the
                    // REQUEST here rather than on an ambient scope, because the note above says this
                    // lambda runs inside a SCHEDULED subscription (DelaySubscription → a ThreadPool
                    // tick): an ambient impersonation scope established by the caller is long gone by
                    // then, so the read would resolve as Anonymous and return a silently PARTIAL
                    // source set — which is precisely the "compiles WRONG" failure this method warns
                    // about two comments up, and how a starved read surfaced as a completely
                    // genuine-looking CS0246 (#1218). See Doc/Architecture/QueryIdentity.
                    Observable.Defer(() => mesh.Query<MeshNode>(MeshQueryRequest.FromQuery(q).AsSystem()))
                        .Scan(ImmutableDictionary<string, MeshNode>.Empty, NodeCompileShaping.ApplyQueryChange)
                        .Throttle(SourceProbeQuietWindow)
                        .Take(1)
                        .Select(map => new SourceProbeLeg(
                            q, (IReadOnlyCollection<MeshNode>)map.Values.ToList(), null))
                        // 🚨 NAME the failure instead of erasing it. `.Catch(_ => empty)` made a
                        // dead query indistinguishable from an empty one, which is the whole
                        // defect: the surviving legs then produced a PARTIAL set that compiled.
                        .Catch((Exception ex) => Observable.Return(new SourceProbeLeg(
                            q, [], $"{ex.GetType().Name}: {ex.Message}")))))
                .Select(legs =>
                {
                    var failed = legs.Where(l => l.Failure is not null).ToList();
                    var merged = legs
                        .SelectMany(l => l.Nodes)
                        .Where(n => !string.IsNullOrEmpty(n.Path))
                        .GroupBy(n => n.Path)
                        .Select(g => g.First())
                        .ToList();

                    if (failed.Count > 0)
                    {
                        var reason = string.Join("; ", failed.Select(l => $"query '{l.Query}' → {l.Failure}"));
                        // Warning, not Debug: this is now a load-bearing diagnosis. It is the
                        // difference between "this NodeType is broken" and "this pod could not
                        // read the mesh", and an operator staring at a stalled rollout needs it.
                        logger.LogWarning(
                            "Source discovery for '{SelfPath}': {Failed} of {Total} source quer(ies) "
                            + "did NOT answer, so the source set is UNESTABLISHED and the compile will "
                            + "not run against the {Partial} node(s) the surviving queries returned. {Reason}",
                            selfPath, failed.Count, legs.Count, merged.Count, reason);
                        return SourceSnapshot.Unavailable(reason, merged);
                    }

                    logger.LogDebug(
                        "Source discovery for '{SelfPath}': direct mesh read returned {Count} source node(s).",
                        selfPath, merged.Count);
                    return SourceSnapshot.Established(merged);
                })
                // 🚨 NEVER win the race with an ESTABLISHED EMPTY set. A probe that legitimately
                // matched nothing must stay silent and let the cached query (and the outer
                // Timeout) decide — compiling against no sources when the cache holds them is
                // strictly worse than the stall this exists to dodge. An UNESTABLISHED report is
                // NOT filtered: RaceSourceSnapshot holds it back until both legs have answered,
                // so it never pre-empts a healthy set but is never lost either.
                .Where(s => !s.IsEstablished || s.Sources.Count > 0));

        return accessService
            // The scope INSIDE DelaySubscription, never around it: it is then entered on the
            // delayed subscribe — the very tick that calls mesh.Query and reads the ambient
            // identity — instead of on the composing thread whose AsyncLocal the hop discards.
            // Same ordering as GetSourceCollection.
            //
            // 🚨 RunAsSystem, never `Observable.Using(access.ImpersonateAsSystem, …)` (#1444/#1790).
            // Both enter the scope at Subscribe, which is the property this ordering needs; only
            // `Using` also defers the DISPOSE to whichever thread the probe terminates on, leaving
            // the subscriber latched as System. RunAsSystem keeps the entry and closes the exit.
            .RunAsSystem(
                () => probe)
            // A fault ESCAPING the per-leg catches (the whole CombineLatest, not one query) is
            // still unestablishment — the probe found out nothing.
            .Catch((Exception ex) => Observable.Return(SourceSnapshot.Unavailable(
                $"the direct source probe for '{selfPath}' faulted ({ex.GetType().Name}: {ex.Message})")))
            .DelaySubscription(subscriptionDelay ?? SourceStallProbeDelay);
    }

    /// <summary>
    /// IObservable end-to-end. Source discovery rides the cached SyncedQuery
    /// registered for this NodeType; @@ include resolution composes via
    /// <see cref="ResolveCodeIncludes"/>. The only Task→Observable bridge is
    /// the Roslyn <see cref="CompileAsync"/> call. No <c>Observable.FromAsync</c>,
    /// no <c>await</c> on hub round-trips — both are the canonical deadlock
    /// patterns documented in <c>Doc/Architecture/AsynchronousCalls.md</c>.
    /// </summary>
    private IObservable<(string? Path, string? InputDigest, ActivityLog Log)> CompileCore(
        MeshNode node, NodeTypeDefinition? ntDef, string selfPath, ActivityLog log,
        IReadOnlyList<MeshNode>? sourcesOverride = null)
    {
        var nodeName = cacheService.SanitizeNodeName(node.Path);
        var executedQueries = CodeQueryResolver
            .ExpandAll(ntDef?.Sources, CodeQueryResolver.DefaultSources, selfPath)
            .Concat(CodeQueryResolver.ExpandAll(ntDef?.Tests, CodeQueryResolver.DefaultTests, selfPath))
            .ToList();
        var matchedCodePaths = new List<string>();

        // Source discovery: on the compile path this ALWAYS receives the snapshot
        // GetAssemblyLocationWithLog already took (one snapshot per compile — see its
        // remarks), so SnapshotSources short-circuits to it rather than racing a second
        // discovery. The override is also how HandleCreateRelease injects its uncached
        // IMeshService.Query snapshot — authoritative for the just-modified Code node.
        // Without an override, the .Take(1) on the cached observable could pick up the
        // pre-update Initial emission and the V2 compile would silently consume V1 source
        // — that was the V1↔V2 mismatch root cause in CodeEditRecompileTest.
        // ⏱️ PHASE TIMING. The 2026-07-27 outage was diagnosed — three times, wrongly — by inferring
        // WHERE a compile spends its time from the gaps between existing log lines. The activity log
        // showed 45.20s between "Invoking compiler…" and the source queries resolving, against 2.83s
        // of Roslyn, but nothing said WHY, so every fix attempted so far has been a guess at the
        // mechanism. This records the actual split into the compile's own ActivityLog, where it is
        // readable per compile (`get @{Type}/_Activity/compile-…`) instead of reconstructable only
        // by subtracting microsecond timestamps.
        var discoveryClock = System.Diagnostics.Stopwatch.StartNew();
        var discoverCodeFiles = SnapshotSources(ntDef, selfPath, sourcesOverride)
            .Select(matches =>
            {
                // The dedup + executable filter + join order live in the toolchain
                // (NodeCompileShaping, #1707) — they shape the compile input.
                var (acc, matched) = NodeCompileShaping.CollectCompileSources(
                    matches, node.Path, logger);
                matchedCodePaths.AddRange(matched);
                logger.LogDebug(
                    "Source discovery for {NodePath}: matched {Count} Code nodes from {QueryCount} queries",
                    node.Path, matchedCodePaths.Count, executedQueries.Count);
                return acc;
            });

        return discoverCodeFiles
            // Stage: the cell-surface single-home gate (issue #1649 part 3) — refuses to
            // recompile another NodeType's `cellSurface: true` Source into THIS assembly.
            // Runs on the discovered snapshot (matchedCodePaths is populated by the Select
            // above), before any include resolution or Roslyn work is spent. The thrown
            // CompilationException propagates to RunCompile's terminal write-back
            // (CompilationStatus=Error, CompilationError = the naming message).
            .SelectMany(codeFiles =>
                ValidateCellSurfaceSingleHome(node.Path, selfPath, matchedCodePaths)
                    .Select(_ => codeFiles))
            .SelectMany(codeFiles =>
            {
                // Stage: resolve @@ include references reactively. Each include lookup
                // composes via ResolveCodeIncludes (already an IObservable<string>). No await.
                if (codeFiles.Count == 0)
                    return Observable.Return(codeFiles);

                IObservable<List<CodeConfiguration>> includeChain =
                    Observable.Return(new List<CodeConfiguration>(codeFiles.Count));
                foreach (var codeFile in codeFiles)
                {
                    var cf = codeFile;
                    includeChain = includeChain.SelectMany(acc =>
                        ResolveCodeIncludes(cf.Code!, new HashSet<string>(), node.Path)
                            .Select(resolvedCode =>
                            {
                                acc.Add(resolvedCode != cf.Code ? cf with { Code = resolvedCode } : cf);
                                return acc;
                            }));
                }
                return includeChain;
            })
            .SelectMany(codeFiles =>
            {
                // Final stage: combine + compile. The Roslyn `Compile` call itself is
                // the only Task→Observable bridge in this whole method. The join order
                // lives in the toolchain (it shapes the emitted bytes).
                var codeFile = NodeCompileShaping.CombineSources(codeFiles);
                var configuration = ntDef?.Configuration;
                var contentCollections = ntDef?.ContentCollections;

                // Snapshot the discovery into the activity log: every executed query +
                // every matched Code path. Lets the response carry "compile saw N
                // source files from queries [Q1, Q2…]" without re-running the pipeline.
                var discoveryLog = AppendInfo(log,
                    $"⏱ Source discovery took {discoveryClock.ElapsedMilliseconds}ms "
                    + $"({(sourcesOverride is not null ? "reused source snapshot" : "synced query")}) — "
                    + "everything below this line is Roslyn.");
                foreach (var q in executedQueries)
                    discoveryLog = AppendInfo(discoveryLog, $"Source query: {q}");
                if (matchedCodePaths.Count == 0)
                {
                    discoveryLog = AppendWarning(discoveryLog,
                        $"Source discovery for '{node.Path}' matched 0 Code nodes — " +
                        "check that the Source Code nodes exist and the NodeType's " +
                        "`Sources` list points at them.");
                }
                else
                {
                    discoveryLog = AppendInfo(discoveryLog,
                        $"Source discovery matched {matchedCodePaths.Count} Code node(s): " +
                        string.Join(", ", matchedCodePaths));
                }

                // 🚨 Compile on the ThreadPool via Task.Run, never inline and never the IoPool.
                // For in-memory compilation CompileAsyncCore has NO await before the synchronous
                // Roslyn Emit (CompileToMemory), so CompileAsync() runs the ENTIRE compile
                // synchronously — the old `CompileAsync(...).ToObservable()` ran it on whatever
                // thread subscribed (the activity hub's action block), wedging the mesh; and
                // `_ioPool.Run` re-entered/parked on the Compile pool's SemaphoreSlim gate (idle
                // 40s wait). Task.Run (TaskScheduler.Default) has no gate and never captures the
                // calling scheduler — see OnThreadPool.
                //
                // 🚨 …and BOUND it. Everything inside CompileAsync — the NuGet restore for a
                // `#r "nuget:…"` (network IO with no timeout of its own), source generators,
                // Roslyn Emit, the disk write — had no wall clock around it, so a hung leaf
                // parked the type at Compiling for the life of the activation with nothing able
                // to recover it (the status lock correctly absorbs later triggers). The token
                // BoundLeg hands in is tripped when the bound fires, so NuGet/Roslyn actually
                // stop; the TimeoutException propagates (it is not a CompilationException, so the
                // Catch below leaves it alone) to RunCompile's terminal Error write.
                return BoundLeg(
                        ct => OnThreadPool(() =>
                            CompileAsync(codeFile, configuration, contentCollections, node, ct)),
                        _cacheOptions.RoslynCompileTimeout, "roslyn-compile", node.Path)
                    .Select(emit =>
                    {
                        var actualPath = emit.Path;
                        ActivityLog finalLog;
                        string? finalPath;
                        // 🚨 DIAGNOSTICS GO TO THE ACTIVITY. A compile's warnings belong on the
                        // record of that compile — the activity is what a reader opens, what the
                        // Tests/Overview areas render and what the runner streams, so a warning
                        // that only reached a server log reached nobody. Appended BEFORE the
                        // outcome line so the log reads in the order it happened; Warning severity
                        // never flips ActivityLog.Finish's terminal status, so surfacing them
                        // cannot turn a green compile red on its own.
                        discoveryLog = emit.Warnings.Aggregate(discoveryLog, AppendWarning);
                        if (cacheService.IsDiskCacheEnabled)
                        {
                            if (actualPath != null && File.Exists(actualPath))
                            {
                                logger.LogDebug(
                                    "Compiled assembly for node {NodePath} at {DllPath}",
                                    node.Path, actualPath);
                                finalPath = actualPath;
                                finalLog = AppendInfo(discoveryLog,
                                    $"Compiled assembly written to {actualPath}.");
                            }
                            else
                            {
                                logger.LogWarning(
                                    "Assembly compilation succeeded but DLL not found at {DllPath}", actualPath);
                                finalPath = null;
                                finalLog = AppendError(discoveryLog,
                                    $"Compilation succeeded but DLL not found at {actualPath}.");
                            }
                        }
                        else
                        {
                            logger.LogDebug("Compiled assembly for node {NodePath} (in-memory)", node.Path);
                            finalPath = actualPath;
                            finalLog = AppendInfo(discoveryLog,
                                $"Compiled assembly loaded in-memory ({actualPath}).");
                        }
                        return (finalPath, emit.InputDigest,
                            finalLog.FinishByOutcome((int)hub.Version));
                    })
                    // 🚨 THE SINGLE REPORTER of a compile failure. Every CompilationException —
                    // Roslyn diagnostics from the disk emit (EmitCompilationToDirectory) or the
                    // in-memory emit (CompileToMemory), and the "could not be persisted" loss from
                    // EmitToDiskWithRetry — funnels here, and ONLY here is the exception logged.
                    // This is the only site that has all three of: the exception + its stack, the
                    // node path, and the source-discovery report. The emit sites used to log the
                    // same diagnostics too, which double-counted every failure in prod and put a
                    // context-free record first — do not re-add a log next to a `throw new
                    // CompilationException`.
                    .Catch<(string?, string?, ActivityLog), CompilationException>(ex =>
                    {
                        var diag = CompileDiagnostics.BuildSourceDiscoveryReport(executedQueries, matchedCodePaths);
                        // 🚨 ORDER BY ACTIONABILITY — issue #1840. This report leads with the
                        // COMPILER'S VERDICT and carries a BOUNDED source-discovery sample after
                        // it. The old template put the (unbounded) discovery report first and left
                        // the diagnostics to the exception the console formatter prints afterwards
                        // — correct in a full pod log, useless in the incident ticket, because the
                        // red-log watcher keeps only LogWatcherOptions.MaxSampleLength (2000)
                        // characters of a burst. With 26 matched Code nodes the listing alone is
                        // ~2.4 kB, so the capture ended "…[truncated]" mid-listing and the CS ids
                        // never reached the ticket. Nothing was ever discarded — it was ordered
                        // out of the budget. See CompileDiagnostics.FormatCompileFailureReport.
                        //
                        // The exception is still attached: the full diagnostic set stays available
                        // in a complete pod log, and the parser reads the fault's own message off
                        // the exception line (which is what the incident fingerprint keys on, so
                        // this reordering cannot fork existing incidents).
                        logger.LogError(ex, "{CompileFailure}",
                            CompileDiagnostics.FormatCompileFailureReport(
                                node.Path, ex.Message, executedQueries, matchedCodePaths));
                        // The ActivityLog is NOT size-capped, so it keeps the complete diagnostics
                        // AND the complete matched-node list — this is where the bounded sample
                        // above tells the reader to look.
                        var failedLog = AppendError(discoveryLog,
                                $"Compilation failed: {ex.Message}\n--- Source discovery ---\n{diag}")
                            .Finish((int)hub.Version, ActivityStatus.Failed);
                        return Observable.Return<(string?, string?, ActivityLog)>(
                            (null, null, failedLog));
                    });
            });
    }

    /// <summary>
    /// The cell-surface single-home gate (issue #1649 part 3): a NodeType whose resolved source
    /// set reaches into ANOTHER NodeType's <c>Source/</c>/<c>Test/</c> subtree (a <c>shared=</c>
    /// consumption) must not compile when that owner declares
    /// <see cref="NodeTypeDefinition.CellSurface"/> — <c>shared=</c> recompiles the owner's
    /// public types into this assembly, and with the owner's assembly on the kernel's cell
    /// surface every bare-name cell call would be ambiguous between the two copies
    /// (<c>CS0433</c>, observed live in Education#171). Failing the CONSUMER's compile with a
    /// message naming the owner prevents the duplicate copy from ever existing.
    ///
    /// <para>Zero-cost for the common case: a source set that never leaves <paramref name="selfPath"/>
    /// derives no foreign owners and performs no reads. Owner reads are bounded
    /// (<see cref="ReadTimeoutBehavior.EmitNull"/>) and degrade OPEN — a stalled/absent owner
    /// read cannot park an innocent consumer on a transient mesh stall; the persistent
    /// misconfiguration still fails deterministically on the next healthy compile.</para>
    /// </summary>
    private IObservable<Unit> ValidateCellSurfaceSingleHome(
        string nodePath, string selfPath, IReadOnlyList<string> sourcePaths)
    {
        var owners = NodeTypeDependencyGraph.ForeignSourceOwners(selfPath, sourcePaths);
        if (owners.IsEmpty)
            return Observable.Return(Unit.Default);

        return owners
            .Select(owner => ReadCompileSourceNode(owner, ReadTimeoutBehavior.EmitNull)
                .Take(1)
                .Select(ownerNode =>
                {
                    // EmitNull collapses "absent" and "stalled" into one null — deliberately, so
                    // a transient mesh blip can never turn into a hard compile failure here (the
                    // gate's fail-open direction, documented above). But the degradation must be
                    // LOUD: a null owner read skips exactly this owner's single-home validation,
                    // and if that owner really is cell-surface the CS0433 this gate exists to
                    // prevent surfaces later in cells — this line is what ties that back here.
                    if (ownerNode is null)
                    {
                        logger.LogWarning(
                            "Cell-surface single-home gate for '{SelfPath}': the owning NodeType "
                            + "'{Owner}' could not be read (absent or stalled) — skipping ITS "
                            + "single-home validation for this compile; if '{Owner}' declares "
                            + "cellSurface, re-compile once it is reachable",
                            selfPath, owner, owner);
                        return (Owner: owner, IsCellSurface: false);
                    }
                    return (Owner: owner,
                        IsCellSurface: ownerNode.ContentAs<NodeTypeDefinition>(JsonOptions)?.CellSurface == true);
                }))
            .Merge()
            .Where(t => t.IsCellSurface)
            .ToList()
            .SelectMany(violations =>
            {
                if (violations.Count == 0)
                    return Observable.Return(Unit.Default);
                var names = string.Join(", ", violations
                    .Select(v => $"'{v.Owner}'")
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
                return Observable.Throw<Unit>(new CompilationException(nodePath,
                    $"Cell-surface single-home violation: '{selfPath}' consumes Source/Test code owned by "
                    + $"cell-surface NodeType {names}. A `cellSurface: true` NodeType's source is "
                    + "single-home: a `shared=` consumer recompiles the same public types into a second "
                    + "assembly, making every bare-name kernel-cell call ambiguous (CS0433). Remove the "
                    + $"shared= source entry on '{selfPath}', or clear cellSurface on the owning type."));
            });
    }

    /// <summary>
    /// On a FAILED compile, re-derive the diagnostics in their structured, per-source-file
    /// form by assembling ONE LSP-style compilation (skeleton tree + one tree per src/test
    /// Code node, each carrying the MeshNode path as its <c>FilePath</c>) — exactly the model
    /// <see cref="SpeculativeCompilation"/> / <see cref="MeshNodeLanguageService"/> use, so a
    /// diagnostic's <see cref="Lsp.SourceLocation.SourcePath"/> is the Code node path. This is
    /// what lets the GUI mark each error at its exact line/column in a Monaco editor and link to
    /// the source. Runs only on failure (off the hub via <see cref="OnThreadPool{T}(Func{T})"/>),
    /// so the working success emit path is untouched. Reuses <see cref="GetCompilationInputsAsync"/>
    /// (already source-discovery-, @@-include- and NuGet-resolved).
    /// </summary>
    private IObservable<IReadOnlyList<Lsp.DiagnosticInfo>> BuildFailureDiagnostics(
        MeshNode node, IReadOnlyList<MeshNode>? sourcesOverride)
        => GetCompilationInputsAsync(node, sourcesOverride)
            .Take(1)
            .SelectMany(inputs => inputs is null
                ? Observable.Return<IReadOnlyList<Lsp.DiagnosticInfo>>(Array.Empty<Lsp.DiagnosticInfo>())
                : OnThreadPool(() => CompileDiagnostics.DiagnoseInputs(inputs)))
            // 🚨 BOUNDED with the same clock as the emit leg — this runs on the FAILURE path,
            // where a hang is worst: the compile has already failed and this is what stands
            // between that failure and its terminal Error write. Unbounded, a stalled
            // re-compilation for diagnostics turned a plain Roslyn error into a Compiling wedge.
            // Expiry lands in the best-effort Catch below (empty diagnostics), so the real
            // failure still reports — with its flattened summary intact.
            .Timeout(_cacheOptions.RoslynCompileTimeout)
            // Diagnostics are best-effort GUI sugar — a failure here must never break the
            // compile result; the flattened FormatCompileFailure summary still surfaces.
            .Catch<IReadOnlyList<Lsp.DiagnosticInfo>, Exception>(ex =>
            {
                logger.LogDebug(ex, "Structured failure-diagnostics capture failed for {NodePath} (best-effort)", node.Path);
                return Observable.Return<IReadOnlyList<Lsp.DiagnosticInfo>>(Array.Empty<Lsp.DiagnosticInfo>());
            });

    /// <inheritdoc />
    public IObservable<NodeCompilationResult?> CompileAndGetConfigurations(
        MeshNode node,
        IReadOnlyList<MeshNode>? sourcesOverride = null)
        => GetAssemblyLocationWithLog(node, sourcesOverride).SelectMany(attempt =>
        {
            var (assemblyLocation, _, log, sources) = attempt;
            if (string.IsNullOrEmpty(assemblyLocation))
                // Failed compile: capture the per-source-file Roslyn diagnostics (one
                // LSP-style per-file-tree compilation of all this NodeType's src+test) so
                // the Settings → Progress error page can mark each error at its exact
                // position in a Monaco editor and link to the Code node. Failure-only — the
                // working success emit is untouched. The flattened summary still lives on
                // the ActivityLog (FormatCompileFailure).
                //
                // Diagnosed against the SNAPSHOT THAT FAILED, not a fresh discovery: a
                // re-read could return a different set and then report diagnostics for code
                // the failing compile never saw (and pay the discovery a second time).
                return BuildFailureDiagnostics(node, sources)
                    .Select(diags => (NodeCompilationResult?)new NodeCompilationResult(
                        null, [], log, Diagnostics: diags));

            // The per-source version snapshot folds the SAME set the compile consumed
            // (attempt.Sources) — see GetAssemblyLocationWithLog: one snapshot per compile,
            // so CompiledSources records what was compiled instead of a second, independently
            // raced observation. Compose via SelectMany so the observable chain stays
            // reactive (no Task bridges, no .Result deadlocks).
            var ntDef = node.ContentAs<NodeTypeDefinition>(JsonOptions);
            var selfPath = ntDef != null ? node.Path : node.NodeType ?? node.Path;
            return DiscoverSourceVersionSnapshot(ntDef, selfPath ?? "", sources)
                // 🚨 Assembly load + GetTypes() + MeshNodeProviderAttribute reflection +
                // config instantiation is heavy, synchronous, blocking work. Run it on the
                // ThreadPool (Task.Run), never inline (would wedge whatever hub action block
                // emitted upstream) and never the IoPool's gated blocking factory.
                //
                // 🚨 BOUNDED: this leg runs USER code (an attribute constructor, a type
                // initializer), so "it returns" is not a guarantee the framework can make —
                // one blocking static ctor used to pin the type at Compiling forever. Nothing
                // here is cancellable (a running type initializer cannot be interrupted), so
                // the bound settles the state machine with a terminal Error naming the leg and
                // the abandoned load thread is the honest cost.
                .SelectMany(snapshot => BoundLeg(
                    _ => OnThreadPool(() =>
                        CompileResultFromAssembly(
                            node, assemblyLocation, log, snapshot, attempt.InputDigest)),
                    _cacheOptions.AssemblyLoadTimeout, "assembly-load", node.Path))
                // Re-Finish the log after CompileResultFromAssembly. CompileCore already
                // finished it, but CompileResultFromAssembly's downstream steps
                // (assembly load, MeshNodeProviderAttribute reflection) can append fresh
                // Error messages — those need to flip Status to Failed.
                // FinishByOutcome re-reads the log: an Error appended after the first
                // finish flips Status to Failed; warnings stay in the transcript without
                // demoting a green compile (the pin/fold split-brain fix — see
                // ActivityLog.FinishByOutcome).
                .Select(result => result is null
                    ? result
                    : result with { Log = result.Log?.FinishByOutcome((int)hub.Version) })
                .SelectMany(result => UploadToStoreIfNeeded(result, node));
        });

    /// <summary>
    /// After a successful Roslyn compile, push the bytes through <see cref="IAssemblyStore"/>
    /// so cross-silo readers can hydrate the same compile output without recompiling.
    /// Stamps the returned <see cref="AssemblyStoreLocation"/> onto a new
    /// <see cref="NodeCompilationResult"/>; the watcher then denormalises Collection +
    /// ContentPath onto <c>NodeTypeDefinition.LatestAssembly{Collection,Path}</c>.
    /// <para>
    /// Upload failures don't fail the compile — the local assembly is still usable in
    /// the producing silo, only cross-silo activation needs the store. We log and pass
    /// the un-stamped result through so the compile completes and a fresh Release
    /// MeshNode still gets written.
    /// </para>
    /// <para>
    /// Memory-mode compiles (<c>memory://...</c>) skip the upload — there are no bytes
    /// on disk to read, and the in-memory ALC the cache service holds is per-process by
    /// design. Memory mode is reserved for unit-test fast paths; production silos run
    /// disk-cache mode where this upload always happens.
    /// </para>
    /// </summary>
    private IObservable<NodeCompilationResult?> UploadToStoreIfNeeded(NodeCompilationResult? result, MeshNode node)
    {
        if (result is null
            || string.IsNullOrEmpty(result.AssemblyLocation)
            || result.AssemblyLocation.StartsWith("memory://", StringComparison.Ordinal))
            return Observable.Return(result);
        if (result.NodeTypeConfigurations.Count == 0)
            return Observable.Return(result);
        if (_assemblyStore is NullAssemblyStore)
        {
            // A null store is a misconfiguration in any non-trivial host. Log once
            // per compile so the operator notices — silent skip strands cross-silo
            // activation with Status=Ok + null assembly fields.
            logger.LogWarning(
                "Compile for {NodePath} produced an assembly but IAssemblyStore is NullAssemblyStore — " +
                "downstream cross-silo activation will see Status=Ok with null LatestAssembly fields. " +
                "Register a real IAssemblyStore (AddBlobAssemblyStore / AddFileSystemAssemblyStore).",
                node.Path);
            return Observable.Return(result);
        }

        byte[] dll;
        byte[]? pdb = null;
        try
        {
            dll = File.ReadAllBytes(result.AssemblyLocation);
            var pdbPath = Path.ChangeExtension(result.AssemblyLocation, ".pdb");
            if (File.Exists(pdbPath))
                pdb = File.ReadAllBytes(pdbPath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "AssemblyStore upload skipped for {NodePath}: could not read bytes from {Path}",
                node.Path, result.AssemblyLocation);
            return Observable.Return(result);
        }

        var version = node.Version > 0 ? node.Version : 1;
        var uploadBound = _cacheOptions.AssemblyStoreUploadTimeout;
        return _assemblyStore.PutWithLocation(node.Path, version, dll, pdb)
            .Select(loc => (NodeCompilationResult?)(result with
            {
                Collection = string.IsNullOrEmpty(loc.Collection) ? null : loc.Collection,
                ContentPath = string.IsNullOrEmpty(loc.ContentPath) ? null : loc.ContentPath,
                Version = version
            }))
            // 🚨 BOUNDED — the last unbounded leg of the compile pipeline. A wedged blob
            // endpoint (no timeout of its own) left the compile hanging AFTER a perfectly good
            // Roslyn emit, so the NodeType sat at Compiling forever with the assembly already
            // on disk. Expiry is deliberately NOT terminal-Error: an upload failure has never
            // failed a compile (see the summary above), and a bound must not silently change
            // that contract — the TimeoutException falls into the same Catch as any other
            // upload fault, which logs, records the leg on the ActivityLog and passes the
            // un-stamped result through. The compile SETTLES (Ok, with a loud warning about
            // cross-silo activation) instead of hanging.
            .Timeout(uploadBound)
            .Catch<NodeCompilationResult?, Exception>(ex =>
            {
                logger.LogWarning(ex,
                    "AssemblyStore upload failed for {NodePath}@v{Version}; compile still succeeded locally",
                    node.Path, version);
                var reason = ex is TimeoutException
                    ? $"did not complete within {uploadBound.TotalSeconds:0}s"
                    : $"failed — {ex.Message}";
                // Surface it where compile diagnosis actually looks: the activity log the
                // terminal write copies from. A silent Ok whose LatestAssembly* fields are null
                // is how cross-silo activation breaks with no trace.
                var log = result.Log;
                if (log is null)
                    return Observable.Return(result);
                return Observable.Return<NodeCompilationResult?>(result with
                {
                    Log = AppendWarning(log,
                        $"Compile leg 'assembly-store-upload' for '{node.Path}'@v{version} {reason}. "
                        + "The compile SUCCEEDED locally and settles Ok, but the assembly was not "
                        + "published to the store — cross-silo activation of this NodeType will not "
                        + "find it until a later compile uploads successfully.")
                });
            });
    }

    /// <inheritdoc />
    public IObservable<NodeCompilationResult?> GetConfigurationsFromExistingAssembly(string localPath, string nodeTypePath)
    {
        if (string.IsNullOrEmpty(localPath))
            return Observable.Return((NodeCompilationResult?)null);

        // Synthesise a minimal MeshNode for CompileResultFromAssembly's ALC bookkeeping.
        // The Path is what determines the cache key — the rest of the node is irrelevant
        // for this path because we're not running Roslyn.
        var stubNode = new MeshNode(MeshNode.FromPath(nodeTypePath).Id, MeshNode.FromPath(nodeTypePath).Namespace);
        var log = new ActivityLog(ActivityCategory.Compilation) { HubPath = nodeTypePath };
        var result = CompileResultFromAssembly(stubNode, localPath, log,
            ImmutableDictionary<string, long>.Empty);
        // CompileResultFromAssembly hands back a log that's still Running (the
        // constructor default). Finish it so callers (response.Log readers,
        // activity-MeshNode renderers) see a terminal Succeeded / Failed.
        // ActivityLog.Finish reads its own Messages: any Error appended along
        // the way (assembly load fail, reflection fail) flips status to Failed
        // automatically — we pass Succeeded as the floor, the message-level
        // dominates a higher severity. CompileCore on the fresh-compile path
        // already Finishes; this branch covers the assembly-hydration shortcut.
        if (result is not null)
        {
            result = result with
            {
                Log = (result.Log ?? log).FinishByOutcome((int)hub.Version)
            };
        }
        return Observable.Return(result);
    }

    /// <summary>
    /// Reactive: emits the assembled <see cref="CompilationInputs"/> for the NodeType — every
    /// source as its own paired <c>(Path, Code)</c> entry, the @@-include resolution applied,
    /// NuGet refs resolved, the skeleton (assembly attribute + generated provider class)
    /// generated separately. Callers build their own <c>CSharpCompilation</c> or
    /// <c>AdhocWorkspace</c> from these inputs — per-file syntax trees mean positions in
    /// language-service queries map back to what the user is editing in Monaco.
    /// <para>
    /// Distinct from the emit path (<see cref="CompileCore"/>) which concatenates all sources
    /// into one syntax tree to produce an assembly. Used by <c>MeshNodeLanguageService</c>
    /// (hover / completion / diagnostics) and <c>SpeculativeCompilation</c> (the /code pre-flight
    /// check). Does NOT register NuGet probing directories — that's emit-path bookkeeping.
    /// </para>
    /// </summary>
    public IObservable<CompilationInputs?> GetCompilationInputsAsync(
        MeshNode node,
        IReadOnlyList<MeshNode>? sourcesOverride = null)
    {
        if (string.IsNullOrEmpty(node.NodeType))
            return Observable.Return<CompilationInputs?>(null);

        NodeTypeDefinition? selfDef = node.ContentAs<NodeTypeDefinition>(JsonOptions);
        IObservable<NodeTypeDefinition?> resolveDef = selfDef != null
            ? Observable.Return<NodeTypeDefinition?>(selfDef)
            // EmitNull — same deliberate hold as GetCompilationInputs above (activation path).
            : ReadCompileSourceNode(node.NodeType, ReadTimeoutBehavior.EmitNull)
                .Select(typeNode => typeNode.ContentAs<NodeTypeDefinition>(JsonOptions));
        string selfPath = selfDef != null ? node.Path : node.NodeType;

        return resolveDef.SelectMany(ntDef =>
            SnapshotSources(ntDef, selfPath, sourcesOverride)
                .SelectMany(matches =>
                {
                    var pairs = NodeCompileShaping.CollectSourcePairs(matches);
                    return ResolveIncludesForPairs(pairs)
                        .SelectMany(resolvedPairs =>
                            // Reactive input assembly — NOT in the pool (see
                            // AssembleCompilationInputs). Only the Roslyn Emit border
                            // touches the pool.
                            AssembleCompilationInputs(node, ntDef, resolvedPairs));
                }));
    }

    /// <summary>Resolves @@ includes for each source independently, preserving paths. Sequential aggregation matches <see cref="CompileCore"/>.</summary>
    private IObservable<List<(string Path, CodeConfiguration Config, long LastModifiedTicks)>>
        ResolveIncludesForPairs(IReadOnlyList<(string Path, CodeConfiguration Config, long LastModifiedTicks)> pairs)
    {
        if (pairs.Count == 0)
            return Observable.Return(new List<(string, CodeConfiguration, long)>());

        IObservable<List<(string, CodeConfiguration, long)>> chain =
            Observable.Return(new List<(string, CodeConfiguration, long)>(pairs.Count));
        foreach (var p in pairs)
        {
            var pair = p;
            chain = chain.SelectMany(acc =>
                ResolveCodeIncludes(pair.Config.Code!, new HashSet<string>(), pair.Path)
                    .Select(resolvedCode =>
                    {
                        var resolvedConfig = !ReferenceEquals(resolvedCode, pair.Config.Code)
                            ? pair.Config with { Code = resolvedCode }
                            : pair.Config;
                        acc.Add((pair.Path, resolvedConfig, pair.LastModifiedTicks));
                        return acc;
                    }));
        }
        return chain;
    }

    private IObservable<CompilationInputs?> AssembleCompilationInputs(
        MeshNode node,
        NodeTypeDefinition? ntDef,
        IReadOnlyList<(string Path, CodeConfiguration Config, long LastModifiedTicks)> resolvedPairs)
    {
        var nodeName = cacheService.SanitizeNodeName(node.Path);

        // Skeleton: assembly attribute + generated provider class. Passing codeFile=null
        // suppresses user-code emission so the skeleton stays decoupled from user sources.
        var rawSkeleton = _attributeGenerator.GenerateAttributeSource(
            node, codeFile: null, ntDef?.Configuration, ntDef?.ContentCollections);
        var (skeleton, skeletonNugetRefs) = NuGetDirectiveParser.Extract(rawSkeleton);

        // User #r "nuget:..." directives can sit in any source file. Aggregate across files
        // so cross-file references resolve. (Skeleton-derived nugetRefs is normally empty —
        // the generator doesn't emit #r — but handle them for forward-compatibility.)
        var allNugetRefs = new List<NuGetPackageReference>(skeletonNugetRefs);
        var strippedSources = new List<(string Path, string Code, long LastModifiedTicks)>(resolvedPairs.Count);
        foreach (var p in resolvedPairs)
        {
            var (stripped, refs) = NuGetDirectiveParser.Extract(p.Config.Code ?? string.Empty);
            allNugetRefs.AddRange(refs);
            strippedSources.Add((p.Path, stripped, p.LastModifiedTicks));
        }

        // A legacy `#r "nuget:MeshWeaver.BusinessRules.Generator"` must NOT reach the NuGet resolver
        // (the generator ships built-in now) — see GeneratorPipeline.StripBuiltInScopeGeneratorRef.
        GeneratorPipeline.StripBuiltInScopeGeneratorRef(
            allNugetRefs, builtInPresent: GeneratorPipeline.BuiltInGeneratorPaths.Count > 0);

        // 🚨 100% reactive — NO await, and the input assembly is NOT wrapped in
        // _ioPool.Run. The only async leaf is NuGet restore (network IO), and it runs
        // ONLY when a `#r "nuget:"` directive is present — bridged through the IoPool
        // reactively. The common case (no NuGet) is a pure Observable.Return, so the
        // pipeline stays reactive end-to-end and never blocks/parks: only Roslyn's
        // synchronous Emit touches the pool (see CompileCore → OnThreadPool/InvokeBlocking).
        IObservable<ImmutableArray<MetadataReference>> referencesObs =
            allNugetRefs.Count > 0
                ? _ioPool.Run(ct => nugetResolver.ResolveAsync(allNugetRefs, targetFramework: null, ct))
                    .Select(resolved => References
                        .Concat(resolved.AssemblyPaths.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)))
                        .ToImmutableArray())
                : Observable.Return(References.ToImmutableArray());

        return referencesObs.Select(references =>
        {
            var parseOptions = EmitPipeline.CreateParseOptions();
            var compilationOptions = EmitPipeline.CreateCompilationOptions();

            var sourcesArray = strippedSources
                .Select(s => (s.Path, s.Code))
                .ToImmutableArray();

            var versions = ImmutableDictionary<string, long>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);
            foreach (var s in strippedSources)
                versions = versions.SetItem(s.Path, s.LastModifiedTicks);

            return (CompilationInputs?)new CompilationInputs(
                AssemblyName: $"DynamicNode_{nodeName}",
                Sources: sourcesArray,
                SkeletonSource: skeleton,
                // The skeleton above is generated with codeFile:null, so its `using`s are
                // file-scoped to a tree that holds no user code. Under the per-file trees the
                // language service needs for Monaco positions that leaves every source without the
                // framework imports the emit path gives it by concatenation — the #1802 false FAIL.
                // Render the same scope as `global using` so both paths agree.
                GlobalUsingsSource: _attributeGenerator.GenerateGlobalUsingsSource(
                    strippedSources.Select(s => (string?)s.Code)),
                References: references,
                ParseOptions: parseOptions,
                CompilationOptions: compilationOptions,
                SourceVersions: versions);
        });
    }

    /// <param name="generatedInputDigest">The stage-1 CONTENT KEY digest of the compile that
    /// produced these bytes (#1707 slice 4), or null when no compile ran in this process — a
    /// disk-cache hit or the assembly-hydration shortcut. Null simply leaves the record without a
    /// content key; the toolchain entry still governs.</param>
    private NodeCompilationResult? CompileResultFromAssembly(
        MeshNode node, string assemblyLocation, ActivityLog log,
        ImmutableDictionary<string, long> compiledSources,
        string? generatedInputDigest = null)
    {

            var nodeName = cacheService.SanitizeNodeName(node.Path);

            try
            {
                // Load from the exact path recorded on the node, not from the shared
                // GetDllPath(nodeName) shorthand. Each release writes to a unique subdir
                // so V1 and V2 ALCs are separate; loading from the canonical shared path
                // would always return V1's assembly after V2 compiles. In-memory
                // assemblies keep the old keyed-by-nodeName path.
                // PIN the context across BOTH the load AND the GetTypes / MeshNodeProviderAttribute
                // scan below. A concurrent recompile/eviction/teardown must not Unload this context
                // while we reflect over its assembly, or the scan faults with
                // "TypeLoadException: could not load type '…MeshNodeProviderAttribute' … because the
                // format is invalid" (the flaky Orleans dynamic-compilation race). The pin releases
                // when this method returns; Dispose() drains pins before Unload().
                //
                // 🚨 Resolve and pin as ONE operation (PinForScan), never as two statements. This
                // used to resolve the context, then pin it — and an eviction landing in between
                // (a concurrent recompile superseding this assembly) made the pin throw on a
                // reference that was live one instruction earlier. The catch below then recorded
                // an EMPTY configuration list, which the per-instance activation path reads as
                // authoritative: the hub binds the mesh defaults and, because a hub resolves its
                // configuration exactly once at activation, serves "Area not found" for its whole
                // lifetime. That is #1151's residual — a freshly installed package's root left
                // without its own type's areas by a recompile it merely ran next to.
                using var pinned = cacheService.PinForScan(
                    nodeName,
                    assemblyLocation.StartsWith("memory://", StringComparison.Ordinal)
                        ? null
                        : assemblyLocation);
                var context = pinned.Context;
                var assembly = context.LoadNodeAssembly();
                if (assembly == null)
                {
                    // Promoted from Warning → Error: this is the root cause that
                    // cascades into every downstream "SubscribeRequest timed out"
                    // for hubs of this NodeType. Log noise from the cascade was
                    // hiding this single offender — make it stand out so the
                    // operator sees the cause, not the symptoms.
                    logger.LogError(
                        "Failed to load assembly for {NodePath} — the per-node hub for this " +
                        "NodeType (and every instance of it) cannot activate. Subscribe / GetData " +
                        "calls to its grains will time out. Common causes: corrupt cached .dll " +
                        "(delete .mesh-cache to force recompile), source compilation error " +
                        "(check the Code node's diagnostics), or missing dependency.",
                        node.Path);
                    // The build is NOT usable → record NO assembly. Downstream `ok` is
                    // `Error is null && !IsNullOrEmpty(AssemblyLocation)`, so a null location
                    // makes ok=false → CompilationStatus=Error, NO release, the emergency
                    // compilation-error overlay renders the failure on the Overview, and the
                    // first-build kickoff (gated on Status==null) does NOT retry. Recording the
                    // assemblyLocation here was the wedge: it read as success (Status=Ok) while the
                    // per-node hub could not actually activate against it → Subscribe parked.
                    return new NodeCompilationResult(null, [],
                        AppendError(log,
                            $"Failed to load assembly at {assemblyLocation} — the build is not usable " +
                            "(corrupt cached .dll or a missing dependency)."),
                        compiledSources);
                }

                var configurations = new List<NodeTypeConfiguration>();
                foreach (var type in assembly.GetTypes())
                {
                    if (typeof(MeshNodeProviderAttribute).IsAssignableFrom(type) && !type.IsAbstract)
                    {
                        var attribute = (MeshNodeProviderAttribute?)Activator.CreateInstance(type);
                        if (attribute != null)
                        {
                            foreach (var meshNode in attribute.Nodes)
                            {
                                var hubConfig = meshNode.HubConfiguration;
                                if (hubConfig != null)
                                {
                                    configurations.Add(new NodeTypeConfiguration
                                    {
                                        NodeType = meshNode.Path,
                                        DataType = typeof(object),
                                        HubConfiguration = hubConfig,
                                        DisplayName = meshNode.Name,
                                        Icon = meshNode.Icon,
                                    });
                                }
                            }
                        }
                    }
                }

                logger.LogDebug("Extracted {Count} NodeTypeConfigurations from {AssemblyLocation}",
                    configurations.Count, assemblyLocation);

                // The per-type DEPENDENCY RECORD (#1707 slice 2), read off the EMITTED assembly's
                // AssemblyRef table — Roslyn emits a ref only for assemblies the produced code
                // actually uses, so this is the pruned, true dependency set, computed here where
                // the assembly is already loaded for the attribute scan (both disk and memory
                // modes, and the hydration shortcut, funnel through this method).
                var dependencies = Compiler.CompiledDependencies.Compute(
                    assembly.GetReferencedAssemblies().Select(n => n.Name),
                    NodeTypeCompilationHelpers.DependencyIdResolverOf(hub),
                    NodeTypeCompilationHelpers.ProcessToolchainId,
                    // The CONTENT KEY (#1707 slice 4) is completed here: stage 1 came from the
                    // emit, and the PRUNED reference surfaces are the record's own entries — the
                    // set Roslyn proved the bytes actually bind.
                    generatedInputDigest);

                return new NodeCompilationResult(
                    assemblyLocation, configurations, log, compiledSources,
                    CompiledDependencies: dependencies);
            }
            catch (Exception ex)
            {
                // The assembly loaded but its types can't be realised — typically a MISSING
                // DEPENDENCY (e.g. a referenced package that isn't deployed). Surface the loader
                // detail so the overlay Overview names the actual problem ("could not load type X").
                // Record NO assembly (location null): ok=false → CompilationStatus=Error, NO release,
                // the overlay renders the failure, and the kickoff does not retry (Status != null).
                var detail = ex is System.Reflection.ReflectionTypeLoadException rtl
                    ? string.Join("; ", rtl.LoaderExceptions
                        .Where(e => e is not null).Select(e => e!.Message).Distinct())
                    : ex.Message;
                logger.LogWarning(ex,
                    "Failed to extract NodeTypeConfigurations from {AssemblyLocation}: {Detail}",
                    assemblyLocation, detail);
                return new NodeCompilationResult(null, [],
                    AppendError(log, $"Failed to load the compiled assembly — {detail}"),
                    compiledSources);
            }
    }

    /// <summary>
    /// Compiles CodeConfiguration into an assembly using Roslyn.
    /// Supports both disk-based and in-memory compilation.
    /// </summary>
    private Task<CompileEmit> CompileAsync(
        CodeConfiguration? codeFile,
        string? hubConfiguration,
        IReadOnlyList<ContentCollectionConfig>? contentCollections,
        MeshNode node,
        CancellationToken ct)
    {
        var nodeName = cacheService.SanitizeNodeName(node.Path);
        // Single-flight: GetOrAdd with Lazy<T> ensures the factory runs at
        // most once even under concurrent entry. All callers receive the
        // SAME Task and await its result. The continuation evicts the entry
        // once the task settles so a future invalidation triggers a fresh
        // compile instead of returning the stale completed task.
        var lazy = _inflightCompiles.GetOrAdd(nodeName, n =>
            new Lazy<Task<CompileEmit>>(() => RunCompileAndEvict(
                codeFile, hubConfiguration, contentCollections, node, n, ct)));
        return lazy.Value;
    }

    private async Task<CompileEmit> RunCompileAndEvict(
        CodeConfiguration? codeFile,
        string? hubConfiguration,
        IReadOnlyList<ContentCollectionConfig>? contentCollections,
        MeshNode node,
        string nodeName,
        CancellationToken ct)
    {
        try
        {
            return await CompileAsyncCore(codeFile, hubConfiguration, contentCollections, node, nodeName, ct);
        }
        finally
        {
            _inflightCompiles.TryRemove(nodeName, out _);
        }
    }

    private async Task<CompileEmit> CompileAsyncCore(
        CodeConfiguration? codeFile,
        string? hubConfiguration,
        IReadOnlyList<ContentCollectionConfig>? contentCollections,
        MeshNode node,
        string nodeName,
        CancellationToken ct)
    {
        if (cacheService.IsDiskCacheEnabled)
        {
            cacheService.EnsureCacheDirectoryExists();
        }

        ct.ThrowIfCancellationRequested();

        // Generate full source with MeshNodeProviderAttribute (including content collections)
        var rawSource = _attributeGenerator.GenerateAttributeSource(node, codeFile, hubConfiguration, contentCollections);

        // Strip #r "nuget:..." directives — Roslyn compilation (unlike scripting) does not process them.
        // The BusinessRules scope generator is pulled in ONLY when the node Source EXPLICITLY declares
        // `#r "nuget:MeshWeaver.BusinessRules.Generator"` — no auto-injection heuristic (that forced
        // generator resolution on the compile path for any node merely mentioning IScope). Explicit
        // #r → resolved here → discovered + run by RunSourceGenerators from the resolved assemblies.
        var (source, extractedRefs) = NuGetDirectiveParser.Extract(rawSource);
        // 🚨 Same legacy-#r strip as AssembleCompilationInputs — this path previously lacked it
        // (see the release-folder compile below for the full story: resolving the legacy
        // generator #r hard-fails now that the mesh-local feed is gone).
        var nugetRefList = extractedRefs.ToList();
        GeneratorPipeline.StripBuiltInScopeGeneratorRef(
            nugetRefList, builtInPresent: GeneratorPipeline.BuiltInGeneratorPaths.Count > 0);
        var nugetRefs = nugetRefList.ToArray();
        IEnumerable<MetadataReference> references = References;
        IReadOnlyList<string> nugetAssemblyPaths = [];
        if (nugetRefs.Length > 0)
        {
            var resolved = await nugetResolver.ResolveAsync(nugetRefs, targetFramework: null, ct);
            references = References.Concat(
                resolved.AssemblyPaths.Select(p => MetadataReference.CreateFromFile(p)));
            nugetAssemblyPaths = resolved.AssemblyPaths;
            cacheService.RegisterProbingDirectories(nodeName, resolved.ProbingDirectories);
        }

        // Write source file for debugging (only for disk cache)
        var sourcePath = cacheService.GetSourcePath(nodeName);
        if (cacheService.IsDiskCacheEnabled && _cacheOptions.EnableSourceDebugging)
        {
            await File.WriteAllTextAsync(sourcePath, source, ct);
            logger.LogDebug("Wrote source file for debugging: {SourcePath}", sourcePath);
        }

        logger.LogInformation("Compiling assembly for {NodeName} ({Mode}, {NuGetRefs} NuGet refs)",
            nodeName, cacheService.IsDiskCacheEnabled ? "disk" : "in-memory", nugetRefs.Length);

        // Parse + compile via the toolchain (EmitPipeline, #1707) — source path and encoding
        // embedded (critical for PDB source linking); canonical options; generators applied.
        var assemblyName = $"DynamicNode_{nodeName}";

        // 🚨 THE CONTENT KEY's first stage (#1707 slice 4), taken HERE — the last point at which
        // the fully generated input exists as text, and before a single Roslyn call. It keys the
        // exact thing the toolchain's full-MVID proxy stands in for: what Roslyn is actually fed.
        // The NuGet-resolved assemblies ride in as generator candidates, so a change in what a
        // `#r "nuget:"` resolves to moves the key (GeneratorPipeline.EffectiveGeneratorPaths is
        // the same set RunSourceGenerators loads — one resolution, no drift).
        var generatedInputDigest = GeneratedInputIdentity.OfGeneratedInput(
            assemblyName,
            source,
            EmitPipeline.OptionsFingerprint,
            GeneratedInputIdentity.CompilerIdentity,
            GeneratedInputIdentity.AssemblyFileIdentities(
                GeneratorPipeline.EffectiveGeneratorPaths(nugetAssemblyPaths)));
        var compilation = GeneratorPipeline.RunSourceGenerators(
            EmitPipeline.CreateEmitCompilation(
                source,
                assemblyName,
                references,
                parsePath: cacheService.IsDiskCacheEnabled && _cacheOptions.EnableSourceDebugging ? sourcePath : "",
                ct),
            nugetAssemblyPaths, logger, ct);

        string? actualPath;
        // The compile's own diagnostics, on the way to the activity. Empty is a real answer here —
        // it means the compiler produced none, not that nobody looked.
        IReadOnlyList<string> warnings = [];
        if (cacheService.IsDiskCacheEnabled)
        {
            var emitted = default(EmittedArtifact);
            actualPath = EmitPipeline.EmitToDiskWithRetry(
                cacheService.CacheDirectory, nodeName, EmitPipeline.DiskEmitAttempts, logger,
                releaseDir => emitted = EmitPipeline.EmitCompilationToDirectory(
                    compilation, nodeName, node.Path, releaseDir, ct));
            warnings = emitted.Warnings;
        }
        else
        {
            // The in-memory path compiles the same tree, so it must report the same diagnostics —
            // a warning that appears only when the disk cache is on would be a warning that depends
            // on a deployment's caching mode, which is exactly the kind of difference nobody finds.
            warnings = EmitPipeline.Warnings(compilation.GetDiagnostics(ct));
            var (assemblyBytes, pdbBytes) = EmitPipeline.EmitToMemory(compilation, node.Path, ct);
            cacheService.LoadAssemblyFromBytes(nodeName, assemblyBytes, pdbBytes);
            actualPath = $"memory://{nodeName}";
        }

        logger.LogInformation("Successfully compiled assembly for {NodePath} to {ActualPath}", node.Path, actualPath);
        return new CompileEmit(actualPath, generatedInputDigest, warnings);
    }
}
