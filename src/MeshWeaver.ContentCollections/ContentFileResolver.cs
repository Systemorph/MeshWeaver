using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
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

            // 🚨 NEVER issue this read on the ROOT MESH HUB — the router. The /api/content endpoint
            // holds the DI-injected IMessageHub, which in the mesh's root container IS mesh/{id}
            // (MeshHostApplicationBuilder makes the mesh hub's provider the ASP.NET root provider),
            // so this GetDataRequest used to make the router an END of the delivery in BOTH
            // directions: the request reached the per-node hub stamped Sender = mesh/{id}, and the
            // GetDataResponse (or, for a denied/missing node, the DeliveryFailure) was addressed
            // straight back at mesh/{id}. Same-silo that reply short-circuits on the routing
            // service's local table and everything looks fine; CROSS-silo it has to arrive over the
            // cluster-wide memory stream, and it does not — so the request never answers, the
            // caller waits out its full 60 s budget and the route 500s with a TimeoutException.
            //
            // Prod signature (memex-cloud, 2 replicas, issue #1729): each pod served /api/content
            // ONLY for the nodes whose per-node hub grain it happened to host and hung for ~60 s on
            // every other node, so round-robin across the two replicas made ~half of all requests to
            // ANY given asset hang — broken images on course/doc pages, and a red live-smoke gate.
            // The pod's own diagnostics named the caller exactly as documented:
            //   [STALE-CALLBACK] mesh/IJ1R4… : GetDataRequest@AgenticEngineering(55672ms)
            //     → ROUTED onTarget=False state=Forwarded ⇒ no handler was ever entered
            //   System.TimeoutException: No response received in hub mesh/IJ1R4… within 00:01:00
            // while the OWNING silo logged the matching pair:
            //   ROUTER_TRAFFIC: GetDataResponse has the mesh hub as target (sender: MeshWeaver,
            //   target: mesh/IJ1R4…)
            // i.e. the reply WAS produced and had nowhere to go.
            //
            // ReadIssuingHub is the shared seam for this: it hops a root-hub caller onto
            // portal/reads-{meshId} — routing-registered so responses land on it cross-silo, and
            // sharing the mesh's type registry and permission evaluator — and returns any NON-router
            // hub unchanged, so in-mesh callers (the deck export's SlideAssetInliner, a portal hub,
            // a test client) keep their identity byte-for-byte.
            //
            // 🚨 …and NOT portal/nodeops-{meshId}, which is where this read used to be issued
            // (2c796d297, following GetMeshNode and every node-CRUD path). That hub is the mesh's
            // ONE node-CRUD EXECUTION hub: every CreateNodeRequest / CreateOrUpdateNodeRequest in
            // the mesh runs on its single action block, one turn at a time, and the turn loop does
            // not advance until the current turn's observable completes. A reply DELIVERED there
            // therefore sits in the buffer until the block drains — so this bounded read burned its
            // whole budget on an answer that had already arrived, for as long as a bulk node-CRUD
            // burst lasted. A content UPLOAD is exactly such a burst (one indexing activity per
            // file, each a CreateNode plus a CreateOrUpdateNodeRequest on that block), which is why
            // a freshly uploaded file 503'd for minutes and then healed untouched — issue #2901,
            // Cause B in Doc/Architecture/ContentRoute503. The read hub registers NO handlers, so
            // the only thing its block ever dispatches is the reply we are waiting for.
            //
            // 🚨 …and BOUND IT. Without a budget of its own this read's only terminal is the hub's
            // 60 s RequestTimeout, which is the framework's last-resort ceiling — the number that
            // has to cover a cold NodeType compile — not a budget an HTTP request ever chose. When
            // the owning hub is unreachable, still starting, or its reply is dropped in transit
            // (MeshWeaver#1742), the caller therefore waited a full minute and answered 500 with the
            // hub's own impatience ("No response received in hub … the target hub was not found"),
            // which names neither the file nor what to do about it. Issues #1563 and #1693.
            //
            // ReadBudget.Default (10 s) is the SAME budget GetMeshNode has always defaulted to, and
            // it errors with a typed HubUnreachableException — a TimeoutException subclass, so every
            // transient-failure classifier keeps recognising it — which BlazorHostingExtensions
            // .ContentFailure maps to a retryable 503 instead of a fail:-level 500. This is a READ:
            // it is idempotent, it cancels nothing, and abandoning it costs only the answer (which
            // is why the "never put a client-side ceiling on a mesh operation" rule in
            // MeshService's remarks — about WRITES that keep running — does not apply here).
            var issuingHub = hub.ReadIssuingHub();
            return issuingHub.Observe(
                    new GetDataRequest(new ContentCollectionReference(candidates)),
                    o =>
                    {
                        o = o.WithTarget(targetAddress);
                        return caller is null ? o : o.WithAccessContext(caller);
                    })
                .Take(1)
                .FailIfNoFirstEmission(
                    issuingHub, targetAddress.ToString() ?? string.Empty, "content collection config")
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
    /// form arrives as a <see cref="JsonElement"/>.
    ///
    /// <para>🚨 EVERY property of <see cref="ContentCollectionConfig"/> must survive this
    /// projection, and <c>DocAssetWireProjectionTest.EveryConfigProperty_SurvivesTheWireProjection</c> keeps it true:
    /// a hand-written projection that reads only the fields its author happened to need is a
    /// SILENT truncation of the config, and the missing field surfaces far away as a failure that
    /// names neither this method nor the property it dropped.</para>
    ///
    /// <para>Three of them have already cost outages. <see cref="ContentCollectionConfig.IsStatic"/>
    /// — without it every legitimately-published remote collection fails a caller's mount check.
    /// <see cref="ContentCollectionConfig.Address"/> — without it an inherited collection loses its
    /// owner and a child node's file resolves against the ancestor's folder.
    /// <see cref="ContentCollectionConfig.Settings"/> — the provider-specific payload, and for an
    /// <c>EmbeddedResource</c> collection the ONLY thing that names the assembly and resource
    /// prefix its files live in. Dropping it turned every doc-page image into
    /// <c>ArgumentException: AssemblyName required for EmbeddedResource</c> raised at the far end
    /// of the pipeline, in <see cref="EmbeddedResourceStreamProviderFactory"/> (issues #2122/#2123):
    /// the registration DID supply the assembly, the wire read threw it away.</para>
    ///
    /// <para><c>IsStatic</c>/<c>IsEditable</c>/<c>ExposeInChildren</c> are suppressed when false, so
    /// an absent property means false — the safe value for all three.</para>
    /// </summary>
    /// <param name="delivery">The response delivery, or <c>null</c>.</param>
    /// <returns>The configs, or <c>null</c> when the response carried none.</returns>
    public static IReadOnlyCollection<ContentCollectionConfig>? ReadCollectionConfigs(
        IMessageDelivery? delivery) =>
        delivery?.Message switch
        {
            GetDataResponse { Data: JsonElement je } => je.EnumerateArray()
                .Select(ToConfig)
                .ToArray(),
            GetDataResponse { Data: IReadOnlyCollection<ContentCollectionConfig> direct } => direct,
            _ => null
        };

    /// <summary>
    /// One wire element → one <see cref="ContentCollectionConfig"/>, every property carried across.
    /// </summary>
    /// <param name="e">The JSON element for a single config.</param>
    /// <returns>The reconstructed config.</returns>
    private static ContentCollectionConfig ToConfig(JsonElement e) =>
        new()
        {
            Name = Text(e, "name") ?? "",
            SourceType = Text(e, "sourceType") ?? "",
            DisplayName = Text(e, "displayName"),
            BasePath = Text(e, "basePath"),
            Order = e.TryGetProperty("order", out var orderProp)
                    && orderProp.ValueKind == JsonValueKind.Number
                    && orderProp.TryGetInt32(out var order)
                ? order
                : 0,
            IsEditable = Flag(e, "isEditable"),
            IsStatic = Flag(e, "isStatic"),
            ExposeInChildren = Flag(e, "exposeInChildren"),
            Address = e.TryGetProperty("address", out var addressProp)
                ? ToAddress(addressProp)
                : null,
            Settings = ToSettings(e),
        };

    /// <summary>Reads a string property, or <c>null</c> when absent or not a string.</summary>
    /// <param name="e">The element to read from.</param>
    /// <param name="name">The camel-cased wire property name.</param>
    /// <returns>The string value, or <c>null</c>.</returns>
    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    /// <summary>Reads a boolean property; anything but an explicit <c>true</c> reads as false.</summary>
    /// <param name="e">The element to read from.</param>
    /// <param name="name">The camel-cased wire property name.</param>
    /// <returns>The flag value.</returns>
    private static bool Flag(JsonElement e, string name) =>
        e.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.True;

    /// <summary>
    /// Reads the provider-specific <c>Settings</c> map. Keys are NOT name-policy-converted on the
    /// wire (only property names are), so <c>AssemblyName</c> comes back exactly as it was
    /// registered. A non-string value is skipped rather than stringified — the contract is
    /// <c>IReadOnlyDictionary&lt;string,string&gt;</c>.
    /// </summary>
    /// <param name="e">The element to read from.</param>
    /// <returns>The settings, or <c>null</c> when the config carried none.</returns>
    private static IReadOnlyDictionary<string, string>? ToSettings(JsonElement e)
    {
        if (!e.TryGetProperty("settings", out var settings)
            || settings.ValueKind != JsonValueKind.Object)
            return null;
        var builder = ImmutableDictionary.CreateBuilder<string, string>();
        foreach (var property in settings.EnumerateObject())
            if (property.Value.ValueKind == JsonValueKind.String
                && property.Value.GetString() is { } value)
                builder[property.Name] = value;
        return builder.Count == 0 ? null : builder.ToImmutable();
    }

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
