using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.ContentCollections;

/// <summary>
/// One <c>/api/content/…</c> reference, resolved to the collection and the collection-relative file
/// path a server-side read needs.
/// </summary>
/// <param name="Collection">
/// The owning node's collection config, exactly as that node reported it — <b>unrenamed</b>, so a
/// caller can still read <see cref="ContentCollectionConfig.IsStatic"/> and report the collection's
/// real name.
/// </param>
/// <param name="QualifiedName">
/// <c>{nodePath}/{collectionName}</c> — the name the config must be registered under before it is
/// read, so two nodes inheriting the same ancestor collection cannot collide in one content
/// service's cache.
/// </param>
/// <param name="FilePath">The path of the file <b>within that collection</b>.</param>
/// <param name="Owner">The node the reference resolved to.</param>
public sealed record ContentFileResolution(
    ContentCollectionConfig Collection,
    string QualifiedName,
    string FilePath,
    Address Owner)
{
    /// <summary>The config renamed to <see cref="QualifiedName"/> and attributed to <see cref="Owner"/>.</summary>
    public ContentCollectionConfig QualifiedConfig =>
        Collection with { Name = QualifiedName, Address = Owner };
}

/// <summary>
/// Outcome of <see cref="ContentFileResolver.Resolve"/>: either a resolution or the reason there
/// isn't one. The reason is a plain sentence a caller can put in a 404 or a log line.
/// </summary>
/// <param name="Resolution">The resolution, or <c>null</c>.</param>
/// <param name="Reason">Why there is none, or <c>null</c> when there is one.</param>
public sealed record ContentFileResolutionResult(ContentFileResolution? Resolution, string? Reason)
{
    /// <summary>An unresolvable reference, with the reason.</summary>
    public static ContentFileResolutionResult NotFound(string reason) => new(null, reason);

    /// <summary>A resolved reference.</summary>
    public static ContentFileResolutionResult Found(ContentFileResolution resolution) =>
        new(resolution, null);
}

/// <summary>
/// 🚨 THE ONE server-side reading of a content reference: <c>{node}/{collection}/{file}</c> or
/// <c>{node}/{file…}</c> → the collection to open and the path inside it.
///
/// <para><b>Two shapes, one rule.</b> The owning node is resolved with
/// <see cref="IPathResolver"/> (longest real-node prefix). When the first remaining segment names a
/// collection on that node it IS the collection and the rest is the file; otherwise the whole
/// remainder is a path in the node's default <c>content</c> collection, put back behind the node's
/// own path when the collection is inherited from an ancestor
/// (<see cref="CombineOwnerRelative"/>).</para>
///
/// <para><b>Why this is shared rather than re-derived per caller.</b> A second, "obvious" reading —
/// treat the FIRST segment as the collection name — is right for an authored
/// <c>content:{collection}/{path}</c> reference and wrong for every URL the product renders, where
/// the first segment is the node's partition. The two readings agree on the file path and disagree
/// on the collection, so the wrong one fails as "collection not found" and a caller that treats
/// that as "asset missing" produces a silently broken image. The deck export did exactly that
/// (issue #990). Callers ask here instead of splitting the string themselves.</para>
///
/// <para>Reading the node's collections goes through a <c>GetDataRequest</c> to the owning node,
/// which carries <c>[RequiresPermission(Read)]</c> — so the resolution is gated by exactly the
/// decision an ordinary node read makes. There is no parallel rule set here to drift, and no config
/// cache: caching the answer would let one authorized caller warm a short-circuit every later
/// caller reuses.</para>
/// </summary>
public static class ContentFileResolver
{
    /// <summary>
    /// Resolves a content reference. <paramref name="reference"/> is the part AFTER
    /// <see cref="ContentCollectionsExtensions.ContentFileRoutePrefix"/> —
    /// <c>{node}/{collection}/{file}</c> or <c>{node}/{file…}</c> — already percent-decoded
    /// (<see cref="ContentCollectionsExtensions.DecodeCollectionPath"/>).
    /// </summary>
    /// <param name="hub">The hub used to resolve the path and ask the owning node.</param>
    /// <param name="reference">The decoded, prefix-less reference.</param>
    /// <param name="caller">
    /// The identity to attribute the collection-config read to, or <c>null</c> to let the post
    /// pipeline stamp the ambient one (in-mesh callers already run under the user's context).
    /// </param>
    /// <returns>A single-emission observable carrying the resolution or the reason there is none.</returns>
    public static IObservable<ContentFileResolutionResult> Resolve(
        IMessageHub hub,
        string reference,
        AccessContext? caller = null)
    {
        // 🚨 TRAVERSAL GUARD, on the DECODED reference, before anything else. `%2E%2E` and `%2F`
        // survive URL normalisation and only become `..` and `/` once a caller has decoded per
        // segment — and FileSystemStreamProvider resolves a collection-relative path with a bare
        // Path.Combine, so an un-guarded `..` reads outside the collection's BasePath, i.e. another
        // partition's files. It lives HERE rather than in each caller because the export's
        // references come out of user-authored slide markup with raw-HTML passthrough: a slide could
        // otherwise point the server at anything the portal can read. (The content route also checks
        // this before calling us; belt and braces on a path where the cost of being wrong is a file
        // disclosure.)
        if (!StaticAssetMount.IsSafeRelativePath(reference))
            return Observable.Return(
                ContentFileResolutionResult.NotFound("Invalid content path"));

        // Cold: nothing is resolved and nothing is posted until somebody subscribes.
        return Observable.Defer(() => hub.ServiceProvider
            .GetRequiredService<IPathResolver>()
            .ResolvePath(reference))
            .Take(1).SelectMany(resolution =>
        {
            if (resolution is null || string.IsNullOrEmpty(resolution.Prefix))
                return Observable.Return(
                    ContentFileResolutionResult.NotFound("No matching node found for path"));
            if (string.IsNullOrEmpty(resolution.Remainder))
                return Observable.Return(
                    ContentFileResolutionResult.NotFound("File path is required"));

            var remainderParts = resolution.Remainder.Split('/');
            var explicitCollection =
                ContentCollectionsExtensions.DecodeCollectionName(remainderParts[0]);
            var defaultCollection = ContentCollectionsExtensions.DefaultCollectionName;
            var targetAddress = (Address)resolution.Prefix;

            // ONE round trip for both candidate shapes: the named collection (when there is a file
            // path after it) and the node's default content collection.
            var candidates = remainderParts.Length >= 2 && explicitCollection != defaultCollection
                ? new[] { explicitCollection, defaultCollection }
                : [defaultCollection];

            return hub.Observe(
                    new GetDataRequest(new ContentCollectionReference(candidates)),
                    o =>
                    {
                        o = o.WithTarget(targetAddress);
                        return caller is null ? o : o.WithAccessContext(caller);
                    })
                .Take(1)
                .Select(delivery =>
                {
                    var configs = ReadCollectionConfigs(delivery);

                    // Prefer the explicitly-named collection — there the file path is relative to
                    // the COLLECTION's own root, exactly as the file browser lists it.
                    //
                    // Otherwise the node's default collection, with the whole remainder as a
                    // NODE-relative path.
                    var (sourceConfig, filePath) =
                        remainderParts.Length >= 2
                        && configs?.FirstOrDefault(c => c.Name == explicitCollection) is { } named
                            ? (named, string.Join('/', remainderParts.Skip(1)))
                            : (configs?.FirstOrDefault(c => c.Name == defaultCollection),
                                CombineOwnerRelative(
                                    configs?.FirstOrDefault(c => c.Name == defaultCollection)?.Address,
                                    resolution.Prefix,
                                    resolution.Remainder!));

                    if (sourceConfig is null)
                        return ContentFileResolutionResult.NotFound(
                            $"No content collection at '{resolution.Prefix}' serves '{resolution.Remainder}'");

                    if (string.IsNullOrEmpty(filePath))
                        return ContentFileResolutionResult.NotFound("File path is required");

                    return ContentFileResolutionResult.Found(new ContentFileResolution(
                        sourceConfig,
                        $"{resolution.Prefix}/{sourceConfig.Name}",
                        filePath,
                        targetAddress));
                });
        });
    }

    /// <summary>
    /// Puts the resolved node's OWN path back in front of a node-relative file path when the
    /// collection is inherited from an ancestor.
    ///
    /// <para>A Space's <c>content</c> collection is mounted on the partition root with
    /// <c>ExposeInChildren</c>, so a child hub reports the ancestor's config verbatim — same
    /// <c>BasePath</c>. The file of node <c>Org/Project</c> therefore lives at
    /// <c>Project/{file}</c> inside it. Owner == node (or an unknown owner) ⇒ no prefix.</para>
    /// </summary>
    /// <param name="collectionOwner">The address the collection is registered on, if known.</param>
    /// <param name="nodePath">The node the request resolved to.</param>
    /// <param name="filePath">The node-relative file path.</param>
    /// <returns>The collection-relative file path.</returns>
    public static string CombineOwnerRelative(Address? collectionOwner, string nodePath, string filePath)
    {
        var owner = collectionOwner?.ToString()?.Trim('/');
        var node = nodePath.Trim('/');
        if (string.IsNullOrEmpty(owner)
            || string.Equals(owner, node, StringComparison.OrdinalIgnoreCase)
            || !node.StartsWith(owner + "/", StringComparison.OrdinalIgnoreCase))
            return filePath;
        return $"{node[(owner.Length + 1)..]}/{filePath}";
    }

    /// <summary>
    /// Projects the collection configs out of the owning hub's <c>GetDataResponse</c>. The remote
    /// form arrives as a <see cref="JsonElement"/>; <see cref="ContentCollectionConfig.IsStatic"/>
    /// and <see cref="ContentCollectionConfig.Address"/> MUST survive that projection — without the
    /// first, every legitimately-published remote collection fails a caller's mount check; without
    /// the second, an inherited collection loses its owner and a child node's file resolves against
    /// the ancestor's folder. <c>IsStatic</c> is suppressed when false, so an absent property means
    /// false — the safe value either way.
    /// </summary>
    /// <param name="delivery">The response delivery, or <c>null</c>.</param>
    /// <returns>The configs, or <c>null</c> when the response carried none.</returns>
    public static IReadOnlyCollection<ContentCollectionConfig>? ReadCollectionConfigs(
        IMessageDelivery? delivery) =>
        delivery?.Message switch
        {
            GetDataResponse { Data: JsonElement je } => je.EnumerateArray()
                .Select(e => new ContentCollectionConfig
                {
                    Name = e.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "",
                    SourceType = e.TryGetProperty("sourceType", out var typeProp) ? typeProp.GetString() ?? "" : "",
                    BasePath = e.TryGetProperty("basePath", out var pathProp) ? pathProp.GetString() : null,
                    IsStatic = e.TryGetProperty("isStatic", out var staticProp)
                               && staticProp.ValueKind == JsonValueKind.True,
                    Address = e.TryGetProperty("address", out var addressProp)
                        ? ToAddress(addressProp)
                        : null,
                })
                .ToArray(),
            GetDataResponse { Data: IReadOnlyCollection<ContentCollectionConfig> direct } => direct,
            _ => null
        };

    /// <summary>
    /// Reads a collection config's owning address off the wire. Absent, non-string or empty ⇒
    /// <c>null</c>, which <see cref="CombineOwnerRelative"/> treats as "owner unknown" and leaves
    /// the file path alone — the conservative reading.
    /// </summary>
    private static Address? ToAddress(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String)
            return null;
        var value = element.GetString();
        if (string.IsNullOrEmpty(value))
            return null;
        return value;
    }
}
