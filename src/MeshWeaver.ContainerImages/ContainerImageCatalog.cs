using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Security.Cryptography;
using System.Text;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.ContainerImages;

/// <summary>
/// Turns manifests the mirror served into mesh nodes: one <see cref="ContainerImageRecord"/> per
/// (repository, reference) observed, under the configured
/// <see cref="ContainerImageOptions.ImageRoot"/>.
///
/// <para><b>The mirror records what it SEES</b> — it never fetches anything speculatively. A real
/// pull fetches the index for a tag and then the manifest for its own architecture, so both
/// records appear from that one pull, and the index's <see cref="ContainerImageRecord.Platforms"/>
/// entries are digests whose own records carry the layers. That keeps recording free: no extra
/// upstream request, no added pull latency, and no upstream load a cache would later have to
/// justify.</para>
///
/// <para>🚨 <b>Recording is OFF unless <see cref="ContainerImageOptions.ImageRoot"/> is set</b>,
/// and it is OBSERVATIONAL: a failed write is logged by the caller and never fails the pull. The
/// mirror's job is to serve bytes correctly; the record is a by-product, and a by-product that can
/// break the primary function is a liability. Switching recording off — like switching the whole
/// mirror off — is a configuration change, never a migration.</para>
/// </summary>
public static class ContainerImageCatalog
{
    /// <summary>The node-type identifier for observed container images.</summary>
    public const string NodeType = "ContainerImage";

    /// <summary>Separator between the repository and reference halves of a node id.</summary>
    private const string IdSeparator = "--";

    /// <summary>Registers the ContainerImage node type on the mesh builder. Call alongside the
    /// portal's <c>MapContainerImages</c>; the two halves are independent — the mirror serves
    /// without this, and this is inert without the mirror.</summary>
    /// <typeparam name="TBuilder">The mesh builder type.</typeparam>
    /// <param name="builder">The mesh builder.</param>
    /// <returns>The builder, for chaining.</returns>
    public static TBuilder AddContainerImages<TBuilder>(this TBuilder builder)
        where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.WithMeshType<ContainerImageRecord>();
        return builder;
    }

    /// <summary>Builds the MeshNode definition for the ContainerImage node type.</summary>
    /// <returns>The node-type node.</returns>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Container Image",
        NodeType = MeshNode.NodeTypePath,
        Icon = "/static/NodeTypeIcons/box.svg",
        HubConfiguration = config => config
            .AddDefaultLayoutAreas()
            .AddMeshDataSource(source => source.WithContentType<ContainerImageRecord>())
    };

    /// <summary>
    /// The node id for one observed (repository, reference) pair — deterministic, so a consumer
    /// that knows the tag knows the path, which is the whole of "a pin bump moves ONE reference".
    ///
    /// <para>Both halves are reduced to <c>[A-Za-z0-9._-]</c>, so <c>sha256:ab…</c> becomes
    /// <c>sha256-ab…</c> and a slashed repository becomes one segment. 🚨 That reduction is LOSSY
    /// — <c>a/b</c> and <c>a-b</c> would collide — so when it actually changed anything, an
    /// 8-hex fingerprint of the EXACT pair is appended. The common case (a single-segment
    /// repository and a plain tag) stays verbatim and predictable; the ambiguous case stays
    /// distinct. Silently letting two images share a node would make the closure answer for one
    /// of them wrong, which is the one failure this data must not have.</para>
    /// </summary>
    /// <param name="repository">The repository name, slashes intact.</param>
    /// <param name="reference">The tag or digest as requested.</param>
    /// <returns>The node id.</returns>
    public static string NodeId(string repository, string reference)
    {
        var repositorySegment = Sanitize(repository, out var repositoryChanged);
        var referenceSegment = Sanitize(reference, out var referenceChanged);
        var id = $"{repositorySegment}{IdSeparator}{referenceSegment}";
        if (!repositoryChanged && !referenceChanged)
            return id;
        var fingerprint = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes($"{repository}\n{reference}")))
            .ToLowerInvariant()[..8];
        return $"{id}-{fingerprint}";
    }

    private static string Sanitize(string value, out bool changed)
    {
        var builder = new StringBuilder(value.Length);
        changed = false;
        foreach (var c in value)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')
            {
                builder.Append(c);
                continue;
            }
            builder.Append('-');
            changed = true;
        }
        // An empty result would collapse every such value onto one id, so it counts as CHANGED
        // even when nothing was replaced (an empty input) — that is what makes the fingerprint
        // below disambiguate it.
        if (builder.Length != 0)
            return builder.ToString();
        changed = true;
        return "-";
    }

    /// <summary>
    /// The record for one manifest the mirror served, or null when the bytes are not an OCI/Docker
    /// v2 manifest (see <see cref="OciManifestDocument.TryParse"/> — the mirror still SERVES those
    /// bytes; it records nothing about them).
    ///
    /// <para>🚨 The digest is COMPUTED over the served bytes, not taken from the upstream's
    /// <c>Docker-Content-Digest</c> header. A manifest is defined by its content hash, so
    /// computing it is both cheaper to trust and correct when the header is absent — and a
    /// recorded digest that disagreed with the bytes would silently break every pin derived from
    /// it.</para>
    /// </summary>
    /// <param name="registry">The upstream host these bytes came from.</param>
    /// <param name="repository">The repository name.</param>
    /// <param name="reference">The reference as requested (tag or digest).</param>
    /// <param name="manifest">The manifest body exactly as served.</param>
    /// <param name="observedBy">The caller the mirror authenticated, when it named one.</param>
    /// <param name="observedAt">The observation timestamp (UTC).</param>
    /// <returns>The record, or null when the bytes are not a manifest this mirror models.</returns>
    public static ContainerImageRecord? Describe(
        string registry,
        string repository,
        string reference,
        byte[] manifest,
        string? observedBy,
        DateTimeOffset observedAt)
    {
        if (!OciManifestDocument.TryParse(manifest, out var document))
            return null;

        var digest = "sha256:" + Convert.ToHexString(SHA256.HashData(manifest)).ToLowerInvariant();
        return new ContainerImageRecord
        {
            Registry = registry,
            Repository = repository,
            Reference = reference,
            Digest = digest,
            MediaType = document.MediaType,
            IsIndex = document.IsIndex,
            ObservedAt = observedAt,
            ObservedBy = observedBy,
            ConfigDigest = document.Config?.Digest,
            ClosureSize = document.ClosureSize,
            Layers = document.Layers.IsDefaultOrEmpty
                ? []
                : [.. document.Layers.Select(l => new ContainerImageBlob(l.Digest, l.MediaType, l.Size))],
            Platforms = document.Manifests.IsDefaultOrEmpty
                ? []
                : [.. document.Manifests.Select(m => new ContainerImagePlatform(
                    m.Digest, m.MediaType, m.Os, m.Architecture, m.Variant))],
        };
    }

    /// <summary>
    /// The node one record lands as: id from <see cref="NodeId"/>, namespace
    /// <paramref name="imageRoot"/>.
    /// </summary>
    /// <param name="imageRoot">The configured mesh path image records live under.</param>
    /// <param name="record">The record to store.</param>
    /// <returns>The node.</returns>
    public static MeshNode BuildNode(string imageRoot, ContainerImageRecord record) =>
        new(NodeId(record.Repository, record.Reference), imageRoot)
        {
            Name = $"{record.Repository}:{record.Reference}",
            NodeType = NodeType,
            Content = record,
        };

    /// <summary>
    /// Writes <paramref name="record"/> under <paramref name="imageRoot"/>, upserting so a
    /// re-pull of the same reference refreshes it rather than racing a create against an update.
    ///
    /// <para>Stored under the SYSTEM identity, for the same reason the webhook inbox is: the
    /// caller is an instance key, not a mesh user with write access anywhere — the repository
    /// allowlist plus <c>IContainerImageAuthenticator</c> IS the authorization, and the caller is
    /// recorded in <see cref="ContainerImageRecord.ObservedBy"/> rather than impersonated.</para>
    ///
    /// <para>Cold — nothing is written until subscribed.</para>
    /// </summary>
    /// <param name="hub">The hub whose services resolve the mesh.</param>
    /// <param name="imageRoot">The mesh path image records live under.</param>
    /// <param name="record">The record to store.</param>
    /// <returns>The stored node.</returns>
    public static IObservable<MeshNode> Record(
        IMessageHub hub, string imageRoot, ContainerImageRecord record)
    {
        var mesh = hub.ServiceProvider.GetService<IMeshService>();
        if (mesh is null)
            return Observable.Throw<MeshNode>(
                new InvalidOperationException("The mesh service is not available."));
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var node = BuildNode(imageRoot, record);
        // 🚨 RunAsSystem, never `Observable.Using(() => access.ImpersonateAsSystem(), …)`. That
        // shape opens an AsyncLocal scope on the SUBSCRIBING thread and disposes it on whichever
        // thread the work terminates on — leaving the subscriber running as System afterwards
        // (#1790). RunAsSystem seals the scope to the subscribe.
        return accessService.RunAsSystem(() => mesh.CreateOrUpdateNode(node).Take(1));
    }
}
