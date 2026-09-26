using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.ContentCollections;

/// <summary>
/// Sets, replaces and removes a node's PICTURE — the image a node shows as its avatar or icon
/// (<see cref="MeshNode.Icon"/>). This is the one-click form of what the icon picker has always
/// done by hand: the image is written into the node's OWN default content collection and the node's
/// <see cref="MeshNode.Icon"/> is set to the canonical <c>content:{path}</c> reference, which every
/// renderer resolves to the access-controlled <c>/api/content/{nodePath}/{path}</c> URL
/// (<c>MeshNodeImageHelper.ResolveContentPath</c>). So the picture shows wherever the node's icon
/// already shows — the portal header avatar, the public profile, cards and mentions — with no
/// second field and no second store.
///
/// <para><b>Managed folder.</b> Pictures written here live under <see cref="Folder"/> with a fresh,
/// server-generated file name per upload (the user's file name is never used, so nothing in it can
/// traverse or collide, and the changed URL defeats a browser cache holding the old picture). When
/// a picture is REPLACED or REMOVED, the previous file is deleted only if it lives in that managed
/// folder — an icon the owner pointed at some other file of theirs is never touched.</para>
///
/// <para><b>Order of effects.</b> The bytes are saved first, then the node is updated through the
/// one mutation API (<c>GetMeshNodeStream(path).Update</c>), which enforces the caller's
/// <c>Update</c> permission on the node. If that update is refused or fails, the file just written
/// is deleted again before the error propagates, so a refused change leaves nothing behind.</para>
///
/// <para>Everything is cold and observable end-to-end: nothing happens until Subscribe, and every
/// I/O leaf runs on the collection's pool. Full reference: <c>Doc/GUI/ProfilePage</c>.</para>
/// </summary>
public static class NodeImageUpload
{
    /// <summary>The folder, inside the node's default content collection, that holds managed pictures.</summary>
    public const string Folder = "picture";

    /// <summary>The largest picture accepted, in bytes (5 MiB).</summary>
    public const long MaxBytes = 5L * 1024 * 1024;

    /// <summary>
    /// The accepted image extensions (lower case, with the dot). SVG is deliberately NOT accepted:
    /// an uploaded SVG is served from the portal's own origin and can carry script, which an
    /// avatar has no need for.
    /// </summary>
    public static readonly ImmutableHashSet<string> AllowedExtensions =
        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, ".png", ".jpg", ".jpeg", ".gif", ".webp");

    /// <summary>The <c>accept</c> attribute value for a file input offering exactly <see cref="AllowedExtensions"/>.</summary>
    public const string AcceptAttribute = "image/png,image/jpeg,image/gif,image/webp";

    /// <summary>
    /// Whether <paramref name="fileName"/> has an accepted image extension.
    /// </summary>
    /// <param name="fileName">The uploaded file's name.</param>
    public static bool IsAllowed(string? fileName)
        => !string.IsNullOrEmpty(fileName) && AllowedExtensions.Contains(Path.GetExtension(fileName));

    /// <summary>
    /// The <c>content:</c> reference for a managed picture file, e.g. <c>content:picture/3f2a….png</c>.
    /// </summary>
    /// <param name="storedFileName">The generated file name inside <see cref="Folder"/>.</param>
    public static string IconReference(string storedFileName) => $"content:{Folder}/{storedFileName}";

    /// <summary>
    /// The collection-relative path of the managed picture <paramref name="icon"/> points at, or
    /// null when the icon is anything else (an emoji, a URL, an SVG, or a file outside
    /// <see cref="Folder"/>) — which is exactly the set of icons this class may delete.
    /// </summary>
    /// <param name="icon">A node's current <see cref="MeshNode.Icon"/>.</param>
    public static string? ManagedFilePath(string? icon)
    {
        const string prefix = "content:" + Folder + "/";
        if (string.IsNullOrEmpty(icon) || !icon.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;
        var name = icon[prefix.Length..];
        // One segment only, and never a traversal: only files this class generated qualify.
        return name.Length == 0 || name.Contains('/') || name.Contains('\\') || name.Contains("..")
            ? null
            : $"{Folder}/{name}";
    }

    /// <summary>
    /// Saves <paramref name="openStream"/> as the node's new picture and points
    /// <see cref="MeshNode.Icon"/> at it; the previous managed picture, if any, is deleted.
    /// Emits the new icon reference once the node has been updated.
    /// </summary>
    /// <param name="hub">The calling hub (a portal circuit's hub or a node hub) — the write runs
    /// under its current <see cref="AccessContext"/>.</param>
    /// <param name="nodePath">The node whose picture this is.</param>
    /// <param name="originalFileName">The uploaded file's name — only its extension is used.</param>
    /// <param name="length">The upload's length in bytes, checked against <see cref="MaxBytes"/>.</param>
    /// <param name="openStream">Opens the picture's bytes; invoked on the collection's pool.</param>
    /// <returns>A cold, single-emission observable of the new <c>content:</c> reference.</returns>
    public static IObservable<string> Replace(
        IMessageHub hub, string nodePath, string originalFileName, long length, Func<Stream> openStream)
    {
        if (string.IsNullOrWhiteSpace(nodePath))
            return Observable.Throw<string>(new ArgumentException("A node path is required.", nameof(nodePath)));
        if (!IsAllowed(originalFileName))
            return Observable.Throw<string>(new InvalidOperationException(
                $"'{originalFileName}' is not a supported picture — use one of "
                + string.Join(", ", AllowedExtensions.OrderBy(e => e, StringComparer.Ordinal)) + "."));
        if (length <= 0 || length > MaxBytes)
            return Observable.Throw<string>(new InvalidOperationException(
                $"A picture must be between 1 byte and {MaxBytes / (1024 * 1024)} MB; this one is {length} bytes."));

        var storedFileName = $"{Guid.NewGuid():N}{Path.GetExtension(originalFileName).ToLowerInvariant()}";
        var newIcon = IconReference(storedFileName);
        var logger = Logger(hub);

        return Observable.Defer(() => ResolveCollection(hub, nodePath))
            .SelectMany(collection => collection.SaveFile(Folder, storedFileName, openStream)
                .LastOrDefaultAsync()
                .SelectMany(_ => SetIcon(hub, nodePath, newIcon)
                    // A refused/failed node update must not strand the bytes it was about to show.
                    .Catch<string?, Exception>(ex => collection.DeleteFile($"{Folder}/{storedFileName}")
                        .Catch<Unit, Exception>(cleanupEx =>
                        {
                            logger?.LogWarning(cleanupEx,
                                "Picture upload for {Node}: could not delete {File} after the node update failed",
                                nodePath, storedFileName);
                            return Observable.Return(Unit.Default);
                        })
                        .SelectMany(_ => Observable.Throw<string?>(ex))))
                .SelectMany(previousIcon => DeletePrevious(collection, previousIcon, newIcon, nodePath, logger)
                    .Select(_ => newIcon)));
    }

    /// <summary>
    /// Clears the node's picture (<see cref="MeshNode.Icon"/> = null) and deletes the managed
    /// picture file it pointed at, if any. Emits once the node has been updated.
    /// </summary>
    /// <param name="hub">The calling hub — the write runs under its current access context.</param>
    /// <param name="nodePath">The node whose picture to remove.</param>
    /// <returns>A cold, single-emission observable.</returns>
    public static IObservable<Unit> Remove(IMessageHub hub, string nodePath)
    {
        if (string.IsNullOrWhiteSpace(nodePath))
            return Observable.Throw<Unit>(new ArgumentException("A node path is required.", nameof(nodePath)));
        var logger = Logger(hub);
        return Observable.Defer(() => SetIcon(hub, nodePath, null))
            .SelectMany(previousIcon => ManagedFilePath(previousIcon) is null
                ? Observable.Return(Unit.Default)
                : ResolveCollection(hub, nodePath)
                    .SelectMany(collection => DeletePrevious(collection, previousIcon, null, nodePath, logger)));
    }

    /// <summary>
    /// Writes <paramref name="icon"/> onto the node through the one mutation API and emits the icon
    /// the node carried BEFORE the write (read inside the update, so it is the value this write
    /// actually replaced).
    /// </summary>
    private static IObservable<string?> SetIcon(IMessageHub hub, string nodePath, string? icon)
    {
        string? previous = null;
        return hub.GetMeshNodeStream(nodePath)
            .Update(node =>
            {
                previous = node.Icon;
                return node with { Icon = icon };
            })
            .Take(1)
            .Select(_ => previous);
    }

    /// <summary>Deletes the previous managed picture unless it is the one just written.</summary>
    private static IObservable<Unit> DeletePrevious(
        ContentCollection collection, string? previousIcon, string? currentIcon, string nodePath, ILogger? logger)
    {
        var previousFile = ManagedFilePath(previousIcon);
        if (previousFile is null || string.Equals(previousIcon, currentIcon, StringComparison.Ordinal))
            return Observable.Return(Unit.Default);
        // The node already shows the new picture; an old file that will not delete is a leftover in
        // the owner's Files, not a failed change — so it is reported, never raised.
        return collection.DeleteFile(previousFile)
            .LastOrDefaultAsync()
            .Catch<Unit, Exception>(ex =>
            {
                logger?.LogWarning(ex, "Picture change for {Node}: previous picture {File} could not be deleted",
                    nodePath, previousFile);
                return Observable.Return(Unit.Default);
            });
    }

    /// <summary>
    /// Resolves the node's default content collection from the owning node's hub (config only) and
    /// opens it on the calling hub — the mechanism <c>MeshOperations.Upload</c> and the out-of-band
    /// content transfer use. Errors when the node declares no editable default collection.
    /// </summary>
    private static IObservable<ContentCollection> ResolveCollection(IMessageHub hub, string nodePath)
    {
        var access = hub.ServiceProvider.GetService<AccessService>();
        var captured = access?.Context ?? access?.CircuitContext;
        var address = (Address)nodePath;
        return OutOfBandContentTransfer.ResolveDestination(
                hub, address, ContentCollectionsExtensions.DefaultCollectionName,
                o => ContentImportExtensions.ConfigurePost(o, address, captured))
            .SelectMany(collection => collection switch
            {
                null => Observable.Throw<ContentCollection>(new InvalidOperationException(
                    $"'{nodePath}' has no '{ContentCollectionsExtensions.DefaultCollectionName}' collection to hold a picture.")),
                { Config.IsEditable: false } => Observable.Throw<ContentCollection>(new InvalidOperationException(
                    $"The '{ContentCollectionsExtensions.DefaultCollectionName}' collection of '{nodePath}' is read-only.")),
                _ => Observable.Return(collection),
            });
    }

    private static ILogger? Logger(IMessageHub hub)
        => hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(NodeImageUpload).FullName!);
}
