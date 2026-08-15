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
        [config => config.AddLayout(layout =>
            layout.WithView(OgCardLayoutArea.AreaName, OgCardLayoutArea.Render))];
}
