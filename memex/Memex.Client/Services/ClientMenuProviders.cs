using MeshWeaver.Mesh;

namespace Memex.Client.Services;

/// <summary>
/// A client-side, app-level menu provider — the native shell's counterpart to the platform's
/// <c>INodeMenuProvider</c> (which serves a mesh node's <c>Node</c>/<c>Mesh</c>/<c>AI</c> menus through
/// <c>hub.GetMenu</c>). Some portal menus aren't a mesh node's layout area at all but app destinations
/// (Settings, the user/account menu); those come from these providers instead. The shell renders BOTH
/// sources through the SAME DevExpress popup mechanism, so the shell hardcodes <b>no</b> menu item list:
/// adding an entry to a context means adding it to that context's provider, never editing the shell.
/// </summary>
/// <remarks>
/// Items use the same <see cref="NodeMenuItemDefinition"/> contract as the platform menus. App-only
/// destinations (no mesh node area) carry an <see cref="NodeMenuItemDefinition.Area"/> with the
/// <c>"client:"</c> convention (e.g. <c>"client:settings"</c>) that the shell intercepts and opens the
/// matching native view/page; everything else stays generic node-area navigation.
/// </remarks>
public interface IClientMenuProvider
{
    /// <summary>The menu context this provider contributes to (e.g. <c>"Settings"</c>, <c>"User"</c>).</summary>
    string Context { get; }

    /// <summary>This provider's complete set of items for its context.</summary>
    IReadOnlyList<NodeMenuItemDefinition> GetItems();
}

/// <summary>Well-known <c>client:</c> destinations the shell maps to native views/pages.</summary>
public static class ClientDestinations
{
    public const string Prefix = "client:";
    public const string Settings = "client:settings";
    public const string Voice = "client:voice";
    public const string Instances = "client:instances";
    public const string Profile = "client:profile";

    /// <summary>Model providers &amp; keys — reserved for a later Settings entry.</summary>
    public const string ModelProviders = "client:models";
}

/// <summary>
/// The Settings (⚙) menu — provider-driven, NOT hardcoded in the shell. Today it carries "Zoom &amp;
/// display" (the native <c>SettingsPage</c>); a "Model providers &amp; keys" entry slots in here later
/// (see <see cref="ClientDestinations.ModelProviders"/>) without touching the shell.
/// </summary>
public sealed class SettingsMenuProvider : IClientMenuProvider
{
    public string Context => "Settings";

    public IReadOnlyList<NodeMenuItemDefinition> GetItems() =>
    [
        new("Zoom & display", ClientDestinations.Settings, Icon: "🔍", Order: 0,
            Tooltip: "Text size and display zoom"),
        // Room for: new("Model providers & keys", ClientDestinations.ModelProviders, Icon: "🔑", Order: 10),
    ];
}

/// <summary>
/// The User/account (👤) menu — provider-driven (the user is "also a menu"). My profile (the device
/// user's node area), Voice (the on-device <c>VoiceView</c>), and Manage instances (<c>InstanceManagerView</c>).
/// </summary>
public sealed class UserMenuProvider : IClientMenuProvider
{
    public string Context => "User";

    public IReadOnlyList<NodeMenuItemDefinition> GetItems() =>
    [
        new("My profile", ClientDestinations.Profile, Icon: "👤", Order: 0),
        new("Voice", ClientDestinations.Voice, Icon: "🎙", Order: 10),
        new("Manage instances", ClientDestinations.Instances, Icon: "🧩", Order: 20),
    ];
}
