using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Kernel;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

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
        // Compose a reactive chain on the hub's own workspace stream — .Select the
        // MeshNode out of each change, .Where non-null, .Take(1) to wait for the
        // first populated emission, then .Subscribe to fire the dispatch + response.
        // Handler itself returns Processed() immediately; the callback fires once
        // the workspace has loaded the node.
        hub.GetWorkspace().GetStream(new MeshNodeReference())
            ?.Select(change => change.Value)
            .Where(node => node is not null)
            .Take(1)
            .Subscribe(node =>
            {
                if (node!.Content is not CodeConfiguration code || !code.IsExecutable)
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
                var kernelAddress = AddressExtensions.CreateKernelAddress(
                    "code-" + hub.Address.Path.Replace('/', '-'));

                // Fire-and-forget — 1:1 with ExecutionManager in interactive markdown.
                // Progress + stdout stream into the kernel's layout area at submissionId.
                hub.Post(
                    new SubmitCodeRequest(code.Code ?? string.Empty) { Id = submissionId },
                    o => o.WithTarget(kernelAddress));

                hub.Post(
                    new ExecuteScriptResponse
                    {
                        Success = true,
                        SubmissionId = submissionId,
                        OutputAreaReference = submissionId
                    },
                    o => o.ResponseFor(request));
            });
        return request.Processed();
    }
}
