using System.Text.Json;
using Microsoft.Maui.Storage;

namespace Memex.Client.Services;

/// <summary>A named portal endpoint (e.g. "memex" → https://memex.meshweaver.cloud).</summary>
public sealed record PortalSite(string Name, string Url);

/// <summary>
/// The app's configured portal endpoints + the selected one, persisted in <see cref="Preferences"/>.
/// Empty on first launch → the app opens Settings so the user configures a URL. Supports multiple.
/// </summary>
public sealed class PortalUrlStore
{
    private const string SitesKey = "portal.sites";
    private const string SelectedKey = "portal.selected";

    public List<PortalSite> Sites { get; private set; } = new();
    public int SelectedIndex { get; private set; }

    public PortalSite? Selected =>
        SelectedIndex >= 0 && SelectedIndex < Sites.Count ? Sites[SelectedIndex] : null;

    public bool HasAny => Sites.Count > 0;

    public PortalUrlStore() => Load();

    private void Load()
    {
        var json = Preferences.Default.Get(SitesKey, "");
        Sites = string.IsNullOrEmpty(json)
            ? new()
            : JsonSerializer.Deserialize<List<PortalSite>>(json) ?? new();
        SelectedIndex = Preferences.Default.Get(SelectedKey, 0);
    }

    private void Save()
    {
        Preferences.Default.Set(SitesKey, JsonSerializer.Serialize(Sites));
        Preferences.Default.Set(SelectedKey, SelectedIndex);
    }

    public void Add(string name, string url)
    {
        url = Normalize(url);
        if (string.IsNullOrWhiteSpace(url)) return;
        Sites.Add(new PortalSite(string.IsNullOrWhiteSpace(name) ? url : name.Trim(), url));
        if (Sites.Count == 1) SelectedIndex = 0;
        Save();
    }

    public void Remove(PortalSite site)
    {
        Sites.Remove(site);
        if (SelectedIndex >= Sites.Count) SelectedIndex = Math.Max(0, Sites.Count - 1);
        Save();
    }

    public void Select(PortalSite site)
    {
        var i = Sites.IndexOf(site);
        if (i >= 0) { SelectedIndex = i; Save(); }
    }

    private static string Normalize(string url)
    {
        url = url?.Trim() ?? "";
        if (url.Length == 0) return "";
        return url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : "https://" + url;
    }
}
