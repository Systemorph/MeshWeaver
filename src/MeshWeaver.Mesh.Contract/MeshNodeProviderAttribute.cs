using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Mesh;

/// <summary>
/// Attribute to define mesh nodes at the assembly level.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public abstract class MeshNodeProviderAttribute : Attribute
{
    /// <summary>
    /// Gets the mesh nodes defined by this attribute.
    /// </summary>
    public virtual IEnumerable<MeshNode> Nodes => [];

    /// <summary>
    /// Gets the address types to register with the mesh hub.
    /// Key is the short type name (e.g., "story"), Value is the address type.
    /// </summary>
    public virtual IEnumerable<KeyValuePair<string, Type>> AddressTypes => [];

    /// <summary>
    /// Configuration applied to the MESH hub itself when this assembly is installed
    /// (<c>MeshBuilder.InstallAssemblies</c>) — the attribute-carried form of
    /// <c>builder.ConfigureHub(...)</c>. Empty by default.
    /// </summary>
    public virtual IEnumerable<Func<MessageHubConfiguration, MessageHubConfiguration>> HubConfigurations => [];

    /// <summary>
    /// Configuration applied to EVERY per-node hub — the attribute-carried form of
    /// <c>builder.ConfigureDefaultNodeHub(...)</c>. This is the registration surface packs like
    /// Courses/Observability need (type registrations, default areas) and the one an in-mesh
    /// plugin structurally cannot reach. Empty by default.
    /// </summary>
    public virtual IEnumerable<Func<MessageHubConfiguration, MessageHubConfiguration>> DefaultNodeHubConfigurations => [];

    /// <summary>
    /// Configuration applied to the <see cref="MeshBuilder"/> itself when this assembly is
    /// installed — the full builder surface (<c>AddMeshNodes</c>/<c>AddMeshNodesIfAbsent</c>,
    /// <c>WithMeshType</c>, <c>ConfigureServices</c>, <c>ConfigureDefaultNodeHub</c>, …) in one
    /// hook. This is how a module whose registration already exists as a builder extension
    /// (e.g. <c>AddMarkdownExport()</c>) boot-loads WITHOUT decomposing that extension into the
    /// narrower hooks above — the compiled-in call and the boot-pack path stay one code path,
    /// so they cannot drift. The narrower hooks remain for declarative packs. Empty by default.
    /// </summary>
    public virtual IEnumerable<Func<MeshBuilder, MeshBuilder>> BuilderConfigurations => [];

    /// <summary>
    /// The module's VIEW registrations (control → view mappings) — the form a view pack contributes
    /// that a live swap can replace (policy <c>module-live-update-default</c>): a module held in its own
    /// load context has them re-read by the layout client from its CURRENT generation
    /// (<see cref="MeshWeaver.Layout.Client.IViewContributionSource"/>), where an <c>AddViews</c> inside
    /// <see cref="HubConfigurations"/> is folded into the mesh hub's configuration once, for the life of
    /// the process. Empty by default.
    /// </summary>
    public virtual IEnumerable<Func<MeshWeaver.Layout.Client.LayoutClientConfiguration, MeshWeaver.Layout.Client.LayoutClientConfiguration>> Views => [];

    /// <summary>
    /// Creates a mesh node from a hub configuration using a string prefix.
    /// </summary>
    protected MeshNode CreateFromHubConfiguration(string prefix, string name,
        Func<MessageHubConfiguration, MessageHubConfiguration> hubConfiguration)
        => new(prefix)
        {
            Name = name,
            HubConfiguration = hubConfiguration
        };

    /// <summary>
    /// Creates a mesh node from a hub configuration using an Address.
    /// </summary>
    protected MeshNode CreateFromHubConfiguration(Address address, string name,
        Func<MessageHubConfiguration, MessageHubConfiguration> hubConfiguration)
        => CreateFromHubConfiguration(address.ToString(), name, hubConfiguration);
}
