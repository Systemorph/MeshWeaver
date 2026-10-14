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
/// host, a host refused the key, the tag names something that is not a publication index, an
/// entry is not a valid descriptor, a bundle entry names no package, the sidecar manifest or its
/// <c>_complete</c> is missing, the index is larger than one, or the read failed). The mounts are
/// asked in order and the first Published ends the reading. Unreadable is never Absent: "I could not
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

    /// <summary>The index-entry annotation marking the publication's sidecar manifest.</summary>
    public const string RoleAnnotation = "io.meshweaver.role";

    /// <summary>The <see cref="RoleAnnotation"/> value of the sidecar manifest's entry.</summary>
    public const string SidecarsRole = "sidecars";

    /// <summary>The layer annotation carrying a file's publication-relative name.</summary>
    public const string TitleAnnotation = "org.opencontainers.image.title";

    /// <summary>The sidecar that makes a publication whole; <c>bundle-fetch</c> refuses one without it.</summary>
    public const string CompletionSentinel = "_complete";

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
            .Aggregate(ImmutableArray<LiveSourcePublication>.Empty, (read, one) => read.Add(one))
            .Select(read => new LivePublicationReading(identity, read));

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

            var mounts = RegistryTokenResolver.WithLegacyTokens(options, options.EffectiveRegistries);
            return AskInOrder(resolver, mounts, 0, repository, identity, source, [])
                .Select(answers => Decide(source, repository, identity, answers));
        })
        .Catch((Exception ex) => Observable.Return(LiveSourcePublication.Unreadable(source,
            $"reading the live publication failed ({ex.Message})")));

    /// <summary>
    /// The mounts asked ONE AT A TIME in configuration order, and the reading STOPS at the first
    /// that answers Published (MeshWeaver#6743): a later mount can no longer change that verdict
    /// (<see cref="Decide"/> prefers a publication), so asking it only lets a slow mount run the
    /// gate's answer budget out and turn a decided Published into Indeterminate.
    /// </summary>
    private IObservable<ImmutableArray<LiveSourcePublication?>> AskInOrder(
        RegistryTokenResolver resolver, IReadOnlyList<PluginRegistryReference> mounts, int next,
        string repository, string identity, string source, ImmutableArray<LiveSourcePublication?> answers) =>
        next >= mounts.Count
            ? Observable.Return(answers)
            : FromMount(resolver, mounts[next], repository, identity, source)
                .DefaultIfEmpty(null)
                .SelectMany(answer => answer?.IndexDigest is not null
                    ? Observable.Return(answers.Add(answer))
                    : AskInOrder(resolver, mounts, next + 1, repository, identity, source, answers.Add(answer)));

    /// <summary>One mount's answer: Published, Absent (404 on the tag), Unreadable, or null when
    /// the mount declares no artifact host and so has nothing to say. A publication counts only
    /// once its sidecar manifest — the one carrying <c>_complete</c> — has been read and holds
    /// that sentinel, which is what <c>bundle-fetch</c> refuses to materialise without.</summary>
    private IObservable<LiveSourcePublication?> FromMount(
        RegistryTokenResolver resolver, PluginRegistryReference mount, string repository, string identity, string source) =>
        RegistryReleaseIdentities.ArtifactHostOf(hub, resolver, mount, logger)
            .SelectMany(oci =>
            {
                if (oci is null)
                    return Observable.Return<LiveSourcePublication?>(null);
                var reference = $"{oci.Registry}/{repository}:{identity}";
                return oci.GetManifest(repository, identity, MaxIndexBytes)
                    .SelectMany(manifest =>
                    {
                        var index = FromIndex(source, reference, manifest);
                        if (index.Reading.IndexDigest is null || index.SidecarDigest is not { } sidecar)
                            return Observable.Return<LiveSourcePublication?>(index.Reading);
                        return oci.GetManifest(repository, sidecar, MaxIndexBytes)
                            .Select(sidecarManifest => (LiveSourcePublication?)(
                                SidecarRefusal($"{oci.Registry}/{repository}@{sidecar}", sidecarManifest) is { } refusal
                                    ? LiveSourcePublication.Unreadable(source, refusal)
                                    : index.Reading));
                    });
            })
            .Catch((RegistryManifestUnknownException _) =>
                Observable.Return<LiveSourcePublication?>(LiveSourcePublication.Absent(source)))
            .Catch((Exception ex) => Observable.Return<LiveSourcePublication?>(LiveSourcePublication.Unreadable(source,
                $"{RegistryReleaseIdentities.HostOf(mount.Url)}: {ex.Message}")));

    private LiveSourcePublication Decide(
        string source, string repository, string identity, ImmutableArray<LiveSourcePublication?> answers)
    {
        var said = answers.Where(a => a is not null).Select(a => a!).ToImmutableArray();
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
    /// by its <see cref="PackageAnnotation"/>, plus the digest of its ONE sidecar manifest (the
    /// entry of the publication <c>artifactType</c> annotated <c>io.meshweaver.role=sidecars</c>),
    /// which the caller reads for <c>_complete</c>. Pure. Unreadable — never a smaller set, because
    /// a smaller set is the direction that reads a package as withdrawn, and never a sealed package,
    /// because <c>bundle-fetch</c> pulls the WHOLE index and could not materialise it — when the
    /// manifest is not a publication index (not an OCI image index by media type and
    /// <c>schemaVersion</c> 2, or not the publication <c>artifactType</c>), when ANY entry, bundle,
    /// sidecar or other, is not a valid descriptor (no media type, no well-formed sha256 digest, no
    /// positive size), when there is not exactly one sidecar entry, or when a bundle entry names no
    /// package (MeshWeaver#6743).
    /// </summary>
    internal static PublicationIndex FromIndex(string source, string reference, OciManifest manifest)
    {
        static PublicationIndex Refused(string source, string reason) =>
            new(LiveSourcePublication.Unreadable(source, reason), null);

        if (manifest.Bytes.Length > MaxIndexBytes)
            return Refused(source, $"{reference} is larger than a publication index");
        try
        {
            using var document = JsonDocument.Parse(manifest.Bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !string.Equals(manifest.MediaType, OciRegistryClient.ImageIndexMediaType, StringComparison.OrdinalIgnoreCase)
                || (StringOf(root, "mediaType") is { } declared
                    && !string.Equals(declared, OciRegistryClient.ImageIndexMediaType, StringComparison.OrdinalIgnoreCase))
                || !root.TryGetProperty("schemaVersion", out var schema)
                || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version) || version != 2
                || !string.Equals(StringOf(root, "artifactType"), PublicationArtifactType, StringComparison.OrdinalIgnoreCase)
                || !root.TryGetProperty("manifests", out var entries) || entries.ValueKind != JsonValueKind.Array)
                return Refused(source, $"{reference} is not a publication index");

            var packages = ImmutableArray.CreateBuilder<string>();
            var sidecars = ImmutableArray.CreateBuilder<string>();
            foreach (var entry in entries.EnumerateArray())
            {
                if (!IsDescriptor(entry, requirePositiveSize: true))
                    return Refused(source,
                        $"{reference} names an entry that is not a valid OCI descriptor (media type, sha256 "
                        + "digest and positive size required), so the publication cannot be pulled");
                var artifactType = StringOf(entry, "artifactType");
                JsonElement? annotations = entry.TryGetProperty("annotations", out var a) && a.ValueKind == JsonValueKind.Object
                    ? a
                    : null;
                if (string.Equals(artifactType, PublicationArtifactType, StringComparison.OrdinalIgnoreCase)
                    && annotations is { } sidecarAnnotations
                    && string.Equals(StringOf(sidecarAnnotations, RoleAnnotation), SidecarsRole, StringComparison.Ordinal))
                {
                    sidecars.Add(StringOf(entry, "digest")!);
                    continue;
                }
                if (!string.Equals(artifactType, BundleArtifactType, StringComparison.OrdinalIgnoreCase))
                    continue;
                var package = annotations is { } bundleAnnotations ? StringOf(bundleAnnotations, PackageAnnotation) : null;
                if (string.IsNullOrWhiteSpace(package))
                    return Refused(source,
                        $"{reference} names a bundle entry with no '{PackageAnnotation}' annotation, so the "
                        + "packages it publishes cannot be named");
                packages.Add(package.Trim());
            }
            if (sidecars.Count != 1)
                return Refused(source,
                    $"{reference} names {sidecars.Count} sidecar manifest(s) ('{RoleAnnotation}={SidecarsRole}'), "
                    + $"not one, so its '{CompletionSentinel}' cannot be found");
            return new(new LiveSourcePublication(source, manifest.Digest, packages.ToImmutable(), null), sidecars[0]);
        }
        catch (JsonException ex)
        {
            return Refused(source, $"{reference} is not JSON ({ex.Message})");
        }
    }

    /// <summary>
    /// Why the sidecar manifest of a publication would make <c>bundle-fetch</c> refuse it, or null
    /// when it would not: it must be an image manifest whose config and every layer are valid
    /// descriptors, with one layer titled <c>_complete</c> of positive size — the sentinel
    /// <c>bundle-fetch</c> dies without. Pure.
    /// </summary>
    internal static string? SidecarRefusal(string reference, OciManifest manifest)
    {
        try
        {
            using var document = JsonDocument.Parse(manifest.Bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("config", out var config) || !IsDescriptor(config, requirePositiveSize: false)
                || !root.TryGetProperty("layers", out var layers) || layers.ValueKind != JsonValueKind.Array)
                return $"{reference} is not a sidecar manifest (config and layers required)";
            var complete = false;
            foreach (var layer in layers.EnumerateArray())
            {
                if (!IsDescriptor(layer, requirePositiveSize: false))
                    return $"{reference} names a layer that is not a valid OCI descriptor, so the publication cannot be pulled";
                if (layer.TryGetProperty("annotations", out var annotations) && annotations.ValueKind == JsonValueKind.Object
                    && string.Equals(StringOf(annotations, TitleAnnotation), CompletionSentinel, StringComparison.Ordinal)
                    && layer.GetProperty("size").GetInt64() > 0)
                    complete = true;
            }
            return complete
                ? null
                : $"{reference} carries no non-empty '{CompletionSentinel}', so bundle-fetch would refuse to materialise it";
        }
        catch (JsonException ex)
        {
            return $"{reference} is not JSON ({ex.Message})";
        }
    }

    /// <summary>An OCI descriptor as <c>oras pull</c> needs it: a media type, a well-formed sha256
    /// digest and a numeric size (positive where an empty blob cannot be what is named).</summary>
    private static bool IsDescriptor(JsonElement element, bool requirePositiveSize) =>
        element.ValueKind == JsonValueKind.Object
        && !string.IsNullOrWhiteSpace(StringOf(element, "mediaType"))
        && StringOf(element, "digest") is { } digest && OciDigest.IsWellFormed(digest)
        && element.TryGetProperty("size", out var size)
        && size.ValueKind == JsonValueKind.Number && size.TryGetInt64(out var bytes)
        && (requirePositiveSize ? bytes > 0 : bytes >= 0);

    private static string? StringOf(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>A publication index as read: the reading it supports, and its sidecar manifest's
/// digest (null unless the index is a readable publication).</summary>
/// <param name="Reading">Published (pending the sidecar check) or Unreadable.</param>
/// <param name="SidecarDigest">The one sidecar entry's digest.</param>
internal sealed record PublicationIndex(LiveSourcePublication Reading, string? SidecarDigest);

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
        Sources.Where(s => s.Refusal is not null).Select(s => $"source '{s.Source}': {s.Refusal}").ToImmutableArray() is { IsEmpty: false } r
            ? string.Join("; ", r)
            : null;

    /// <summary>The packages sealed across every published source.</summary>
    public IEnumerable<string> Packages => Sources.SelectMany(s => s.Packages);
}
