using System.Text.Json;
using Microsoft.Maui.Storage;

namespace Memex.Client.Services;

/// <summary>One configured memex instance: a portal base URL + the OAuth-minted token for it.</summary>
public sealed class MemexInstance
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string? Token { get; set; }

    /// <summary>True for THIS app's own in-process monolith mesh on SQLite (the default instance — no URL,
    /// no sign-in; rendered natively against the local hub). False for remote (HTTP) memex instances.</summary>
    public bool IsLocal { get; set; }

    /// <summary>Local is always ready; a remote instance needs an OAuth-minted token.</summary>
    public bool IsAuthenticated => IsLocal || !string.IsNullOrEmpty(Token);
}

/// <summary>
/// The instances the user has added (the native management GUI's model), persisted locally — this is
/// the bootstrap layer: each instance is a base URL + an OAuth-minted token. Rich per-installation
/// config lives in the mesh on the MemexClient node once connected (see Doc/GUI/DataBindingMaui).
/// </summary>
public sealed class InstanceStore
{
    private const string Key = "memex.instances";
    private const string SeededKey = "memex.instances.seeded";

    /// <summary>Display name of the default in-process monolith/SQLite instance.</summary>
    public const string LocalName = "Local";

    /// <summary>The public, shared memex — seeded as an additional instance on first launch.</summary>
    public const string PublicMemexUrl = "https://memex.meshweaver.cloud";

    public List<MemexInstance> Instances { get; private set; } = new();

    /// <summary>The default in-process monolith/SQLite mesh — always present, always first.</summary>
    public MemexInstance Local => Instances.First(i => i.IsLocal);

    public InstanceStore() => Load();

    private void Load()
    {
        var json = Preferences.Default.Get(Key, "");
        Instances = string.IsNullOrEmpty(json)
            ? new()
            : JsonSerializer.Deserialize<List<MemexInstance>>(json) ?? new();

        // The app's OWN in-process monolith mesh on SQLite is ALWAYS present as the default "Local"
        // instance (no URL, no sign-in — it IS this app's mesh), and always first. Inserted here so it
        // exists on every launch (incl. older saved lists that predate it) and after the user removes
        // everything else. The portal renders against it by default.
        if (!Instances.Any(i => i.IsLocal))
        {
            Instances.Insert(0, new MemexInstance { Name = LocalName, IsLocal = true });
            Save();
        }

        // One-time seed of the public memex as an additional, connectable instance. Guarded by a flag so
        // removing it doesn't bring it back on the next start.
        if (!Preferences.Default.Get(SeededKey, false))
        {
            Preferences.Default.Set(SeededKey, true);
            if (!Instances.Any(i => i.Url == PublicMemexUrl))
            {
                Instances.Add(new MemexInstance { Name = "memex", Url = PublicMemexUrl });
                Save();
            }
        }
    }

    public void Save() => Preferences.Default.Set(Key, JsonSerializer.Serialize(Instances));

    public MemexInstance Add(string name, string url, string? token = null)
    {
        url = Normalize(url);
        var inst = new MemexInstance
        {
            Name = string.IsNullOrWhiteSpace(name) ? url : name.Trim(),
            Url = url,
            Token = string.IsNullOrWhiteSpace(token) ? null : token.Trim(),
        };
        Instances.Add(inst);
        Save();
        return inst;
    }

    public void Remove(MemexInstance inst)
    {
        if (inst.IsLocal) return;   // the in-process Local mesh is the app's own — never removable.
        Instances.Remove(inst);
        Save();
    }

    private static string Normalize(string url)
    {
        url = url?.Trim() ?? "";
        if (url.Length == 0) return "";
        return url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : "https://" + url;
    }
}
