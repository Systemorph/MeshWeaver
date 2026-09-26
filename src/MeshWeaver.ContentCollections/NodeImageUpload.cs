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
    /// null when the icon is anything else — an emoji, a URL, an SVG, a file outside
    /// <see cref="Folder"/>, or a file inside it whose name this class did NOT generate. Exactly
    /// that set is what this class may delete.
    ///
    /// <para>🚨 The folder alone is not the marker: an owner can point their icon at
    /// <c>content:picture/me.png</c> by hand (Settings → Metadata). A managed name is the server's
    /// own shape — 32 lower-case hex digits and an accepted lower-case extension — which a
    /// hand-picked file name does not have by accident.</para>
    /// </summary>
    /// <param name="icon">A node's current <see cref="MeshNode.Icon"/>.</param>
    public static string? ManagedFilePath(string? icon)
    {
        const string prefix = "content:" + Folder + "/";
        if (string.IsNullOrEmpty(icon) || !icon.StartsWith(prefix, StringComparison.Ordinal))
            return null;
        var name = icon[prefix.Length..];
        return IsGeneratedName(name) ? $"{Folder}/{name}" : null;
    }

    /// <summary>Whether <paramref name="name"/> has the shape <see cref="Replace"/> generates.</summary>
    private static bool IsGeneratedName(string name)
    {
        var dot = name.IndexOf('.');
        if (dot != 32)
            return false;
        for (var i = 0; i < dot; i++)
            if (!char.IsAsciiHexDigitLower(name[i]) && !char.IsAsciiDigit(name[i]))
                return false;
        var extension = name[dot..];
        return AllowedExtensions.Contains(extension)
               && string.Equals(extension, extension.ToLowerInvariant(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Saves <paramref name="openStream"/> as the node's new picture and points
    /// <see cref="MeshNode.Icon"/> at it; the previous managed picture, if any, is deleted.
    /// Emits the new icon reference once the node has been updated.
    /// </summary>
    /// <param name="hub">The calling hub (a portal circuit's hub or a node hub). The caller's
    /// <see cref="AccessContext"/> is captured when this is CALLED and restored around every write
    /// the pipeline makes, so no pool or reply hop can drop it.</param>
    /// <param name="nodePath">The node whose picture this is.</param>
    /// <param name="originalFileName">The uploaded file's name — only its extension is used.</param>
    /// <param name="length">The declared length in bytes, checked against <see cref="MaxBytes"/> up
    /// front. It is a claim, not a limit: the bytes actually read are counted too, and a stream that
    /// runs past <see cref="MaxBytes"/> fails the save and its partial file is deleted.</param>
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
            return Observable.Throw<string>(TooLarge(length));

        var storedFileName = $"{Guid.NewGuid():N}{Path.GetExtension(originalFileName).ToLowerInvariant()}";
        var storedPath = $"{Folder}/{storedFileName}";
        var newIcon = IconReference(storedFileName);
        var logger = Logger(hub);
        var caller = CallerOf(hub);

        return Observable.Defer(() => ResolveCollection(hub, nodePath, caller))
            .SelectMany(collection => collection
                .SaveFile(Folder, storedFileName, () => new LimitedReadStream(openStream(), MaxBytes))
                .LastOrDefaultAsync()
                .CarryAccessContext(hub.ServiceProvider, caller)
                // A failed save (an over-long stream included) or a refused node update must not
                // strand the bytes it wrote.
                .SelectMany(_ => SetIcon(hub, nodePath, newIcon).CarryAccessContext(hub.ServiceProvider, caller))
                .Catch<string?, Exception>(ex => Discard(collection, storedPath, nodePath, logger)
                    .SelectMany(_ => Observable.Throw<string?>(ex)))
                .SelectMany(previousIcon => DeletePrevious(collection, previousIcon, newIcon, nodePath, logger)
                    .Select(_ => newIcon)));
    }

    /// <summary>
    /// Clears the node's picture (<see cref="MeshNode.Icon"/> = null) and deletes the managed
    /// picture file it pointed at, if any. Emits once the node has been updated.
    /// </summary>
    /// <param name="hub">The calling hub — the caller's access context is captured on the call and
    /// restored around each write.</param>
    /// <param name="nodePath">The node whose picture to remove.</param>
    /// <returns>A cold, single-emission observable.</returns>
    public static IObservable<Unit> Remove(IMessageHub hub, string nodePath)
    {
        if (string.IsNullOrWhiteSpace(nodePath))
            return Observable.Throw<Unit>(new ArgumentException("A node path is required.", nameof(nodePath)));
        var logger = Logger(hub);
        var caller = CallerOf(hub);
        return Observable.Defer(() => SetIcon(hub, nodePath, null))
            .CarryAccessContext(hub.ServiceProvider, caller)
            .SelectMany(previousIcon => ManagedFilePath(previousIcon) is null
                ? Observable.Return(Unit.Default)
                : ResolveCollection(hub, nodePath, caller)
                    .SelectMany(collection => DeletePrevious(collection, previousIcon, null, nodePath, logger)));
    }

    private static InvalidOperationException TooLarge(long length)
        => new($"A picture must be between 1 byte and {MaxBytes / (1024 * 1024)} MB; this one is {length} bytes.");

    /// <summary>The identity the caller is acting as right now — request-scoped, else the circuit's.</summary>
    private static AccessContext? CallerOf(IMessageHub hub)
    {
        var access = hub.ServiceProvider.GetService<AccessService>();
        return access?.Context ?? access?.CircuitContext;
    }

    /// <summary>Deletes a file this upload wrote; a failure to do so is logged, never raised over the real error.</summary>
    private static IObservable<Unit> Discard(ContentCollection collection, string path, string nodePath, ILogger? logger)
        => collection.DeleteFile(path)
            .LastOrDefaultAsync()
            .Catch<Unit, Exception>(ex =>
            {
                logger?.LogWarning(ex, "Picture upload for {Node}: could not delete {File} after the upload failed",
                    nodePath, path);
                return Observable.Return(Unit.Default);
            });

    /// <summary>
    /// A read-only pass-through that FAILS once more than <c>limit</c> bytes have been read — the
    /// size ceiling enforced on the bytes themselves, not on a length the caller declared.
    /// </summary>
    private sealed class LimitedReadStream(Stream inner, long limit) : Stream
    {
        private long _read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => _read;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
            => Count(inner.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private int Count(int n)
        {
            _read += n;
            if (_read > limit)
                throw TooLarge(_read);
            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
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
    private static IObservable<ContentCollection> ResolveCollection(
        IMessageHub hub, string nodePath, AccessContext? captured)
    {
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
            })
            .CarryAccessContext(hub.ServiceProvider, captured);
    }

    private static ILogger? Logger(IMessageHub hub)
        => hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(NodeImageUpload).FullName!);
}
