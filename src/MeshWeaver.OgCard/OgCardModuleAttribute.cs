using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

[assembly: MeshWeaver.OgCard.OgCardModule]

namespace MeshWeaver.OgCard;

/// <summary>
/// Module registration for the <c>OgCard</c> link-preview layout area. Listing this DLL under
/// <c>Modules:Assemblies</c> registers the area on every per-node hub — the <c>@@</c> markdown
/// embed resolves it on the EMBEDDING document's hub, so it must be available mesh-wide.
///
/// <para>Why this is a module and not core: the external-URL card fetches ARBITRARY pages
/// server-side (<c>OpenGraphPreviewService</c>, which stays a core singleton) — an outbound-fetch
/// surface a locked-down deployment may want off. Delisting the module removes the area; embeds
/// in existing documents then render the standard area-not-found placeholder.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class OgCardModuleAttribute : MeshNodeProviderAttribute
{
    /// <inheritdoc />
    public override IEnumerable<Func<MessageHubConfiguration, MessageHubConfiguration>> DefaultNodeHubConfigurations =>
        [OgCardExtensions.ConfigureHub];
}

/// <summary>
/// The module's registration surface. Production installs it via <c>Modules:Assemblies</c>
/// (<see cref="OgCardModuleAttribute"/>); a mesh that composes it explicitly — a test fixture, a
/// bespoke host — calls <see cref="AddOgCard"/> for the identical per-node-hub registration.
/// </summary>
public static class OgCardExtensions
{
    internal static MessageHubConfiguration ConfigureHub(MessageHubConfiguration config)
        => config.AddLayout(layout =>
            layout.WithView(OgCardLayoutArea.AreaName, OgCardLayoutArea.Render));

    /// <summary>Registers the <c>OgCard</c> area on every per-node hub of this mesh.</summary>
    public static MeshBuilder AddOgCard(this MeshBuilder builder)
        => builder.ConfigureDefaultNodeHub(ConfigureHub);
}
