using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// 🚨 <b>What each source has published under one framework identity, read LIVE from the fleet
/// registry at the moment the gate runs</b> (policy <c>registry-fed-gate-reads-live-publication</c>).
///
/// <para>A published root filled by the <c>bundle-fetch</c> init container is a SNAPSHOT: the
/// publication <c>plugins/&lt;source&gt;:&lt;identity&gt;</c> as it stood when the running pod started.
/// That tag moves whenever the source publishes again under the same identity, and the pod that
/// starts on the next release pulls the tag as it stands THEN. A gate that counted the snapshot
/// therefore answered about a publication no future pod will pull: a package withdrawn since this
/// pod started still counted as sealed, and one added since was missing
/// (MeshWeaver#6622). This type reads the tag itself — the publication index, one manifest GET per
/// source — and names the packages its bundle entries carry, so the gate counts what the next pod
/// will actually fetch. The release record (<c>plugins/releases:&lt;version&gt;</c>) is not touched
/// and gains nothing: it still binds version → identity, and the identity's publications are read
/// where they live.</para>
///
/// <para><b>Three answers per source, because they decide different things.</b> Published (the
/// tag resolves to a publication index; its bundle entries are the sealed set), Absent (every
/// declared artifact host answered <c>404 MANIFEST_UNKNOWN</c> for the tag — an established
/// absence, so nothing is sealed for that source), and Unreadable (no mount declares an artifact
/// host, a host refused the key, the tag names something that is not a publication index, a
/// bundle entry names no package, or the read failed). Unreadable is never Absent: "I could not
/// look" holds as Indeterminate, it is never read as "nothing is published".</para>
///
/// <para>The key goes only to the artifact host the registry mount itself declares — the rule
/// <see cref="RegistryReleaseIdentities"/> and <see cref="PluginBundleClient"/> already apply.</para>
/// </summary>
public sealed class RegistryLivePublications
{
    /// <summary>The registry namespace every source's publication repository lives under.</summary>
    public const string PublicationRepositoryPrefix = "plugins/";

    /// <summary>The <c>artifactType</c> a publication index carries, and its sidecar entry.</summary>
    public const string PublicationArtifactType = OciRegistryClient.PublicationConfigMediaType;

    /// <summary>The <c>artifactType</c> of a bundle entry in a publication index.</summary>
    public const string BundleArtifactType = "application/vnd.meshweaver.bundle.v1+json";

    /// <summary>The index-entry annotation naming the package a bundle entry carries.</summary>
    public const string PackageAnnotation = "io.meshweaver.package";

    /// <summary>A publication index names one manifest per bundle; anything larger is not one.</summary>
    internal const int MaxIndexBytes = 4 * 1024 * 1024;

    private readonly IMessageHub hub;
    private readonly ILogger? logger;

    /// <summary>Creates the reader.</summary>
    /// <param name="hub">The mesh hub — supplies the I/O pools, the registry mounts
    /// (<see cref="PluginCatalogOptions"/>) and their credentials (<see cref="RegistryTokenResolver"/>).</param>
    /// <param name="logger">Optional.</param>
    public RegistryLivePublications(IMessageHub hub, ILogger? logger = null)
    {
        this.hub = hub;
        this.logger = logger;
    }

    /// <summary>
    /// Reads every source's live publication under <paramref name="identity"/>, one source at a time.
    /// </summary>
    /// <remarks>Cold; emits once; never faults — every failure is a source's
    /// <see cref="LiveSourcePublication.Refusal"/>, which the gate turns into Indeterminate.</remarks>
    /// <param name="identity">The framework identity the publications are tagged with.</param>
    /// <param name="sources">The source names, as the publisher named them.</param>
    public IObservable<LivePublicationReading> Read(string identity, IReadOnlyCollection<string> sources) =>
        sources
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.Ordinal)
            .Select(source => ReadSource(identity, source))
            .Concat()
            .ToList()
            .Select(read => new LivePublicationReading(identity, [.. read]));

    /// <summary>One source's live publication — the mounts asked in configuration order, the first
    /// that holds the tag answering.</summary>
    private IObservable<LiveSourcePublication> ReadSource(string identity, string source) =>
        Observable.Defer(() =>
        {
            var repository = PublicationRepositoryPrefix + source.ToLowerInvariant();
            if (!RegistryReleaseIdentities.IsSegment(source)
                || string.Equals(source, "releases", StringComparison.OrdinalIgnoreCase)
                || !RegistryReleaseIdentities.IsIdentity(identity))
                return Observable.Return(LiveSourcePublication.Unreadable(source,
                    $"'{repository}:{identity}' is not a publication the registry can name"));

            var options = hub.ServiceProvider.GetService<PluginCatalogOptions>();
            var resolver = hub.ServiceProvider.GetService<RegistryTokenResolver>();
            if (options is null || resolver is null)
                return Observable.Return(LiveSourcePublication.Unreadable(source,
                    "no plugin registry is configured, so the live publication cannot be read"));

            return RegistryTokenResolver.WithLegacyTokens(options, options.EffectiveRegistries)
                .Select(mount => FromMount(resolver, mount, repository, identity, source))
                .Concat()
                .ToList()
                .Select(answers => Decide(source, repository, identity, answers));
        })
        .Catch((Exception ex) => Observable.Return(LiveSourcePublication.Unreadable(source,
            $"reading the live publication failed ({ex.Message})")));

    /// <summary>One mount's answer: Published, Absent (404 on the tag), Unreadable, or null when
    /// the mount declares no artifact host and so has nothing to say.</summary>
    private IObservable<LiveSourcePublication?> FromMount(
        RegistryTokenResolver resolver, PluginRegistryReference mount, string repository, string identity, string source) =>
        RegistryReleaseIdentities.ArtifactHostOf(hub, resolver, mount, logger)
            .SelectMany(oci => oci is null
                ? Observable.Return<LiveSourcePublication?>(null)
                : oci.GetManifest(repository, identity)
                    .Select(manifest => (LiveSourcePublication?)FromIndex(source, $"{oci.Registry}/{repository}:{identity}", manifest)))
            .Catch((RegistryManifestUnknownException _) =>
                Observable.Return<LiveSourcePublication?>(LiveSourcePublication.Absent(source)))
            .Catch((Exception ex) => Observable.Return<LiveSourcePublication?>(LiveSourcePublication.Unreadable(source,
                $"{RegistryReleaseIdentities.HostOf(mount.Url)}: {ex.Message}")));

    private LiveSourcePublication Decide(
        string source, string repository, string identity, IList<LiveSourcePublication?> answers)
    {
        var said = answers.Where(a => a is not null).Select(a => a!).ToList();
        var decided = said.FirstOrDefault(a => a.IndexDigest is not null)
            ?? said.FirstOrDefault(a => a.Refusal is not null)
            ?? said.FirstOrDefault()
            ?? LiveSourcePublication.Unreadable(source,
                $"no registry mount declares an artifact host, so {repository}:{identity} cannot be read");
        logger?.LogInformation(
            "[ReleaseGate] live publication {Repository}:{Identity}: {State}",
            repository, identity,
            decided.IndexDigest is { } digest
                ? $"published {digest}, {decided.Packages.Length} package(s)"
                : decided.Refusal is { } refusal ? $"unreadable ({refusal})" : "absent (no tag)");
        return decided;
    }

    /// <summary>
    /// The packages a publication index names: every entry whose <c>artifactType</c> is a bundle's,
    /// by its <see cref="PackageAnnotation"/>. Pure. A manifest that is not a publication index, or
    /// a bundle entry naming no package, is Unreadable — never a smaller set, because a smaller set
    /// is the direction that reads a package as withdrawn.
    /// </summary>
    internal static LiveSourcePublication FromIndex(string source, string reference, OciManifest manifest)
    {
        if (manifest.Bytes.Length > MaxIndexBytes)
            return LiveSourcePublication.Unreadable(source, $"{reference} is larger than a publication index");
        try
        {
            using var document = JsonDocument.Parse(manifest.Bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !string.Equals(StringOf(root, "artifactType"), PublicationArtifactType, StringComparison.OrdinalIgnoreCase)
                || !root.TryGetProperty("manifests", out var entries) || entries.ValueKind != JsonValueKind.Array)
                return LiveSourcePublication.Unreadable(source, $"{reference} is not a publication index");

            var packages = ImmutableArray.CreateBuilder<string>();
            foreach (var entry in entries.EnumerateArray())
            {
                if (!string.Equals(StringOf(entry, "artifactType"), BundleArtifactType, StringComparison.OrdinalIgnoreCase))
                    continue;
                var package = entry.TryGetProperty("annotations", out var annotations)
                              && annotations.ValueKind == JsonValueKind.Object
                    ? StringOf(annotations, PackageAnnotation)
                    : null;
                if (string.IsNullOrWhiteSpace(package))
                    return LiveSourcePublication.Unreadable(source,
                        $"{reference} names a bundle entry with no '{PackageAnnotation}' annotation, so the "
                        + "packages it publishes cannot be named");
                packages.Add(package.Trim());
            }
            return new LiveSourcePublication(source, manifest.Digest, packages.ToImmutable(), null);
        }
        catch (JsonException ex)
        {
            return LiveSourcePublication.Unreadable(source, $"{reference} is not JSON ({ex.Message})");
        }
    }

    private static string? StringOf(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>One source's live publication under one identity.</summary>
/// <param name="Source">The source, as the publisher named it.</param>
/// <param name="IndexDigest">The publication index digest the tag resolved to, or null when the
/// source has no publication (Absent) or it could not be read (Unreadable).</param>
/// <param name="Packages">The packages the publication's bundle entries name; empty unless
/// published.</param>
/// <param name="Refusal">Why the publication could not be read, or null.</param>
public sealed record LiveSourcePublication(
    string Source, string? IndexDigest, ImmutableArray<string> Packages, string? Refusal)
{
    /// <summary>An established absence: the tag does not exist on any declared artifact host.</summary>
    public static LiveSourcePublication Absent(string source) => new(source, null, [], null);

    /// <summary>The publication could not be read, with the reason.</summary>
    public static LiveSourcePublication Unreadable(string source, string reason) => new(source, null, [], reason);
}

/// <summary>Every source's live publication under one identity, as the gate counts them.</summary>
/// <param name="Identity">The framework identity read.</param>
/// <param name="Sources">One reading per source.</param>
public sealed record LivePublicationReading(string Identity, ImmutableArray<LiveSourcePublication> Sources)
{
    /// <summary>Why the set could not be established — every unreadable source named — or null
    /// when every source was read (published or established absent).</summary>
    public string? Refusal =>
        Sources.Where(s => s.Refusal is not null).Select(s => $"source '{s.Source}': {s.Refusal}").ToList() is { Count: > 0 } r
            ? string.Join("; ", r)
            : null;

    /// <summary>The packages sealed across every published source.</summary>
    public IEnumerable<string> Packages => Sources.SelectMany(s => s.Packages);
}
