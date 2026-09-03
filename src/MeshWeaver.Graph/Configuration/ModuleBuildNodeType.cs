using System.Collections.Generic;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Registers <c>ModuleBuild</c> as a first-class NodeType — the rows of the fleet's CI module build
/// ledger at <see cref="Root"/><c>/{key}</c>, each carrying a <see cref="ModuleBuildRecord"/>
/// (<c>Doc/Architecture/ModuleBuildArchitecture</c> → "Content-addressed outputs").
///
/// <para>The writer is <c>.github/scripts/module-build-ledger.py</c> in the reusable module-pack
/// lane, speaking to the registry portal's MCP endpoint as a dedicated CI user. The type ships in
/// the framework (like <see cref="BuildNodeType"/>) so every registry portal has it without a content
/// package to install, and the schema the lane writes against is this record — one definition.</para>
///
/// <para>🚨 <b>Deliberately NOT under <c>Admin/Build</c>.</b> That root is the in-portal NodeType
/// bake's coordination node (<see cref="BuildNodeType.RootPath"/>), whose hub arbitrates claims over
/// its children; the CI ledger is a different protocol (create-if-absent IS the mutex, heartbeat
/// staleness IS the takeover rule) and must not sit where the arbiter enumerates chunks. It is still
/// in the Admin partition, because the subject of a build decision must not be able to write the
/// decision for everyone — and the CI user gets a partition-admin grant scoped to exactly
/// <see cref="Root"/> (<c>Admin/ModuleBuilds/_Access/{user}_Access</c>, <c>MainNode = "Admin/ModuleBuilds"</c>),
/// never a global-admin one.</para>
/// </summary>
public static class ModuleBuildNodeType
{
    /// <summary>The node-type identifier string for ModuleBuild nodes.</summary>
    public const string NodeType = "ModuleBuild";

    /// <summary>The ledger root — every record is a direct child named by its build key.</summary>
    public const string Root = "Admin/ModuleBuilds";

    /// <summary>
    /// Registers the ModuleBuild node type on the mesh builder: the MeshNode definition, the
    /// autocomplete exclusion (a 64-hex key is nothing a human links to), and the record types in
    /// the hub's type registry so the typed payload survives a cross-silo write.
    /// </summary>
    /// <typeparam name="TBuilder">The mesh builder type.</typeparam>
    /// <param name="builder">The mesh builder to configure.</param>
    /// <returns>The same builder, to allow fluent chaining.</returns>
    public static TBuilder AddModuleBuildType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(NodeType);
        builder.ConfigureHub(config => config
            .WithType<ModuleBuildRecord>(nameof(ModuleBuildRecord))
            .WithType<ModuleBuildRun>(nameof(ModuleBuildRun))
            .WithType<ModuleBuildArtifact>(nameof(ModuleBuildArtifact))
            .WithType<ModuleBuildTests>(nameof(ModuleBuildTests)));
        return builder;
    }

    /// <summary>
    /// Builds the MeshNode definition for the ModuleBuild node type: the record payload, no UI create
    /// (only the CI lane writes these nodes), the default views.
    /// </summary>
    /// <returns>The ModuleBuild MeshNode definition.</returns>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Module Build",
        NodeType = MeshNode.NodeTypePath,   // this MeshNode IS a NodeType definition
        Icon = "/static/NodeTypeIcons/task-list.svg",
        ExcludeFromContext = new HashSet<string> { "create" }, // no UI create — only the CI ledger script writes these
        Content = new NodeTypeDefinition
        {
            Description = "One row of the CI module build ledger: which run built (or is building) a "
                + "module at a given content address, against which platform, with what verdict — "
                + "so a second run of the same key reuses the first instead of building again.",
        },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<ModuleBuildRecord>())
            .AddDefaultLayoutAreas()
    };
}
