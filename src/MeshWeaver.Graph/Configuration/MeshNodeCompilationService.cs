using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _inflightCompiles =
        new(StringComparer.Ordinal);

    // Query expansion lives in CodeQueryResolver now so the NodeType Configuration
    // side menu can evaluate the *same* queries the compiler uses — the Sources /
    // Tests lists displayed in the UI are guaranteed to match the files compiled.
    //
    // Default Roslyn references are process-wide: TPA list + a few well-known
    // additions never change at runtime. Eager static-field init runs once at
    // type load and the result is then a plain field read on every compile —
    // no Lazy property dispatch, no synchronization, zero per-compile cost.
    private static readonly IReadOnlyList<MetadataReference> _references = GetDefaultReferences();

    /// <summary>
    /// Builds the process-wide MetadataReference list — TPA assemblies plus a few
    /// well-known additions. Uses <see cref="MetadataReference.CreateFromFile(string, MetadataReferenceProperties, DocumentationProvider)"/>
    /// (mmap, lazy read) — Roslyn typically reads only a small fraction of each
    /// assembly's metadata, so the upfront cost is tiny. An earlier attempt at
    /// <see cref="MetadataReference.CreateFromStream(Stream, MetadataReferenceProperties, DocumentationProvider, string?)"/>
    /// to avoid finalizer pressure ended up reading the whole DLL into managed
    /// memory eagerly — net 10%+ slower in the autocomplete-test CPU profile,
    /// since most of those bytes were never touched. The file-handle finalizer
    /// pressure those references add is also tiny in practice (the static field
    /// holds them for the process lifetime; finalizers only run at shutdown).
    /// </summary>
    private static List<MetadataReference> GetDefaultReferences()
    {
        var references = new List<MetadataReference>();

        var trustedAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (trustedAssemblies != null)
        {
            foreach (var path in trustedAssemblies.Split(Path.PathSeparator))
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    continue;
                try
                {
                    references.Add(MetadataReference.CreateFromFile(path));
                }
                catch
                {
                    // Skip assemblies that can't be loaded
                }
            }
        }

        // Three well-known additions in case TPA didn't include them. Dedup
        // against TPA-derived set by Display path so we don't double-load.
        var seen = new HashSet<string>(
            references.Select(r => r.Display ?? string.Empty),
            StringComparer.OrdinalIgnoreCase);
        foreach (var assembly in new[]
        {
            typeof(object).Assembly,                                           // System.Runtime
            typeof(System.ComponentModel.DataAnnotations.KeyAttribute).Assembly, // DataAnnotations
            typeof(System.Text.Json.Serialization.JsonPropertyNameAttribute).Assembly, // System.Text.Json
        })
        {
            if (!string.IsNullOrEmpty(assembly.Location)
                && File.Exists(assembly.Location)
                && seen.Add(assembly.Location))
            {
                try
                {
                    references.Add(MetadataReference.CreateFromFile(assembly.Location));
                }
                catch
                {
                    // Skip if it can't be loaded
                }
            }
        }

        return references;
    }

    /// <summary>
    /// Regex matching @@path references in code files. The capture must LOOK LIKE A NODE PATH —
    /// it starts with a word character and continues with path characters only — because this
    /// pattern runs over RAW C# SOURCE, where <c>@@</c> also appears in prose: XML doc comments
    /// citing the markdown embed idiom (<c>@@("area/Search")</c>) and string literals in tests
    /// asserting exactly that idiom.
    ///
    /// <para>🚨 The permissive predecessor (<c>@@([^\s#\]]+)</c>, shared with the AI
    /// InlineReferenceResolver, which reads PROSE where that is correct) scraped those fragments
    /// as include paths — <c>("area/CoverCta")&lt;/c&gt;</c>, <c>("Install/area/CoverCta")"),</c> —
    /// and each garbage match cost a SERIAL 15s GetMeshNode timeout on the resolving hub. On memex
    /// 2026-07-29 that stall starved the Store root's activation reads (its subtree holds ~44 Code
    /// nodes): SubscribeRequest hit its 60s ceiling and the page died with "activation faulted".
    /// A scanner over source code must reject anything a node path cannot begin with — quotes,
    /// parentheses, XML markup.</para>
    /// </summary>
    internal static readonly Regex CodeIncludePattern = new(@"@@([\w][\w\-./]*)", RegexOptions.Compiled);

    private IObservable<string> ResolveCodeIncludes(string code, HashSet<string> resolved)
    {
        if (string.IsNullOrWhiteSpace(code) || !code.Contains("@@"))
            return Observable.Return(code);

        var matches = CodeIncludePattern.Matches(code);
        if (matches.Count == 0)
            return Observable.Return(code);

        // For each @@ match, fetch the referenced node via composed hub.GetMeshNode
        // (NEVER await — that's a 100% deadlock). Each result feeds the recursive
        // resolution; the final substituted string is built up in left-to-right order
        // by serially aggregating the per-match observables.
        IObservable<string> chain = Observable.Return(code);
        foreach (Match match in matches)
        {
            var path = match.Groups[1].Value;
            var matchValue = match.Value;
            chain = chain.SelectMany(current =>
            {
                if (!resolved.Add(path))
                    return Observable.Return(current.Replace(matchValue, string.Empty));

                // EmitNull — see the note on the NodeType-definition reads below: this is the
                // hub-ACTIVATION path, where turning a transient stall into a hard fault would
                // park the type. Behaviour is unchanged (the include stays unresolved and the
                // LogWarning below fires); the read itself now also logs the stall + diagnostics.
                return hub.GetMeshNode(path, TimeSpan.FromSeconds(15), ReadTimeoutBehavior.EmitNull)
                    .SelectMany(referencedNode =>
                    {
                        if (referencedNode?.Content is CodeConfiguration cf
                            && !string.IsNullOrWhiteSpace(cf.Code))
                        {
                            logger.LogDebug("Resolved code include @@{Path}", path);
                            return ResolveCodeIncludes(cf.Code, resolved)
                                .Select(resolvedInner => current.Replace(matchValue, resolvedInner));
                        }
                        logger.LogWarning("Could not resolve code include @@{Path}", path);
                        return Observable.Return(current);
                    });
            });
        }

        return chain;
    }

    /// <inheritdoc />
    public IObservable<string?> GetAssemblyLocation(MeshNode node)
        => GetAssemblyLocationWithLog(node).Select(t => t.Path);

    /// <summary>
    /// One compile attempt's outcome: the assembly path (null on failure), the
    /// <see cref="ActivityLog"/>, and — the point of carrying it — the ONE source snapshot
    /// the attempt was taken against. Every downstream stage
    /// (<see cref="DiscoverSourceVersionSnapshot"/>, <see cref="BuildFailureDiagnostics"/>)
    /// reuses <see cref="Sources"/> instead of re-discovering; see
    /// <see cref="GetAssemblyLocationWithLog"/>.
    /// </summary>
    private readonly record struct CompileAttempt(
        string? Path, ActivityLog Log, IReadOnlyList<MeshNode> Sources);

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
            return Observable.Return(new CompileAttempt(null,
                AppendInfo(log, $"Skipped — node '{node.Path}' has no NodeType.")
                    .Finish((int)hub.Version, ActivityStatus.Succeeded),
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
            resolveDef = hub.GetMeshNode(node.NodeType, TimeSpan.FromSeconds(15), ReadTimeoutBehavior.EmitNull)
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
                                AppendInfo(log,
                                    $"Cache hit — returning {cachedDllPath} (effective LastModified={effectiveLastModified:O}).")
                                    .Finish((int)hub.Version, ActivityStatus.Succeeded),
                                sources));
                        }
                    }

                    // Hand the snapshot down as the override — CompileCore then short-circuits
                    // its own SnapshotSources to this authoritative point-in-time set.
                    return CompileCore(node, ntDef, selfPath, log, sources)
                        .Select(t => new CompileAttempt(t.Path, t.Log, sources));
                });
        });
    }

    private static ActivityLog AppendInfo(ActivityLog log, string message)
        => log with { Messages = log.Messages.Add(new LogMessage(message, LogLevel.Information)) };

    private static ActivityLog AppendWarning(ActivityLog log, string message)
        => log with { Messages = log.Messages.Add(new LogMessage(message, LogLevel.Warning)) };

    private static ActivityLog AppendError(ActivityLog log, string message)
        => log with { Messages = log.Messages.Add(new LogMessage(message, LogLevel.Error)) };

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
                    (acc, n) => acc.SetItem(n.Path, n.LastModified.UtcTicks)));

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
    {
        var bound = _cacheOptions.SourceSnapshotTimeout;

        // A caller-supplied override is already an authoritative point-in-time snapshot.
        if (sourcesOverride is not null)
            return Observable.Return<IEnumerable<MeshNode>>(sourcesOverride);

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
        return RaceSourceSnapshot(
                DirectSourceProbe(ntDef, selfPath, TimeSpan.Zero),
                Observable.Defer(() => ResolveSources(ntDef, selfPath, null).Take(1)))
            .Timeout(bound)
            .Catch<IEnumerable<MeshNode>, TimeoutException>(ex =>
                Observable.Throw<IEnumerable<MeshNode>>(new TimeoutException(
                    $"Source snapshot for '{selfPath}' did not emit within {bound.TotalSeconds:0}s — "
                    + "neither the direct mesh read nor the synced source query "
                    + $"('{NodeSources.CacheId(selfPath)}') produced a source set. The compile fails "
                    + "terminally instead of parking at Compiling forever; retry via the Compile "
                    + "button / a fresh RequestedReleaseAt.", ex)));
    }

    /// <summary>
    /// The snapshot race between the authoritative direct mesh read (<paramref name="directProbe"/> —
    /// emits at most one NON-EMPTY source set, stays silent and completes when it finds nothing) and
    /// the cached synced query's first emission (<paramref name="cachedFirst"/>). Extracted as a pure
    /// combinator so the race's correctness semantics are deterministically unit-testable
    /// (CodeEditRecompileTest.SourceSnapshot_*).
    ///
    /// <para>🚨 An EMPTY cached answer must never WIN the race (issue #612, CI run 30004790036
    /// "sub-case b"). The cached synced query replays its latest set SYNCHRONOUSLY on subscribe,
    /// while the probe cannot answer before its chunk quiet window — so under the old
    /// <c>Merge(...).FirstAsync()</c> shape a cached query that had latched EMPTY (a missed
    /// source-create update under load — the stale-synced-query class) ALWAYS beat the probe, the
    /// compile consumed ZERO sources, the configuration lambda's CS0103 parked the type, and every
    /// retry — including the explicit un-parking RequestedReleaseAt re-trigger — re-failed
    /// identically: a permanent wedge at Status=Error with no release. The probe leg already
    /// refuses to emit empty for exactly this reason ("compiling the type against NOTHING [is]
    /// strictly worse than the stall"); this applies the same rule to the cached leg.</para>
    ///
    /// <para>Semantics: the first NON-EMPTY answer from either side wins immediately (a healthy
    /// cached query still settles the snapshot with zero probe latency — the #690 regression
    /// guard). EMPTY settles only by CONSENSUS: both legs completed without producing a source —
    /// then a source-less, configuration-only NodeType still compiles. A cached leg that never
    /// emits at all leaves the race to the probe / the caller's outer <c>Timeout</c>, unchanged.</para>
    /// </summary>
    internal static IObservable<IEnumerable<MeshNode>> RaceSourceSnapshot(
        IObservable<IEnumerable<MeshNode>> directProbe,
        IObservable<IEnumerable<MeshNode>> cachedFirst)
        => directProbe
            .Merge(cachedFirst
                .Select(static s => (IReadOnlyCollection<MeshNode>)(s as IReadOnlyCollection<MeshNode> ?? s.ToList()))
                .Where(static s => s.Count > 0)
                .Select(static s => (IEnumerable<MeshNode>)s))
            .Take(1)
            .DefaultIfEmpty(Enumerable.Empty<MeshNode>());

    /// <summary>
    /// How long the cached synced source query gets before the uncached probe is even subscribed.
    /// Comfortably past a warm cache hit or one cold storage read, so the probe stays dormant in
    /// every healthy compile and only wakes for a genuinely stalled subscription.
    /// </summary>
    private static readonly TimeSpan SourceStallProbeDelay = TimeSpan.FromSeconds(3);

    /// <summary>How long the uncached probe waits for its chunked Initial to go quiet before
    /// treating the accumulated set as complete.</summary>
    private static readonly TimeSpan SourceProbeQuietWindow = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Fold one <see cref="QueryResultChange{T}"/> into the accumulating path→node map — pure, so
    /// the chunk-accumulation contract is unit-testable. Initial/Reset/Added/Updated set; Removed
    /// deletes. Keyed by path, so a re-delivered chunk is idempotent rather than a duplicate.
    /// </summary>
    internal static ImmutableDictionary<string, MeshNode> ApplyQueryChange(
        ImmutableDictionary<string, MeshNode> acc, QueryResultChange<MeshNode> change)
    {
        if (change?.Items is not { Count: > 0 } items)
            return acc;
        foreach (var node in items)
        {
            if (string.IsNullOrEmpty(node?.Path))
                continue;
            acc = change.ChangeType == QueryChangeType.Removed
                ? acc.Remove(node.Path)
                : acc.SetItem(node.Path, node);
        }
        return acc;
    }

    /// <summary>
    /// The uncached escape hatch behind <see cref="SnapshotSources"/>: the SAME expanded source
    /// queries issued straight at <see cref="IMeshService"/>, bypassing the synced-query cache whose
    /// missed Initial is what idles until the 45s heartbeat. Subscription-delayed, so a healthy
    /// compile never issues it. Failures complete EMPTY rather than erroring — this is a fallback
    /// racing a primary, and it must never be the thing that fails a compile.
    /// </summary>
    private IObservable<IEnumerable<MeshNode>> DirectSourceProbe(
        NodeTypeDefinition? ntDef, string selfPath, TimeSpan? subscriptionDelay = null)
    {
        var queries = CodeQueryResolver
            .ExpandAll(ntDef?.Sources, CodeQueryResolver.DefaultSources, selfPath)
            .Concat(CodeQueryResolver.ExpandAll(ntDef?.Tests, CodeQueryResolver.DefaultTests, selfPath))
            .ToArray();
        if (queries.Length == 0)
            return Observable.Empty<IEnumerable<MeshNode>>();

        var mesh = hub.ServiceProvider.GetService<IMeshService>();
        if (mesh is null)
            return Observable.Empty<IEnumerable<MeshNode>>();

        return Observable
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
                    // below and the leg degrades to empty, exactly like any other faulted leg.
                    Observable.Defer(() => mesh.Query<MeshNode>(MeshQueryRequest.FromQuery(q)))
                        .Scan(ImmutableDictionary<string, MeshNode>.Empty, ApplyQueryChange)
                        .Throttle(SourceProbeQuietWindow)
                        .Take(1)
                        .Select(map => (IReadOnlyCollection<MeshNode>)map.Values.ToList())
                        .Catch((Exception _) => Observable.Return<IReadOnlyCollection<MeshNode>>([]))))
                .Select(results => results
                    .SelectMany(r => r ?? [])
                    .Where(n => !string.IsNullOrEmpty(n.Path))
                    .GroupBy(n => n.Path)
                    .Select(g => g.First())
                    .ToList())
                // 🚨 NEVER win the race with an EMPTY set. Every per-query leg degrades to empty on
                // error, so a probe that hit a cold partition or a permission wall would otherwise
                // emit "no sources", beat the cached query, and compile the type against NOTHING —
                // strictly worse than the stall it exists to dodge. No sources found ⇒ stay silent
                // and let the cached query (and the outer Timeout) decide.
                .Where(merged => merged.Count > 0)
                .Select(merged =>
                {
                    logger.LogDebug(
                        "Source discovery for '{SelfPath}': direct mesh read returned {Count} source node(s).",
                        selfPath, merged.Count);
                    return (IEnumerable<MeshNode>)merged;
                }))
            .Catch((Exception _) => Observable.Empty<IEnumerable<MeshNode>>())
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
    private IObservable<(string? Path, ActivityLog Log)> CompileCore(
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
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var acc = new List<CodeConfiguration>();
                foreach (var n in matches)
                {
                    if (string.IsNullOrEmpty(n.Path) || !seen.Add(n.Path))
                        continue;
                    if (n.Content is CodeConfiguration cf
                        && !string.IsNullOrWhiteSpace(cf.Code))
                    {
                        // Skip executable scripts — they run via the kernel
                        // (ExecuteScriptRequest), not folded into the parent
                        // NodeType's Roslyn unit. Top-level statements would
                        // collide with class declarations from Source/ siblings
                        // ("Top-level statements must precede namespace and
                        // type declarations"). Test/ commonly mixes both
                        // shapes; this filter lets both coexist.
                        if (cf.IsExecutable)
                        {
                            logger.LogDebug(
                                "Source discovery for {NodePath}: skipping executable Code {CodePath} — runs via kernel only",
                                node.Path, n.Path);
                            continue;
                        }
                        acc.Add(cf);
                        matchedCodePaths.Add(n.Path);
                    }
                }
                logger.LogDebug(
                    "Source discovery for {NodePath}: matched {Count} Code nodes from {QueryCount} queries",
                    node.Path, matchedCodePaths.Count, executedQueries.Count);
                return acc;
            });

        return discoverCodeFiles
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
                        ResolveCodeIncludes(cf.Code!, new HashSet<string>())
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
                // the only Task→Observable bridge in this whole method.
                CodeConfiguration? codeFile = codeFiles.Count switch
                {
                    0 => null,
                    1 => codeFiles[0],
                    _ => new CodeConfiguration { Code = string.Join("\n\n", codeFiles.Select(cf => cf.Code)) }
                };
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
                    .Select(actualPath =>
                    {
                        ActivityLog finalLog;
                        string? finalPath;
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
                        return (finalPath, finalLog.Finish((int)hub.Version, ActivityStatus.Succeeded));
                    })
                    .Catch<(string?, ActivityLog), CompilationException>(ex =>
                    {
                        var diag = BuildSourceDiscoveryReport(executedQueries, matchedCodePaths);
                        logger.LogError(ex, "Failed to compile assembly for node {NodePath}. {Diagnostics}",
                            node.Path, diag);
                        var failedLog = AppendError(discoveryLog,
                                $"Compilation failed: {ex.Message}\n--- Source discovery ---\n{diag}")
                            .Finish((int)hub.Version, ActivityStatus.Failed);
                        return Observable.Return<(string?, ActivityLog)>((null, failedLog));
                    });
            });
    }

    /// <summary>
    /// Formats a failed Roslyn <c>Emit</c>'s diagnostics into a complete, never-empty error
    /// message — each line carries the diagnostic <c>CS####</c> id, severity, source line and
    /// message. Falls back to Warning-severity diagnostics when there are no Errors, and to an
    /// explanatory sentence when Emit failed with NO diagnostics at all (typically a missing
    /// source file or a configuration lambda referencing a type that was never compiled). The
    /// previous <c>Where(Severity == Error).Select(GetMessage)</c> produced a bare
    /// "Compilation failed for 'X':" whenever the failure carried no Error-severity diagnostic.
    /// </summary>
    private static string FormatCompileFailure(string nodePath, IEnumerable<Diagnostic> diagnostics)
    {
        var joined = string.Join('\n', diagnostics
            .Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
            .OrderByDescending(d => d.Severity)
            .Select(d =>
            {
                var loc = d.Location.IsInSource
                    ? $" (line {d.Location.GetLineSpan().StartLinePosition.Line + 1})"
                    : "";
                return $"{d.Id} {d.Severity}{loc}: {d.GetMessage()}";
            }));
        return !string.IsNullOrEmpty(joined)
            ? $"Compilation failed for '{nodePath}':\n{joined}"
            : $"Compilation failed for '{nodePath}': Roslyn emit failed but produced no error/warning "
              + "diagnostics — this usually means a source file was not found, or the configuration "
              + "lambda references a type that was never compiled (see the source-discovery report below).";
    }

    // Sentinel FilePath for the generated skeleton tree — must match the one the LSP uses
    // so skeleton-internal diagnostics (framework noise the user can't act on) are filtered out.
    private const string SkeletonDiagnosticsPath = "__skeleton__.cs";

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
                : OnThreadPool(() => DiagnoseInputs(inputs)))
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

    private static IReadOnlyList<Lsp.DiagnosticInfo> DiagnoseInputs(CompilationInputs inputs)
    {
        var trees = new List<SyntaxTree>(inputs.Sources.Length + 1)
        {
            CSharpSyntaxTree.ParseText(
                Microsoft.CodeAnalysis.Text.SourceText.From(inputs.SkeletonSource),
                inputs.ParseOptions, path: SkeletonDiagnosticsPath),
        };
        foreach (var (path, code) in inputs.Sources)
            trees.Add(CSharpSyntaxTree.ParseText(
                Microsoft.CodeAnalysis.Text.SourceText.From(code), inputs.ParseOptions, path: path));

        // Structured failure diagnostics are best-effort: pass no generator candidates so
        // RunSourceGenerators is a no-op here (the authoritative flat summary from the production
        // compile already reflects generation). Avoids loading any generator on every failed compile.
        var compilation = RunSourceGenerators(
            CSharpCompilation.Create(inputs.AssemblyName, trees, inputs.References, inputs.CompilationOptions),
            Array.Empty<string>(), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, CancellationToken.None);

        var diags = compilation.GetDiagnostics();
        if (diags.IsDefaultOrEmpty) return Array.Empty<Lsp.DiagnosticInfo>();

        var result = new List<Lsp.DiagnosticInfo>(diags.Length);
        foreach (var d in diags)
        {
            if (d.Severity is not (DiagnosticSeverity.Error or DiagnosticSeverity.Warning)) continue;
            // Skeleton-internal diagnostics are framework noise the user can't act on.
            if (d.Location.SourceTree?.FilePath == SkeletonDiagnosticsPath) continue;
            result.Add(ToDiagnosticInfo(d));
        }
        // Errors first, then by file then position — stable order for the GUI.
        return result
            .OrderByDescending(d => d.Severity)
            .ThenBy(d => d.Location?.SourcePath, StringComparer.Ordinal)
            .ThenBy(d => d.Location?.Range.Start.Line ?? 0)
            .ToList();
    }

    private static Lsp.DiagnosticInfo ToDiagnosticInfo(Diagnostic d)
    {
        Lsp.SourceLocation? location = null;
        if (d.Location.IsInSource && d.Location.SourceTree?.FilePath is { Length: > 0 } path)
        {
            var span = d.Location.GetLineSpan();
            location = new Lsp.SourceLocation(
                path,
                new Lsp.SourceRange(
                    new Lsp.SourcePosition(span.StartLinePosition.Line, span.StartLinePosition.Character),
                    new Lsp.SourcePosition(span.EndLinePosition.Line, span.EndLinePosition.Character)));
        }
        return new Lsp.DiagnosticInfo(d.Id, MapDiagnosticSeverity(d.Severity), d.GetMessage(), location);
    }

    private static Lsp.DiagnosticSeverity MapDiagnosticSeverity(DiagnosticSeverity s) => s switch
    {
        DiagnosticSeverity.Hidden => Lsp.DiagnosticSeverity.Hidden,
        DiagnosticSeverity.Info => Lsp.DiagnosticSeverity.Info,
        DiagnosticSeverity.Warning => Lsp.DiagnosticSeverity.Warning,
        DiagnosticSeverity.Error => Lsp.DiagnosticSeverity.Error,
        _ => Lsp.DiagnosticSeverity.Info,
    };

    private static string BuildSourceDiscoveryReport(IReadOnlyList<string> executedQueries, IReadOnlyList<string> matchedCodePaths)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Executed source queries ({executedQueries.Count}):");
        foreach (var q in executedQueries)
            sb.AppendLine($"  - {q}");
        sb.AppendLine($"Matched Code nodes ({matchedCodePaths.Count}):");
        if (matchedCodePaths.Count == 0)
            sb.AppendLine("  (none) — the configuration lambda cannot reference types because no source files were included. Check that your Source Code nodes exist and that the NodeType's `sources` list points at them.");
        else
            foreach (var p in matchedCodePaths)
                sb.AppendLine($"  - {p}");
        return sb.ToString();
    }

    /// <inheritdoc />
    public IObservable<NodeCompilationResult?> CompileAndGetConfigurations(
        MeshNode node,
        IReadOnlyList<MeshNode>? sourcesOverride = null)
        => GetAssemblyLocationWithLog(node, sourcesOverride).SelectMany(attempt =>
        {
            var (assemblyLocation, log, sources) = attempt;
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
                        CompileResultFromAssembly(node, assemblyLocation, log, snapshot)),
                    _cacheOptions.AssemblyLoadTimeout, "assembly-load", node.Path))
                // Re-Finish the log after CompileResultFromAssembly. CompileCore already
                // Finished it Succeeded, but CompileResultFromAssembly's downstream steps
                // (assembly load, MeshNodeProviderAttribute reflection) can append fresh
                // Error messages — those need to flip Status to Failed.
                // ActivityLog.Finish(version, override) takes MAX(override, GetFinalStatus
                // from Messages), so an Error message appended after the first Finish
                // bumps Status to Failed automatically.
                .Select(result => result is null
                    ? result
                    : result with { Log = result.Log?.Finish((int)hub.Version, ActivityStatus.Succeeded) })
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
                Log = (result.Log ?? log).Finish((int)hub.Version, ActivityStatus.Succeeded)
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
            : hub.GetMeshNode(node.NodeType, TimeSpan.FromSeconds(15), ReadTimeoutBehavior.EmitNull)
                .Select(typeNode => typeNode.ContentAs<NodeTypeDefinition>(JsonOptions));
        string selfPath = selfDef != null ? node.Path : node.NodeType;

        return resolveDef.SelectMany(ntDef =>
            SnapshotSources(ntDef, selfPath, sourcesOverride)
                .SelectMany(matches =>
                {
                    var pairs = CollectSourcePairs(matches);
                    return ResolveIncludesForPairs(pairs)
                        .SelectMany(resolvedPairs =>
                            // Reactive input assembly — NOT in the pool (see
                            // AssembleCompilationInputs). Only the Roslyn Emit border
                            // touches the pool.
                            AssembleCompilationInputs(node, ntDef, resolvedPairs));
                }));
    }

    /// <summary>
    /// Discovers source <c>(Path, CodeConfiguration, LastModifiedTicks)</c> triples — mirrors
    /// the dedup + IsExecutable filter from <see cref="CompileCore"/>'s discovery step but
    /// retains paths alongside configurations so language services can address each file.
    /// </summary>
    private static List<(string Path, CodeConfiguration Config, long LastModifiedTicks)>
        CollectSourcePairs(IEnumerable<MeshNode> matches)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pairs = new List<(string, CodeConfiguration, long)>();
        foreach (var n in matches)
        {
            if (string.IsNullOrEmpty(n.Path) || !seen.Add(n.Path)) continue;
            if (n.Content is CodeConfiguration cf
                && !string.IsNullOrWhiteSpace(cf.Code)
                && !cf.IsExecutable)
            {
                pairs.Add((n.Path, cf, n.LastModified.UtcTicks));
            }
        }
        return pairs;
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
                ResolveCodeIncludes(pair.Config.Code!, new HashSet<string>())
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
        // (the generator ships built-in now) — see StripBuiltInScopeGeneratorRef.
        StripBuiltInScopeGeneratorRef(allNugetRefs, builtInPresent: BuiltInGeneratorPaths.Count > 0);

        // 🚨 100% reactive — NO await, and the input assembly is NOT wrapped in
        // _ioPool.Run. The only async leaf is NuGet restore (network IO), and it runs
        // ONLY when a `#r "nuget:"` directive is present — bridged through the IoPool
        // reactively. The common case (no NuGet) is a pure Observable.Return, so the
        // pipeline stays reactive end-to-end and never blocks/parks: only Roslyn's
        // synchronous Emit touches the pool (see CompileCore → OnThreadPool/InvokeBlocking).
        IObservable<ImmutableArray<MetadataReference>> referencesObs =
            allNugetRefs.Count > 0
                ? _ioPool.Run(ct => nugetResolver.ResolveAsync(allNugetRefs, targetFramework: null, ct))
                    .Select(resolved => _references
                        .Concat(resolved.AssemblyPaths.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)))
                        .ToImmutableArray())
                : Observable.Return(_references.ToImmutableArray());

        return referencesObs.Select(references =>
        {
            var parseOptions = new CSharpParseOptions(documentationMode: DocumentationMode.Diagnose);
            var compilationOptions = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithOptimizationLevel(OptimizationLevel.Debug)
                .WithPlatform(Platform.AnyCpu);

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
                References: references,
                ParseOptions: parseOptions,
                CompilationOptions: compilationOptions,
                SourceVersions: versions);
        });
    }

    private NodeCompilationResult? CompileResultFromAssembly(
        MeshNode node, string assemblyLocation, ActivityLog log,
        ImmutableDictionary<string, long> compiledSources)
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
                // when this method returns; Dispose() drains pins before Unload(). Pin throws if the
                // context is already unloading → the catch below records it as a load miss (retryable).
                var context = assemblyLocation.StartsWith("memory://", StringComparison.Ordinal)
                    ? cacheService.GetOrCreateLoadContext(nodeName)
                    : cacheService.GetOrCreateLoadContextForPath(nodeName, assemblyLocation);
                using var scanPin = context.Pin();
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

                return new NodeCompilationResult(assemblyLocation, configurations, log, compiledSources);
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
    private Task<string?> CompileAsync(
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
            new Lazy<Task<string?>>(() => RunCompileAndEvict(
                codeFile, hubConfiguration, contentCollections, node, n, ct)));
        return lazy.Value;
    }

    private async Task<string?> RunCompileAndEvict(
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

    private async Task<string?> CompileAsyncCore(
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
        StripBuiltInScopeGeneratorRef(nugetRefList, builtInPresent: BuiltInGeneratorPaths.Count > 0);
        var nugetRefs = nugetRefList.ToArray();
        IEnumerable<MetadataReference> references = _references;
        IReadOnlyList<string> nugetAssemblyPaths = [];
        if (nugetRefs.Length > 0)
        {
            var resolved = await nugetResolver.ResolveAsync(nugetRefs, targetFramework: null, ct);
            references = _references.Concat(
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

        // Parse with source path and encoding embedded (critical for PDB source linking)
        var sourceText = Microsoft.CodeAnalysis.Text.SourceText.From(source, System.Text.Encoding.UTF8);
        var parseOptions = new CSharpParseOptions(documentationMode: DocumentationMode.Diagnose);
        var syntaxTree = CSharpSyntaxTree.ParseText(
            sourceText,
            parseOptions,
            path: cacheService.IsDiskCacheEnabled && _cacheOptions.EnableSourceDebugging ? sourcePath : "",
            cancellationToken: ct);

        var assemblyName = $"DynamicNode_{nodeName}";

        var compilation = RunSourceGenerators(CSharpCompilation.Create(
            assemblyName,
            syntaxTrees: [syntaxTree],
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithOptimizationLevel(OptimizationLevel.Debug)
                .WithPlatform(Platform.AnyCpu)), nugetAssemblyPaths, logger, ct);

        string? actualPath;
        if (cacheService.IsDiskCacheEnabled)
        {
            actualPath = await CompileToDiskAsync(compilation, nodeName, node.Path, ct);
        }
        else
        {
            CompileToMemory(compilation, nodeName, node.Path, ct);
            actualPath = $"memory://{nodeName}";
        }

        logger.LogInformation("Successfully compiled assembly for {NodePath} to {ActualPath}", node.Path, actualPath);
        return actualPath;
    }

    /// <summary>
    /// Paths of source-generator assemblies fed to EVERY dynamic-node compilation, resolved once
    /// per process. 🚨 The platform does NOT ship any — business rules / scopes moved OUT of the
    /// platform (see the comment blocks in <c>MeshWeaver.Graph.csproj</c> and
    /// <c>Memex.Portal.Distributed.csproj</c>): the scope runtime is a shared-source library node
    /// in the <c>MeshWeaver.Plugins/BusinessRules</c> plugin, pulled into a consumer's compilation
    /// via <c>shared=@BusinessRules/Scope/Source</c>, and the plugin carries the
    /// <c>ScopeCodeGenerator</c> SOURCE for the generator-injection seam. So on a deployed image
    /// this list is EMPTY. It only fills when a <c>MeshWeaver.BusinessRules.Generator.dll</c> is
    /// physically present next to the app (a dev/self-host tree that placed one there) — kept as
    /// graceful degradation for such trees, and because the legacy-<c>#r</c> strip below keys off it.
    /// </summary>
    private static readonly IReadOnlyList<string> BuiltInGeneratorPaths = ResolveBuiltInGenerators();

    /// <summary>
    /// NuGet package id (and, with <c>.dll</c>, assembly file name) of the BusinessRules scope
    /// source generator. When a copy is present in the app base (<see cref="BuiltInGeneratorPaths"/>
    /// non-empty), a legacy <c>#r "nuget:MeshWeaver.BusinessRules.Generator"</c> is redundant and is
    /// filtered out of BOTH the generator list (avoid a double-run → CS0101, see
    /// <see cref="RunSourceGenerators"/>) and the NuGet resolve set (avoid a dead round-trip, see
    /// <see cref="AssembleCompilationInputs"/>).
    /// </summary>
    private const string BuiltInScopeGeneratorId = "MeshWeaver.BusinessRules.Generator";

    private static IReadOnlyList<string> ResolveBuiltInGenerators()
    {
        var path = Path.Combine(AppContext.BaseDirectory, BuiltInScopeGeneratorId + ".dll");
        return File.Exists(path) ? [path] : [];
    }

    /// <summary>
    /// Removes a legacy <c>#r "nuget:MeshWeaver.BusinessRules.Generator"</c> from the NuGet resolve
    /// set when the generator ships built-in (<paramref name="builtInPresent"/>). The generator is
    /// now part of the platform, so that <c>#r</c> is redundant — and RESOLVING it hard-fails on a
    /// deployed image: after <c>BakeMeshLocalFeed</c> was removed (#395) the mesh-local feed
    /// (<c>dist/packages</c>) is gone, so NuGet throws
    /// <c>"The local source '/app/dist/packages' doesn't exist"</c> and breaks every deployed scope
    /// node still carrying the legacy <c>#r</c> (the prod BalanceSheet failure). Behaviour is
    /// unchanged: the built-in generator still emits the <c>IScope&lt;,&gt;</c> implementations, and
    /// <see cref="RunSourceGenerators"/> already de-dups the generator itself (CS0101). When the
    /// built-in is somehow absent the <c>#r</c> is kept so the generator can still resolve via NuGet.
    /// Other package references are never touched.
    /// </summary>
    internal static void StripBuiltInScopeGeneratorRef(List<NuGetPackageReference> refs, bool builtInPresent)
    {
        if (builtInPresent)
            refs.RemoveAll(r => string.Equals(
                r.Id, BuiltInScopeGeneratorId, StringComparison.OrdinalIgnoreCase));
    }

    private static CSharpCompilation RunSourceGenerators(
        CSharpCompilation compilation, IReadOnlyList<string> generatorAssemblyPaths, ILogger logger, CancellationToken ct)
    {
        // Always include the built-in scope generator, plus any OTHER generator a node #r'd. Filter a
        // node's own `#r "nuget:MeshWeaver.BusinessRules.Generator"` OUT — otherwise the same generator
        // loads from two paths (built-in + baked) and runs twice → duplicate IScope<,> implementations
        // → CS0101. Legacy nodes that still carry that #r keep compiling (built-in supersedes it, and
        // AssembleCompilationInputs strips the #r from the NuGet resolve set so it never round-trips).
        IReadOnlyList<string> allPaths = BuiltInGeneratorPaths.Count == 0
            ? generatorAssemblyPaths
            : [.. BuiltInGeneratorPaths,
               .. generatorAssemblyPaths.Where(p => !string.Equals(
                   Path.GetFileName(p), BuiltInScopeGeneratorId + ".dll", StringComparison.OrdinalIgnoreCase))];
        if (allPaths.Count == 0)
            return compilation;
        var generators = SourceGeneratorLoader.Discover(allPaths, logger);
        if (generators.IsDefaultOrEmpty)
            return compilation;
        var driver = CSharpGeneratorDriver.Create(generators);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out _, ct);
        return (CSharpCompilation)updated;
    }

    /// <summary>
    /// Compiles and emits assembly to a unique per-compile subdirectory, then VERIFIES the
    /// assembly actually landed on disk and re-emits when it did not. Each compile writes to
    /// {cacheDir}/{nodeName}_{ticks_hex}/ so V1 and V2 DLLs coexist on disk without overwriting.
    ///
    /// <para>🩹 Self-heal: on container deployments the cache directory is an ephemeral
    /// <c>/tmp/...</c>; a "successful" Roslyn emit can leave NO file on disk when the just-written
    /// assembly is evicted before the next read. That used to poison the NodeType permanently with
    /// a sticky "Compilation succeeded but DLL not found" error (prod AgenticPension/Datenpunkt,
    /// 2026-06-22) — the grain never recompiled. We now re-emit the lost artifact (a genuine compile
    /// error is NOT retried) and, if it still cannot be persisted, surface a clear, loud failure
    /// instead of a silent poison. See <see cref="EmitToDiskWithRetry"/>.</para>
    /// </summary>
    private Task<string> CompileToDiskAsync(CSharpCompilation compilation, string nodeName, string nodePath, CancellationToken ct)
        => Task.FromResult(EmitToDiskWithRetry(
            cacheService.CacheDirectory, nodeName, DiskEmitAttempts, logger,
            releaseDir =>
            {
                var dllPath = Path.Combine(releaseDir, $"{nodeName}.dll");
                var pdbPath = Path.Combine(releaseDir, $"{nodeName}.pdb");
                var xmlDocPath = Path.Combine(releaseDir, $"DynamicNode_{nodeName}.xml");

                using (var dllStream = File.Create(dllPath))
                using (var pdbStream = File.Create(pdbPath))
                using (var xmlDocStream = File.Create(xmlDocPath))
                {
                    var emitOptions = new EmitOptions(
                        debugInformationFormat: DebugInformationFormat.PortablePdb,
                        pdbFilePath: pdbPath);

                    var emitResult = compilation.Emit(
                        dllStream, pdbStream, xmlDocumentationStream: xmlDocStream,
                        options: emitOptions, cancellationToken: ct);

                    if (!emitResult.Success)
                    {
                        // Deterministic compile error — propagates straight out of the retry loop.
                        var errorMessage = FormatCompileFailure(nodePath, emitResult.Diagnostics);
                        logger.LogError("{ErrorMessage}", errorMessage);
                        throw new CompilationException(nodePath, errorMessage);
                    }
                }
                // Streams flushed + closed here, before EmitToDiskWithRetry verifies the file.
                return dllPath;
            }));

    /// <summary>
    /// Number of times <see cref="EmitToDiskWithRetry"/> re-emits when a "successful" Roslyn
    /// emit leaves no assembly on disk (ephemeral-cache eviction). Three attempts recover a
    /// transient lost write while still failing fast on a genuinely unwritable cache directory.
    /// </summary>
    internal const int DiskEmitAttempts = 3;

    /// <summary>
    /// Emits to a fresh per-attempt subdirectory under <paramref name="cacheDirectory"/> and
    /// confirms the assembly actually persisted, re-emitting up to <paramref name="maxAttempts"/>
    /// times when the DLL is missing or empty afterward. <paramref name="emitToReleaseDir"/> runs
    /// the real Roslyn emit into the supplied directory and returns the DLL path it wrote; it may
    /// throw <see cref="CompilationException"/> for a genuine compile error, which propagates
    /// immediately (NEVER retried — only a lost/empty artifact triggers a re-emit). Extracted and
    /// <c>internal</c> so the lost-write self-heal is unit-testable without a real flaky filesystem.
    /// </summary>
    internal static string EmitToDiskWithRetry(
        string cacheDirectory,
        string nodeName,
        int maxAttempts,
        ILogger logger,
        Func<string, string> emitToReleaseDir)
    {
        string? lastDllPath = null;

        static void TryDeleteDir(string dir)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* best-effort cleanup */ }
        }

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var timestamp = DateTimeOffset.UtcNow.Ticks.ToString("x");
            // Unique published name. Discovery orders by the dir's LastWriteTime, NOT by parsing the
            // ticks out of the name (see TryGetLatestCachedDllPath), so a GUID suffix guarantees the
            // atomic Directory.Move below never collides — on a coarse clock or two rapid compiles —
            // while still matching the `{nodeName}_*` glob.
            var releaseDir = Path.Combine(cacheDirectory, $"{nodeName}_{timestamp}_{Guid.NewGuid():N}");
            lastDllPath = Path.Combine(releaseDir, $"{nodeName}.dll");

            // 🚨 Emit into a STAGING dir whose name does NOT match the `{nodeName}_*` discovery glob
            // (TryGetLatestCachedDllPath), then atomically publish it by renaming to the discoverable
            // name only AFTER the DLL is fully written + verified. The DLL file exists at 0 bytes and
            // grows during compilation.Emit (File.Create + Emit is NOT atomic); without staging, a
            // concurrent reader can discover the half-written DLL and LoadFromAssemblyPath a truncated
            // image → a native crash (SIGSEGV) or a BadImageFormat that deletes the artifact and churns
            // the compile. A directory rename on the same filesystem is atomic, so a reader sees either
            // nothing or the COMPLETE artifact.
            var stagingDir = Path.Combine(cacheDirectory, $".staging-{nodeName}-{timestamp}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(stagingDir);

            // The real emit (a genuine compile error throws straight through — never retried). Discard
            // the half-written staging dir first so a failed emit leaves no partial artifact behind (the
            // old code leaked a glob-discoverable `{nodeName}_{ticks}` dir here — the same hazard).
            string stagedDllPath;
            try
            {
                stagedDllPath = emitToReleaseDir(stagingDir);
            }
            catch
            {
                TryDeleteDir(stagingDir);
                throw;
            }

            // Confirm the bytes are genuinely on disk, then atomically publish. EVERY fault here is a
            // RETRYABLE publish failure — an ephemeral-cache eviction racing the size read, or a
            // transient rename IO error — so discard staging and re-emit rather than aborting the compile.
            try
            {
                if (File.Exists(stagedDllPath) && new FileInfo(stagedDllPath).Length > 0)
                {
                    Directory.Move(stagingDir, releaseDir);
                    return lastDllPath;
                }

                logger.LogWarning(
                    "Emit for {NodeName} reported success but the assembly was missing or empty at " +
                    "{DllPath} after flush (attempt {Attempt}/{Max}); re-emitting.",
                    nodeName, stagedDllPath, attempt, maxAttempts);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex,
                    "Publishing the emitted assembly for {NodeName} failed (attempt {Attempt}/{Max}); re-emitting.",
                    nodeName, attempt, maxAttempts);
            }

            // Drop the staging directory so the retry starts clean.
            TryDeleteDir(stagingDir);
        }

        throw new CompilationException(nodeName,
            $"Compilation succeeded but the emitted assembly for '{nodeName}' could not be persisted to " +
            $"'{cacheDirectory}' after {maxAttempts} attempts (last target '{lastDllPath}'). The compilation " +
            "host's cache directory may be read-only or evicting files.");
    }

    /// <summary>
    /// Compiles and loads assembly directly to memory (no disk I/O).
    /// </summary>
    private void CompileToMemory(CSharpCompilation compilation, string nodeName, string nodePath, CancellationToken ct)
    {
        using var dllStream = new MemoryStream();
        using var pdbStream = new MemoryStream();

        var emitOptions = new EmitOptions(
            debugInformationFormat: DebugInformationFormat.PortablePdb);

        var emitResult = compilation.Emit(dllStream, pdbStream, options: emitOptions, cancellationToken: ct);

        if (!emitResult.Success)
        {
            var errorMessage = FormatCompileFailure(nodePath, emitResult.Diagnostics);
            logger.LogError("{ErrorMessage}", errorMessage);
            throw new CompilationException(nodePath, errorMessage);
        }

        // Load assembly from bytes immediately
        var assemblyBytes = dllStream.ToArray();
        var pdbBytes = pdbStream.ToArray();
        cacheService.LoadAssemblyFromBytes(nodeName, assemblyBytes, pdbBytes);
    }

    /// <summary>
    /// Compiles a node type to a specific release folder.
    /// This method is thread-safe and multi-process safe when used with CompilationLock.
    /// The caller is responsible for acquiring the lock before calling this method.
    /// </summary>
    /// <param name="release">The NodeTypeRelease containing all compilation inputs.</param>
    /// <param name="node">The MeshNode being compiled.</param>
    /// <param name="releaseFolder">Target folder for the compiled assembly.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Compilation result with assembly location and configurations.</returns>
    internal async Task<NodeCompilationResult?> CompileToReleaseAsync(
        NodeTypeRelease release,
        MeshNode node,
        string releaseFolder,
        CancellationToken ct = default)
    {
        var sanitizedPath = release.GetSanitizedPath();

        logger.LogInformation("Compiling {NodePath} to release folder {ReleaseFolder}", node.Path, releaseFolder);

        // Ensure release folder exists
        Directory.CreateDirectory(releaseFolder);

        var dllPath = Path.Combine(releaseFolder, $"{sanitizedPath}.dll");
        var pdbPath = Path.Combine(releaseFolder, $"{sanitizedPath}.pdb");
        var sourcePath = Path.Combine(releaseFolder, $"{sanitizedPath}.cs");
        var xmlDocPath = Path.Combine(releaseFolder, $"{sanitizedPath}.xml");

        ct.ThrowIfCancellationRequested();

        // Generate source code
        var codeConfig = string.IsNullOrEmpty(release.Code) ? null : new CodeConfiguration { Code = release.Code };
        var rawSource = _attributeGenerator.GenerateAttributeSource(node, codeConfig, release.HubConfiguration, release.ContentCollections);

        // Strip #r "nuget:..." directives — Roslyn compilation (unlike scripting) does not process them.
        var (source, extractedRefs) = NuGetDirectiveParser.Extract(rawSource);
        // 🚨 Same legacy-#r strip as AssembleCompilationInputs: a node Source still carrying
        // `#r "nuget:MeshWeaver.BusinessRules.Generator"` must NOT reach the NuGet resolver —
        // the generator ships built-in, and the mesh-local feed it used to resolve from is gone
        // (#395), so resolving hard-fails ("The local source '…/dist/packages' doesn't exist").
        // This RELEASE-folder compile path previously lacked the strip: the PensionFund sample
        // (legacy #r intact) compiled green on CI only because the workflow's pack step happened
        // to recreate the feed — the strip in the OTHER compile path never covered this one.
        var nugetRefList = extractedRefs.ToList();
        StripBuiltInScopeGeneratorRef(nugetRefList, builtInPresent: BuiltInGeneratorPaths.Count > 0);
        var nugetRefs = nugetRefList.ToArray();
        IEnumerable<MetadataReference> references = _references;
        IReadOnlyList<string> probingDirs = [];
        IReadOnlyList<string> nugetAssemblyPaths = [];
        if (nugetRefs.Length > 0)
        {
            var resolved = await nugetResolver.ResolveAsync(nugetRefs, targetFramework: null, ct);
            references = _references.Concat(
                resolved.AssemblyPaths.Select(p => MetadataReference.CreateFromFile(p)));
            probingDirs = resolved.ProbingDirectories;
            nugetAssemblyPaths = resolved.AssemblyPaths;
        }

        // Write source file for debugging
        if (_cacheOptions.EnableSourceDebugging)
        {
            await File.WriteAllTextAsync(sourcePath, source, ct);
            logger.LogDebug("Wrote source file: {SourcePath}", sourcePath);
        }

        // Parse with source path embedded for PDB source linking
        var sourceText = Microsoft.CodeAnalysis.Text.SourceText.From(source, System.Text.Encoding.UTF8);
        var parseOptions = new CSharpParseOptions(documentationMode: DocumentationMode.Diagnose);
        var syntaxTree = CSharpSyntaxTree.ParseText(
            sourceText,
            parseOptions,
            path: _cacheOptions.EnableSourceDebugging ? sourcePath : "",
            cancellationToken: ct);

        var assemblyName = sanitizedPath;

        var compilation = RunSourceGenerators(CSharpCompilation.Create(
            assemblyName,
            syntaxTrees: [syntaxTree],
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithOptimizationLevel(OptimizationLevel.Debug)
                .WithPlatform(Platform.AnyCpu)), nugetAssemblyPaths, logger, ct);

        // Emit to release folder
        await using var dllStream = File.Create(dllPath);
        await using var pdbStream = File.Create(pdbPath);
        await using var xmlDocStream = File.Create(xmlDocPath);

        var emitOptions = new EmitOptions(
            debugInformationFormat: DebugInformationFormat.PortablePdb,
            pdbFilePath: pdbPath);

        var emitResult = compilation.Emit(dllStream, pdbStream, xmlDocumentationStream: xmlDocStream, options: emitOptions, cancellationToken: ct);

        if (!emitResult.Success)
        {
            // Clean up partial files on failure
            await dllStream.DisposeAsync();
            await pdbStream.DisposeAsync();
            await xmlDocStream.DisposeAsync();

            try { Directory.Delete(releaseFolder, recursive: true); } catch { /* ignore */ }

            var errorMessage = FormatCompileFailure(node.Path, emitResult.Diagnostics);
            logger.LogError("{ErrorMessage}", errorMessage);
            throw new CompilationException(node.Path, errorMessage);
        }

        // Close streams before writing metadata
        await dllStream.DisposeAsync();
        await pdbStream.DisposeAsync();
        await xmlDocStream.DisposeAsync();

        // Write the NodeTypeRelease as release.json (contains all metadata)
        var metadataPath = Path.Combine(releaseFolder, "release.json");
        var metadataJson = JsonSerializer.Serialize(release, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(metadataPath, metadataJson, ct);

        // Persist NuGet probing directories alongside the release so the load context
        // can probe for transitive dependencies at load time.
        if (probingDirs.Count > 0)
        {
            var probingPath = Path.Combine(releaseFolder, "probing.json");
            var probingJson = JsonSerializer.Serialize(probingDirs);
            await File.WriteAllTextAsync(probingPath, probingJson, ct);
        }

        logger.LogInformation("Successfully compiled {NodePath} to {DllPath}", node.Path, dllPath);

        // Load and extract configurations
        return await LoadAndExtractConfigurationsFromReleaseAsync(release, releaseFolder, ct);
    }

    /// <summary>
    /// Loads an assembly from a release folder and extracts NodeTypeConfigurations.
    /// </summary>
    internal async Task<NodeCompilationResult?> LoadAndExtractConfigurationsFromReleaseAsync(
        NodeTypeRelease release,
        string releaseFolder,
        CancellationToken _)
    {
        var sanitizedPath = release.GetSanitizedPath();
        var dllPath = Path.Combine(releaseFolder, $"{sanitizedPath}.dll");

        try
        {
            // PIN across load + the GetTypes/MeshNodeProviderAttribute scan below — same
            // unload-during-scan race as CompileResultFromAssembly (TypeLoadException 'format is
            // invalid'). Released when the method returns; Dispose() drains pins before Unload().
            var context = cacheService.GetOrCreateLoadContextForRelease(release, releaseFolder);
            using var scanPin = context.Pin();
            var assembly = context.LoadNodeAssembly();
            if (assembly == null)
            {
                logger.LogWarning("Failed to load assembly from {DllPath}", dllPath);
                return new NodeCompilationResult(dllPath, []);
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

            logger.LogDebug("Extracted {Count} NodeTypeConfigurations from {DllPath}", configurations.Count, dllPath);
            return new NodeCompilationResult(dllPath, configurations);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to extract NodeTypeConfigurations from {DllPath}", dllPath);
            return new NodeCompilationResult(dllPath, []);
        }
    }
}

/// <summary>
/// Exception thrown when compilation fails.
/// </summary>
public class CompilationException : Exception
{
    /// <summary>The mesh path of the node whose compilation failed.</summary>
    public string NodePath { get; }

    /// <summary>
    /// Initializes a new instance of the exception for a failed compilation.
    /// </summary>
    /// <param name="nodePath">The mesh path of the node whose compilation failed.</param>
    /// <param name="message">The error message describing the failure.</param>
    public CompilationException(string nodePath, string message)
        : base(message)
    {
        NodePath = nodePath;
    }

    /// <summary>
    /// Initializes a new instance of the exception for a failed compilation, wrapping an
    /// underlying cause.
    /// </summary>
    /// <param name="nodePath">The mesh path of the node whose compilation failed.</param>
    /// <param name="message">The error message describing the failure.</param>
    /// <param name="innerException">The underlying exception that caused the failure.</param>
    public CompilationException(string nodePath, string message, Exception innerException)
        : base(message, innerException)
    {
        NodePath = nodePath;
    }
}
