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
using Microsoft.Extensions.DependencyInjection;

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
    /// <summary>The tab id under <c>/_Setting/GlobalSettings</c> (GlobalSettingsNodeType.TabHref).</summary>
    public const string TabId = "About";

    /// <summary>Data id the plugin grid binds to (rows published via <c>host.UpdateData</c>).</summary>
    private const string PluginsDataId = "aboutPlugins";

    // The menu entry is a seeded UiContribution node (PlatformSettingsTabAreas) — the tab's
    // registration surface is the SettingsAbout layout area, not a compiled provider.

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

        // Serving-since sits directly under the build id: together they answer "which build, and
        // since when" — the pair the header chip deliberately keeps short.
        var startedLine = StartedAtMarkdown(
            ShippedReleaseSeed.StartedAtUtc,
            host.Hub.ServiceProvider.GetService<AccessService>().ViewerZoneId(),
            key => host.Localize(key));

        return
            $"**{host.Localize("about.version")}:** `{version}`\n\n" +
            $"{commitLine}\n\n" +
            (startedLine is null ? "" : $"{startedLine}\n\n") +
            $"**{host.Localize("about.repository")}:** [Systemorph/MeshWeaver]({ShippedReleaseSeed.RepositoryUrl})\n\n" +
            $"**{host.Localize("about.runtime")}:** .NET {Environment.Version}";
    }

    /// <summary>
    /// The "serving since" line — WHEN this build started answering, in the viewer's own zone.
    ///
    /// <para>The version answers "which build" and never "since when". After a roll the version can
    /// be identical (a re-deploy of the same image) or minutes old, and only a timestamp separates
    /// "this has been live all week" from "this landed while you were reading" — the question asked
    /// every time somebody needs to know whether a portal actually rolled forward.</para>
    ///
    /// <para>🚨 The stored value is UTC; this renders it in the VIEWER's zone. A bare UTC clock is
    /// the thing that misleads — 13:35 UTC shown to a Zurich reader is an hour wrong in summer, and
    /// silently so. Pure and localizer-taking so the conversion is testable without a hub.</para>
    ///
    /// <para>Returns <c>null</c> when the start time is unknown (<c>default</c> — a host that
    /// refuses process introspection, or WASM). Rendering the epoch, or substituting "now", would
    /// read as a fresh deploy on every render.</para>
    /// </summary>
    internal static string? StartedAtMarkdown(
        DateTimeOffset startedAtUtc, string? viewerZoneId, Func<string, string> localize)
    {
        if (startedAtUtc == default) return null;

        var local = DisplayTimeExtensions.ToDisplayTime(startedAtUtc, viewerZoneId);
        return $"**{localize("about.lastDeployed")}:** {local:yyyy-MM-dd HH:mm} (UTC{local:zzz})";
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
            // A held update reads DIFFERENTLY from an available one on purpose: an install that has
            // refused a build must not look like one that is simply about to take it. The reason
            // itself is admin-scoped and lives on the Updates tab; here the ordinary user learns
            // that the deployment is deliberately not moving.
            PlatformUpdateAvailability.UpdateHeld =>
                $"**{localize("about.updateStatus")}:** ⏸️ {localize("about.updateHeld")} — " +
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
