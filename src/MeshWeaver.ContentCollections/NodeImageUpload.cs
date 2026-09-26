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
/// <para><b>What is accepted.</b> PNG, JPEG, GIF and WebP, up to <see cref="MaxBytes"/>. Both are
/// checked on the BYTES, not on what the client declared: the bytes are read (bounded, inside the
/// collection's pool leaf) and must carry the image signature matching the extension, so an HTML
/// or script file renamed to <c>.png</c> is refused before anything is written.</para>
///
/// <para><b>Managed names.</b> Pictures written here live under <see cref="Folder"/> with a fresh,
/// server-generated file name per upload (the user's file name is never used, and the changed URL
/// defeats a browser cache holding the old picture). When a picture is REPLACED or REMOVED, the
/// previous file is deleted only if its name has that generated shape — an icon the owner pointed
/// at any other file of theirs is never touched.</para>
///
/// <para><b>Order of effects and identity.</b> The bytes are saved first, then the node is updated
/// through the one mutation API (<c>GetMeshNodeStream(path).Update</c>), which enforces the caller's
/// <c>Update</c> permission. A failed save or a refused update deletes the file just written. The
/// caller's <see cref="AccessContext"/> is captured when the method is CALLED and every write runs
/// under it (<c>RunAs</c> for the subscription, <c>CarryAccessContext</c> for the continuations).</para>
///
/// <para><b>Failures</b> are <see cref="NodeImageUploadException"/>s whose
/// <see cref="NodeImageUploadException.Reason"/> (<see cref="NodeImageUploadFailure"/>) a GUI maps
/// to a localized message; the exception text itself is English, for logs.</para>
///
/// <para>Everything is cold and observable end-to-end. Full reference: <c>Doc/GUI/ProfilePage</c>.</para>
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

    /// <summary>Whether <paramref name="fileName"/> has an accepted image extension.</summary>
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
    /// <see cref="AccessContext"/> is captured when this is CALLED and every write runs under it.</param>
    /// <param name="nodePath">The node whose picture this is.</param>
    /// <param name="originalFileName">The uploaded file's name — only its extension is used.</param>
    /// <param name="length">The declared length in bytes — a fast pre-check only; the bytes actually
    /// read are counted against <see cref="MaxBytes"/> too.</param>
    /// <param name="openStream">Opens the picture's bytes; invoked on the collection's pool.</param>
    /// <returns>A cold, single-emission observable of the new <c>content:</c> reference.</returns>
    public static IObservable<string> Replace(
        IMessageHub hub, string nodePath, string originalFileName, long length, Func<Stream> openStream)
    {
        if (string.IsNullOrWhiteSpace(nodePath))
            return Observable.Throw<string>(new ArgumentException("A node path is required.", nameof(nodePath)));
        if (!IsAllowed(originalFileName))
            return Observable.Throw<string>(new NodeImageUploadException(NodeImageUploadFailure.UnsupportedType,
                $"'{originalFileName}' is not a supported picture — use one of "
                + string.Join(", ", AllowedExtensions.OrderBy(e => e, StringComparer.Ordinal)) + "."));
        if (length <= 0 || length > MaxBytes)
            return Observable.Throw<string>(TooLarge(length));

        var extension = Path.GetExtension(originalFileName).ToLowerInvariant();
        var storedFileName = $"{Guid.NewGuid():N}{extension}";
        var storedPath = $"{Folder}/{storedFileName}";
        var newIcon = IconReference(storedFileName);
        var logger = Logger(hub);
        var caller = CallerOf(hub);

        return AsCaller(hub, caller, () => ResolveCollection(hub, nodePath, caller))
            .SelectMany(collection => AsCaller(hub, caller, () => collection
                    // The factory runs INSIDE the collection's pool leaf: the bounded read and the
                    // signature check happen there, synchronously, never on the caller's thread.
                    .SaveFile(Folder, storedFileName, () => ReadValidatedPicture(openStream, extension))
                    .LastOrDefaultAsync())
                .SelectMany(_ => AsCaller(hub, caller, () => SetIcon(hub, nodePath, newIcon)))
                // A failed save or a refused node update must not strand the bytes it wrote.
                .Catch<string?, Exception>(ex => AsCaller(hub, caller,
                        () => Discard(collection, storedPath, nodePath, logger))
                    .SelectMany(_ => Observable.Throw<string?>(ex)))
                .SelectMany(previousIcon => AsCaller(hub, caller,
                        () => DeletePrevious(collection, previousIcon, newIcon, nodePath, logger))
                    .Select(_ => newIcon)));
    }

    /// <summary>
    /// Clears the node's picture (<see cref="MeshNode.Icon"/> = null) and deletes the managed
    /// picture file it pointed at, if any. Emits once the node has been updated.
    /// </summary>
    /// <param name="hub">The calling hub — the caller's access context is captured on the call and
    /// every write runs under it.</param>
    /// <param name="nodePath">The node whose picture to remove.</param>
    /// <returns>A cold, single-emission observable.</returns>
    public static IObservable<Unit> Remove(IMessageHub hub, string nodePath)
    {
        if (string.IsNullOrWhiteSpace(nodePath))
            return Observable.Throw<Unit>(new ArgumentException("A node path is required.", nameof(nodePath)));
        var logger = Logger(hub);
        var caller = CallerOf(hub);
        return AsCaller(hub, caller, () => SetIcon(hub, nodePath, null))
            .SelectMany(previousIcon => ManagedFilePath(previousIcon) is null
                ? Observable.Return(Unit.Default)
                : AsCaller(hub, caller, () => ResolveCollection(hub, nodePath, caller))
                    .SelectMany(collection => AsCaller(hub, caller,
                        () => DeletePrevious(collection, previousIcon, null, nodePath, logger))));
    }

    /// <summary>
    /// Runs <paramref name="work"/> as <paramref name="caller"/> — both while it is SUBSCRIBED
    /// (<c>RunAs</c>, so a write that snapshots the ambient identity at subscribe sees the caller)
    /// and while it NOTIFIES (<c>CarryAccessContext</c>, so the next step's continuation does too).
    /// </summary>
    private static IObservable<T> AsCaller<T>(IMessageHub hub, AccessContext? caller, Func<IObservable<T>> work)
        => hub.ServiceProvider.GetService<AccessService>()
            .RunAs(caller, work)
            .CarryAccessContext(hub.ServiceProvider, caller);

    /// <summary>
    /// Reads the upload into memory, BOUNDED by <see cref="MaxBytes"/> (the length that counts is
    /// the one actually read), and checks that the bytes carry the image signature the extension
    /// promises. Synchronous by design: it runs inside the collection's pool leaf.
    /// </summary>
    private static MemoryStream ReadValidatedPicture(Func<Stream> openStream, string extension)
    {
        var buffer = new MemoryStream();
        using (var source = openStream())
        {
            var chunk = new byte[81920];
            int read;
            while ((read = source.Read(chunk, 0, chunk.Length)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxBytes)
                    throw TooLarge(buffer.Length);
            }
        }
        if (!HasImageSignature(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), extension))
            throw new NodeImageUploadException(NodeImageUploadFailure.NotAnImage,
                $"The file's content is not a {extension.TrimStart('.').ToUpperInvariant()} image.");
        buffer.Position = 0;
        return buffer;
    }

    /// <summary>
    /// Whether <paramref name="bytes"/> start with the signature of the format
    /// <paramref name="extension"/> names — PNG, JPEG, GIF (87a/89a) or WebP (RIFF…WEBP).
    /// </summary>
    /// <param name="bytes">The file's bytes.</param>
    /// <param name="extension">The lower-case extension, with the dot.</param>
    public static bool HasImageSignature(ReadOnlySpan<byte> bytes, string extension) => extension switch
    {
        ".png" => bytes.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
        ".jpg" or ".jpeg" => bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]),
        ".gif" => bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8),
        ".webp" => bytes.Length >= 12 && bytes.StartsWith("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8),
        _ => false,
    };

    private static NodeImageUploadException TooLarge(long length)
        => new(NodeImageUploadFailure.TooLarge,
            $"A picture must be between 1 byte and {MaxBytes / (1024 * 1024)} MB; this one is {length} bytes.");

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
                null => Observable.Throw<ContentCollection>(new NodeImageUploadException(
                    NodeImageUploadFailure.NoCollection,
                    $"'{nodePath}' has no '{ContentCollectionsExtensions.DefaultCollectionName}' collection to hold a picture.")),
                { Config.IsEditable: false } => Observable.Throw<ContentCollection>(new NodeImageUploadException(
                    NodeImageUploadFailure.NoCollection,
                    $"The '{ContentCollectionsExtensions.DefaultCollectionName}' collection of '{nodePath}' is read-only.")),
                _ => Observable.Return(collection),
            });
    }

    private static ILogger? Logger(IMessageHub hub)
        => hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(NodeImageUpload).FullName!);
}

/// <summary>
/// Why a <see cref="NodeImageUpload"/> was refused — an OPEN vocabulary of string constants
/// (policy <c>open-vocabulary-string-constants</c>). A GUI maps a reason to the catalog key
/// <c>profile.pictureError.{reason}</c> and falls back to the generic message for a reason it does
/// not know.
/// </summary>
public static class NodeImageUploadFailure
{
    /// <summary>The file's extension is not one of <see cref="NodeImageUpload.AllowedExtensions"/>.</summary>
    public const string UnsupportedType = "unsupportedType";

    /// <summary>The bytes exceed <see cref="NodeImageUpload.MaxBytes"/> (or the upload is empty).</summary>
    public const string TooLarge = "tooLarge";

    /// <summary>The bytes do not carry the image signature the extension promises.</summary>
    public const string NotAnImage = "notAnImage";

    /// <summary>The node has no editable default content collection to hold a picture.</summary>
    public const string NoCollection = "noCollection";
}

/// <summary>
/// A refused picture upload. <see cref="Reason"/> is the machine-readable cause
/// (<see cref="NodeImageUploadFailure"/>); <see cref="Exception.Message"/> is English, for logs.
/// </summary>
/// <param name="reason">The <see cref="NodeImageUploadFailure"/> value.</param>
/// <param name="message">The English description, for logs.</param>
public sealed class NodeImageUploadException(string reason, string message) : InvalidOperationException(message)
{
    /// <summary>The machine-readable cause — a <see cref="NodeImageUploadFailure"/> value.</summary>
    public string Reason { get; } = reason;
}
