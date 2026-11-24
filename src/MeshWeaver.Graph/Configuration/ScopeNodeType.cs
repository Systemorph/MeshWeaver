using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Provides configuration for <b>Scope</b> nodes — first-class BusinessRules scopes.
///
/// <para>A Scope node holds the <c>IScope&lt;TIdentity,TState&gt;</c> C# source as its
/// <see cref="CodeConfiguration"/> Content, exactly like a <see cref="CodeNodeType"/>, but is
/// typed <c>"Scope"</c> so it reads as a scope in the graph. The scope generator itself is not
/// associated with this type: the BusinessRules package DECLARES it on its <c>BusinessRules/Scope</c>
/// NodeType, and it runs only over compiles that pull that type's sources (policy
/// <c>declared-source-generators</c>, <c>MeshWeaver.Compiler.DeclaredGenerators</c>).</para>
///
/// <para>Authored by putting <c>// NodeType: Scope</c> in the file's <c>&lt;meshweaver&gt;</c>
/// header (see <c>CSharpFileParser</c>); the source then lives under <c>{NodeType}/Source/</c>
/// like any other Code source and is folded into the owning NodeType's Roslyn unit, where the
/// declared generator, when the unit pulls it, emits the <c>{Scope}Proxy : ScopeBase&lt;…&gt;</c>
/// implementations.</para>
/// </summary>
public static class ScopeNodeType
{
    /// <summary>The NodeType value used to identify scope nodes.</summary>
    public const string NodeType = "Scope";

    /// <summary>Registers the built-in "Scope" MeshNode on the mesh builder.</summary>
    public static TBuilder AddScopeType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(NodeType);
        return builder;
    }

    /// <summary>
    /// Creates the MeshNode definition for the Scope node type. Like Code, a Scope node is
    /// primary content (the scope source), browsable and editable through the code views.
    /// </summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Scope",
        Icon = "/static/NodeTypeIcons/code.svg",
        IsSatelliteType = false,
        ExcludeFromContext = new HashSet<string> { "search", "create", "content" },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<CodeConfiguration>())
            .AddDefaultLayoutAreas()
            .ApplyNodeHubContributions(NodeType)
    };
}
