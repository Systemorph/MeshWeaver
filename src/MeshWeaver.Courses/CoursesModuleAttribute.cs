using MeshWeaver.GitSync;
using MeshWeaver.Hosting.AspNetCore;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Threading;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

[assembly: MeshWeaver.Courses.CoursesMeshModule]
[assembly: MeshWeaver.Courses.CoursesEndpointModule]

namespace MeshWeaver.Courses;

/// <summary>
/// The mesh half of the Courses module: listing <c>MeshWeaver.Courses.dll</c> under
/// <c>Modules:Assemblies</c> registers <see cref="CourseAssetService"/> — the GitHub-contents
/// resolver behind the entitlement-gated asset endpoint.
///
/// <para>Course delivery is a PRODUCT concern, not platform: a deployment that hosts no courses
/// carries neither the resolver nor its route. Nothing else in the platform references these
/// types, so delisting is total.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class CoursesMeshModuleAttribute : MeshNodeProviderAttribute
{
    /// <inheritdoc />
    public override IEnumerable<MeshNode> Nodes =>
    [
        new MeshNode("MeshWeaver.Courses")
        {
            Name = "Course assets",
            NodeType = "ModuleDefinition",
        }
        .WithGlobalServiceRegistry(services => services.AddCourses()),
    ];
}

/// <summary>
/// The endpoint half: <c>GET /assets/{Space}/{path…}</c> maps through the host's
/// <c>app.MapMeshModuleEndpoints()</c>.
///
/// <para>This is the module's OWN surface, not the portal's client API — course assets exist only
/// where courses do — so delisting removing the route wholesale (a 404) is the right semantic.
/// Contrast the Observability and Speech ingest routes, which stay in the host behind an
/// optional-service 503 because clients are configured against them regardless.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class CoursesEndpointModuleAttribute : MeshEndpointProviderAttribute
{
    /// <inheritdoc />
    public override IEnumerable<Action<IEndpointRouteBuilder>> EndpointConfigurations =>
    [
        endpoints => endpoints.MapCourseAssets(),
    ];
}

/// <summary>
/// The module's registration surface. Production installs it via <c>Modules:Assemblies</c>
/// (<see cref="CoursesMeshModuleAttribute"/>); a test fixture or bespoke host calls
/// <see cref="AddCourses"/> for the identical registration — the two lanes must never drift.
/// </summary>
public static class CoursesExtensions
{
    /// <summary>The named <see cref="HttpClient"/> the asset resolver fetches GitHub contents with.</summary>
    public const string HttpClientName = "MeshWeaver.Courses.CourseAssets";

    /// <summary>
    /// Registers the course-asset resolver. It reads the GitHub App credentials the GitSync
    /// module already binds (<c>GitHub:App:*</c>) — a Space's assets resolve as the installation,
    /// with no per-user credential — and self-skips when none are configured.
    /// </summary>
    public static IServiceCollection AddCourses(this IServiceCollection services)
    {
        // Two constraints that pull in opposite directions, satisfied together:
        //
        // 🚨 SINGLETON, never AddHttpClient<CourseAssetService>. The service holds the resolved
        //    download_url promise cache as an INSTANCE field so concurrent requests for one file
        //    share a round-trip; a typed client registers T as TRANSIENT, which would give every
        //    request its own empty cache and silently defeat the whole design.
        //
        // 🚨 …but a singleton must not capture a bare HttpClient either. Holding one for the
        //    process lifetime pins a single HttpMessageHandler — no connection recycling, and DNS
        //    changes are never picked up (the classic long-lived-HttpClient trap). A NAMED client
        //    from IHttpClientFactory gives the factory's rotating handler pool while leaving the
        //    consumer a singleton, which is exactly the combination this needs.
        services.AddHttpClient(HttpClientName);
        services.AddSingleton(sp => new CourseAssetService(
            ioPools: sp.GetRequiredService<IoPoolRegistry>(),
            options: sp.GetRequiredService<IOptions<GitHubAppOptions>>(),
            appTokens: sp.GetService<GitHubAppTokenService>(),
            logger: sp.GetService<ILogger<CourseAssetService>>(),
            httpClient: sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName)));
        return services;
    }
}
