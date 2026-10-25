using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using MeshWeaver.ContentCollections;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.MailCopy;

/// <summary>One attachment as a provider lists it — metadata only, never bytes.</summary>
/// <param name="Id">The provider's attachment id.</param>
/// <param name="Name">The file name.</param>
/// <param name="ContentType">The MIME type.</param>
/// <param name="Size">The size in bytes the provider reports.</param>
/// <param name="IsInline">Whether it is an inline image of the body.</param>
/// <param name="Kind">A <see cref="MailAttachmentKinds"/> value.</param>
public sealed record MailAttachmentInfo(string Id, string? Name, string? ContentType, long Size, bool IsInline, string Kind);

/// <summary>
/// The provider-neutral seam of the copy's attachments: ONE mailbox account of ONE provider, read for
/// the attachments of its messages. A provider's copy builds one for the connection it syncs with;
/// <see cref="MailAttachments.Collect"/> does the rest identically for every provider.
/// </summary>
public interface IMailAttachmentSource
{
    /// <summary>The provider segment (<c>outlook</c>, <c>gmail</c>, …).</summary>
    string Provider { get; }

    /// <summary>The account this source reads (the provider's own id for the mailbox).</summary>
    string? AccountId { get; }

    /// <summary>The message's attachments, metadata only. Cold; emits once.</summary>
    IObservable<ImmutableList<MailAttachmentInfo>> List(string messageId);

    /// <summary>One file attachment's bytes, or null when the provider no longer has it. Cold; emits once.</summary>
    IObservable<byte[]?> Fetch(string messageId, string attachmentId);
}

/// <summary>
/// Where a copied message's attachment files live: the content collection of the COPY NODE, and the
/// content index that follows them. Deleting a message's files removes the index (chunks and Document
/// nodes) with them.
/// </summary>
public interface IMailAttachmentStore
{
    /// <summary>
    /// Stores <paramref name="bytes"/> as <c>{copyPath}/attachments/{relativePath}</c> as the owner,
    /// raises the content-indexing notification, and emits the full stored path.
    /// </summary>
    IObservable<string> Save(string owner, string copyPath, string relativePath, byte[] bytes);

    /// <summary>
    /// Deletes EVERYTHING in the copy node's attachments collection — every file, with the chunks and
    /// <c>Document</c> node the content index derived from it. 🚨 By enumerating the collection, never from
    /// the copy's recorded list: a save that was interrupted before its list was recorded, and a list a
    /// tombstone replaced, leave files no list names. A failure is NOT swallowed — the caller must not
    /// delete the node (and with it the only handle on the files) until this has succeeded. A collection
    /// with nothing in it is not an error.
    /// </summary>
    IObservable<Unit> DeleteAll(string owner, string copyPath);

    /// <summary>
    /// <see cref="DeleteAll"/> for a LEGACY copy node, <c>{owner}/_MailCopy/{key}/{id}</c> — what retiring a legacy copy
    /// deletes its files with. A store that cannot reach the legacy place refuses, so its node (the only handle on the
    /// files) is kept.
    /// </summary>
    IObservable<Unit> DeleteLegacy(string owner, string legacyCopyPath)
        => Observable.Throw<Unit>(new NotSupportedException("This attachment store cannot delete legacy attachment files."));
}

/// <summary>
/// The attachment rules of the opt-in copy (owner decisions, 2026-10-24): real FILE attachments up to
/// <see cref="MaxBytes"/> each are stored as content under the copy and indexed; inline images and item
/// or reference attachments are skipped; a larger file stays with the provider. The same rules for every
/// provider.
/// </summary>
public static class MailAttachments
{
    /// <summary>The largest file stored, in bytes (25 MB).</summary>
    public const long MaxBytes = 25L * 1024 * 1024;

    /// <summary>The content collection every node has for attachments.</summary>
    public const string Collection = "attachments";

    private const int MaxNameLength = 100;

    /// <summary>The folder of one attachment: 128 bits of SHA-256 of its id, 32 lowercase hex characters. Pure.</summary>
    public static string FolderOf(string attachmentId)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"attachment\n{attachmentId}")).AsSpan(0, 16));

    /// <summary>
    /// A file name that is safe as one path segment: no separators or control characters, no leading
    /// dots, at most 100 characters (the extension kept); <c>attachment</c> when nothing is left. Pure.
    /// </summary>
    public static string CleanFileName(string? name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string((name ?? "")
                .Select(c => c is '/' or '\\' || char.IsControl(c) || invalid.Contains(c) ? '_' : c).ToArray())
            .Trim().TrimStart('.').Trim();
        if (cleaned.Length > MaxNameLength)
        {
            var extension = Path.GetExtension(cleaned);
            extension = extension.Length > 20 ? "" : extension;
            cleaned = cleaned[..(MaxNameLength - extension.Length)] + extension;
        }
        return cleaned.Length == 0 ? "attachment" : cleaned;
    }

    /// <summary>The path of the attachment's file relative to the collection: <c>{folder}/{fileName}</c>. Pure.</summary>
    public static string RelativePath(MailAttachmentInfo info) => $"{FolderOf(info.Id)}/{CleanFileName(info.Name)}";

    /// <summary>
    /// Whether <paramref name="copyPath"/> is one of <paramref name="owner"/>'s copy nodes —
    /// <c>{owner}_MailCopy/{key}/{id}</c>, exactly — and <paramref name="relativePath"/> (when given) a
    /// path INSIDE its attachments collection: no empty, <c>.</c> or <c>..</c> segment, no backslash. The
    /// only targets the attachment store writes to or empties as System. Pure.
    /// </summary>
    public static bool IsCopyTarget(string owner, string copyPath, string? relativePath = null)
        => IsUnder(MailCopyNodeTypes.NamespaceFor(owner), owner, copyPath, relativePath);

    /// <summary>
    /// <see cref="IsCopyTarget"/> for a copy node still at the LEGACY place, <c>{owner}/_MailCopy/{key}/{id}</c>
    /// in the owner's home partition — the only other target the move of a legacy copy reads and empties. Pure.
    /// </summary>
    public static bool IsLegacyCopyTarget(string owner, string copyPath, string? relativePath = null)
        => IsUnder(MailCopyNodeTypes.LegacyNamespaceFor(owner), owner, copyPath, relativePath);

    private static bool IsUnder(string root, string owner, string copyPath, string? relativePath)
    {
        static bool Plain(string segment) => segment.Length > 0 && segment is not "." and not ".." && !segment.Contains('\\');
        var prefix = root + "/";
        if (!MailCopyNodeTypes.IsValidOwner(owner)
            || !copyPath.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        var rest = copyPath[prefix.Length..].Split('/');
        if (rest.Length != 2 || !rest.All(Plain))
            return false;
        return relativePath is null || relativePath.Split('/').All(Plain);
    }

    /// <summary>The full stored path of a copy's attachment file. Pure.</summary>
    public static string StoredPathOf(string copyPath, string relativePath) => $"{copyPath}/{Collection}/{relativePath}";

    /// <summary>
    /// Whether a copy may have attachment FILES: it records a stored one, or its list is not recorded yet
    /// although the provider said the message has attachments (a save may have been interrupted). Pure.
    /// </summary>
    public static bool MayHoldFiles(MailMessageCopy? copy)
        // 🚨 Not decided from the recorded list alone: a retry after an interrupted save records only
        // 'fetch-failed' entries while the earlier file still exists, so whenever the provider said the
        // message has attachments the collection is enumerated and emptied.
        => copy is not null
           && (copy.HasAttachments == true
               || copy.Attachments?.Any(a => a.StoredPath is not null) == true);

    /// <summary>Why <paramref name="info"/> is not stored, or null when it is. Pure.</summary>
    public static string? SkipReason(MailAttachmentInfo info)
        => info.Kind switch
        {
            MailAttachmentKinds.File when info.IsInline => MailAttachmentSkips.Inline,
            MailAttachmentKinds.File when info.Size > MaxBytes => MailAttachmentSkips.TooLarge,
            MailAttachmentKinds.File => null,
            MailAttachmentKinds.Reference => MailAttachmentSkips.Reference,
            MailAttachmentKinds.Item => MailAttachmentSkips.Item,
            // Kind is an open vocabulary: a value no rule claims is reported as such, never reinterpreted as an item.
            _ => MailAttachmentSkips.Unsupported,
        };

    private static MailAttachmentCopy Described(MailAttachmentInfo info) => new()
    {
        AttachmentId = info.Id,
        Name = info.Name,
        ContentType = info.ContentType,
        Size = info.Size,
        IsInline = info.IsInline,
        Kind = info.Kind,
    };

    /// <summary>
    /// Decides every attachment of one message: lists them, skips what the rules skip WITHOUT downloading
    /// it (the size is the provider's), and stores the rest — one at a time. A failure of one attachment is
    /// recorded as <see cref="MailAttachmentSkips.FetchFailed"/> and never fails the others; only the
    /// listing itself can error (the caller then leaves the message undecided). Cold.
    /// <para>🚨 INTERNAL on purpose (#7133 review): its saves are invoked only after the listing and the fetch
    /// have emitted, so the store would judge whoever subscribed rather than whoever composed the call, and
    /// it would read THIS assembly as the calling code. The engine's background path is its one caller, and
    /// that path declares System on the owner's behalf at every store call (BackgroundMailAttachmentStore).</para>
    /// </summary>
    internal static IObservable<ImmutableList<MailAttachmentCopy>> Collect(
        IMailAttachmentSource source, IMailAttachmentStore store, string owner, string copyPath, string messageId, ILogger? logger = null)
        => source.List(messageId).Take(1).SelectMany(infos => infos
            .Select(info => Observable.Defer(() => Decide(source, store, owner, copyPath, messageId, info, logger)))
            .Concat()
            .ToList()
            .Select(list => list.ToImmutableList()));

    private static IObservable<MailAttachmentCopy> Decide(
        IMailAttachmentSource source, IMailAttachmentStore store, string owner, string copyPath, string messageId,
        MailAttachmentInfo info, ILogger? logger)
    {
        var described = Described(info);
        if (SkipReason(info) is { } reason)
            return Observable.Return(described with { SkippedReason = reason });
        var relative = RelativePath(info);
        return source.Fetch(messageId, info.Id).Take(1)
            .SelectMany(bytes => bytes is null
                ? Observable.Return(described with { SkippedReason = MailAttachmentSkips.FetchFailed })
                // The size the provider listed can be wrong; the bytes are what count.
                : bytes.LongLength > MaxBytes
                    ? Observable.Return(described with { SkippedReason = MailAttachmentSkips.TooLarge })
                    : store.Save(owner, copyPath, relative, bytes)
                        .Take(1)
                        .Select(stored => described with { StoredPath = stored }))
            .DefaultIfEmpty(described with { SkippedReason = MailAttachmentSkips.FetchFailed })
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex, "Mail copy: an attachment of a message of {Owner} could not be stored", owner);
                return Observable.Return(described with { SkippedReason = MailAttachmentSkips.FetchFailed });
            });
    }
}

/// <summary>
/// The attachment files in the mesh: <c>{copyPath}/attachments/…</c>, the <c>attachments</c> content
/// collection EVERY node has (the partition root's <c>content</c> collection is not the copy's — a nested
/// node has none of its own, and an inherited one is refused). Writes run as System on the owner's behalf,
/// like the copies themselves, and go through the OWNING node's hub; the content index follows through
/// the upload and delete notifications, so chunks and the <c>Document</c> node live at
/// <c>{copyPath}/attachments/_Documents/…</c> — under the copy.
/// </summary>
public sealed class MeshMailAttachmentStore(Func<IMessageHub?> hub, ILogger? logger = null) : IMailAttachmentStore
{
    /// <summary>How long the collection lookup at the owning node may take.</summary>
    public static readonly TimeSpan LookupBudget = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    [ImpersonationSurface]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public IObservable<string> Save(string owner, string copyPath, string relativePath, byte[] bytes)
        => Gated(Assembly.GetCallingAssembly(), nameof(Save), owner, copyPath, relativePath, () => Mesh().SelectMany(m => m.Access
            .RunAsSystemFor(governedBy: null, onBehalfOf: owner, () => m.Hub
                .SaveContentFile(copyPath)
                .To(MailAttachments.Collection, relativePath)
                .Post(bytes))
            .Take(1)
            .Select(answer =>
            {
                if (!answer.Success)
                    throw new InvalidOperationException($"The attachment could not be stored: {answer.Error}");
                // The post-upload seam: the content index reacts off-band; a mesh without it is a no-op.
                m.Hub.RaiseContentUploaded($"{copyPath}/{MailAttachments.Collection}", relativePath);
                return MailAttachments.StoredPathOf(copyPath, relativePath);
            })));

    /// <inheritdoc />
    [ImpersonationSurface]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public IObservable<Unit> DeleteAll(string owner, string copyPath)
        => Gated(Assembly.GetCallingAssembly(), nameof(DeleteAll), owner, copyPath, null, () => Mesh().SelectMany(m => m.Access
            .RunAsSystemFor(governedBy: null, onBehalfOf: owner, () => Collection(m.Hub, copyPath)
                .SelectMany(collection => Everything(collection, "/")
                    // Sequentially: each delete raises its own notification, and the first failure stops
                    // the run and reaches the caller.
                    // 🚨 System declared on EACH delete: the items arrive from the I/O pool, and a delete captures the
                    // identity its index notification carries when it is called.
                    .Select(item => m.Access.RunAsSystemFor(governedBy: null, onBehalfOf: owner,
                        () => item.IsFolder ? collection.DeleteFolder(item.Path) : collection.DeleteFile(item.Path)))
                    .Concat()
                    .LastOrDefaultAsync()
                    .Select(_ => Unit.Default)))
            .Take(1))
            .Do(_ => logger?.LogDebug("Mail copy: the attachments of {Copy} were deleted", copyPath)));

    /// <inheritdoc />
    [ImpersonationSurface]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public IObservable<Unit> DeleteLegacy(string owner, string legacyCopyPath)
        => MailAttachments.IsLegacyCopyTarget(owner, legacyCopyPath)
            ? MailCopyAuthority.Gated(hub(), Assembly.GetCallingAssembly(), $"{nameof(MeshMailAttachmentStore)}.{nameof(DeleteLegacy)}", owner,
                Permission.Delete, () => Mesh().SelectMany(m => m.Access
                    .RunAsSystemFor(governedBy: null, onBehalfOf: owner, () => Collection(m.Hub, legacyCopyPath)
                        .SelectMany(collection => Everything(collection, "/")
                            // 🚨 System declared on EACH delete: the items arrive from the I/O pool, and a delete captures the
                    // identity its index notification carries when it is called.
                    .Select(item => m.Access.RunAsSystemFor(governedBy: null, onBehalfOf: owner,
                        () => item.IsFolder ? collection.DeleteFolder(item.Path) : collection.DeleteFile(item.Path)))
                            .Concat()
                            .LastOrDefaultAsync()
                            .Select(_ => Unit.Default)))
                    .Take(1)))
            : Observable.Throw<Unit>(new ArgumentException($"'{legacyCopyPath}' is not one of the legacy mail copies of '{owner}'.", nameof(legacyCopyPath)));

    // 🚨 The caller is authorized (MailCopyAuthority) and the TARGET is pinned to the owner's own copy
    // nodes: these run as System, so a path anywhere else would be a System write to — or an emptying
    // of — any node's attachments.
    private IObservable<T> Gated<T>(Assembly caller, string surface, string owner, string copyPath, string? relativePath, Func<IObservable<T>> work)
        => MailAttachments.IsCopyTarget(owner, copyPath, relativePath)
            ? MailCopyAuthority.Gated(hub(), caller, $"{nameof(MeshMailAttachmentStore)}.{surface}", owner,
                surface == nameof(DeleteAll) ? Permission.Delete : Permission.Update, work)
            : Observable.Throw<T>(new ArgumentException(
                $"'{copyPath}' is not one of the mail copies of '{owner}'; attachments are stored only under {MailCopyNodeTypes.NamespaceFor(owner)}/{{key}}/{{id}}.",
                nameof(copyPath)));

    private static IObservable<(string Path, bool IsFolder)> Everything(ContentCollection collection, string folder)
        => collection.GetFiles(folder).Select(file => (file.Path, false))
            .Concat(collection.GetFolders(folder).Select(f => (f.Path, true)))
            .Catch<(string Path, bool IsFolder), DirectoryNotFoundException>(_ => Observable.Empty<(string, bool)>());

    // The copy node's attachments collection, resolved the way the content route resolves it (the owning
    // node answers for its own config) and registered on the portal's content service under its
    // qualified name.
    private static IObservable<ContentCollection> Collection(IMessageHub hub, string copyPath)
        => ContentFileResolver.Resolve(hub, $"{copyPath}/{MailAttachments.Collection}/x")
            .Take(1)
            .Timeout(LookupBudget)
            .SelectMany(result =>
            {
                if (result.Resolution is not { } resolution)
                    return Observable.Throw<ContentCollection>(new InvalidOperationException(
                        $"The attachments of '{copyPath}' could not be resolved: {result.Reason}"));
                var contentService = hub.ServiceProvider.GetRequiredService<IContentService>();
                contentService.AddConfiguration(resolution.QualifiedConfig);
                return contentService.GetCollection(resolution.QualifiedName)
                    .Select(collection => collection ?? throw new InvalidOperationException($"The attachments of '{copyPath}' could not be opened."));
            });

    private IObservable<Portal> Mesh()
        => Observable.Defer(() => hub() is { } portal
            ? Observable.Return(new Portal(portal, portal.ServiceProvider.GetService<AccessService>()))
            : Observable.Throw<Portal>(new InvalidOperationException("The mail attachments cannot be reached: no mesh is available on this host.")));

    private sealed record Portal(IMessageHub Hub, AccessService? Access);
}
