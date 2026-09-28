using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Where an old settings link lands now that every tab lives in the app of the thing it changes.
/// A settings page asked for a tab it no longer carries answers with a redirect to the tab's home —
/// never by silently opening its own first tab, which is what an unknown id used to do.
/// </summary>
internal static class SettingsRedirect
{
    /// <summary>The retired node-settings Files tab: the node's ⋯ → Files opens the same browser.</summary>
    internal const string LegacyFilesTab = "Files";

    /// <summary>
    /// The redirect for <paramref name="tabId"/> on <paramref name="host"/>'s settings page, or
    /// <c>null</c> when the tab belongs here. Checked in this order:
    /// <list type="number">
    /// <item>an ALIAS on this hub (<see cref="SettingsMenuItemsExtensions.AliasSettingsTab"/>) — a tab
    /// merged into another tab of the same page;</item>
    /// <item>the retired Files tab → the node's own Files area;</item>
    /// <item>a PARTITION-ROOT tab asked for below the root → the same tab on the root;</item>
    /// <item>a tab that moved into the Admin app (<see cref="AdminAppNodeType.RedirectIfRelocated"/>);</item>
    /// <item>a tab that moved into the person app → the viewer's own
    /// <see cref="PersonApp.TabHref"/>.</item>
    /// </list>
    /// </summary>
    /// <param name="host">The settings page's host.</param>
    /// <param name="tabId">The requested tab id, query string stripped.</param>
    internal static UiControl? For(LayoutAreaHost host, string? tabId)
    {
        if (string.IsNullOrEmpty(tabId))
            return null;
        var config = host.Hub.Configuration;
        var hubPath = host.Hub.Address.ToString();

        if (config.Get<SettingsTabAliases>() is { } aliases
            && aliases.Map.TryGetValue(tabId, out var alias)
            && !string.Equals(alias, tabId, StringComparison.OrdinalIgnoreCase))
            return Controls.Redirect(TabHref(hubPath, alias));

        if (string.Equals(tabId, LegacyFilesTab, StringComparison.OrdinalIgnoreCase))
            return Controls.Redirect(MeshNodeLayoutAreas.BuildUrl(hubPath, MeshNodeLayoutAreas.FilesArea));

        if (!SettingsMenuItemsExtensions.IsPartitionRoot(hubPath)
            && config.Get<PartitionRootSettingsTabs>() is { } rootOnly
            && rootOnly.Ids.Contains(tabId))
            return Controls.Redirect(TabHref(hubPath.Split('/', 2)[0], tabId));

        if (AdminAppNodeType.RedirectIfRelocated(host, tabId) is { } toAdmin)
            return toAdmin;

        if (config.Get<PersonAppRelocatedTabs>() is { } personal
            && personal.Map.TryGetValue(tabId, out var personalTab))
        {
            var viewer = host.Hub.ServiceProvider.GetService<AccessService>().ViewerId();
            if (!string.IsNullOrEmpty(viewer) && !PersonApp.IsOwnRoot(hubPath, viewer))
                return Controls.Redirect(PersonApp.TabHref(viewer, personalTab));
        }

        return null;
    }

    private static string TabHref(string hubPath, string tabId)
        => "/" + new LayoutAreaReference(MeshNodeLayoutAreas.SettingsArea) { Id = tabId }.ToHref(hubPath);
}
