using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh;

/// <summary>
/// A section a MODULE contributes to the owner's profile page (<c>/{user}/EditProfile</c>) — the
/// profile twin of <see cref="SettingsMenuItemDefinition"/>. The platform renders the built-in
/// sections (picture, basics, bio, links, showcase) and then every contributed section, sorted by
/// <see cref="Order"/>, each under its own heading with the stable id <c>profile-section-{Id}</c>.
/// <see cref="ContentBuilder"/> receives the user node as a SNAPSHOT taken when the page last
/// changed shape; a section that shows live node values binds to
/// <c>host.Workspace.GetMeshNodeStream()</c> itself. A deployment without the module renders the
/// same page without that section; core never references the contributing module.
///
/// <para>Contributed exactly like a settings tab: a provider registered on the USER hub through
/// <c>AddProfileSections(…)</c>, which a module reaches from its own configuration with
/// <c>AddNodeHubContribution(UserNodeType.NodeType, userHub =&gt; userHub.AddProfileSections(…))</c>.
/// Full reference: <c>Doc/GUI/ProfilePage</c>.</para>
/// </summary>
/// <param name="Id">Stable section id — the DOM id of the section and the de-duplication key.</param>
/// <param name="Title">English section heading. Localized through <see cref="TitleKey"/>.</param>
/// <param name="ContentBuilder">Builds the section body (the page supplies the heading).</param>
/// <param name="Order">Sort order among the CONTRIBUTED sections. Every contributed section renders
/// after the built-in ones (picture, basics, bio, links, showcase), lowest order first.</param>
/// <param name="RequiredPermission">What the viewer must hold on the user node to see the section.
/// Defaults to <see cref="Permission.Update"/>: the profile editor is an owner surface, and a
/// section such as a subscription must never render for a visitor.</param>
/// <param name="Icon">Optional icon shown beside the heading (a <c>FluentIcons</c> value).</param>
public record ProfileSectionDefinition(
    string Id,
    string Title,
    ProfileSectionBuilder ContentBuilder,
    int Order = 0,
    Permission RequiredPermission = Permission.Update,
    object? Icon = null)
{
    /// <summary>
    /// Optional localization key for <see cref="Title"/> (e.g. <c>profile.subscription</c>). When
    /// set, <see cref="Localized"/> replaces the English title with the viewer's translation.
    /// </summary>
    public string? TitleKey { get; init; }

    /// <summary>This section with <see cref="Title"/> resolved into the current viewer's language.</summary>
    /// <param name="access">The viewer's access service; null keeps the English title.</param>
    public ProfileSectionDefinition Localized(AccessService? access)
        => TitleKey is { Length: > 0 } key ? this with { Title = access.Localize(key) } : this;
}

/// <summary>
/// Builds the body of a contributed profile section. Runs on the USER hub's layout area, so
/// <paramref name="host"/> is that hub's host and <paramref name="userNode"/> is the live user node
/// (null while it has not loaded). Return any control tree — a reactive body is a
/// <c>Controls.Stack.WithView((h, _) =&gt; observable)</c>, never a <c>Task</c>.
/// </summary>
/// <param name="host">The profile page's layout-area host (the user hub).</param>
/// <param name="userNode">The user node the page edits.</param>
public delegate UiControl ProfileSectionBuilder(LayoutAreaHost host, MeshNode? userNode);

/// <summary>
/// Yields profile sections reactively — the profile twin of <see cref="SettingsMenuItemProvider"/>.
/// A static provider <c>Observable.Return</c>s its sections; one that depends on a live check (an
/// entitlement, a feature flag) re-emits when that check resolves. Emit an empty list, never
/// <c>Observable.Empty</c>, when there is nothing to contribute.
/// </summary>
/// <param name="host">The profile page's layout-area host (the user hub).</param>
/// <param name="context">The rendering context of the profile area.</param>
public delegate IObservable<IReadOnlyList<ProfileSectionDefinition>> ProfileSectionProvider(
    LayoutAreaHost host, RenderingContext context);
