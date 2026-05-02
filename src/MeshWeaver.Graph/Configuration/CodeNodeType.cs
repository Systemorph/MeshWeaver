using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Kernel;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

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
                var activityId = submissionId;
                var activityNamespace = $"{hub.Address.Path}/_Activity";
                var activityPath = $"{activityNamespace}/{activityId}";
                var activityNode = new MeshNode(activityId, activityNamespace)
                {
                    Name = $"Script run {activityId[..Math.Min(8, activityId.Length)]}",
                    NodeType = ActivityNodeType.NodeType,
                    MainNode = hub.Address.Path,
                    State = MeshNodeState.Active,
                    Content = new ActivityLog("ScriptExecution")
                    {
                        Id = activityId,
                        HubPath = hub.Address.Path,
                        Status = ActivityStatus.Running
                    }
                };

                var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
                meshService.CreateNode(activityNode).Subscribe(
                    _ =>
                    {
                        // Node created. Fire SubmitCodeRequest at the Activity hub
                        // (which now hosts the kernel handlers).
                        hub.Post(
                            new SubmitCodeRequest(code.Code ?? string.Empty)
                            {
                                Id = submissionId,
                                ActivityLogPath = activityPath
                            },
                            o => o.WithTarget(new Address(activityPath)));

                        // Stamp LastExecutedAt onto the Code MeshNode so the
                        // Content area can show "Last executed: …" without a
                        // separate query of activity children. Reactive write
                        // via the canonical workspace.UpdateMeshNode — same
                        // path the CompileWatcher uses on the NodeType.
                        try
                        {
                            var workspace = hub.GetWorkspace();
                            workspace.UpdateMeshNode(curr =>
                                curr.Content is CodeConfiguration cfg
                                    ? curr with { Content = cfg with { LastExecutedAt = DateTimeOffset.UtcNow } }
                                    : curr);
                        }
                        catch
                        {
                            // Workspace might not be ready (cold-start race) —
                            // missing LastExecutedAt is a UI nicety, not a
                            // correctness invariant. Activity log is still written.
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
