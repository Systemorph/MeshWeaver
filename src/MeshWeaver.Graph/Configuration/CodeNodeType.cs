using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Kernel;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Provides configuration for Code nodes in the graph.
/// Code nodes represent source code files attached to NodeType definitions.
/// </summary>
public static class CodeNodeType
{
    /// <summary>
    /// The NodeType value used to identify code nodes.
    /// </summary>
    public const string NodeType = "Code";

    /// <summary>
    /// Registers the built-in "Code" MeshNode on the mesh builder.
    /// </summary>
    public static TBuilder AddCodeType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(NodeType);
        builder.ConfigureNodeTypeAccess(a => a.WithPublicRead(NodeType));
        return builder;
    }

    /// <summary>
    /// The sub-namespace for source code files. Code nodes live under
    /// <c>{NodeTypePath}/Source/</c> alongside (not inside) their parent NodeType.
    /// This is a content folder, not a satellite namespace.
    /// </summary>
    public const string SourceSubNamespace = "Source";

    /// <summary>
    /// The sub-namespace for test code files. Tests live under
    /// <c>{NodeTypePath}/Test/</c> alongside (not inside) their parent NodeType.
    /// This is a content folder, not a satellite namespace.
    /// </summary>
    public const string TestSubNamespace = "Test";

    /// <summary>
    /// Creates a MeshNode definition for the Code node type.
    /// Code nodes are primary content (source files), not satellite metadata â€”
    /// they are browsable, addressable, and first-class children of their NodeType.
    /// </summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Code",
        Icon = "/static/NodeTypeIcons/code.svg",
        IsSatelliteType = false,
        ExcludeFromContext = new HashSet<string> { "search", "create" },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<CodeConfiguration>())
            .AddDefaultLayoutAreas()
            .AddCodeViews()
            .WithHandler<ExecuteScriptRequest>(HandleExecuteScript)
    };

    /// <summary>
    /// The address a Code node's <see cref="SubmitCodeRequest"/> is dispatched to, by language.
    /// C# (the default) — and any non-executing language (json/sql/markdown) — has no separate runtime,
    /// so it runs in-process on the Activity hub's Roslyn kernel (its own <paramref name="activityPath"/>).
    /// A foreign language routes to the stable address where its worker participant registers over the
    /// mesh (the gRPC bridge), which executes and patches the SAME ActivityLog so output surfaces
    /// identically regardless of language:
    /// <list type="bullet">
    ///   <item><c>python</c> → <c>py/python-kernel</c> (clients/python: <c>meshweaver.worker</c>).</item>
    ///   <item><c>javascript</c> / <c>typescript</c> → <c>node/node-kernel</c> (clients/typescript worker).</item>
    /// </list>
    /// </summary>
    public static Address ResolveKernelAddress(string? language, string activityPath) =>
        language?.ToLowerInvariant() switch
        {
            "python" => new Address("py", "python-kernel"),
            "javascript" or "typescript" => new Address("node", "node-kernel"),
            _ => new Address(activityPath),
        };

    /// <summary>
    /// True when <paramref name="language"/> runs on a foreign-language worker (a connected gate)
    /// rather than in-process on the Activity hub's Roslyn kernel — i.e. when
    /// <see cref="ResolveKernelAddress"/> routes to a stable <c>py/*</c> or <c>node/*</c> address.
    /// The in-process C# path always writes a terminal status (its kernel calls
    /// <c>ActivityLogLogger.Complete</c>); a foreign run reaches a terminal status ONLY if a worker
    /// is connected, so the dispatch must reconcile the ActivityLog when one is not.
    /// </summary>
    private static bool IsForeignLanguage(string? language) =>
        language?.ToLowerInvariant() is "python" or "javascript" or "typescript";

    /// <summary>Log channel for every dispatch-side diagnostic emitted by this type.</summary>
    private const string LoggerCategory = "MeshWeaver.Graph.CodeNodeType";

    /// <summary>
    /// Wall-clock budget for the <see cref="PartitionDefinition"/> lookup that decides where a
    /// run's Activity node is written. The lookup is a LIVE workspace query
    /// (<see cref="PartitionRegistry.GetPartition"/>) and a live query that never converges
    /// simply never emits — before #841 the dispatch chain sat on that non-emission forever:
    /// no Activity node, no response, no log line, nothing at Warning+. The budget converts
    /// that silent stop into a <see cref="TimeoutException"/> that reaches the caller AND the
    /// logger. It is a diagnosability boundary, not a retry: nothing is re-attempted.
    /// </summary>
    private static readonly TimeSpan ActivityParentLookupTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Resolves the namespace a script run's <c>Activity</c> node is written under. Three
    /// layers, in order: the Code node's own <see cref="CodeConfiguration.ActivityParentPath"/>,
    /// the partition's <see cref="PartitionDefinition.DefaultActivityParentPath"/>, then the
    /// partition root. The <c>{viewer}</c> sentinel at either configured layer expands to
    /// <paramref name="viewerHome"/> (falling back to <paramref name="partitionRoot"/> when no
    /// viewer identity is available).
    ///
    /// <para>Extracted so the ONE way this can stop without producing an answer — a partition
    /// lookup that never emits — is pinned by a test rather than discovered in production. The
    /// stream is bounded by <paramref name="lookupBudget"/> and an empty completion resolves to
    /// "no definition" instead of completing without an emission; either way exactly one of
    /// <c>OnNext</c> / <c>OnError</c> always reaches the subscriber.</para>
    /// </summary>
    /// <param name="partitionDefinitions">Live stream of the partition's definition (null when none).</param>
    /// <param name="codeActivityParentPath">The Code node's configured override, if any.</param>
    /// <param name="viewerHome">The calling user's home partition, used to expand <c>{viewer}</c>.</param>
    /// <param name="partitionRoot">The Code node's own partition — the default and the fallback.</param>
    /// <param name="lookupBudget">Wall-clock budget for the partition lookup.</param>
    public static IObservable<string> ResolveActivityParent(
        IObservable<PartitionDefinition?> partitionDefinitions,
        string? codeActivityParentPath,
        string? viewerHome,
        string partitionRoot,
        TimeSpan lookupBudget) =>
        partitionDefinitions
            .Take(1)
            // A source that COMPLETES without emitting means "no partition definition", which
            // is the default case — not a reason to end the dispatch chain in silence.
            .DefaultIfEmpty()
            .Timeout(lookupBudget, Observable.Throw<PartitionDefinition?>(
                new TimeoutException(
                    $"Activity-parent resolution stalled: the PartitionDefinition lookup for "
                    + $"partition '{partitionRoot}' produced no answer within "
                    + $"{lookupBudget.TotalSeconds:F0}s. The script was NOT started and no Activity "
                    + "node was created.")))
            .Select(partition =>
            {
                var unresolved = codeActivityParentPath ?? partition?.DefaultActivityParentPath;
                return unresolved switch
                {
                    null => partitionRoot,
                    "{viewer}" when !string.IsNullOrEmpty(viewerHome) => viewerHome!,
                    "{viewer}" => partitionRoot,
                    var p => p
                };
            });

    /// <summary>
    /// Runs the Code node's own script. Reads the local workspace for the node's
    /// <see cref="CodeConfiguration"/>, validates <c>IsExecutable</c>, dispatches
    /// <see cref="SubmitCodeRequest"/> to the <see cref="ResolveKernelAddress">language kernel</see>,
    /// and posts an <see cref="ExecuteScriptResponse"/> with the submission id + output-area
    /// reference so callers can subscribe to live progress without ever addressing
    /// the kernel themselves.
    /// </summary>
    private static IMessageDelivery HandleExecuteScript(
        IMessageHub hub, IMessageDelivery<ExecuteScriptRequest> request)
    {
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(LoggerCategory);

        // 🚨 #841 — THE dispatch sink. Every branch that ends this dispatch without an
        // Activity node goes through one of these two, and both do BOTH things: answer
        // the caller AND leave a Warning+ trace. Before the fix the branches only posted
        // a response, and the canonical caller (MCP execute_script) never observed it —
        // a dispatch that produced no Activity node produced no evidence anywhere, which
        // is exactly what made this issue undiagnosable in production.
        void RefuseDispatch(string error)
        {
            logger?.LogWarning(
                "ExecuteScript refused on {Hub}: {Error} No Activity node was created.",
                hub.Address, error);
            hub.Post(new ExecuteScriptResponse { Success = false, Error = error },
                o => o.ResponseFor(request));
        }

        // Same sink for a FAULT rather than a refusal: the exception TYPE and full STACK
        // go to the logger, and the wire error keeps the type name. Reducing an abort to
        // `.Message` is what made the sibling compile NRE (#890) undiagnosable — see #892.
        void FaultDispatch(Exception exception, string context)
        {
            logger?.LogError(exception,
                "ExecuteScript faulted on {Hub}: {Context}. No Activity node was created.",
                hub.Address, context);
            hub.Post(
                new ExecuteScriptResponse
                {
                    Success = false,
                    Error = $"{context}: {exception.GetType().Name}: {exception.Message}"
                },
                o => o.ResponseFor(request));
        }

        // One-shot read of this hub's own MeshNode via GetDataRequest (posted to self) â€”
        // true request/response, no SubscribeRequest+immediate-unsubscribe. Handler
        // itself returns Processed() immediately; the callback below fires when the
        // response arrives.
        // Strict timeout (the default): a stalled read must NOT be answered as
        // "Not executable" — that tells the caller the script is misconfigured when in
        // fact the mesh never replied, and it is unfixable by the user. The error sink
        // below turns the timeout into a truthful failure response (and, being a
        // handler, gives the OnError somewhere to land instead of the timer thread).
        hub.GetMeshNode(hub.Address.ToString())
            .Subscribe(node =>
            {
                // Three distinct facts, three distinct verdicts. Folding them into one
                // "Not executable" line told a caller whose node had merely become
                // unreadable that their script was misconfigured.
                if (node is null)
                {
                    RefuseDispatch(
                        $"No readable node at '{hub.Address}': it does not exist, or the caller "
                        + "may not read it.");
                    return;
                }

                if (node.Content is not CodeConfiguration code)
                {
                    RefuseDispatch(
                        $"Node '{hub.Address}' carries {node.Content?.GetType().Name ?? "no content"}, "
                        + "not a CodeConfiguration — there is nothing to execute.");
                    return;
                }

                if (!code.IsExecutable)
                {
                    RefuseDispatch(
                        $"Not executable: '{hub.Address}' has CodeConfiguration.IsExecutable = false.");
                    return;
                }

                var submissionId = request.Message.SubmissionId ?? Guid.NewGuid().ToString("N");

                // Create an ActivityLog MeshNode for this run â€” scripts'
                // Log.LogInformation(...) calls will append to it, and callers
                // subscribe via GetRemoteStream<MeshNode, MeshNodeReference> to
                // watch progress live. Created via IMeshService.CreateNode so it
                // flows through the standard create pipeline (RLS, persistence).
                //
                // The Activity hub also HOSTS the kernel: ActivityNodeType.HubConfiguration
                // adds AddKernelSubHubHandlers, so SubmitCodeRequest sent to the activity
                // path lands inside the activity's own action block. Replies route
                // through the standard MeshNode chain â€” no `kernel/*` standalone hub.
                // Activities live under {ActivityParentPath}/_Activity/{guid}.
                // ActivityParentPath defaults to the partition root (the user's home)
                // when null â€” every script run shows up in the user's activity feed,
                // and the satellite path is shallow enough that routing materialises
                // it reliably. The originating Code node is preserved on MainNode
                // + ActivityLog.HubPath, so the link back is intact regardless of
                // where the activity is stored.
                // Resolve where to write the activity. Three layers, in order:
                //   1. Code-node config: code.ActivityParentPath wins if set.
                //   2. Partition-level: PartitionDefinition.DefaultActivityParentPath
                //      via the Replay-cached PartitionRegistry observable.
                //   3. Default: the partition root (first segment of the Code path).
                // The "{viewer}" sentinel at any layer expands to the calling
                // user's home (so docs partition can route runs into whoever's
                // browsing them). Resolution composes into the create-activity
                // chain â€” the per-partition lookup is async (workspace query)
                // so we keep it observable end-to-end.
                var accessService = hub.ServiceProvider.GetService<MeshWeaver.Messaging.AccessService>();
                var viewerHome = accessService?.Context?.ObjectId
                                 ?? accessService?.CircuitContext?.ObjectId;
                var partitionRoot = hub.Address.Segments.Length > 0 ? hub.Address.Segments[0] : hub.Address.Path;

                var partitionRegistry = hub.ServiceProvider.GetService<PartitionRegistry>();
                var partitionDefaultStream = partitionRegistry?.GetPartition(partitionRoot)
                    ?? Observable.Return<PartitionDefinition?>(null);

                var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
                ResolveActivityParent(
                        partitionDefaultStream,
                        code.ActivityParentPath,
                        viewerHome,
                        partitionRoot,
                        ActivityParentLookupTimeout)
                    .SelectMany(activityParentPath =>
                    {
                        var activityId = submissionId;
                        var activityNamespace = $"{activityParentPath}/_Activity";
                        var activityPath = $"{activityNamespace}/{activityId}";
                        // MainNode points to the activity's PARENT â€” the user's
                        // home or configured target. SatelliteAccessRule delegates
                        // access to MainNode, so this must be a node the viewer
                        // can read. ActivityLog.HubPath preserves the originating
                        // Code node so the link back is intact.
                        var activityNode = new MeshNode(activityId, activityNamespace)
                        {
                            Name = $"Script run {activityId[..Math.Min(8, activityId.Length)]}",
                            NodeType = ActivityNodeType.NodeType,
                            MainNode = activityParentPath,
                            State = MeshNodeState.Active,
                            Content = new ActivityLog("ScriptExecution")
                            {
                                Id = activityId,
                                HubPath = hub.Address.Path,
                                Status = ActivityStatus.Running
                            }
                        };
                        return meshService.CreateNode(activityNode)
                            .Select(created => (created, activityPath));
                    })
                    .Subscribe(
                        tuple =>
                        {
                            var (_, activityPath) = tuple;
                            // Node created. Fire SubmitCodeRequest at the Activity
                            // hub (which now hosts the kernel handlers). Forward
                            // the caller-supplied Inputs so the script can read
                            // them off the `Inputs` global â€” the canonical channel
                            // for script-templated operations (export, importâ€¦).
                            // C# runs in-process on the Activity hub's Roslyn kernel; a foreign language
                            // routes to the worker that owns its runtime (see ResolveKernelAddress).
                            var submitTarget = ResolveKernelAddress(code.Language, activityPath);
                            var submit = new SubmitCodeRequest(code.Code ?? string.Empty)
                            {
                                Id = submissionId,
                                ActivityLogPath = activityPath,
                                Language = string.IsNullOrWhiteSpace(code.Language) ? "csharp" : code.Language,
                                Inputs = request.Message.Inputs
                            };

                            if (IsForeignLanguage(code.Language))
                            {
                                // Foreign-language run: the script executes on a connected worker
                                // (py/python-kernel, node/node-kernel), which patches the ActivityLog
                                // to a terminal status when done. But submitTarget is a STREAM-ROUTED
                                // address: on the distributed portal a post to it with NO subscriber is
                                // silently absorbed by the Orleans memory stream (no DeliveryFailure),
                                // so with no worker connected the run would stay Running forever — the
                                // opposite of the "nothing hangs" promise in
                                // Doc/Architecture/PythonCodeNodes. Fail fast on a presence miss, and
                                // Observe the response so a monolith NotFound / a mid-flight disconnect
                                // still reconciles the ActivityLog instead of parking.
                                var presence = hub.ServiceProvider.GetService<IParticipantPresence>();
                                if (presence is not null && !presence.IsConnected(submitTarget))
                                {
                                    FailActivity(hub, activityPath,
                                        $"No {submit.Language} worker connected at '{submitTarget}'. The " +
                                        "language gate is not running in this deployment — see " +
                                        "Doc/Architecture/PythonCodeNodes.");
                                }
                                else
                                {
                                    hub.Observe<SubmitCodeResponse>(submit, o => o.WithTarget(submitTarget))
                                        .Take(1)
                                        .Subscribe(
                                            // Success or a handled script error: the worker has already
                                            // patched the ActivityLog to its terminal status — nothing to do.
                                            _ => { },
                                            // The delivery faulted (target unreachable / disconnected)
                                            // before any terminal write — reconcile so the run ends.
                                            ex => FailActivity(hub, activityPath,
                                                $"{submit.Language} run failed: "
                                                + $"{ex.GetType().Name}: {ex.Message}", ex));
                                }
                            }
                            else
                            {
                                // C# in-process: the Activity hub's Roslyn kernel always calls
                                // ActivityLogLogger.Complete, so the ActivityLog can never hang here.
                                hub.Post(submit, o => o.WithTarget(submitTarget));
                            }

                            // Stamp Last{ExecutedAt,ExecutedBy,ActivityPath} onto
                            // the Code MeshNode so the Content area can show
                            // "Last executed: <when> by <who>" and embed the last
                            // activity's Progress area for the Output pane â€”
                            // without separately querying activity children.
                            try
                            {
                                var workspace = hub.GetWorkspace();
                                var stampLogger = logger;
                                // .Subscribe is mandatory: Update is Observable.Create â€”
                                // the partition write only runs on Subscribe. Discarding
                                // the observable silently drops the stamp.
                                workspace.GetMeshNodeStream().Update(curr =>
                                    curr.Content is CodeConfiguration cfg
                                        ? curr with
                                        {
                                            Content = cfg with
                                            {
                                                LastExecutedAt = DateTimeOffset.UtcNow,
                                                LastExecutedBy = viewerHome,
                                                LastActivityPath = activityPath,
                                                // Fingerprint what was ACTUALLY submitted (submit.Code /
                                                // submit.Language), not `cfg` — the two agree today, but
                                                // stamping the node's current content would silently
                                                // record "up to date" for a run of something else the
                                                // moment any future path submits a transformed source.
                                                LastExecutedCodeHash =
                                                    CodeFingerprint.Of(submit.Code, submit.Language)
                                            }
                                        }
                                        : curr)
                                    .Subscribe(
                                        _ => { },
                                        ex => stampLogger?.LogWarning(ex,
                                            "CodeNodeType: stamp UpdateMeshNode failed for {Hub}",
                                            hub.Address));
                            }
                            catch (Exception stampEx)
                            {
                                // The workspace might not be ready (cold-start race) — the
                                // missing stamp is a UI nicety, not a correctness invariant,
                                // and the Activity log IS written. But a bare `catch {}` here
                                // hid whatever went wrong; name it, with type + stack.
                                logger?.LogWarning(stampEx,
                                    "CodeNodeType: could not stamp last-execution fields on {Hub} "
                                    + "(run {Activity} is unaffected)", hub.Address, activityPath);
                            }

                            hub.Post(
                                new ExecuteScriptResponse
                                {
                                    Success = true,
                                    SubmissionId = submissionId,
                                    OutputAreaReference = submissionId,
                                    ActivityLog = activityPath
                                },
                                o => o.ResponseFor(request));
                        },
                        // Covers BOTH pre-create stages: a stalled activity-parent resolution
                        // (TimeoutException from ResolveActivityParent) and a refused/failed
                        // CreateNode (UnauthorizedAccessException / InvalidOperationException
                        // from IMeshService). Previously this sink posted a `.Message`-only
                        // response that nobody observed and logged NOTHING — the exact shape
                        // of #841's "no Activity node, nothing at Warning+".
                        err => FaultDispatch(err, "Could not start the script run"));
            },
            // The self-read failed (timeout / access denial). Answer the caller with what
            // actually happened instead of the misleading "Not executable" verdict above,
            // and log it — a stalled mesh read is an infrastructure fault, not a config error.
            readError => FaultDispatch(readError, "Could not read the Code node to execute"));
        return request.Processed();
    }

    /// <summary>
    /// Reconcile a foreign-language run's ActivityLog to a terminal <see cref="ActivityStatus.Failed"/>
    /// when the worker never wrote one — no worker was connected, or the submission delivery faulted.
    /// Writes through the external-node stream handle (routes to the ActivityLog's owning hub and
    /// carries the caller's AccessContext across the Subscribe boundary); <see cref="ActivityLog.Fail"/>
    /// appends the error and stamps <c>End</c>, so the Output pane leaves "Running…" with the reason.
    /// </summary>
    private static void FailActivity(IMessageHub hub, string activityPath, string error,
        Exception? exception = null)
    {
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger(LoggerCategory);
        // Log the REASON, not only a failure to record it: the activity content is visible
        // to whoever opens that node, but an operator grepping stdout for a run that never
        // produced output had nothing to find (#841). When a delivery fault drove us here,
        // the exception object carries type + stack to the logger — never just `.Message`.
        if (exception is null)
            logger?.LogWarning(
                "CodeNodeType: failing run {Activity} dispatched from {Hub}: {Error}",
                activityPath, hub.Address, error);
        else
            logger?.LogWarning(exception,
                "CodeNodeType: failing run {Activity} dispatched from {Hub}: {Error}",
                activityPath, hub.Address, error);
        hub.GetWorkspace().GetMeshNodeStream(activityPath).Update(curr =>
            curr.Content is ActivityLog log
                ? curr with { Content = log.Fail(error) }
                : curr)
            .Subscribe(
                _ => { },
                ex => logger?.LogWarning(ex,
                    "CodeNodeType: failed to reconcile ActivityLog {Activity} to Failed", activityPath));
    }
}
