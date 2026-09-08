using System.Reactive.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// One bundle the registry has PUSHED to the fleet's OCI registry, as recorded when the publication
/// was registered (<c>Doc/Architecture/PluginBundlesInTheRegistry</c>, "Publishing").
/// </summary>
/// <param name="Source">The source the bundle was published from (<c>Plugins</c>, <c>Education</c>),
/// or null when the record does not say.</param>
/// <param name="Package">The package id.</param>
/// <param name="Version">The bundle's version — the content version from <c>manifest.lock</c>, the
/// same value the bundle index and the catalog advertise for it.</param>
/// <param name="Reference">The digest-addressed reference a consumer fetches by:
/// <c>cr.meshweaver.cloud/plugins/&lt;source&gt;/&lt;package&gt;@sha256:…</c>.</param>
public sealed record PublicationArtifact(string? Source, string Package, string Version, string Reference);

/// <summary>
/// The registry-side record of pushed publications — what lets the bundle index
/// (<c>/api/plugins/bundles/index.json</c>) and the catalog index (<c>/api/plugins</c>) name an
/// <c>artifact</c> per bundle. The read is a seam: the registry's endpoints ask, and whatever a
/// host registers answers. The platform's default answers NONE, so the field appears only once a
/// deployment records pushes; a consumer reading <c>null</c> takes the HTTP bundle route exactly
/// as before.
/// </summary>
public interface IPublicationArtifacts
{
    /// <summary>Every recorded artifact. Cold; emits at least once; an empty list is the ordinary
    /// answer on a registry that has pushed nothing.</summary>
    IObservable<IReadOnlyList<PublicationArtifact>> Read() =>
        Observable.Return((IReadOnlyList<PublicationArtifact>)[]);
}

/// <summary>The platform default: no publication has been pushed, so no bundle carries an
/// <c>artifact</c>. Registered by <c>AddPluginCatalog</c> unless a host registered its own.</summary>
public sealed class NoPublicationArtifacts : IPublicationArtifacts
{
}

/// <summary>The one match rule both indexes apply to a recorded artifact.</summary>
public static class PublicationArtifactLookup
{
    /// <summary>Registers <see cref="NoPublicationArtifacts"/> unless a host already registered
    /// its own <see cref="IPublicationArtifacts"/> — TryAdd, so the host's answer wins.</summary>
    public static IServiceCollection WithPublicationArtifactsDefault(this IServiceCollection services)
    {
        services.TryAddSingleton<IPublicationArtifacts, NoPublicationArtifacts>();
        return services;
    }

    /// <summary>
    /// The reference recorded for <paramref name="package"/> at one of <paramref name="versions"/>
    /// (tried in order; nulls skipped), or null. The source is compared only when BOTH sides state
    /// one — a record without a source matches the package wherever it is served from, and an
    /// entry without one is matched on package and version alone.
    /// </summary>
    public static string? ReferenceFor(
        this IReadOnlyList<PublicationArtifact> artifacts, string? source, string package,
        params string?[] versions)
    {
        foreach (var version in versions)
        {
            if (string.IsNullOrWhiteSpace(version))
                continue;
            var match = artifacts.FirstOrDefault(a =>
                string.Equals(a.Package, package, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.Version, version, StringComparison.Ordinal)
                && (string.IsNullOrWhiteSpace(a.Source) || string.IsNullOrWhiteSpace(source)
                    || string.Equals(a.Source, source, StringComparison.OrdinalIgnoreCase)));
            if (match is not null)
                return match.Reference;
        }
        return null;
    }
}
