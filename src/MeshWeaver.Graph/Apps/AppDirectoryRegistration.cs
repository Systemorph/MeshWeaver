using System.Collections.Immutable;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.Apps;

/// <summary>
/// Wires the app directory: the <see cref="LauncherArrangementPaths.NodeType"/> node type (typed in
/// the static registry), the process-wide <see cref="AppDirectory"/>, its warm per-viewer
/// <see cref="AppDirectoryCache"/>, the arrangement reader, and the query provider that answers
/// <c>{user}/_Apps</c>. Registered on every mesh; what the home READS is chosen by
/// <c>Home:AppSource</c> (<see cref="HomeAppSource"/>).
/// </summary>
public static class AppDirectoryRegistration
{
    /// <summary>Registers the app directory on the mesh builder.</summary>
    public static TBuilder AddAppDirectory<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateArrangementNodeType());
        builder.AddAutocompleteExcludedTypes(LauncherArrangementPaths.NodeType);
        builder.ConfigureHub(config => config
            .WithType<LauncherArrangement>(nameof(LauncherArrangement))
            .WithType<LauncherEntry>(nameof(LauncherEntry)));
        builder.ConfigureServices(services => services
            .AddSingleton<AppDirectory>()
            .AddSingleton<AppDirectoryCache>()
            .AddSingleton<LauncherArrangementSource>()
            // A plain AddSingleton, never TryAdd: providers are an enumerable fan-in.
            .AddSingleton<IMeshQueryProvider>(sp => new AppDirectoryQueryProvider(sp)));
        return builder;
    }

    /// <summary>The <see cref="LauncherArrangementPaths.NodeType"/> definition: one system-managed,
    /// user-owned node per viewer at <c>{user}/_Settings/Launcher</c>, beside the other per-user
    /// settings — excluded from search, create and content listings.</summary>
    public static MeshNode CreateArrangementNodeType() => new(LauncherArrangementPaths.NodeType)
    {
        Name = "Launcher Arrangement",
        Icon = "/static/NodeTypeIcons/layout.svg",
        IsSatelliteType = true,
        ExcludeFromContext = ImmutableHashSet.Create("search", "create", "content"),
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<LauncherArrangement>())
    };
}

/// <summary>
/// <c>Home:AppSource</c> — where the home's Apps band reads its tiles from. A CLOSED two-value
/// deployment setting, not an extensible vocabulary: nothing can claim a third value. Absent means
/// the default, <see cref="Directory"/>; <see cref="Records"/> is the explicit escape hatch while the
/// per-user <c>_App</c> records still exist (MeshWeaver.Plugins Store/AppsOnTheInstance §8). Any
/// other value is a configuration ERROR (<see cref="IsKnown"/>): the home's render logs it as an
/// error naming the key and serves the default rather than failing the page — a typo in a
/// deployment record must not take every home down.
/// </summary>
public static class HomeAppSource
{
    /// <summary>The configuration key.</summary>
    public const string ConfigKey = "Home:AppSource";

    /// <summary>The viewer's own <c>{user}/_App</c> records — the legacy source, only when set explicitly.</summary>
    public const string Records = "Records";

    /// <summary>The app directory — the apps the viewer can see, via <c>{user}/_Apps</c> (the default).</summary>
    public const string Directory = "Directory";

    /// <summary>Whether <paramref name="value"/> selects the directory: everything except an explicit
    /// <see cref="Records"/>. Absent and <see cref="Directory"/> select it by definition; an invalid
    /// value (<see cref="IsKnown"/> false) is an error the caller reports, and gets the default. Pure.</summary>
    public static bool IsDirectory(string? value) =>
        !string.Equals(value?.Trim(), Records, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="value"/> is a valid setting: absent, <see cref="Directory"/> or
    /// <see cref="Records"/>. Anything else is a configuration error. Pure.</summary>
    public static bool IsKnown(string? value) =>
        string.IsNullOrWhiteSpace(value)
        || string.Equals(value.Trim(), Directory, StringComparison.OrdinalIgnoreCase)
        || string.Equals(value.Trim(), Records, StringComparison.OrdinalIgnoreCase);
}
