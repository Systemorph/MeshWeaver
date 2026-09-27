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
/// instance is, then the same About page every user can open (build, "is it current?", installed
/// plugins). The facts are the ones an administrator asks first on a freshly installed instance:
/// the public host, the platform build and its commit, the runtime, the registry and the instance id
/// this installation registers under, and whether the image serves a closed type set.
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
            host.Hub.ServiceProvider.GetService<InstanceConsentService>()?.Target(),
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
    /// The instance facts, in reading order. Pure — every input is an argument, so the rows are
    /// testable without a hub or a registry.
    /// </summary>
    internal static IReadOnlyList<InstanceFact> Facts(
        IConfiguration? configuration,
        (PluginRegistryReference Registry, string InstanceId, bool Keyed)? registration,
        bool closedTypeSet,
        Func<string, string> localize)
    {
        var host = configuration?["Portal:BaseUrl"] ?? configuration?["PublicBaseUrl"];
        var sha = ShippedReleaseSeed.CommitHash;
        return
        [
            new(localize("adminApp.fact.host"), string.IsNullOrWhiteSpace(host) ? "—" : host),
            new(localize("adminApp.fact.version"), ShippedReleaseSeed.InstalledPlatformVersion),
            new(localize("adminApp.fact.commit"), sha is { Length: > 0 } ? sha : "—"),
            new(localize("adminApp.fact.runtime"), System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription),
            new(localize("adminApp.fact.registry"), registration is { } r
                ? (string.IsNullOrEmpty(r.Registry.Name) ? r.Registry.Url : $"{r.Registry.Name} — {r.Registry.Url}")
                : localize("adminApp.fact.notRegistered")),
            new(localize("adminApp.fact.instanceId"), registration?.InstanceId ?? "—"),
            new(localize("adminApp.fact.closedTypeSet"), localize(closedTypeSet ? "whoAmI.yes" : "whoAmI.no")),
        ];
    }
}
