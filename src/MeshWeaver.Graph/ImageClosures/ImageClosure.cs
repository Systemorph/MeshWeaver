using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Graph.ImageClosures;

/// <summary>
/// One file in an image's closure: its path under <c>/app</c>, its content hash and its size.
/// </summary>
public sealed record ImageClosureFile
{
    /// <summary>The path relative to the application directory (<c>/app</c>), forward slashes.</summary>
    public string Path { get; init; } = "";

    /// <summary>The lowercase hex SHA-256 of the file's bytes.</summary>
    public string Sha256 { get; init; } = "";

    /// <summary>The file's size in bytes.</summary>
    public long Bytes { get; init; }
}

/// <summary>
/// The closure of one platform (runtime identifier) of a multi-platform image.
/// </summary>
public sealed record ImageClosurePlatform
{
    /// <summary>The runtime identifier, e.g. <c>linux-x64</c>.</summary>
    public string Rid { get; init; } = "";

    /// <summary>The DENOMINATOR: how many files this platform's closure holds. A record whose
    /// <see cref="Files"/> disagrees with it is refused (<see cref="ImageClosureRecord.TryParse"/>),
    /// so a truncated record can never read as a smaller closure.</summary>
    public int FileCount { get; init; }

    /// <summary>The SHA-256 of the published <c>meshweaver-surface.manifest</c>.</summary>
    public string ManifestSha256 { get; init; } = "";

    /// <summary>Every file, ordered by path.</summary>
    public ImmutableList<ImageClosureFile> Files { get; init; } = ImmutableList<ImageClosureFile>.Empty;
}

/// <summary>
/// 🚨 An image's closure as queryable mesh data (#4066, policy <c>image-closure-as-mesh-data</c>,
/// <c>Doc/Architecture/ImageClosureAsMeshData</c>). Written by the CD build for every image it
/// publishes, AFTER <c>check-platform-reference-set.sh</c> accepted the same bytes — so the record is
/// what the producer-side assertion asserted, never a re-derivation.
///
/// <para><b>Identity is the DIGEST.</b> The node is keyed by <see cref="Repository"/> + <see cref="Digest"/>,
/// which a retag or a promotion never changes; <see cref="Tags"/> is an attribute — the tags the
/// writer knew when it wrote, not the complete set the image ever carries.</para>
///
/// <para><b>A reader states its coverage.</b> Use <see cref="ImageClosureCoverage"/>: it reports
/// N of M expected digests read and names each missing one as NOT MEASURED. An absent record is
/// never a clean answer.</para>
/// </summary>
public sealed record ImageClosure
{
    /// <summary>The image repository, e.g. <c>memex-portal-ai</c>.</summary>
    public string Repository { get; init; } = "";

    /// <summary>The image (index) digest, <c>sha256:&lt;64 hex&gt;</c> — the identity.</summary>
    public string Digest { get; init; } = "";

    /// <summary>The tags the writer knew at write time.</summary>
    public ImmutableList<string> Tags { get; init; } = ImmutableList<string>.Empty;

    /// <summary>The platform commit the image was built from.</summary>
    public string PlatformCommit { get; init; } = "";

    /// <summary>The platform version the image states — carried opaque, never parsed.</summary>
    public string PlatformVersion { get; init; } = "";

    /// <summary>The framework identity the image's <c>/app</c> states, when it ships one.</summary>
    public string? FrameworkIdentity { get; init; }

    /// <summary>The CD run that wrote the record.</summary>
    public string RunUrl { get; init; } = "";

    /// <summary>When the receiving instance recorded it (UTC).</summary>
    public DateTimeOffset RecordedAt { get; init; }

    /// <summary>One closure per platform, ordered by runtime identifier.</summary>
    public ImmutableList<ImageClosurePlatform> Platforms { get; init; } = ImmutableList<ImageClosurePlatform>.Empty;

    /// <summary>The total file count over all platforms.</summary>
    [JsonIgnore]
    public int FileCount => Platforms.Sum(p => p.FileCount);
}

/// <summary>
/// The wire record main-cd POSTs (<c>.github/scripts/image-closure-record.py</c> writes it) and its
/// validating parser. Refusals are data, never exceptions.
/// </summary>
public static partial class ImageClosureRecord
{
    /// <summary>The <c>event</c> discriminator.</summary>
    public const string EventName = "image-closure";

    /// <summary>The serializer both halves agree on (Web camelCase).</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [GeneratedRegex("^sha256:[0-9a-f]{64}$")]
    private static partial Regex DigestShape();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Shape();

    private sealed record Wire
    {
        public string? Event { get; init; }
        public string? Repository { get; init; }
        public string? Digest { get; init; }
        public ImmutableList<string>? Tags { get; init; }
        public string? PlatformCommit { get; init; }
        public string? PlatformVersion { get; init; }
        public string? FrameworkIdentity { get; init; }
        public string? RunUrl { get; init; }
        public ImmutableList<WirePlatform?>? Platforms { get; init; }
    }

    // Every member nullable on purpose: valid JSON can say `null` for any of them (a null platform,
    // a null files list, a null file), and the parser must REFUSE that shape, never dereference it.
    private sealed record WirePlatform
    {
        public string? Rid { get; init; }
        public int? FileCount { get; init; }
        public string? ManifestSha256 { get; init; }
        public ImmutableList<WireFile?>? Files { get; init; }
    }

    private sealed record WireFile
    {
        public string? Path { get; init; }
        public string? Sha256 { get; init; }
        public long? Bytes { get; init; }
    }

    /// <summary>
    /// Parses and validates a delivered body. Returns the closure, or null with <paramref name="why"/>
    /// naming the refusal: not an <see cref="EventName"/> record, a digest that is not the identity
    /// shape, no platform, a platform whose <see cref="ImageClosurePlatform.FileCount"/> disagrees with
    /// its file list, a null platform / file list / file, a missing or malformed manifest or file hash,
    /// a missing size, or two platforms with one runtime identifier.
    /// </summary>
    /// <param name="body">The raw delivered JSON.</param>
    /// <param name="recordedAt">The receive time to stamp.</param>
    /// <param name="why">The refusal reason when null is returned.</param>
    public static ImageClosure? TryParse(string? body, DateTimeOffset recordedAt, out string why)
    {
        why = "";
        if (string.IsNullOrWhiteSpace(body))
        {
            why = "empty body";
            return null;
        }
        Wire? wire;
        try
        {
            wire = JsonSerializer.Deserialize<Wire>(body, Json);
        }
        catch (JsonException ex)
        {
            why = $"not JSON: {ex.Message}";
            return null;
        }
        if (wire is null || !string.Equals(wire.Event, EventName, StringComparison.Ordinal))
        {
            why = $"not an '{EventName}' record";
            return null;
        }
        if (string.IsNullOrWhiteSpace(wire.Repository))
        {
            why = "no repository";
            return null;
        }
        if (wire.Digest is null || !DigestShape().IsMatch(wire.Digest))
        {
            why = $"digest '{wire.Digest}' is not sha256:<64 lowercase hex> — the record's identity IS the digest";
            return null;
        }
        var wirePlatforms = wire.Platforms ?? ImmutableList<WirePlatform?>.Empty;
        if (wirePlatforms.Count == 0)
        {
            why = "no platform — a closure of nothing is not a closure";
            return null;
        }
        var platforms = ImmutableList.CreateBuilder<ImageClosurePlatform>();
        foreach (var p in wirePlatforms)
        {
            if (p is null || string.IsNullOrWhiteSpace(p.Rid))
            {
                why = "a platform is null or names no runtime identifier";
                return null;
            }
            if (p.ManifestSha256 is null || !Sha256Shape().IsMatch(p.ManifestSha256))
            {
                why = $"platform {p.Rid} carries no surface-manifest hash or a malformed one ('{p.ManifestSha256}')";
                return null;
            }
            if (p.Files is null)
            {
                why = $"platform {p.Rid} carries no file list";
                return null;
            }
            if (p.FileCount is not { } count || count <= 0 || count != p.Files.Count)
            {
                why = $"platform {p.Rid} states fileCount {p.FileCount?.ToString() ?? "<none>"} but carries {p.Files.Count} file(s) — a truncated record must never read as a smaller closure";
                return null;
            }
            var files = ImmutableList.CreateBuilder<ImageClosureFile>();
            foreach (var f in p.Files)
            {
                if (f is null || string.IsNullOrWhiteSpace(f.Path) || f.Sha256 is null || !Sha256Shape().IsMatch(f.Sha256)
                    || f.Bytes is not { } bytes || bytes < 0)
                {
                    why = $"platform {p.Rid} carries a null file, or one with no path, a malformed sha256 or no size ('{f?.Path}')";
                    return null;
                }
                files.Add(new ImageClosureFile { Path = f.Path, Sha256 = f.Sha256, Bytes = bytes });
            }
            platforms.Add(new ImageClosurePlatform
            {
                Rid = p.Rid.Trim(),
                FileCount = count,
                ManifestSha256 = p.ManifestSha256,
                Files = files.ToImmutable(),
            });
        }
        if (platforms.GroupBy(p => p.Rid, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } dup)
        {
            why = $"two platforms resolve to {dup.Key} — one image, one closure per platform";
            return null;
        }
        return new ImageClosure
        {
            Repository = wire.Repository.Trim(),
            Digest = wire.Digest,
            Tags = (wire.Tags ?? ImmutableList<string>.Empty)
                .Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim())
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableList(),
            PlatformCommit = wire.PlatformCommit?.Trim() ?? "",
            PlatformVersion = wire.PlatformVersion?.Trim() ?? "",
            FrameworkIdentity = string.IsNullOrWhiteSpace(wire.FrameworkIdentity) ? null : wire.FrameworkIdentity.Trim(),
            RunUrl = wire.RunUrl?.Trim() ?? "",
            RecordedAt = recordedAt,
            Platforms = platforms.OrderBy(p => p.Rid, StringComparer.Ordinal).ToImmutableList(),
        };
    }
}

/// <summary>
/// The <see cref="ImageClosure"/> node type, its home and its registration.
/// </summary>
public static class ImageClosureNodes
{
    /// <summary>The node type of one image's closure record.</summary>
    public const string NodeType = "ImageClosure";

    /// <summary>The node type of the index node every record lives under.</summary>
    public const string IndexNodeType = "ImageClosureIndex";

    /// <summary>Where the records live: a System-owned namespace in the Admin partition.</summary>
    public const string Root = "Admin/ImageClosures";

    /// <summary>The webhook inbox target the CD build POSTs to (<c>/api/hooks/Admin/ImageClosures</c>).
    /// An instance allowlists it WITH a secret config key, or the ingest stays unarmed.</summary>
    public const string InboxTarget = Root;

    /// <summary>The node id for one image: <c>{repository}-{64 hex}</c> — the digest IS the identity,
    /// so a retag or a promotion lands on the same node.</summary>
    /// <param name="repository">The image repository.</param>
    /// <param name="digest">The <c>sha256:</c> digest.</param>
    public static string IdOf(string repository, string digest)
    {
        var repo = new string(repository.Trim().Select(c => char.IsLetterOrDigit(c) || c is '-' or '.' ? c : '-').ToArray());
        var hex = digest.StartsWith("sha256:", StringComparison.Ordinal) ? digest["sha256:".Length..] : digest;
        return $"{repo}-{hex}";
    }

    /// <summary>The full node path for one image.</summary>
    /// <param name="repository">The image repository.</param>
    /// <param name="digest">The <c>sha256:</c> digest.</param>
    public static string PathOf(string repository, string digest) => $"{Root}/{IdOf(repository, digest)}";

    /// <summary>The node a closure is stored as.</summary>
    /// <param name="closure">The validated closure.</param>
    public static MeshNode ToNode(ImageClosure closure) =>
        new(IdOf(closure.Repository, closure.Digest), Root)
        {
            Name = $"{closure.Repository} {closure.Digest[..Math.Min(closure.Digest.Length, 19)]}",
            NodeType = NodeType,
            State = MeshNodeState.Active,
            Content = closure,
        };

    /// <summary>The index node the records live under (the inbox's owner node).</summary>
    public static MeshNode IndexNode() => new("ImageClosures", "Admin")
    {
        Name = "Image closures",
        NodeType = IndexNodeType,
        State = MeshNodeState.Active,
    };

    /// <summary>Registers the <see cref="ImageClosure"/> node types and the CD ingest.</summary>
    /// <typeparam name="TBuilder">The mesh builder type.</typeparam>
    /// <param name="builder">The mesh builder.</param>
    public static TBuilder AddImageClosures<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(
            new MeshNode(NodeType)
            {
                Name = "Image Closure",
                NodeType = "NodeType",
                Icon = "/static/NodeTypeIcons/satellite.svg",
                HubConfiguration = config => config
                    .AddDefaultLayoutAreas()
                    .AddMeshDataSource(source => source.WithContentType<ImageClosure>()),
            },
            new MeshNode(IndexNodeType)
            {
                Name = "Image Closure Index",
                NodeType = "NodeType",
                Icon = "/static/NodeTypeIcons/satellite.svg",
                HubConfiguration = config => config.AddDefaultLayoutAreas(),
            });
        builder.WithMeshType<ImageClosure>();
        builder.ConfigureServices(services => ImageClosureIngest.Register(services));
        return builder;
    }
}
