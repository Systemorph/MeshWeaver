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
    /// Code nodes are primary content (source files), not satellite metadata —
    /// they are browsable, addressable, and first-class children of their NodeType.
    /// </summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Code",
        Icon = "/static/NodeTypeIcons/code.svg",
        IsSatelliteType = false,
        ExcludeFromContext = new HashSet<string> { "search", "create" },
        AssemblyLocation = typeof(CodeNodeType).Assembly.Location,
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<CodeConfiguration>())
            .AddDefaultLayoutAreas()
            .AddCodeViews()
            .WithHandler<ExecuteScriptRequest>(HandleExecuteScript)
    };

    /// <summary>
    /// Runs the Code node's own script. Reads the local workspace for the node's
    /// <see cref="CodeConfiguration"/>, validates <c>IsExecutable</c>, dispatches
    /// <see cref="SubmitCodeRequest"/> to the internal kernel address, and posts
    /// an <see cref="ExecuteScriptResponse"/> with the submission id + output-area
    /// reference so callers can subscribe to live progress without ever addressing
    /// the kernel themselves.
    /// </summary>
    private static IMessageDelivery HandleExecuteScript(
        IMessageHub hub, IMessageDelivery<ExecuteScriptRequest> request)
    {
        // One-shot read of this hub's own MeshNode via GetDataRequest (posted to self) —
        // true request/response, no SubscribeRequest+immediate-unsubscribe. Handler
        // itself returns Processed() immediately; the callback below fires when the
        // response arrives.
        hub.GetMeshNode(hub.Address.ToString())
            .Subscribe(node =>
            {
                if (node?.Content is not CodeConfiguration code || !code.IsExecutable)
                {
                    hub.Post(
                        new ExecuteScriptResponse
                        {
                            Success = false,
                            Error = "Not executable (IsExecutable=false or content is not a CodeConfiguration)"
                        },
                        o => o.ResponseFor(request));
                    return;
                }

                var submissionId = request.Message.SubmissionId ?? Guid.NewGuid().ToString("N");

                // Create an ActivityLog MeshNode for this run — scripts'
                // Log.LogInformation(...) calls will append to it, and callers
                // subscribe via GetRemoteStream<MeshNode, MeshNodeReference> to
                // watch progress live. Created via IMeshService.CreateNode so it
                // flows through the standard create pipeline (RLS, persistence).
                //
                // The Activity hub also HOSTS the kernel: ActivityNodeType.HubConfiguration
                // adds AddKernelSubHubHandlers, so SubmitCodeRequest sent to the activity
                // path lands inside the activity's own action block. Replies route
                // through the standard MeshNode chain — no `kernel/*` standalone hub.
                // Activities live under {ActivityParentPath}/_Activity/{guid}.
                // ActivityParentPath defaults to the partition root (the user's home)
                // when null — every script run shows up in the user's activity feed,
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
                // chain — the per-partition lookup is async (workspace query)
                // so we keep it observable end-to-end.
                var accessService = hub.ServiceProvider.GetService<MeshWeaver.Messaging.AccessService>();
                var viewerHome = accessService?.Context?.ObjectId
                                 ?? accessService?.CircuitContext?.ObjectId;
                var partitionRoot = hub.Address.Segments.Length > 0 ? hub.Address.Segments[0] : hub.Address.Path;

                var partitionRegistry = hub.ServiceProvider.GetService<PartitionRegistry>();
                var partitionDefaultStream = partitionRegistry?.GetPartition(partitionRoot)
                    ?? Observable.Return<PartitionDefinition?>(null);

                var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
                partitionDefaultStream
                    .Take(1)
                    .Select(partition =>
                    {
                        var unresolved = code.ActivityParentPath ?? partition?.DefaultActivityParentPath;
                        return unresolved switch
                        {
                            null => partitionRoot,
                            "{viewer}" when !string.IsNullOrEmpty(viewerHome) => viewerHome!,
                            "{viewer}" => partitionRoot,
                            var p => p
                        };
                    })
                    .SelectMany(activityParentPath =>
                    {
                        var activityId = submissionId;
                        var activityNamespace = $"{activityParentPath}/_Activity";
                        var activityPath = $"{activityNamespace}/{activityId}";
                        // MainNode points to the activity's PARENT — the user's
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
                            // them off the `Inputs` global — the canonical channel
                            // for script-templated operations (export, import…).
                            hub.Post(
                                new SubmitCodeRequest(code.Code ?? string.Empty)
                                {
                                    Id = submissionId,
                                    ActivityLogPath = activityPath,
                                    Inputs = request.Message.Inputs
                                },
                                o => o.WithTarget(new Address(activityPath)));

                            // Stamp Last{ExecutedAt,ExecutedBy,ActivityPath} onto
                            // the Code MeshNode so the Content area can show
                            // "Last executed: <when> by <who>" and embed the last
                            // activity's Progress area for the Output pane —
                            // without separately querying activity children.
                            try
                            {
                                var workspace = hub.GetWorkspace();
                                var stampLogger = hub.ServiceProvider.GetService<ILoggerFactory>()
                                    ?.CreateLogger("MeshWeaver.Graph.CodeNodeType");
                                // .Subscribe is mandatory: Update is Observable.Create —
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
                                                LastActivityPath = activityPath
                                            }
                                        }
                                        : curr)
                                    .Subscribe(
                                        _ => { },
                                        ex => stampLogger?.LogWarning(ex,
                                            "CodeNodeType: stamp UpdateMeshNode failed for {Hub}",
                                            hub.Address));
                            }
                            catch
                            {
                                // Workspace might not be ready (cold-start race)
                                // — missing fields are a UI nicety, not a
                                // correctness invariant. Activity log still written.
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
                        err =>
                        {
                            hub.Post(
                                new ExecuteScriptResponse
                                {
                                    Success = false,
                                    Error = $"Failed to create ActivityLog node: {err.Message}"
                                },
                                o => o.ResponseFor(request));
                        });
            });
        return request.Processed();
    }
}
