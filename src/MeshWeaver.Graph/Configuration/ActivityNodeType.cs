using MeshWeaver.Data;
using MeshWeaver.Graph.Security;
using MeshWeaver.Kernel;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Provides configuration for Activity nodes in the graph.
/// Activity nodes are system-generated satellite nodes â€” excluded from search and create contexts.
/// Access is delegated to the MainNode (parent) via SatelliteAccessRule.
/// </summary>
public static class ActivityNodeType
{
    public const string NodeType = "Activity";

    public static TBuilder AddActivityType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(NodeType);
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<INodeTypeAccessRule>(sp =>
                new SatelliteAccessRule(NodeType, sp.GetRequiredService<IMessageHub>()));
            return services;
        });
        return builder;
    }

    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Activity",
        IsSatelliteType = true,
        ExcludeFromContext = new HashSet<string> { "search", "create" },
        // Activity hubs host the kernel directly: SubmitCodeRequest etc. land here,
        // run inside this hub's action block, and write progress to the same
        // ActivityLog node via DataChangeRequest.Update on the local workspace.
        // Replaces the legacy `kernel/*` standalone hub addressing â€” replies route
        // through the standard MeshNode path instead of three special routing rules.
        HubConfiguration = config => config
            .AddActivityViews()
            .AddMeshDataSource(source => source
                .WithContentType<ActivityLog>())
            .AddKernelSubHubHandlers()
            // Per ActivityControlPlane doctrine: every long-running operation
            // runs on an Activity hub. Compile activities accept the
            // RunCompileRequest and own the Roslyn invocation here, leaving the
            // mesh hub and the parent NodeType hub responsive.
            .WithHandler<RunCompileRequest>(NodeTypeCompileActivityHandler.Handle)
            // 🚨 Polymorphic content types this hub must round-trip through JSON.
            // Activity compile reads source Code MeshNodes via GetMeshNodeStream
            // (cross-hub sync, JSON serde at boundary). Without these registrations,
            // `ObjectPolymorphicConverter` falls back to `JsonElement` for
            // `$type=CodeConfiguration` payloads, and the
            // `n.Content is CodeConfiguration cf` filter in
            // MeshNodeCompilationService.CompileCore silently drops every source.
            // Symptom in prod: "⚠ Compilation failed" with no specific error
            // (def.CompilationError stays null → BuildCompilationErrorMarkdown
            // emits the generic fallback string).
            .WithTypes(
                typeof(CodeConfiguration),
                typeof(NodeTypeDefinition),
                typeof(AccessAssignment),
                typeof(RoleAssignment))
    };
}
