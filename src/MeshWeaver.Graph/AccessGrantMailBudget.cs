using System.Reactive.Linq;
using System.Text;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>One claimed access-grant mail slot — the content of an <see cref="AccessGrantMailBudget.NodeType"/> node.</summary>
public sealed record AccessGrantMailSlot
{
    /// <summary>The granter whose budget this slot spends (the assignment's <c>CreatedBy</c>).</summary>
    public string Granter { get; init; } = string.Empty;

    /// <summary>The window the slot belongs to (<see cref="AccessGrantMailBudget.WindowKey"/>).</summary>
    public string Window { get; init; } = string.Empty;

    /// <summary>The grant that claimed it — the AccessAssignment's path.</summary>
    public string Grant { get; init; } = string.Empty;
}

/// <summary>What the access-granted notifier may do for one grant.</summary>
public enum AccessGrantMailVerdict
{
    /// <summary>Within the granter's budget: the recipient's preferences decide, email and Teams included.</summary>
    Mail,

    /// <summary>Budget spent: the recipient gets the bell only.</summary>
    BellOnly,

    /// <summary>The FIRST grant over budget in this window: bell only, and the granter is told once.</summary>
    BellOnlyAndTellGranter,
}

/// <summary>
/// 🚨 THE STRUCTURAL CAP ON ACCESS-GRANT MAIL. A person granting access to many people must never
/// turn into one message in every recipient's mailbox. Measured on memex.meshweaver.cloud
/// 2026-09-29: 72 grants in three minutes, 72 emails and 72 Teams messages (the Parties
/// incident). #5901 silenced grants the SYSTEM writes; this caps the rest — any person, platform
/// admins and API/MCP/script callers included, because a rule nobody can bypass cannot be a
/// convention.
///
/// <para><b>The rule.</b> Per granter, at most <see cref="MailPerWindow"/> access-granted
/// notifications per <see cref="Window"/> may use the external channels (email, Teams). Every
/// further grant in that window reaches the recipient's bell only, and the granter — not the
/// recipients — gets ONE notice that the rest went out silently.</para>
///
/// <para><b>Why the budget lives in the store.</b> The change feed delivers each grant to the
/// replica that WROTE it (<see cref="IMeshChangeFeed"/>), and 72 grants across partitions land on
/// several replicas — a per-process counter would allow <see cref="MailPerWindow"/> × replicas.
/// A slot is instead a node at a DETERMINISTIC path,
/// <c>Admin/_GrantMail/{granter}/{window}-{n}</c>, claimed by CREATING it: a create on a path that
/// exists is refused by the owning hub (one activation per address, cluster-wide) and by the
/// store's unique path, so exactly one grant wins each slot on every replica, with no read and no
/// index lag in the decision. The windows are fixed buckets rather than sliding: a burst straddling
/// a boundary can spend two budgets (2 × <see cref="MailPerWindow"/>), and that bound is the price
/// of a decision that needs no read.</para>
///
/// <para>Fail CLOSED: a claim that cannot be decided (the store refuses for another reason, the
/// write times out) is <see cref="AccessGrantMailVerdict.BellOnly"/> — never mail on a budget we
/// could not see.</para>
/// </summary>
public static class AccessGrantMailBudget
{
    /// <summary>The node type of a claimed slot.</summary>
    public const string NodeType = "AccessGrantMailSlot";

    /// <summary>
    /// Mail-capable access-granted notifications per granter per window. Three covers sharing a
    /// document with a few colleagues; the Parties incident was 72.
    /// </summary>
    public const int MailPerWindow = 3;

    /// <summary>The budget window — the Parties burst took three minutes; ten covers a slower script.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    /// <summary>The hidden namespace segment under the Admin partition (routes to <c>mesh_nodes</c>, like <c>_LogonAction</c>).</summary>
    public const string NamespaceSegment = "_GrantMail";

    /// <summary>The id suffix of the window's one "told the granter" marker.</summary>
    public const string SummarySuffix = "told";

    private static readonly TimeSpan ClaimTimeout = TimeSpan.FromSeconds(15);

    /// <summary>The window a moment falls in — its UTC start, <c>yyyyMMddHHmm</c>. Pure.</summary>
    public static string WindowKey(DateTimeOffset now)
    {
        var ticks = now.UtcTicks - now.UtcTicks % Window.Ticks;
        return new DateTimeOffset(ticks, TimeSpan.Zero).ToString("yyyyMMddHHmm", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A granter id as ONE path segment. Ids are ObjectIds or user names (sometimes an email), so
    /// the usual characters pass unchanged; anything else becomes <c>_</c>. An empty granter shares
    /// one <c>unknown</c> budget — an unattributed grant is the last one to deserve a free pass. Pure.
    /// </summary>
    public static string GranterKey(string? granter)
    {
        if (string.IsNullOrWhiteSpace(granter))
            return "unknown";
        var sb = new StringBuilder(granter.Length);
        foreach (var c in granter.Trim())
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '@' ? c : '_');
        var key = sb.ToString();
        // "." and ".." are path navigation, not a name: they would make the namespace
        // Admin/_GrantMail/. or Admin/_GrantMail/.. — ambiguous for the create and for the sweep's
        // namespace query. A segment made only of dots becomes as many underscores.
        return key.All(c => c == '.') ? new string('_', key.Length) : key;
    }

    /// <summary>The namespace holding one granter's slots: <c>Admin/_GrantMail/{granter}</c>. Pure.</summary>
    public static string NamespaceFor(string? granter)
        => $"{HomeConfigNodeType.AdminPartition}/{NamespaceSegment}/{GranterKey(granter)}";

    /// <summary>
    /// Claims this grant's place in the granter's budget. Run it as System (the Admin partition is
    /// the platform's). Cold; emits one verdict and completes; never throws.
    /// </summary>
    /// <param name="mesh">The mesh service to create the slot through.</param>
    /// <param name="granter">The assignment's <c>CreatedBy</c>.</param>
    /// <param name="grantPath">The assignment's path (recorded on the slot).</param>
    /// <param name="now">The moment of the grant.</param>
    /// <param name="logger">Optional logger.</param>
    /// <returns>A cold observable of one verdict.</returns>
    public static IObservable<AccessGrantMailVerdict> Claim(
        IMeshService mesh, string? granter, string grantPath, DateTimeOffset now, ILogger? logger = null)
    {
        var window = WindowKey(now);
        var ns = NamespaceFor(granter);
        var content = new AccessGrantMailSlot { Granter = granter ?? string.Empty, Window = window, Grant = grantPath };

        IObservable<AccessGrantMailVerdict> ClaimSlot(int n)
            => n > MailPerWindow
                ? TryCreate(mesh, ns, $"{window}-{SummarySuffix}", content)
                    .Select(won => won ? AccessGrantMailVerdict.BellOnlyAndTellGranter : AccessGrantMailVerdict.BellOnly)
                : TryCreate(mesh, ns, $"{window}-{n}", content)
                    .SelectMany(won => won
                        ? (n == 1 ? SweepOldWindows(mesh, ns, window, WindowKey(now - Window), logger) : Observable.Return(System.Reactive.Unit.Default))
                            .Select(_ => AccessGrantMailVerdict.Mail)
                        : ClaimSlot(n + 1));

        return Observable.Defer(() => ClaimSlot(1))
            .Take(1)
            .Timeout(ClaimTimeout)
            .DefaultIfEmpty(AccessGrantMailVerdict.BellOnly)
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex,
                    "AccessGrantMailBudget: could not decide the mail budget of {Granter} for {Grant} — bell only",
                    granter, grantPath);
                return Observable.Return(AccessGrantMailVerdict.BellOnly);
            });
    }

    // true = this call created the slot; false = it already existed (another grant holds it).
    // Any other failure propagates, and Claim turns it into BellOnly.
    private static IObservable<bool> TryCreate(IMeshService mesh, string ns, string id, AccessGrantMailSlot content)
        => mesh.CreateNode(new MeshNode(id, ns)
            {
                NodeType = NodeType,
                Name = id,
                State = MeshNodeState.Active,
                Content = content,
            })
            .Take(1)
            .Select(_ => true)
            .Catch((Exception ex) => IsAlreadyExists(ex)
                ? Observable.Return(false)
                : Observable.Throw<bool>(ex));

    // The first slot of a new window deletes the granter's windows OLDER THAN THE PREVIOUS ONE, so
    // the namespace holds at most two windows' slots (2 × (MailPerWindow + 1) nodes) per granter.
    // 🚨 The previous window is KEPT: a claim dated in it can still be in flight when the new
    // window's first slot is won (its create has not landed yet), and deleting that window's slots
    // under it would let it re-win slot 1 and mail past the budget. A claim is bounded by
    // ClaimTimeout, far shorter than one Window, so nothing older than the previous window can
    // still be claiming. A sweep that fails never withholds the verdict — the slots it left are
    // only storage, removed by the next window's sweep — but it is logged as a warning, since a
    // sweep that keeps failing lets the namespace grow.
    private static IObservable<System.Reactive.Unit> SweepOldWindows(
        IMeshService mesh, string ns, string window, string previousWindow, ILogger? logger)
        => mesh.Query<MeshNode>(MeshQueryRequest.FromQuery($"namespace:{ns} nodeType:{NodeType} select:path,id"))
            .Take(1)
            .Select(change => change.Items
                .Where(n => !n.Id.StartsWith(window + "-", StringComparison.Ordinal)
                            && !n.Id.StartsWith(previousWindow + "-", StringComparison.Ordinal))
                .Select(n => n.Path)
                .ToList())
            .SelectMany(stale => stale.Count == 0
                ? Observable.Return(System.Reactive.Unit.Default)
                : stale.Select(p => mesh.DeleteNode(p).Take(1)).Merge().LastOrDefaultAsync()
                    .Select(_ => System.Reactive.Unit.Default))
            .DefaultIfEmpty(System.Reactive.Unit.Default)
            .Timeout(ClaimTimeout)
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex, "AccessGrantMailBudget: sweeping old windows under {Namespace} failed", ns);
                return Observable.Return(System.Reactive.Unit.Default);
            });

    /// <summary>A create refused because the path is taken — typed first, message as the fallback (<see cref="NodeCreationFailure"/>).</summary>
    internal static bool IsAlreadyExists(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e.Data[NodeCreationFailure.RejectionReasonKey] is NodeCreationRejectionReason.NodeAlreadyExists)
                return true;
            if (e.Message?.StartsWith("Node already exists", StringComparison.Ordinal) == true)
                return true;
        }
        return false;
    }

    /// <summary>Registers the slot node type and its content type on the mesh builder.</summary>
    /// <typeparam name="TBuilder">The mesh builder type.</typeparam>
    /// <param name="builder">The mesh builder.</param>
    /// <returns>The builder.</returns>
    public static TBuilder AddAccessGrantMailSlotType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(new MeshNode(NodeType)
        {
            Name = "Access Grant Mail Slot",
            Icon = "/static/NodeTypeIcons/shield.svg",
            ExcludeFromContext = new HashSet<string> { "search", "create", "content" },
            HubConfiguration = config => config
                .AddMeshDataSource(source => source.WithContentType<AccessGrantMailSlot>())
        });
        builder.AddAutocompleteExcludedTypes(NodeType);
        builder.ConfigureHub(config => config.WithType<AccessGrantMailSlot>(nameof(AccessGrantMailSlot)));
        return builder;
    }
}
