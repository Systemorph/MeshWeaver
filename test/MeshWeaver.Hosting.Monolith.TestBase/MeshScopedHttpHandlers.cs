using System;
using System.Collections.Immutable;
using System.Net.Http;
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
/// enough to need the DNS refresh that handler rotation exists for.</para>
///
/// <para><b>Teardown.</b> The expiry timer was also the factory's ONLY route to disposing a handler
/// pipeline and the service scope it was built in: the factory is not disposable, and a client it
/// creates does not own its handler. Without the timer nothing would ever dispose them. So the mesh
/// owns them instead: a <see cref="MeshHttpHandlerTeardown"/> singleton records the outermost handler
/// of every pipeline and its handler scope, and disposes both when the mesh's container is disposed.
/// Pinned, collectability and disposal alike, by <c>TestMeshHttpHandlersDieWithTheMeshTest</c>.</para>
/// </summary>
public static class MeshScopedHttpHandlers
{
    /// <summary>
    /// Sets every HTTP client's handler lifetime in <paramref name="services"/> to infinite, so the
    /// factory starts no expiry timer that would keep the container reachable, and makes the
    /// container dispose every handler pipeline and handler scope the factory built.
    /// </summary>
    /// <param name="services">The mesh's service collection.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection WithHandlersScopedToTheMesh(this IServiceCollection services)
    {
        services.AddSingleton<MeshHttpHandlerTeardown>();
        return services.ConfigureHttpClientDefaults(http => http
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan)
            .ConfigureAdditionalHttpMessageHandlers((handlers, handlerServices) =>
                handlers.Insert(0, handlerServices.GetRequiredService<MeshHttpHandlerTeardown>()
                    .Own(new PipelineHead(), handlerServices))));
    }

    /// <summary>
    /// The outermost handler of a pipeline. Disposing it disposes every inner handler, down to the
    /// primary one, because a <see cref="DelegatingHandler"/> disposes its inner handler.
    /// </summary>
    private sealed class PipelineHead : DelegatingHandler;
}

/// <summary>
/// Owns, for one mesh container, the handler pipelines and handler scopes its
/// <c>IHttpClientFactory</c> built, and disposes them with the container. See
/// <see cref="MeshScopedHttpHandlers"/>.
/// </summary>
/// <param name="root">The container this teardown is registered in.</param>
public sealed class MeshHttpHandlerTeardown(IServiceProvider root) : IDisposable
{
    private ImmutableList<IDisposable> owned = ImmutableList<IDisposable>.Empty;

    /// <summary>
    /// Records <paramref name="pipelineHead"/> and the handler scope behind
    /// <paramref name="handlerServices"/> for disposal with the container.
    /// </summary>
    /// <param name="pipelineHead">The outermost handler of the pipeline being built.</param>
    /// <param name="handlerServices">The provider the pipeline is built from: the handler scope's,
    /// or the root's when the client suppresses its handler scope (then nothing extra is owned).</param>
    /// <returns><paramref name="pipelineHead"/>, for inserting into the pipeline.</returns>
    internal DelegatingHandler Own(DelegatingHandler pipelineHead, IServiceProvider handlerServices)
    {
        ImmutableInterlocked.Update(ref owned, list => list.Add(pipelineHead));
        // The factory's handler scope hands its own provider to the pipeline builder, and that
        // provider IS the scope: disposing it disposes the scoped services the handlers resolved.
        if (!ReferenceEquals(handlerServices, root) && handlerServices is IDisposable scope)
            ImmutableInterlocked.Update(ref owned, list => list.Add(scope));
        return pipelineHead;
    }

    /// <summary>Disposes every owned pipeline, then every owned handler scope.</summary>
    public void Dispose()
    {
        var toDispose = Interlocked.Exchange(ref owned, ImmutableList<IDisposable>.Empty);
        foreach (var disposable in toDispose)
            disposable.Dispose();
    }
}
