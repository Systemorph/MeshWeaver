using System.Text.Json;
using Microsoft.Maui.Storage;

namespace Memex.Client.Services;

/// <summary>One configured memex instance: a portal base URL + the OAuth-minted token for it.</summary>
public sealed class MemexInstance
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string? Token { get; set; }
    public bool IsAuthenticated => !string.IsNullOrEmpty(Token);
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

    /// <summary>The public, shared memex — seeded as the default instance on first launch.</summary>
    public const string PublicMemexUrl = "https://memex.meshweaver.cloud";

    public List<MemexInstance> Instances { get; private set; } = new();

    public InstanceStore() => Load();

    private void Load()
    {
        var json = Preferences.Default.Get(Key, "");
        Instances = string.IsNullOrEmpty(json)
            ? new()
            : JsonSerializer.Deserialize<List<MemexInstance>>(json) ?? new();

        // One-time seed of the public memex so the list isn't empty on first launch. Guarded by a
        // flag so removing it doesn't bring it back on the next start.
        if (!Preferences.Default.Get(SeededKey, false))
        {
            Preferences.Default.Set(SeededKey, true);
            if (Instances.Count == 0)
            {
                Instances.Add(new MemexInstance { Name = "memex", Url = PublicMemexUrl });
                Save();
            }
        }
    }

    public void Save() => Preferences.Default.Set(Key, JsonSerializer.Serialize(Instances));

    public MemexInstance Add(string name, string url)
    {
        url = Normalize(url);
        var inst = new MemexInstance { Name = string.IsNullOrWhiteSpace(name) ? url : name.Trim(), Url = url };
        Instances.Add(inst);
        Save();
        return inst;
    }

    public void Remove(MemexInstance inst)
    {
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
