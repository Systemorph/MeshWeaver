using System.Collections.Immutable;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Memex.Portal.Shared.Setup;

/// <summary>
/// Which storage backends THIS IMAGE can actually open — the keys of its registered
/// <c>IStorageAdapterFactory</c> services, captured while the service collection is still in hand.
///
/// <para>🚨 <b>Captured, because a key set cannot be recovered later.</b> An
/// <see cref="IServiceProvider"/> can resolve a keyed service you name but cannot enumerate the
/// keys, so a surface that asked at request time could only guess. This is read off the
/// <see cref="IServiceCollection"/> during mesh configuration — after
/// <c>InstallAssemblies</c>, which is when a module's assembly attribute registers its factory —
/// and handed to the setup surface as data.</para>
///
/// <para><b>This is the rule <c>InstanceStorageSelection.Type</c> already states:</b> <i>"resolved
/// against the KEYED IStorageAdapterFactory registrations this image ships. Discovered, never
/// hardcoded: an image without the Cosmos module must not be able to record Cosmos here."</i> A
/// recorded backend the image cannot resolve is a durable answer that fails at the NEXT boot with
/// <c>Unknown storage type</c> — after the wizard is gone.</para>
/// </summary>
/// <param name="Types">The registered keys, in the order the wizard should offer them.</param>
public sealed record StorageBackendCatalog(ImmutableList<string> Types)
{
    /// <summary>A host that captured nothing. Renders as "this image ships no storage backend",
    /// which is a real (if broken) image state and better said than shown as an empty list.</summary>
    public static StorageBackendCatalog Empty { get; } = new([]);

    /// <summary>
    /// The keys currently registered on <paramref name="services"/>.
    ///
    /// <para>Keys are string-keyed by convention (<c>FileSystem</c>, <c>PostgreSql</c>,
    /// <c>Sqlite</c>, <c>Cosmos</c>, <c>Snowflake</c>); a non-string key cannot be a
    /// <c>Graph:Storage:Type</c> value and is skipped rather than stringified into something the
    /// factory lookup would then miss. <c>KeyedService.AnyKey</c> is skipped for the
    /// same reason — it is a wildcard registration, not a backend anyone can name.</para>
    /// </summary>
    /// <param name="services">The collection to read. Never null.</param>
    public static StorageBackendCatalog Discover(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var types = services
            .Where(sd => sd.ServiceType == typeof(IStorageAdapterFactory) && sd.IsKeyedService)
            .Select(sd => sd.ServiceKey)
            .OfType<string>()
            .Where(key => !string.IsNullOrWhiteSpace(key)
                && !ReferenceEquals(key, KeyedService.AnyKey))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToImmutableList();
        return new StorageBackendCatalog(types);
    }

    /// <summary>Whether this image can open <paramref name="type"/>. The gate the setup surface
    /// applies before it writes a manifest — a refusal here is a form error the operator can fix,
    /// which is the only place the mistake is still cheap.</summary>
    /// <param name="type">The candidate <c>Graph:Storage:Type</c>.</param>
    public bool Offers(string? type) =>
        !string.IsNullOrWhiteSpace(type)
        && Types.Any(t => string.Equals(t, type.Trim(), StringComparison.OrdinalIgnoreCase));
}
