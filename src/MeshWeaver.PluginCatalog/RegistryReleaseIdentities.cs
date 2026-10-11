using System.Reactive.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// 🚨 <b>A release's framework identity, for a published root no publisher writes to</b> (policy
/// <c>release-identity-from-registry</c>).
///
/// <para>The gate learns a target release's framework identity from one file,
/// <c>&lt;root&gt;/_releases/&lt;version&gt;</c> (<see cref="PublishedBundleCatalogue"/>). The
/// publisher writes it to the storage IT mounts. An installation whose published root is filled
/// from the fleet registry instead — an init container pulling the bundles of the identity its own
/// image reports into a volume the portal mounts READ-ONLY — never receives that file for any
/// release, so every candidate answers "identity not resolvable" and the installation holds on the
/// release it booted with, indefinitely, with nothing failing.</para>
///
/// <para>The same fact IS published where such an installation can reach it: the registry record
/// <c>plugins/releases:&lt;version&gt;</c>, whose config is
/// <c>{"identity","version"[,"expectedDbVersion"]}</c>
/// (<c>Doc/Architecture/PluginBundlesInTheRegistry</c>). This type reads that record, and
/// <see cref="PublishedBundleCatalogue.ReadWithRegistryIdentity"/> takes the
/// answer where the marker file is absent. Nothing is written: the root may not be writable, and a
/// file this process invented would outlive the answer it was derived from.</para>
///
/// <para><b>What it never does.</b> It never overrides a marker that exists — the publisher's file
/// wins, and the registry is not asked. It never guesses: a record naming another version, an
/// identity that is not a single safe path segment, a registry that declares no artifact host, a
/// refusal or a transport fault all answer null, and the gate then holds exactly as it did before,
/// saying why. And the installation's key is presented only to the artifact host the registry
/// mount ITSELF declares (<see cref="PluginBundleClient.BundleIndex.ArtifactRegistry"/>), the rule
/// <see cref="PluginBundleClient"/> already applies to bundles.</para>
/// </summary>
public sealed class RegistryReleaseIdentities
{
    /// <summary>The registry repository holding one record per platform release, tagged by version.</summary>
    public const string ReleaseRepository = "plugins/releases";

    /// <summary>The media type of a release record's config blob.</summary>
    public const string ReleaseConfigMediaType = "application/vnd.meshweaver.release.v1+json";

    /// <summary>A release record is three short fields; anything larger is not one.</summary>
    internal const long MaxRecordBytes = 16 * 1024;

    // The OCI tag grammar, which is also a single safe path segment: no separator, and "." / ".."
    // are excluded by requiring a word character first and refusing the two below explicitly.
    private static readonly Regex Segment = new(
        "^[A-Za-z0-9_][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant);

    private readonly IMessageHub hub;
    private readonly ILogger? logger;

    /// <summary>Creates the reader.</summary>
    /// <param name="hub">The mesh hub — supplies the I/O pools, the registry mounts
    /// (<see cref="PluginCatalogOptions"/>) and their credentials (<see cref="RegistryTokenResolver"/>).</param>
    /// <param name="logger">Optional.</param>
    public RegistryReleaseIdentities(IMessageHub hub, ILogger? logger = null)
    {
        this.hub = hub;
        this.logger = logger;
    }

    /// <summary>
    /// The record the first registry mount that holds one answers with — mounts asked in
    /// configuration order, one at a time, the next only when the previous named nothing.
    /// </summary>
    /// <remarks>Cold; emits once; never faults — the caller's answer to "could not find out" is
    /// already a hold, and a fault here would turn that hold into a failed check.</remarks>
    /// <param name="version">The platform release.</param>
    public IObservable<ReleaseRecord?> Resolve(string? version) =>
        Observable.Defer(() =>
        {
            var options = hub.ServiceProvider.GetService<PluginCatalogOptions>();
            var resolver = hub.ServiceProvider.GetService<RegistryTokenResolver>();
            if (options is null || resolver is null || !IsSegment(version))
                return Observable.Return<ReleaseRecord?>(null);

            return RegistryTokenResolver.WithLegacyTokens(options, options.EffectiveRegistries)
                .Select(mount => FromMount(resolver, mount, version!))
                .Concat()
                .FirstOrDefaultAsync(record => record is not null);
        })
        .Catch((Exception ex) =>
        {
            logger?.LogWarning(ex,
                "ReleaseIdentity: could not ask the registry for release {Version}'s identity. Cause: {Cause}",
                version, ex.Message);
            return Observable.Return<ReleaseRecord?>(null);
        });

    private IObservable<ReleaseRecord?> FromMount(
        RegistryTokenResolver resolver, PluginRegistryReference mount, string version) =>
        Observable.Defer(() => resolver.ResolveToken(mount).Take(1)
            .SelectMany(token => new PluginBundleClient(hub, mount.Url, token).FetchIndex())
            .SelectMany(index =>
            {
                var host = (index.ArtifactRegistry ?? "").Trim();
                if (host.Length == 0)
                {
                    logger?.LogInformation(
                        "ReleaseIdentity: {Registry} declares no artifact registry, so it cannot name "
                        + "release {Version}'s identity", HostOf(mount.Url), version);
                    return Observable.Return<ReleaseRecord?>(null);
                }

                // The DURABLE key, not the exchanged token: the artifact host decides a pull by
                // forwarding the secret to the registry's key→token exchange, which refuses a token.
                var oci = new OciRegistryClient(
                    hub, host, resolver.ResolveDurableKey(mount),
                    httpClientName: InstanceRegistrationClient.BundleHttpClientName, logger: logger);
                return oci.GetManifest(ReleaseRepository, version).SelectMany(manifest =>
                {
                    if (manifest.Config is not { } config
                        || !string.Equals(config.MediaType, ReleaseConfigMediaType, StringComparison.OrdinalIgnoreCase)
                        || config.Size is <= 0 or > MaxRecordBytes)
                    {
                        logger?.LogWarning(
                            "ReleaseIdentity: {Host}/{Repository}:{Version} is not a release record "
                            + "(config {MediaType}, {Size} byte(s)) — ignored",
                            host, ReleaseRepository, version, manifest.Config?.MediaType, manifest.Config?.Size);
                        return Observable.Return<ReleaseRecord?>(null);
                    }

                    // The descriptor's size is the registry's CLAIM; the bound is enforced on the
                    // bytes as they arrive, so a body larger than a record is refused while it is
                    // read rather than after it has been held in memory.
                    var buffer = new BoundedBuffer(MaxRecordBytes);
                    return oci.GetBlob(ReleaseRepository, config.Digest, () => buffer)
                        .Select(receipt => receipt.Size == config.Size
                            ? ReleaseRecord.Parse(buffer.ToArray(), version)
                            : null)
                        .Do(record =>
                        {
                            if (record is null)
                                logger?.LogWarning(
                                    "ReleaseIdentity: the record at {Host}/{Repository}:{Version} does not "
                                    + "name that version with a usable identity — ignored",
                                    host, ReleaseRepository, version);
                        });
                });
            }))
        // A registry with no record for this release answers 404 — the ordinary case for a release
        // published before the record existed, and never a reason to stop asking the next mount.
        .Catch((RegistryRefusedException ex) =>
        {
            logger?.LogInformation(
                "ReleaseIdentity: {Registry} holds no identity for release {Version} ({Cause})",
                HostOf(mount.Url), version, ex.Message);
            return Observable.Return<ReleaseRecord?>(null);
        })
        .Catch((Exception ex) =>
        {
            logger?.LogWarning(ex,
                "ReleaseIdentity: reading release {Version}'s identity through {Registry} failed. Cause: {Cause}",
                version, HostOf(mount.Url), ex.Message);
            return Observable.Return<ReleaseRecord?>(null);
        });

    /// <summary>A registry mount as a log line may name it: host and port only. A configured URL may
    /// carry userinfo, which must never reach a log.</summary>
    internal static string HostOf(string? url) =>
        Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out var parsed) && parsed.Authority.Length > 0
            ? parsed.Authority
            : "(unparseable registry URL)";

    /// <summary>Whether <paramref name="value"/> can be a framework-identity directory under the
    /// published root: one safe path segment, outside the root's own underscore namespace
    /// (<c>_releases</c>).</summary>
    public static bool IsIdentity(string? value) => IsSegment(value) && !value!.StartsWith('_');

    /// <summary>A memory buffer that refuses to grow past a limit — the write that would exceed it
    /// throws, which faults the transfer.</summary>
    private sealed class BoundedBuffer(long limit) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            Guard(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Guard(buffer.Length);
            base.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            Guard(1);
            base.WriteByte(value);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Guard(count);
            return base.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Guard(buffer.Length);
            return base.WriteAsync(buffer, cancellationToken);
        }

        private void Guard(int count)
        {
            if (Length + count > limit)
                throw new InvalidDataException(
                    $"the release record is larger than {limit} byte(s) — not a release record");
        }
    }

    /// <summary>Whether <paramref name="value"/> is one safe path segment and a legal registry tag.</summary>
    internal static bool IsSegment(string? value) =>
        value is not null && value is not ("." or "..") && Segment.IsMatch(value);

    /// <summary>A release's published facts, as the registry records them.</summary>
    /// <param name="Identity">The framework identity that release's binaries resolve.</param>
    /// <param name="Version">The platform version the record is for.</param>
    /// <param name="ExpectedDbVersion">The schema that release expects, when the publisher knew it.</param>
    public sealed record ReleaseRecord(string Identity, string Version, int? ExpectedDbVersion)
    {
        /// <summary>
        /// Parses a record's config blob. Null — never a partial record — when the bytes are not
        /// JSON, name a version other than <paramref name="expectedVersion"/>, or carry an identity
        /// that could not be a directory under the published root. Pure.
        /// </summary>
        public static ReleaseRecord? Parse(byte[] bytes, string expectedVersion)
        {
            try
            {
                using var document = JsonDocument.Parse(bytes);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("identity", out var identity) || identity.ValueKind != JsonValueKind.String
                    || !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String)
                    return null;
                var id = identity.GetString()!;
                // A leading underscore is the marker directory's own namespace under the root.
                if (!IsIdentity(id)
                    || !string.Equals(version.GetString(), expectedVersion, StringComparison.Ordinal))
                    return null;
                int? schema = root.TryGetProperty("expectedDbVersion", out var expected)
                              && expected.ValueKind == JsonValueKind.Number
                              && expected.TryGetInt32(out var number) && number > 0
                    ? number
                    : null;
                return new ReleaseRecord(id, expectedVersion, schema);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
