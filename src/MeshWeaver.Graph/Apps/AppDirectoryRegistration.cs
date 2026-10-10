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
/// <c>Home:AppSource</c> — where the home's Apps band reads its tiles from. An open vocabulary
/// (policy <c>open-vocabulary-string-constants</c>): an unknown value falls back to
/// <see cref="Directory"/>, the default, and the home's render logs it by name
/// (<see cref="IsKnown"/>). <see cref="Records"/> stays selectable as an explicit escape hatch
/// while the per-user <c>_App</c> records still exist (MeshWeaver.Plugins Store/AppsOnTheInstance §8).
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
    /// <see cref="Records"/> — absent, <see cref="Directory"/>, or an unknown value. Pure.</summary>
    public static bool IsDirectory(string? value) =>
        !string.Equals(value?.Trim(), Records, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="value"/> is absent or one of this vocabulary's values —
    /// anything else is a configuration the deployment should hear about. Pure.</summary>
    public static bool IsKnown(string? value) =>
        string.IsNullOrWhiteSpace(value)
        || string.Equals(value.Trim(), Directory, StringComparison.OrdinalIgnoreCase)
        || string.Equals(value.Trim(), Records, StringComparison.OrdinalIgnoreCase);
}
