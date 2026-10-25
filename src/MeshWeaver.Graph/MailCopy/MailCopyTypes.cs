using System.Collections.Immutable;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.MailCopy;

/// <summary>
/// One source's OPT-IN copy of the person's mail in memex (owner, 2026-10-16: "can you also optionally
/// offer that data is replicated?"): whether it is on, the window it backfilled, and where the next
/// sync resumes. Live reading stays the DEFAULT — a source without this node is read live, nothing is
/// stored. Stored at <c>{owner}_MailCopy/{provider}</c>; the copies are its children, so switching the
/// source off deletes the node and every copy under it in one call.
/// </summary>
public sealed record MailCopySource
{
    /// <summary>The provider — an open string: <c>outlook</c>, <c>teams</c>, …</summary>
    [Editable(false), Browsable(false)]
    public string Provider { get; init; } = "";

    /// <summary>
    /// The copy's GENERATION — a fresh id every time the copy is switched on or has to be rebuilt.
    /// Every copy carries the generation it was written for, and its id includes it, so a late write of
    /// an earlier generation (a sync that was still running when the person switched off, or a rebuild)
    /// can never overwrite, nor be mistaken for, a copy of the current one — and the earlier
    /// generation's copies are removed by generation, never by touching the current source.
    /// </summary>
    [Editable(false), Browsable(false)]
    public string Generation { get; init; } = "";

    /// <summary>
    /// The version of what the copy reads and keeps per message; 0 is a copy made before attachments. A
    /// delta cursor keeps the field projection it was started with, so a copy below
    /// <see cref="CurrentProjection"/> is rebuilt (a new generation, a fresh cursor) before it can keep
    /// attachments. Stays 0 on a copy that was never rebuilt — the type default round-trips.
    /// </summary>
    [Editable(false), Browsable(false)]
    public int Projection { get; init; }

    /// <summary>The projection this build writes and requires of a copy that keeps attachments.</summary>
    public const int CurrentProjection = 1;

    /// <summary>When the owner switched the copy on (UTC).</summary>
    [Editable(false)]
    public DateTimeOffset EnabledAt { get; init; }

    /// <summary>
    /// The Microsoft account the copy was made from (Graph's <c>/me</c> id), or null before the first
    /// sync recorded it. A copy is served only while the connected primary mailbox is THIS account; when
    /// the person replaces the account the copy is rebuilt from the new one, never served under its label.
    /// </summary>
    [Editable(false), Browsable(false)]
    public string? MailboxId { get; init; }

    /// <summary>The oldest message the backfill copies: <see cref="EnabledAt"/> minus <see cref="MailCopyNodeTypes.WindowDays"/> days (UTC).</summary>
    [Editable(false)]
    public DateTimeOffset WindowStart { get; init; }

    /// <summary>
    /// Where the next sync resumes — the provider's own continuation (Graph's <c>@odata.nextLink</c>
    /// while the backfill pages, its <c>@odata.deltaLink</c> once it is done). Null before the first sync.
    /// </summary>
    [Editable(false), Browsable(false)]
    public string? Cursor { get; init; }

    /// <summary>
    /// Whether the backfill finished. Until it has, the inbox keeps reading the source LIVE — a half-made
    /// copy must not stand in for the mailbox.
    /// </summary>
    [Editable(false)]
    public bool Backfilled { get; init; }

    /// <summary>When the last sync finished (UTC).</summary>
    [Editable(false)]
    public DateTimeOffset? LastSyncAt { get; init; }

    /// <summary>Why the last sync failed, or null when it did not.</summary>
    [Editable(false)]
    public string? LastError { get; init; }

    /// <summary>
    /// The sync holding the copy's LEASE, or null when none does. Taken and renewed through the
    /// generation-checked commit at the owning hub, so only one sync — on any replica — reads, writes
    /// and commits a source at a time; another one that finds the lease held stands down.
    /// </summary>
    [Editable(false), Browsable(false)]
    public string? LeaseHolder { get; init; }

    /// <summary>When the lease lapses if its holder stops renewing it (a crashed process) (UTC).</summary>
    [Editable(false), Browsable(false)]
    public DateTimeOffset? LeaseUntil { get; init; }
}

/// <summary>
/// One message copied into memex — what the inbox row and the reading view need, the full body
/// included, and its real file attachments (<see cref="Attachments"/>, each up to <see cref="MailAttachments.MaxBytes"/>).
/// Stored at <c>{owner}_MailCopy/{provider}/{digest}</c> (<see cref="MailCopyNodeTypes.IdFor"/>).
/// </summary>
public sealed record MailMessageCopy
{
    /// <summary>The provider — an open string: <c>outlook</c>, <c>teams</c>, …</summary>
    [Editable(false)]
    public string Provider { get; init; } = "";

    /// <summary>The provider's own IMMUTABLE id — the join key with the row, the verdict and the provider.</summary>
    [Editable(false)]
    public string MessageId { get; init; } = "";

    /// <summary>The generation of the source this copy was written for (<see cref="MailCopySource.Generation"/>).</summary>
    [Editable(false)]
    public string Generation { get; init; } = "";

    /// <summary>The subject.</summary>
    [Editable(false)]
    public string? Subject { get; init; }

    /// <summary>The sender's display name.</summary>
    [Editable(false)]
    public string? From { get; init; }

    /// <summary>The sender's address.</summary>
    [Editable(false)]
    public string? FromAddress { get; init; }

    /// <summary>When it was received (UTC).</summary>
    [Editable(false)]
    public DateTimeOffset? ReceivedAt { get; init; }

    /// <summary><see cref="ReceivedAt"/> as Unix milliseconds.</summary>
    [Editable(false)]
    public long ReceivedTicks { get; init; }

    /// <summary>
    /// What the store orders and pages by: <see cref="ReceivedTicks"/> × 1000 plus a tiebreaker from the
    /// message id (<see cref="MailCopyNodeTypes.OrderKeyOf"/>), so messages received in the same
    /// millisecond still have distinct keys and a page boundary advances past them. A number — a stored
    /// instant's text form depends on its offset and would not compare in order — and below 2^53, so it
    /// compares exactly wherever a store reads it as a double.
    /// </summary>
    [Editable(false)]
    public long OrderKey { get; init; }

    /// <summary>Whether the owner has read it, as the provider said at the last sync.</summary>
    [Editable(false)]
    public bool IsRead { get; init; }

    /// <summary>The message in its provider's own client.</summary>
    [Editable(false)]
    public string? WebLink { get; init; }

    /// <summary>The provider's conversation id.</summary>
    [Editable(false)]
    public string? ConversationId { get; init; }

    /// <summary>The provider's short preview.</summary>
    [Editable(false)]
    public string? Preview { get; init; }

    /// <summary>The full body, as plain text.</summary>
    [Editable(false)]
    public string? Body { get; init; }

    /// <summary>
    /// Whether the provider said the message has attachments (null: it did not say — a delta that was
    /// started before the projection asked for it). What tells a copy that may hold attachment files of
    /// a message whose list is not recorded yet. Once a copy may hold files it stays true on every
    /// rewrite of the same copy, even when a later delta says the message has none: the files an
    /// interrupted save left are removed with the copy only while this says so.
    /// </summary>
    [Editable(false)]
    public bool? HasAttachments { get; init; }

    /// <summary>
    /// The message's real FILE attachments as the opt-in copy stored them, or null while that has not
    /// been decided (the attachments were not fetched yet — a message without any is decided as an empty
    /// list). 🚨 Stays NULLABLE with a null default: a non-null default list is not the type default, so
    /// a stored value would be duplicated on the typed round trip. Written once per copy: an isRead flip
    /// or any other delta event carries the stored list over and never fetches again.
    /// </summary>
    [Editable(false)]
    public ImmutableList<MailAttachmentCopy>? Attachments { get; init; }

    /// <summary>
    /// When what this copy reflects happened (UTC): the time a sync wrote it, or the time the person
    /// ASKED for the Undo that wrote it back. What a late inbox Delete compares against, so a removal
    /// asked before it never removes it, and one asked after it always does.
    /// </summary>
    [Editable(false)]
    public DateTimeOffset CopiedAt { get; init; }

    /// <summary>
    /// A TOMBSTONE: the message was removed at <see cref="CopiedAt"/> (moved out of the Inbox, deleted,
    /// an inbox Delete). It carries no subject, sender or body, is never listed or opened, and is what an
    /// older write — an Undo whose Graph read lost the race to the removal — is refused against.
    /// </summary>
    [Editable(false)]
    public bool Removed { get; init; }
}

/// <summary>
/// One attachment of a copied message and what became of it. Provider-neutral: every provider's copy
/// records its attachments in this one shape (the Executive Assistant reads <see cref="StoredPath"/>).
/// </summary>
public sealed record MailAttachmentCopy
{
    /// <summary>The provider's own attachment id.</summary>
    public string AttachmentId { get; init; } = "";

    /// <summary>The file name as the sender attached it.</summary>
    public string? Name { get; init; }

    /// <summary>The MIME type the provider reported.</summary>
    public string? ContentType { get; init; }

    /// <summary>The size in bytes the provider reported.</summary>
    public long Size { get; init; }

    /// <summary>Whether the provider marks it inline (an image in the body) rather than attached.</summary>
    public bool IsInline { get; init; }

    /// <summary><c>file</c>, <c>item</c> (an attached message or event) or <c>reference</c> (a cloud link).</summary>
    public string Kind { get; init; } = MailAttachmentKinds.File;

    /// <summary>
    /// The full mesh content path of the stored file — <c>{copyPath}/attachments/{digest}/{fileName}</c> —
    /// readable through the content collection of the copy node; null when the attachment was skipped.
    /// </summary>
    public string? StoredPath { get; init; }

    /// <summary>Why it was not stored (<see cref="MailAttachmentSkips"/>), or null when it was.</summary>
    public string? SkippedReason { get; init; }
}

/// <summary>The attachment kinds — an open vocabulary of strings.</summary>
public static class MailAttachmentKinds
{
    /// <summary>A real file.</summary>
    public const string File = "file";

    /// <summary>An attached message or event.</summary>
    public const string Item = "item";

    /// <summary>A link to a file in the cloud.</summary>
    public const string Reference = "reference";
}

/// <summary>Why an attachment was not stored — an open vocabulary of strings.</summary>
public static class MailAttachmentSkips
{
    /// <summary>An inline image of the body.</summary>
    public const string Inline = "inline";

    /// <summary>An item attachment.</summary>
    public const string Item = "item";

    /// <summary>A reference attachment.</summary>
    public const string Reference = "reference";

    /// <summary>Larger than <see cref="MailAttachments.MaxBytes"/>; it stays with the provider.</summary>
    public const string TooLarge = "too-large";

    /// <summary>The kind is one no rule knows; it is not stored.</summary>
    public const string Unsupported = "unsupported";

    /// <summary>It could not be fetched or stored.</summary>
    public const string FetchFailed = "fetch-failed";
}

/// <summary>
/// The node types of the copy, their paths, and their registration. The copies live in a partition of
/// their OWN per owner, <c>{owner}_MailCopy</c> (owner, 2026-10-25: "a dedicated partition just for the
/// inbox for every user" — downloaded mail and attachments do not belong in the person's home partition).
/// It is created as System when the person first switches a copy on, and its one grant is the owner's
/// Viewer grant: nobody else, a global administrator included, can read it.
/// </summary>
public static class MailCopyNodeTypes
{
    /// <summary>The source node's type.</summary>
    public const string SourceNodeType = nameof(MailCopySource);

    /// <summary>One copied message's type.</summary>
    public const string MessageNodeType = nameof(MailMessageCopy);

    /// <summary>
    /// The suffix that names an owner's copy partition, <c>{owner}_MailCopy</c> — NOT the reserved virtual
    /// <c>_Mailbox</c>. Before 2026-10-25 the same word was a SEGMENT inside the owner's home partition
    /// (<see cref="LegacyNamespaceFor"/>); <see cref="MeshMailCopyStore.MoveLegacy"/> moves what is still there.
    /// </summary>
    public const string Segment = "_MailCopy";

    /// <summary>The type of the copy partition's root node, <c>{owner}_MailCopy</c>.</summary>
    public const string PartitionNodeType = "MailCopyPartition";

    /// <summary>How far back a newly switched-on source copies (owner, 2026-10-16: the last 90 days).</summary>
    public const int WindowDays = 90;

    /// <summary>The name of a copy whose message has no subject.</summary>
    public const string NoSubject = "(no subject)";

    /// <summary>The type's icon.</summary>
    public const string Icon = "🗄️";

    /// <summary>The owner's copy partition, <c>{owner}_MailCopy</c> — the path of its root node. Pure.</summary>
    public static string NamespaceFor(string owner) => $"{owner}{Segment}";

    /// <summary>
    /// Where the copies were filed before they had a partition of their own: <c>{owner}/_MailCopy</c>, in the
    /// owner's home partition. Read only to move what is still there (<see cref="MeshMailCopyStore.MoveLegacy"/>). Pure.
    /// </summary>
    public static string LegacyNamespaceFor(string owner) => $"{owner}/{Segment}";

    /// <summary>The source node: <c>{owner}_MailCopy/{provider}</c>.</summary>
    public static string SourcePath(string owner, string provider) => $"{NamespaceFor(owner)}/{provider}";

    /// <summary>The legacy source node: <c>{owner}/_MailCopy/{provider}</c>. Pure.</summary>
    public static string LegacySourcePath(string owner, string provider) => $"{LegacyNamespaceFor(owner)}/{provider}";

    /// <summary>
    /// Whether <paramref name="owner"/> can own a copy: one user id — not empty, no <c>/</c>, and not itself
    /// ending in <see cref="Segment"/>, whose copy partition would be named like another owner's. Pure.
    /// </summary>
    public static bool IsValidOwner(string? owner)
        => !string.IsNullOrWhiteSpace(owner) && !owner.Contains('/')
           && !owner.EndsWith(Segment, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A copy's id: 128 bits of SHA-256 over the generation and the provider's message id, as 32
    /// lowercase hex characters. Deterministic within a generation — the same message always names the
    /// same copy, which makes a re-sync an overwrite rather than a duplicate — and different across
    /// generations, so an earlier generation's late write lands beside the current copy, never on it.
    /// Short, where an Outlook immutable id is well over a hundred characters. Pure.
    /// </summary>
    public static string IdFor(string provider, string generation, string messageId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"copy\n{provider}\n{generation}\n{messageId}"));
        return Convert.ToHexStringLower(hash.AsSpan(0, 16));
    }

    /// <summary>
    /// The copy key of one connected account: the provider itself for the DEFAULT account (the path the
    /// copy always had — nothing to migrate), <c>{provider}.{accountId}</c> for every other one, so each
    /// account is its own copy beside the others at <c>{owner}_MailCopy/{key}</c>. The account id ends up in
    /// a node path, so only a plain id (letters, digits, <c>-</c>, <c>_</c>) is accepted. Pure.
    /// </summary>
    public static string KeyFor(string provider, string? accountId)
        => string.IsNullOrWhiteSpace(accountId) || string.Equals(accountId, "primary", StringComparison.OrdinalIgnoreCase)
            ? provider
            : accountId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
                ? $"{provider}.{accountId}"
                : throw new ArgumentException("The account id is not a plain id; it is never used in a path.", nameof(accountId));

    /// <summary>A copy's path: <c>{owner}_MailCopy/{provider}/{digest}</c>. Pure.</summary>
    public static string CopyPath(string owner, string provider, string generation, string messageId)
        => $"{SourcePath(owner, provider)}/{IdFor(provider, generation, messageId)}";

    /// <summary>
    /// The order key of a message received at <paramref name="receivedTicks"/> (Unix ms) with id
    /// <paramref name="messageId"/>: the time × 1000 plus three decimal digits of a hash of the id. Pure.
    /// </summary>
    public static long OrderKeyOf(long receivedTicks, string messageId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(messageId));
        return receivedTicks * 1000 + BitConverter.ToUInt16(hash, 0) % 1000;
    }

    /// <summary>
    /// Whether <paramref name="generation"/> is a well-formed generation id: 1–64 letters, digits or
    /// <c>-</c>. 🚨 A generation is node content in the owner's own (writable) partition and is spliced
    /// into store queries, so anything else — <c>g OR namespace:Shared</c> — is refused before it reaches
    /// a query. Pure.
    /// </summary>
    public static bool IsValidGeneration(string? generation)
        => generation is { Length: > 0 and <= 64 } && generation.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    /// <summary>
    /// Whether a create faulted because the node already exists — the store's message names it
    /// ("Node already exists: …") and nothing re-exposes it as a type. Anything else is another failure and
    /// must never be mistaken for a duplicate. Pure.
    /// </summary>
    public static bool IsAlreadyExists(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e.Message?.Contains("already exists", StringComparison.OrdinalIgnoreCase) == true)
                return true;
        return false;
    }

    /// <summary>A fresh generation id.</summary>
    public static string NewGeneration() => Guid.NewGuid().ToString("N");

    /// <summary>The root node of the owner's copy partition, <c>{owner}_MailCopy</c>. Pure.</summary>
    public static MeshNode BuildPartitionRoot(string owner) =>
        new(NamespaceFor(owner))
        {
            Name = $"Mail copies of {owner}",
            NodeType = PartitionNodeType,
            Icon = Icon,
            State = MeshNodeState.Active,
            MainNode = NamespaceFor(owner),
        };

    /// <summary>
    /// The owner's ONE grant on the copy partition: <c>{owner}_MailCopy/_Access/{owner}_Access</c>, role
    /// Viewer. Reads run as the owner; every write runs as System, so Viewer is all the owner needs. Pure.
    /// </summary>
    public static MeshNode OwnerViewerGrant(string owner) =>
        new($"{owner}_Access", $"{NamespaceFor(owner)}/_Access")
        {
            NodeType = "AccessAssignment",
            Name = $"{owner} Access",
            MainNode = NamespaceFor(owner),
            Content = new MeshWeaver.Mesh.Security.AccessAssignment
            {
                AccessObject = owner,
                DisplayName = owner,
                Roles = [new MeshWeaver.Mesh.Security.RoleAssignment { Role = MeshWeaver.Mesh.Security.Role.Viewer.Id, Denied = false }],
            },
        };

    /// <summary>
    /// <paramref name="copy"/> as it reads once moved from the copy node <paramref name="from"/> to
    /// <paramref name="to"/>: every stored attachment path under <c>{from}/</c> re-rooted at <c>{to}/</c>, nothing
    /// else changed. A path that is not under <paramref name="from"/> is left as it is. Pure.
    /// </summary>
    public static MailMessageCopy Relocated(MailMessageCopy copy, string from, string to)
        => copy.Attachments is not { Count: > 0 } list
            ? copy
            : copy with
            {
                Attachments = list.Select(a => a.StoredPath is { } stored && stored.StartsWith(from + "/", StringComparison.Ordinal)
                    ? a with { StoredPath = to + stored[from.Length..] }
                    : a).ToImmutableList(),
            };

    /// <summary>The source node. Pure.</summary>
    public static MeshNode BuildSource(string owner, MailCopySource source) =>
        new(source.Provider, NamespaceFor(owner))
        {
            Name = $"Copy of {source.Provider}",
            NodeType = SourceNodeType,
            Icon = Icon,
            State = MeshNodeState.Active,
            Content = source,
        };

    /// <summary>
    /// The tombstone of a removed message: only what identifies it and when it was removed. Pure.
    /// </summary>
    public static MailMessageCopy Tombstone(string provider, string generation, string messageId, DateTimeOffset removedAt) => new()
    {
        Provider = provider,
        Generation = generation,
        MessageId = messageId,
        CopiedAt = removedAt.ToUniversalTime(),
        Removed = true,
    };

    /// <summary>A copy node. The name is the subject, so its page reads as the message. Pure.</summary>
    public static MeshNode BuildCopy(string owner, MailMessageCopy copy) =>
        new(IdFor(copy.Provider, copy.Generation, copy.MessageId), SourcePath(owner, copy.Provider))
        {
            Name = string.IsNullOrWhiteSpace(copy.Subject) ? NoSubject : copy.Subject.Trim(),
            NodeType = MessageNodeType,
            Icon = Icon,
            State = MeshNodeState.Active,
            Content = copy,
        };

    /// <summary>
    /// Registers both types: their node-type nodes and their content types MESH-WIDE — the copies are
    /// written from the portal hub, whose registry picks the discriminator on the wire.
    /// </summary>
    public static TBuilder AddMailCopyTypes<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        // IfAbsent: the Mail module and every other provider module call this, and any one of them may be the only one installed.
        builder.AddMeshNodesIfAbsent(PartitionTypeNode(), SourceTypeNode(), MessageTypeNode());
        builder.AddAutocompleteExcludedTypes(PartitionNodeType, SourceNodeType, MessageNodeType);
        builder.ConfigureHub(config => config
            .WithType<MailCopySource>(nameof(MailCopySource))
            .WithType<MailMessageCopy>(nameof(MailMessageCopy)));
        return builder;
    }

    private static MeshNode PartitionTypeNode() => new(PartitionNodeType)
    {
        NodeType = MeshNode.NodeTypePath,
        Content = new NodeTypeDefinition { KeepsHistory = false },
        Name = "Mail copies",
        Icon = Icon,
        ExcludeFromContext = new HashSet<string> { "create" },
        HubConfiguration = config => config.AddDefaultLayoutAreas(),
    };

    private static MeshNode SourceTypeNode() => new(SourceNodeType)
    {
        NodeType = MeshNode.NodeTypePath,
        // 🚨 No version history: a copy holds a person's full mail, and "Stop and delete the copies"
        // must leave nothing behind — not in the history either (on Postgres the trigger-written
        // snapshots are purged for a type that keeps none).
        Content = new NodeTypeDefinition { KeepsHistory = false },
        Name = "Mail copy",
        Icon = Icon,
        IsSatelliteType = true,
        ExcludeFromContext = new HashSet<string> { "create" },
        HubConfiguration = config => config
            .WithType<MailCopySource>(nameof(MailCopySource))
            .AddMeshDataSource(source => source.WithContentType<MailCopySource>())
            .AddDefaultLayoutAreas()
            // What the person's Inbox copies tab lists for a copy that is on — bound to this node.
            .AddLayout(layout => layout.WithView(MailCopyStatus.Area, MailCopyStatus.Render)),
    };

    private static MeshNode MessageTypeNode() => new(MessageNodeType)
    {
        NodeType = MeshNode.NodeTypePath,
        // 🚨 No version history: a copy holds a person's full mail, and "Stop and delete the copies"
        // must leave nothing behind — not in the history either (on Postgres the trigger-written
        // snapshots are purged for a type that keeps none).
        Content = new NodeTypeDefinition { KeepsHistory = false },
        Name = "Copied message",
        Icon = Icon,
        IsSatelliteType = true,
        ExcludeFromContext = new HashSet<string> { "create" },
        HubConfiguration = config => config
            .WithType<MailMessageCopy>(nameof(MailMessageCopy))
            .AddMeshDataSource(source => source.WithContentType<MailMessageCopy>())
            .AddDefaultLayoutAreas(),
    };
}

/// <summary>The status area of a copy source — what the person's Inbox copies tab lists for a copy that is on, bound to the source node.</summary>
public static class MailCopyStatus
{
    /// <summary>The layout area's name.</summary>
    public const string Area = "Status";

    /// <summary>The area: a heading and the source's read-only fields.</summary>
    public static UiControl Render(LayoutAreaHost host, RenderingContext context)
        => Controls.Stack.WithVerticalGap(4)
            .WithView(Controls.Markdown("A copy of this mailbox is kept in memex."), "Heading")
            .WithView(MeshNodeContentEditorControl.ForType(host.Hub.Address.ToString().Trim('/'), typeof(MailCopySource), canEdit: false), "Fields");
}
