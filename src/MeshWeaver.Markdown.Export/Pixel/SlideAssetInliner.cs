using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.ContentCollections;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Markdown.Export.Pixel;

/// <summary>
/// One content-collection asset the print document referenced and the export could NOT read.
/// </summary>
/// <param name="Reference">The reference exactly as it appears in the document.</param>
/// <param name="Reason">Why it could not be read — a sentence, safe to log or show.</param>
public sealed record UnresolvedSlideAsset(string Reference, string Reason);

/// <summary>
/// The result of inlining a print document's assets: the rewritten document plus, explicitly, what
/// was and was not resolved.
///
/// <para><b>Unresolved is a RESULT, not a log line.</b> Under the print document's CSP
/// (<c>default-src 'none'; img-src data:</c>) an un-inlined reference is a <b>blank image</b> — the
/// browser cannot fall back to fetching it. So "which assets did not resolve" has to leave this
/// method as data the caller can act on, rather than a warning that scrolls past.</para>
/// </summary>
/// <param name="Html">The document with every resolved reference replaced by its <c>data:</c> URI.</param>
/// <param name="Inlined">The references that were replaced.</param>
/// <param name="Unresolved">The references that were left as links, each with its reason.</param>
public sealed record SlideAssetInlining(
    string Html,
    ImmutableArray<string> Inlined,
    ImmutableArray<UnresolvedSlideAsset> Unresolved);

/// <summary>
/// Reads every content-collection asset a composed print document references and rewrites it to a
/// <c>data:</c> URI, so the document the headless browser prints is genuinely self-contained.
///
/// <para>The browser prints from a local file with no network and no session, and the document's
/// CSP allows images only from <c>data:</c> — so this step is not an optimisation, it is the only
/// way a stored picture reaches the PDF at all.</para>
///
/// <para><b>Resolution goes through <see cref="ContentFileResolver"/></b> — the same reading of an
/// <c>/api/content/…</c> reference the content route itself performs (resolve the owning node, then
/// its named-or-default collection, putting the node's own path back in front of the file when the
/// collection is inherited). Splitting the string here instead is what made a stored slide image
/// silently fail to inline: the markdown pipeline writes <c>api/content/{nodePath}/{file}</c>, whose
/// first segment is the node's PARTITION, and reading that first segment as the collection name
/// asks for a collection that does not exist (issue #990).</para>
/// </summary>
public static class SlideAssetInliner
{
    /// <summary>
    /// Inlines the document's content-collection assets.
    /// </summary>
    /// <param name="html">The composed print document.</param>
    /// <param name="hub">
    /// The hub the export runs on — used to resolve the reference (path resolution + the owning
    /// node's collection configs) and to read the bytes. The read runs under the caller's identity.
    /// </param>
    /// <returns>A single-emission observable of the rewritten document and what did not resolve.</returns>
    public static IObservable<SlideAssetInlining> Inline(string html, IMessageHub hub)
    {
        var references = SlidePrintComposer.CollectAssetReferences(html);
        if (references.Length == 0)
            return Observable.Return(new SlideAssetInlining(html, [], []));

        var contentService = hub.ServiceProvider.GetService<IContentService>();
        if (contentService is null)
            return Observable.Return(new SlideAssetInlining(html, [],
                [.. references.Select(r => new UnresolvedSlideAsset(r,
                    "this hub has no content service, so no collection can be read"))]));

        // Independent reads, each on its collection's own I/O pool; Merge lets them overlap and
        // ToArray waits for all of them. Order is restored from `references` below, so the result
        // does not depend on which read finished first.
        return references
            .Select(reference => Read(hub, contentService, reference))
            .Merge()
            .ToArray()
            .Select(reads => Apply(html, references, reads));
    }

    private static SlideAssetInlining Apply(
        string html, ImmutableArray<string> references, IList<AssetRead> reads)
    {
        var byReference = reads.ToDictionary(r => r.Reference, StringComparer.Ordinal);
        var dataUris = new Dictionary<string, string>(StringComparer.Ordinal);
        var inlined = ImmutableArray.CreateBuilder<string>();
        var unresolved = ImmutableArray.CreateBuilder<UnresolvedSlideAsset>();

        foreach (var reference in references)
        {
            var read = byReference[reference];
            if (read.Bytes is null)
            {
                unresolved.Add(new UnresolvedSlideAsset(reference, read.Reason ?? "unresolved"));
                continue;
            }
            dataUris[reference] = SlidePrintComposer.ToDataUri(read.FilePath!, read.Bytes);
            inlined.Add(reference);
        }

        return new SlideAssetInlining(
            SlidePrintComposer.InlineAssets(html, dataUris),
            inlined.ToImmutable(),
            unresolved.ToImmutable());
    }

    private static IObservable<AssetRead> Read(
        IMessageHub hub, IContentService contentService, string reference)
    {
        var relative = SlidePrintComposer.ToContentReference(reference);
        if (relative is null)
            return Observable.Return(
                AssetRead.Failed(reference, "not a content-collection reference"));

        return ContentFileResolver.Resolve(hub, relative)
            .SelectMany(result =>
            {
                if (result.Resolution is null)
                    return Observable.Return(
                        AssetRead.Failed(reference, result.Reason ?? "could not be resolved"));

                var resolution = result.Resolution;
                // Register under the qualified name before reading, exactly as the content route
                // does: two nodes inheriting the same ancestor collection must not share one cache
                // entry.
                contentService.AddConfiguration(resolution.QualifiedConfig);
                return contentService.GetCollection(resolution.QualifiedName)
                    .SelectMany(collection => collection is null
                        ? Observable.Return(AssetRead.Failed(reference,
                            $"content collection '{resolution.Collection.Name}' could not be opened"))
                        : collection.GetContentBytes(resolution.FilePath)
                            .Select(bytes => bytes is null
                                ? AssetRead.Failed(reference,
                                    $"'{resolution.FilePath}' is not in content collection "
                                    + $"'{resolution.Collection.Name}'")
                                : new AssetRead(reference, bytes, resolution.FilePath, null)));
            })
            // A faulted read is one asset's problem, and it must be REPORTED rather than either
            // swallowed or allowed to fail the whole export — the caller decides what a missing
            // picture is worth.
            .Catch((Exception ex) => Observable.Return(AssetRead.Failed(reference, ex.Message)));
    }

    private sealed record AssetRead(string Reference, byte[]? Bytes, string? FilePath, string? Reason)
    {
        public static AssetRead Failed(string reference, string reason) =>
            new(reference, null, null, reason);
    }
}
