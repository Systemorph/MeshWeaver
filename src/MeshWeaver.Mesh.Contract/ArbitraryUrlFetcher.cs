using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Mesh;

/// <summary>
/// A declaration that the named <c>HttpClient</c> <see cref="ClientName"/> fetches ARBITRARY URLs —
/// addresses taken from user data, an agent's input or a document, not a service the platform
/// operates. Declared with <see cref="ArbitraryUrlFetcherExtensions.DeclareArbitraryUrlFetcher"/>.
///
/// <para>What the declaration changes (policy <c>arbitrary-url-fetch-failure-is-warning</c>): when the
/// host's shared HTTP resilience pipeline gives up on a request this client sent — the final exhausted
/// retry, the breaker opening — the resilience telemetry line is written at <c>Warning</c> instead of
/// <c>Error</c>. A remote site that resets its TLS handshake, refuses the connection or times out is a
/// fact about that site, not a defect of the platform, and the fleet's log watcher turns every
/// <c>Error</c> line into an incident. A client that does NOT declare itself keeps <c>Error</c>: a
/// platform or service client giving up IS an incident.</para>
///
/// <para>A registry and not a list of names in the host: the client that knows what it fetches says so
/// where it registers itself, and the host never has to know the module exists.</para>
/// </summary>
public interface IArbitraryUrlFetcher
{
    /// <summary>The name the client is created with (<c>IHttpClientFactory.CreateClient(name)</c>).</summary>
    string ClientName { get; }
}

/// <summary>Declares a named <c>HttpClient</c> as an <see cref="IArbitraryUrlFetcher"/>.</summary>
public static class ArbitraryUrlFetcherExtensions
{
    /// <summary>
    /// Declares that the named client <paramref name="clientName"/> fetches arbitrary, user-supplied
    /// URLs, so the host's resilience telemetry reports its exhausted retries at <c>Warning</c>
    /// (<see cref="IArbitraryUrlFetcher"/>). Registers the declaration only; the client itself is
    /// registered as usual.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="clientName">The name the client is created with.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    public static IServiceCollection DeclareArbitraryUrlFetcher(this IServiceCollection services, string clientName)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientName);
        services.AddSingleton<IArbitraryUrlFetcher>(new ArbitraryUrlFetcherDeclaration(clientName));
        return services;
    }

    private sealed class ArbitraryUrlFetcherDeclaration(string clientName) : IArbitraryUrlFetcher
    {
        public string ClientName { get; } = clientName;
    }
}
