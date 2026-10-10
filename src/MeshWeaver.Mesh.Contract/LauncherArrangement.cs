using System.Collections.Immutable;
using System.ComponentModel;
using System.Text.Json;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Mesh;

/// <summary>
/// A viewer's ARRANGEMENT of the launcher — the one piece of launcher state that is theirs. With
/// the app directory (<c>Home:AppSource = Directory</c>) the apps a viewer sees are computed from
/// the instance's app roots and the access check, and nothing about them is stored per user except
/// this: where the viewer put each tile. ONE node per viewer at
/// <see cref="LauncherArrangementPaths.PathFor"/> (<c>{user}/_Settings/Launcher</c>), written only
/// when the viewer rearranges — plus one seed, taken once from the viewer's legacy
/// <c>{user}/_App</c> records the first time the directory renders for them.
/// </summary>
public sealed record LauncherArrangement
{
    /// <summary>Per app id (the app root's path, or a built-in's id): the tile's placement.</summary>
    public ImmutableDictionary<string, LauncherEntry> Entries { get; init; } =
        ImmutableDictionary.Create<string, LauncherEntry>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The placement of <paramref name="appId"/>, or <c>null</c> when the viewer never
    /// placed it. Pure.</summary>
    public LauncherEntry? For(string appId) =>
        Entries.TryGetValue(appId, out var entry) ? entry : null;

    /// <summary>This arrangement with <paramref name="appId"/> placed in <paramref name="group"/>
    /// at <paramref name="order"/> — flagged as the viewer's own choice. Pure.</summary>
    public LauncherArrangement Place(string appId, string group, int order) =>
        this with
        {
            Entries = Entries.SetItem(appId, new LauncherEntry { Group = group, Order = order, CustomGroup = true }),
        };
}

/// <summary>One tile's placement on the viewer's launcher — the same three values the legacy
/// <see cref="App"/> record carried (<see cref="App.Group"/>, <see cref="App.Order"/> and the
/// <c>customGroup</c> flag).</summary>
public sealed record LauncherEntry
{
    /// <summary>The group the tile sits in. <c>null</c> = never grouped (the app's category
    /// applies); <c>""</c> = deliberately ungrouped.</summary>
    public string? Group { get; init; }

    /// <summary>Position inside the group (lower = earlier); <c>0</c> = never placed.</summary>
    public int Order { get; init; }

    /// <summary>Whether the group is the viewer's own choice rather than a copied category.</summary>
    [Browsable(false)]
    public bool CustomGroup { get; init; }
}

/// <summary>
/// Where the launcher arrangement lives, the reserved virtual namespace the app directory answers,
/// and the one write that rearranges a directory tile.
/// </summary>
public static class LauncherArrangementPaths
{
    /// <summary>The NodeType of the arrangement node.</summary>
    public const string NodeType = "LauncherArrangement";

    /// <summary>The settings segment under a user's home (shared with the other per-user settings).</summary>
    public const string SettingsSegment = "_Settings";

    /// <summary>The arrangement node's id inside <see cref="SettingsSegment"/>.</summary>
    public const string NodeId = "Launcher";

    /// <summary>
    /// The RESERVED virtual namespace the app directory answers: <c>{user}/_Apps/{appId}</c>. Nothing
    /// is stored there — every row is synthesized from the viewer's directory. Distinct from the
    /// legacy record namespace <c>_App</c>, so the two sources never collide.
    /// </summary>
    public const string DirectoryNamespace = "_Apps";

    /// <summary>The arrangement node of <paramref name="userId"/>: <c>{user}/_Settings/Launcher</c>.</summary>
    public static string PathFor(string userId) => $"{userId}/{SettingsSegment}/{NodeId}";

    /// <summary>The settings namespace of <paramref name="userId"/>: <c>{user}/_Settings</c>.</summary>
    public static string SettingsNamespaceFor(string userId) => $"{userId}/{SettingsSegment}";

    /// <summary>The virtual directory namespace of <paramref name="userId"/>: <c>{user}/_Apps</c>.</summary>
    public static string DirectoryNamespaceFor(string userId) => $"{userId}/{DirectoryNamespace}";

    /// <summary>
    /// A directory row's id for an app — the app id with <c>/</c> made path-safe, so a nested app
    /// root still yields exactly one row under <c>{user}/_Apps</c>. Pure.
    /// </summary>
    public static string RowIdFor(string appId) => appId.Replace('/', '~');

    /// <summary>The app id a row id stands for (the inverse of <see cref="RowIdFor"/>). Pure.</summary>
    public static string AppIdOfRow(string rowId) => rowId.Replace('~', '/');

    /// <summary>
    /// Whether <paramref name="path"/> is a directory row (<c>{user}/_Apps/{rowId}</c>), and whose
    /// and which app it stands for. Pure.
    /// </summary>
    public static bool TryParseRow(string? path, out string owner, out string appId)
    {
        owner = "";
        appId = "";
        if (string.IsNullOrEmpty(path))
            return false;
        var parts = path.Trim('/').Split('/');
        if (parts.Length != 3 || !string.Equals(parts[1], DirectoryNamespace, StringComparison.Ordinal)
            || parts[0].Length == 0 || parts[2].Length == 0)
            return false;
        owner = parts[0];
        appId = AppIdOfRow(parts[2]);
        return true;
    }

    /// <summary>
    /// The arrangement a write starts from: a fresh one only when the node has NO content; content
    /// that is present but cannot be read as an arrangement fails LOUDLY — starting fresh there
    /// would replace every saved placement with the one being written.
    /// </summary>
    private static LauncherArrangement Current(MeshNode node, string path, JsonSerializerOptions options)
    {
        if (node.Content is null)
            return new LauncherArrangement();
        return node.ContentAs<LauncherArrangement>(options)
            ?? throw new InvalidOperationException(
                $"The launcher arrangement at '{path}' holds content that is not a {nameof(LauncherArrangement)}; refusing to overwrite it.");
    }

    /// <summary>
    /// Rearranges one directory tile: the placement is written onto the viewer's arrangement node
    /// (through the one mutation API), never onto the virtual row, which has no store behind it.
    /// Cold — the write happens on Subscribe. Errors when <paramref name="rowPath"/> is not a
    /// directory row.
    /// </summary>
    public static IObservable<MeshNode> Place(
        IMeshNodeStreamCache cache, string rowPath, string group, int order, JsonSerializerOptions options)
    {
        if (!TryParseRow(rowPath, out var owner, out var appId))
            return System.Reactive.Linq.Observable.Throw<MeshNode>(
                new ArgumentException($"'{rowPath}' is not an app directory row", nameof(rowPath)));
        var path = PathFor(owner);
        return cache.Update(path, current => current with
        {
            Content = Current(current, path, options).Place(appId, group, order),
        }, options);
    }
}
