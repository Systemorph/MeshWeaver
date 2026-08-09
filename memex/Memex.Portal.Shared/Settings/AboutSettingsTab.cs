using System.Reactive.Linq;
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
using Memex.Portal.Shared.SelfUpdate;

namespace Memex.Portal.Shared.Settings;

/// <summary>
/// Global settings "About" tab — the ONE page that identifies what this portal is running:
/// the platform build (version + the git commit it was built from, linking straight to GitHub),
/// <b>whether that build is current</b>, the runtime, and the live installed-plugin inventory with
/// each plugin's version. Ungated: visible to every user — plugins are part of the build identity,
/// and the inventory is read-only (browsing and provisioning packages is the Store's job; the old
/// admin-only "Plugin Catalog" settings tab is retired in favour of this page).
///
/// <para>The "is it current?" line comes from <see cref="PlatformUpdateStatus"/> — a two-state verdict
/// projected out of the platform-admin-only <c>Admin/UpdatePolicy</c> node, so an ordinary user can
/// answer the question without being handed the update policy itself.</para>
///
/// <para>Build identity comes from the assembly attributes baked in by
/// <c>Directory.Build.props</c> (<see cref="ShippedReleaseSeed.InstalledPlatformVersion"/> /
/// <see cref="ShippedReleaseSeed.CommitHash"/>), so it reflects the deployed image with no
/// runtime plumbing. The plugin inventory is the live install registry
/// (<see cref="CatalogLayoutAreas.ObserveInstalledManifests"/>) — it re-renders the moment an
/// install lands. Pure <see cref="Controls"/> + a <see cref="DataGridControl"/> over a plain row
/// record — no hand-built HTML (GUI rule).</para>
/// </summary>
public static class AboutSettingsTab
{
    /// <summary>The tab id under <c>/_settings/GlobalSettings</c>.</summary>
    public const string TabId = "About";

    /// <summary>Data id the plugin grid binds to (rows published via <c>host.UpdateData</c>).</summary>
    private const string PluginsDataId = "aboutPlugins";

    /// <summary>Registers the About tab with the global settings menu (ungated).</summary>
    public static MessageHubConfiguration AddAboutSettingsTab(this MessageHubConfiguration config)
        => config.AddGlobalSettingsMenuItems(new GlobalSettingsMenuItemProvider(GetTab));

    private static IObservable<IReadOnlyList<GlobalSettingsMenuItemDefinition>> GetTab(
        LayoutAreaHost host, RenderingContext ctx)
        => Observable.Return<IReadOnlyList<GlobalSettingsMenuItemDefinition>>(new[]
        {
            new GlobalSettingsMenuItemDefinition(
                Id: TabId,
                Label: "About",
                ContentBuilder: BuildContent,
                Icon: FluentIcons.Info(),
                Order: 900)
            { LabelKey = "settings.about" }
        });

    internal static UiControl BuildContent(LayoutAreaHost host, StackControl stack)
    {
        stack = stack.WithView(Controls.H2(host.Localize("ui.aboutMeshWeaver")).WithStyle("margin: 0 0 8px 0;"));
        stack = stack.WithView(Controls.Markdown(host.Localize("about.intro")));

        stack = stack.WithView(Controls.Markdown(BuildInfoMarkdown(host)));

        // ── "Is that build current?" — the derived verdict, for EVERY user. ──
        // The comparison (running vs newest known) lives on Admin/UpdatePolicy, which only platform
        // admins can read, so PlatformUpdateStatus.Observe reads it as System and lets ONLY the
        // verdict out. Unknown (no self-update wired / checks off / read failed) renders nothing —
        // an unfounded "up to date" would be worse than no line.
        stack = stack.WithView((h, _) => PlatformUpdateStatus.Observe(h.Hub)
            .Select(status => (UiControl?)UpdateStatusView(h, status))
            .StartWith((UiControl?)Controls.Stack.WithWidth("100%")));

        // ── Installed plugins — the live inventory, one row per install record. ──
        stack = stack.WithView(Controls.H3(host.Localize("about.plugins.title")).WithStyle("margin: 16px 0 4px 0;"));
        stack = stack.WithView((h, _) => CatalogLayoutAreas.ObserveInstalledManifests(h)
            .Select(plugins => plugins.Count == 0
                ? (UiControl)Controls.Markdown(h.Localize("about.plugins.none"))
                : PluginsGrid(h, plugins)));
        return stack;
    }

    /// <summary>
    /// The build-identity block. Version is always present; the commit line links to the exact
    /// GitHub commit when the SHA was baked in (CI + local git), otherwise it is omitted with a note.
    /// </summary>
    private static string BuildInfoMarkdown(LayoutAreaHost host)
    {
        var version = ShippedReleaseSeed.InstalledPlatformVersion;
        var sha = ShippedReleaseSeed.CommitHash;
        var commitLine = sha is { Length: > 0 }
            ? $"**{host.Localize("about.buildCommit")}:** [`{Short(sha)}`]({ShippedReleaseSeed.CommitUrl})"
            : $"**{host.Localize("about.buildCommit")}:** _{host.Localize("about.buildCommitMissing")}_";

        return
            $"**{host.Localize("about.version")}:** `{version}`\n\n" +
            $"{commitLine}\n\n" +
            $"**{host.Localize("about.repository")}:** [Systemorph/MeshWeaver]({ShippedReleaseSeed.RepositoryUrl})\n\n" +
            $"**{host.Localize("about.runtime")}:** .NET {Environment.Version}";
    }

    /// <summary>
    /// The update-status line: a language-neutral glyph plus one translated phrase. Returns an empty
    /// stack for <see cref="PlatformUpdateAvailability.Unknown"/> so nothing renders.
    /// </summary>
    private static UiControl UpdateStatusView(LayoutAreaHost host, PlatformUpdateStatus status) =>
        UpdateStatusMarkdown(status, key => host.Localize(key)) is { } markdown
            ? Controls.Markdown(markdown)
            : Controls.Stack.WithWidth("100%");

    /// <summary>
    /// The update-status markdown, or <c>null</c> when there is nothing to say. Takes the localizer as
    /// a function so the wording is unit-testable without a hub.
    /// </summary>
    internal static string? UpdateStatusMarkdown(PlatformUpdateStatus status, Func<string, string> localize) =>
        status.Availability switch
        {
            PlatformUpdateAvailability.UpdateAvailable =>
                $"**{localize("about.updateStatus")}:** ⬆️ {localize("about.updateAvailable")} — " +
                $"`{status.LatestVersion}`",
            PlatformUpdateAvailability.UpToDate =>
                $"**{localize("about.updateStatus")}:** ✅ {localize("about.upToDate")}",
            _ => null,
        };

    /// <summary>
    /// The inventory grid — a <see cref="DataGridControl"/> over plain rows (the framework's ONE
    /// way to render tabular data), columns localized. <c>ModuleVersion</c> is the module's own
    /// content hash from <c>manifest.lock</c>, shortened for display — the exact-content identifier
    /// support needs when "same version, different content" is the question.
    /// </summary>
    private static UiControl PluginsGrid(LayoutAreaHost host, IReadOnlyList<PackageManifest> plugins)
    {
        var rows = ToRows(plugins);
        host.UpdateData(PluginsDataId, rows);
        return new DataGridControl(new JsonPointerReference(LayoutAreaReference.GetDataPointer(PluginsDataId)))
            .WithColumn(new PropertyColumnControl<string>
            { Property = nameof(PluginRow.Name).ToCamelCase() }
                .WithTitle(host.Localize("about.plugins.column.name")))
            .WithColumn(new PropertyColumnControl<string>
            { Property = nameof(PluginRow.Version).ToCamelCase() }
                .WithTitle(host.Localize("about.plugins.column.version")))
            .WithColumn(new PropertyColumnControl<string>
            { Property = nameof(PluginRow.Module).ToCamelCase() }
                .WithTitle(host.Localize("about.plugins.column.module")))
            .WithColumn(new PropertyColumnControl<string>
            { Property = nameof(PluginRow.Kind).ToCamelCase() }
                .WithTitle(host.Localize("about.plugins.column.kind")))
            .WithColumn(new PropertyColumnControl<string>
            { Property = nameof(PluginRow.Partition).ToCamelCase() }
                .WithTitle(host.Localize("about.plugins.column.partition")));
    }

    /// <summary>
    /// Maps install-registry manifests to grid rows: display name falls back to the package id,
    /// the module content hash is shortened for display, and null-able fields render empty rather
    /// than "null". Public so the mapping is directly testable.
    /// </summary>
    public static IReadOnlyList<PluginRow> ToRows(IEnumerable<PackageManifest> plugins) => plugins
        .Select(p => new PluginRow(
            p.Name ?? p.Id,
            p.Version ?? "",
            Short(p.ModuleVersion ?? ""),
            p.Kind.ToString(),
            p.TargetPartition ?? ""))
        .ToList();

    /// <summary>One row of the plugin grid — a plain record so the grid binds it directly.</summary>
    public record PluginRow(string Name, string Version, string Module, string Kind, string Partition);

    /// <summary>Short display form of a SHA/hash (first 12 chars); full value still drives links.</summary>
    private static string Short(string sha) => sha.Length <= 12 ? sha : sha[..12];
}
