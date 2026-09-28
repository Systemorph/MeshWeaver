using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using MeshWeaver.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Memex.Portal.Shared.Settings;

/// <summary>
/// The Admin app's landing tab — <c>/Admin</c> (<c>/Admin/Settings/Overview</c>): what THIS
/// instance is, then the About page (build, "is it current?", installed plugins). The instance facts
/// are the ones no other tab carries — the public host and whether the image serves a closed type
/// set — and the About part follows them ONCE: the platform version, commit and runtime are About's
/// lines, and the registry and the instance id this installation registers under are the
/// Registration tab's, so the Overview does not repeat either.
///
/// <para>Composed, not duplicated: the About part IS <see cref="AboutSettingsTab.BuildContent"/>, and
/// every other administration surface is its own tab of the app (Updates, Invitations, Sign-in
/// providers, …). Rendered with a <see cref="DataGridControl"/> over plain rows.</para>
/// </summary>
public static class AdminAppOverviewTab
{
    /// <summary>The tab id.</summary>
    public const string TabId = "Overview";

    private const string FactsDataId = "adminOverviewFacts";

    /// <summary>One fact about the instance.</summary>
    /// <param name="Fact">What is described (localized).</param>
    /// <param name="Value">Its value.</param>
    public sealed record InstanceFact(string Fact, string Value);

    /// <summary>Registers the Overview tab on the Admin app (not relocated: the id is new).</summary>
    /// <param name="config">The hub configuration.</param>
    public static MessageHubConfiguration AddAdminAppOverviewTab(this MessageHubConfiguration config)
        => config.AddAdminAppTab(new SettingsMenuItemDefinition(
                Id: TabId,
                Label: "Overview",
                ContentBuilder: (host, stack, _) => BuildContent(host, stack),
                Icon: FluentIcons.Info(),
                // First: /Admin lands here (the settings page opens its first tab).
                Order: -100,
                Keywords: ["overview", "about", "instance", "host", "version", "build", "commit",
                    "registry", "instance id", "image", "runtime", "framework"])
            { LabelKey = "adminApp.overview" },
            relocated: false);

    internal static UiControl BuildContent(LayoutAreaHost host, StackControl stack)
    {
        var facts = Facts(
            host.Hub.ServiceProvider.GetService<IConfiguration>(),
            host.Hub.ServiceProvider.IsClosedTypeSet(),
            key => host.Localize(key));
        host.UpdateData(FactsDataId, facts);

        stack = stack
            .WithView(Controls.H2(host.Localize("adminApp.instanceTitle")).WithStyle("margin: 0 0 8px 0;"))
            .WithView(new DataGridControl(new JsonPointerReference(LayoutAreaReference.GetDataPointer(FactsDataId)))
                .WithColumn(new PropertyColumnControl<string> { Property = nameof(InstanceFact.Fact).ToCamelCase() }
                    .WithTitle(host.Localize("whoAmI.column.fact")))
                .WithColumn(new PropertyColumnControl<string> { Property = nameof(InstanceFact.Value).ToCamelCase() }
                    .WithTitle(host.Localize("whoAmI.column.value"))));
        // The About page every user sees — the build, whether it is current, the installed plugins.
        return AboutSettingsTab.BuildContent(host, stack);
    }

    /// <summary>
    /// The instance facts, in reading order — only the ones no other tab of the app shows. Pure —
    /// every input is an argument, so the rows are testable without a hub.
    /// </summary>
    internal static IReadOnlyList<InstanceFact> Facts(
        IConfiguration? configuration,
        bool closedTypeSet,
        Func<string, string> localize)
    {
        var host = configuration?["Portal:BaseUrl"] ?? configuration?["PublicBaseUrl"];
        return
        [
            new(localize("adminApp.fact.host"), string.IsNullOrWhiteSpace(host) ? "—" : host),
            new(localize("adminApp.fact.closedTypeSet"), localize(closedTypeSet ? "whoAmI.yes" : "whoAmI.no")),
        ];
    }
}
