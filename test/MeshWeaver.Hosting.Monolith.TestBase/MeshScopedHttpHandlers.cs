using System.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Hosting.Monolith.TestBase;

/// <summary>
/// Makes a test mesh's HTTP message handlers live exactly as long as the mesh.
///
/// <para><b>Why.</b> <c>IHttpClientFactory</c> gives every handler it builds a lifetime, two minutes
/// by default. It enforces that with a <see cref="Timer"/>, and the timer's state is the handler's
/// tracking entry. The entry holds the service scope the handler was built in, and the factory holds
/// the container. The process-wide timer queue therefore holds the WHOLE mesh until the lifetime
/// expires, whether or not the mesh was disposed. A production mesh lives as long as its process,
/// so that is harmless there. A test host creates and disposes meshes at a high rate, and each one
/// stayed reachable for two minutes after its test ended. That came to about 80 disposed meshes
/// at once in <c>Memex.Portal.Shared.Test</c>. In a heap dump, 68 of 84 live mesh type registries
/// were rooted through <c>ActiveHandlerTrackingEntry</c> or <c>DefaultHttpClientFactory</c>. Most
/// were from the <c>deployment-report</c> client, which every mesh creates when it is constructed.
/// That retention took the host past the 6 GiB memory watchdog.</para>
///
/// <para><b>The shape.</b> With an INFINITE handler lifetime the factory starts no expiry timer, so
/// nothing outside the mesh refers to it and it is collected with the mesh. No test mesh lives long
/// enough to need the DNS refresh that handler rotation exists for. Pinned by
/// <c>TestMeshHttpHandlersDieWithTheMeshTest</c>.</para>
/// </summary>
public static class MeshScopedHttpHandlers
{
    /// <summary>
    /// Sets every HTTP client's handler lifetime in <paramref name="services"/> to infinite, so the
    /// factory starts no expiry timer that would keep the container reachable.
    /// </summary>
    /// <param name="services">The mesh's service collection.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection WithHandlersScopedToTheMesh(this IServiceCollection services)
        => services.ConfigureHttpClientDefaults(http => http.SetHandlerLifetime(Timeout.InfiniteTimeSpan));
}
