using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeshWeaver.GitSync;

/// <summary>
/// Wiring for the GitHub sync feature. Three entry points the host composes:
/// <list type="bullet">
///   <item><see cref="AddGitHubSyncServices"/> — DI (repo client, credential service,
///     sync service, OAuth service) on the app service collection;</item>
///   <item><see cref="AddGitHubSyncTypes{TBuilder}"/> — registers the
///     <see cref="GitHubCredential"/> / <see cref="GitHubSyncConfig"/> content types on
///     the mesh hub + every per-node hub so their MeshNode content (de)serializes;</item>
///   <item><see cref="GitHubSyncSettingsTab.AddGitHubSyncSettingsTab"/> — the Space-settings GUI tab.</item>
/// </list>
/// The OAuth client id is bound separately by the host from <c>GitHub:OAuth</c>
/// (where <c>IConfiguration</c> is available); absent a client id the Connect flow is
/// gracefully disabled.
/// </summary>
public static class GitHubSyncConfiguration
{
    /// <summary>Registers the GitHub sync services as mesh-scoped singletons (mirrors <c>ModelProviderService</c>).</summary>
    public static IServiceCollection AddGitHubSyncServices(this IServiceCollection services)
    {
        services.AddOptions<GitHubOAuthOptions>();
        services.AddSingleton<IGitHubRepoClient, OctokitGitHubRepoClient>();
        services.AddSingleton<GitHubCredentialService>();
        services.AddSingleton<GitHubSyncService>();
        // Factory so the optional HttpClient param is explicitly defaulted (the service
        // creates its own) — no bare-HttpClient registration required in the container.
        services.AddSingleton(sp => new GitHubOAuthService(
            sp.GetRequiredService<IoPoolRegistry>(),
            sp.GetRequiredService<IOptions<GitHubOAuthOptions>>(),
            sp.GetService<ILogger<GitHubOAuthService>>()));
        return services;
    }

    /// <summary>
    /// Registers the GitHub sync content types on the mesh hub AND every per-node hub so
    /// the <c>{userId}/_Provider/GitHub</c> credential and <c>{spaceId}/_GitSync</c> config
    /// nodes (de)serialize with a resolvable <c>$type</c> (avoids the content-discriminator
    /// reject + JsonElement degradation).
    /// </summary>
    public static TBuilder AddGitHubSyncTypes<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        // NodeType definitions so CreateNode accepts these node types and each node's hub
        // deserializes its content (mirrors ModelProviderNodeType). Hidden from search + create.
        builder.AddMeshNodes(
            new MeshNode(GitHubCredentialService.NodeType)
            {
                Name = "GitHub Credential",
                IsSatelliteType = false,
                ExcludeFromContext = new HashSet<string> { "search", "create" },
                HubConfiguration = config => config
                    .AddMeshDataSource(source => source.WithContentType<GitHubCredential>()),
            },
            new MeshNode(GitHubSyncService.ConfigNodeType)
            {
                Name = "GitHub Sync Config",
                IsSatelliteType = false,
                ExcludeFromContext = new HashSet<string> { "search", "create" },
                HubConfiguration = config => config
                    .AddMeshDataSource(source => source.WithContentType<GitHubSyncConfig>()),
            });

        // Also register the content types on the mesh hub + every per-node hub so reads via
        // GetQuery / GetMeshNodeStream resolve the typed content (and the $type discriminator).
        builder.ConfigureHub(c => c
            .WithType<GitHubCredential>(nameof(GitHubCredential))
            .WithType<GitHubSyncConfig>(nameof(GitHubSyncConfig)));
        builder.ConfigureDefaultNodeHub(c => c
            .WithType<GitHubCredential>(nameof(GitHubCredential))
            .WithType<GitHubSyncConfig>(nameof(GitHubSyncConfig)));
        return builder;
    }
}
