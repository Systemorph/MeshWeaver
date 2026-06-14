using System.Text.Json;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeshWeaver.AI;

/// <summary>
/// The "Command" node type — chat slash-commands AS mesh nodes. Built-in commands
/// (<c>/agent</c>, <c>/model</c>, <c>/harness</c>) ship as read-only nodes under the
/// <see cref="RootNamespace"/> via <see cref="BuiltInCommandProvider"/>; a Space or a NodeType can
/// define its OWN Command nodes, discovered through namespace inheritance (own + ancestors, and the
/// node-type's namespace + ancestors — see <see cref="CommandQueries"/>). Execution drives the same
/// generic node picker the C# <c>MeshNodePickCommand</c> uses. See <c>Doc/AI/ChatCommands.md</c>.
/// </summary>
public static class CommandNodeType
{
    /// <summary>NodeType discriminator.</summary>
    public const string NodeType = "Command";

    /// <summary>Namespace (partition) the built-in command catalog lives under.</summary>
    public const string RootNamespace = "Command";

    /// <summary>
    /// Registers the Command type node, the content type, and the static-node provider that
    /// serves the built-in commands read-only under the <c>Command</c> partition.
    /// </summary>
    public static TBuilder AddCommandType<TBuilder>(this TBuilder builder,
        IReadOnlySet<string>? serveFromPartition = null) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.ConfigureNodeTypeAccess(a => a.WithPublicRead(NodeType));
        builder.ConfigureHub(config => config.WithType<CommandDefinition>(nameof(CommandDefinition)));
        // When the "Command" partition is DB-synced (static-repo import), DO NOT register the
        // read-only in-memory static surfaces — they would shadow Postgres and reject the import's
        // writes (and on the distributed/PG path the in-memory adapter is never consulted by
        // queries, so the commands would be invisible). The import materializes them into the
        // partition; PG serves them. Mirrors AddAgentType — see CommandStaticRepoSource +
        // Doc/Architecture/StaticRepoImport.md.
        var dbSynced = serveFromPartition?.Contains(RootNamespace) == true;
        builder.ConfigureServices(services =>
        {
            services.TryAddSingleton<BuiltInCommandProvider>();
            if (!dbSynced)
            {
                services.AddSingleton<IStaticNodeProvider>(sp => sp.GetRequiredService<BuiltInCommandProvider>());
                services.AddSingleton<IPartitionStorageProvider>(sp =>
                    new StaticNodePartitionStorageProvider(
                        RootNamespace,
                        sp.GetRequiredService<BuiltInCommandProvider>(),
                        description: "Built-in chat commands (read-only)."));
            }
            return services;
        });
        return builder;
    }

    /// <summary>The type-definition node for nodeType="Command".</summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Command",
        Icon = "/static/NodeTypeIcons/sparkle.svg",
        IsSatelliteType = false,
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<CommandDefinition>())
    };

    /// <summary>
    /// The queries that discover the commands available in a context, in priority order: the
    /// global built-in catalog, the current context node + ancestors, and the user's home +
    /// ancestors. Mirrors the agent/model picker query templates (namespace + scope inheritance),
    /// so a Space/NodeType command defined nearer the context overrides a global one by id.
    /// </summary>
    public static string[] CommandQueries(string? contextPath, string? userPath)
    {
        var queries = new List<string> { $"namespace:{RootNamespace} nodeType:{NodeType}" };
        if (!string.IsNullOrEmpty(contextPath))
            queries.Add($"path:{contextPath} nodeType:{NodeType} scope:selfAndAncestors");
        if (!string.IsNullOrEmpty(userPath))
            queries.Add($"path:{userPath} nodeType:{NodeType} scope:selfAndAncestors");
        return queries.ToArray();
    }

    /// <summary>
    /// Projects a mesh-node snapshot into the available commands, deduped by id (the slash word).
    /// Reads the slash word from <see cref="MeshNode.Id"/> and help text from
    /// <see cref="MeshNode.Description"/>; the pick spec is the typed (or JsonElement-fallback)
    /// <see cref="CommandDefinition"/> content. Mirrors <c>AgentPickerProjection.ProjectModels</c>.
    /// </summary>
    public static IReadOnlyList<CommandInfo> ProjectCommands(
        IEnumerable<MeshNode> snapshot, JsonSerializerOptions jsonOptions)
    {
        var byId = new Dictionary<string, CommandInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in snapshot)
        {
            if (string.IsNullOrEmpty(node.Id)) continue;
            if (!string.Equals(node.NodeType, NodeType, StringComparison.OrdinalIgnoreCase)) continue;
            var def = node.Content switch
            {
                CommandDefinition d => d,
                JsonElement je => TryDeserialise(je, jsonOptions),
                _ => null,
            };
            if (def is null) continue;
            byId[node.Id] = new CommandInfo { Id = node.Id, Description = node.Description, Definition = def };
        }
        return byId.Values.OrderBy(c => c.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static CommandDefinition? TryDeserialise(JsonElement je, JsonSerializerOptions opts)
    {
        try { return JsonSerializer.Deserialize<CommandDefinition>(je.GetRawText(), opts); }
        catch { return null; }
    }
}
