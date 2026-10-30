using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// A READ-ONLY sweep of every API-token record on this instance for a principal that is not bound to
/// it (#7598): the record's <c>UserId</c> — what every token verdict authenticates as — differs from
/// the person who wrote it, or from the partition it lives in. It REVOKES NOTHING; it lists.
///
/// <para><b>Shape.</b> The ActivationRecycle shape: a request node at
/// <c>Admin/_TokenAudit/{id}</c>, created by a platform admin (only a global admin may create in the
/// Admin partition), executed by its own hub as System, which writes the result back onto the same
/// node. Authorize as the caller, execute as System: the creator's platform-admin standing is
/// checked AGAIN immediately before the sweep runs, and a node created by anyone else is refused by
/// name and swept nothing. See Doc/Architecture/AccessControl → "Auditing token principals".</para>
///
/// <para><b>Never a secret.</b> A finding names the record's path and id, its principal and its
/// creator. The record's <c>TokenHash</c> is never copied into the report.</para>
/// </summary>
public record TokenPrincipalAudit
{
    /// <summary>The node type.</summary>
    public const string NodeType = "TokenPrincipalAudit";

    /// <summary>Where audit requests are created.</summary>
    public const string Namespace = "Admin/_TokenAudit";

    /// <summary>Why the audit is run. Required: the sweep refuses an unstated reason.</summary>
    public string? Reason { get; init; }

    /// <summary>One of the <see cref="TokenPrincipalAuditStatus"/> values; absent on a new request.</summary>
    public string? Status { get; init; }

    /// <summary>Who the request was created by, as the executor established it.</summary>
    public string? RequestedBy { get; init; }

    /// <summary>When the sweep started.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>When the sweep finished (or was refused or failed).</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>The DENOMINATOR: the partitions the sweep read from, as the store reported them.
    /// <c>null</c> means the store did not report its coverage, which the report states as unknown,
    /// never as "all".</summary>
    public ImmutableList<string>? PartitionsRead { get; init; }

    /// <summary>The DENOMINATOR: how many token records were read and judged.</summary>
    public int TokensRead { get; init; }

    /// <summary>False when a read named a silent provider, dropped part of its rows, or did not
    /// report its partitions: the findings are then a floor, not a complete answer.</summary>
    public bool Complete { get; init; }

    /// <summary>Why the result is not complete, when it is not.</summary>
    public string? Incomplete { get; init; }

    /// <summary>Every token record whose principal is not bound to it.</summary>
    public ImmutableList<TokenPrincipalMismatch> Mismatches { get; init; } = ImmutableList<TokenPrincipalMismatch>.Empty;

    /// <summary>Why the request was refused, or why the sweep failed.</summary>
    public string? Error { get; init; }
}

/// <summary>The open set of <see cref="TokenPrincipalAudit.Status"/> values.</summary>
public static class TokenPrincipalAuditStatus
{
    /// <summary>The sweep is running.</summary>
    public const string Running = "Running";

    /// <summary>The sweep finished; read <see cref="TokenPrincipalAudit.Complete"/> before reading its findings as total.</summary>
    public const string Done = "Done";

    /// <summary>The request was refused (creator not a platform admin, or no reason).</summary>
    public const string Refused = "Refused";

    /// <summary>A read failed; nothing is claimed.</summary>
    public const string Failed = "Failed";
}

/// <summary>The open set of values in <see cref="TokenPrincipalMismatch.Findings"/>.</summary>
public static class TokenPrincipalFinding
{
    /// <summary>The principal is not the one the record's location belongs to: a person's token outside
    /// <c>{UserId}/…</c>, or a service's outside its own token namespace.</summary>
    public const string PrincipalOutsideLocation = "PrincipalOutsideLocation";

    /// <summary>A person's token whose principal is not the person who created the record.</summary>
    public const string PrincipalDiffersFromCreator = "PrincipalDiffersFromCreator";

    /// <summary>A token naming System, which no person's token may ever name.</summary>
    public const string NamesSystem = "NamesSystem";
}

/// <summary>One token record whose principal is not bound to it. Carries no secret and no hash.</summary>
/// <param name="Path">The record's path.</param>
/// <param name="TokenId">The record's node id.</param>
/// <param name="Label">The token's label, as its owner named it.</param>
/// <param name="Principal">The principal the record authenticates as (<c>UserId</c>).</param>
/// <param name="CreatedBy">Who created the record, as the store stamped it.</param>
/// <param name="Findings">One or more of the <see cref="TokenPrincipalFinding"/> values.</param>
public sealed record TokenPrincipalMismatch(
    string Path, string TokenId, string? Label, string? Principal, string? CreatedBy, ImmutableList<string> Findings);

/// <summary>The sweep and its executor.</summary>
public static class TokenPrincipalAuditing
{
    /// <summary>How long one listing may take.</summary>
    public static readonly TimeSpan ReadBudget = TimeSpan.FromMinutes(2);

    private const string TokenRecordType = "ApiToken";

    /// <summary>Registers the request node type, executed by its own hub.</summary>
    public static TBuilder AddTokenPrincipalAudit<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(new MeshNode(TokenPrincipalAudit.NodeType)
        {
            Name = "Token Principal Audit",
            ExcludeFromContext = ImmutableHashSet.Create("search"),
            HubConfiguration = config => config
                .AddMeshDataSource(source => source.WithContentType<TokenPrincipalAudit>())
                .WithInitialization(Execute),
        });
        builder.ConfigureHub(c => c.WithType<TokenPrincipalAudit>(nameof(TokenPrincipalAudit)));
        builder.ConfigureDefaultNodeHub(c => c.WithType<TokenPrincipalAudit>(nameof(TokenPrincipalAudit)));
        return builder;
    }

    /// <summary>
    /// The findings for ONE stored node, or null when it is not a person's or service's token record
    /// (an index row, or no credential at all) or when its principal is bound. Pure.
    /// </summary>
    /// <param name="node">The stored node.</param>
    /// <param name="options">Serializer options that read its content.</param>
    public static TokenPrincipalMismatch? Judge(MeshNode node, JsonSerializerOptions options)
    {
        if (TokenOf(node, options) is not { TokenHash.Length: > 0 } token || string.IsNullOrEmpty(node.Path))
            return null;
        var path = node.Path;
        var principal = token.UserId;
        var findings = ImmutableList<string>.Empty;
        if (string.Equals(principal, WellKnownUsers.System, StringComparison.Ordinal))
            findings = findings.Add(TokenPrincipalFinding.NamesSystem);
        if (!IsBoundToItsLocation(path, token))
            findings = findings.Add(TokenPrincipalFinding.PrincipalOutsideLocation);
        if (!IsService(token)
            && !string.IsNullOrEmpty(node.CreatedBy)
            && !string.Equals(node.CreatedBy, WellKnownUsers.System, StringComparison.Ordinal)
            && !string.Equals(node.CreatedBy, principal, StringComparison.OrdinalIgnoreCase))
            findings = findings.Add(TokenPrincipalFinding.PrincipalDiffersFromCreator);
        return findings.IsEmpty
            ? null
            : new TokenPrincipalMismatch(path, node.Id ?? path, token.Label, principal, node.CreatedBy, findings);
    }

    /// <summary>
    /// Whether a token record's principal belongs to its location: a person's token lives in that
    /// person's own partition (<c>{UserId}/…</c>, or the pre-migration <c>User/{UserId}/…</c>), a
    /// service's in its own token namespace (<see cref="ServiceIdentity.TokenNamespace"/>).
    /// </summary>
    /// <param name="path">The record's path.</param>
    /// <param name="token">The record's content.</param>
    public static bool IsBoundToItsLocation(string path, ApiToken token)
    {
        if (string.IsNullOrEmpty(token.UserId) || string.Equals(token.UserId, WellKnownUsers.System, StringComparison.Ordinal))
            return false;
        if (IsService(token))
            return ServiceIdentity.IsServiceObjectId(token.UserId)
                   && path.StartsWith(ServiceIdentity.TokenNamespace(token.UserId) + "/", StringComparison.OrdinalIgnoreCase);
        return path.StartsWith(token.UserId + "/", StringComparison.OrdinalIgnoreCase)
               || path.StartsWith($"User/{token.UserId}/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsService(ApiToken token) =>
        ServiceIdentity.IsServiceObjectId(token.UserId) || !string.IsNullOrEmpty(token.ServiceIdentityPath);

    /// <summary>The credential a node carries, read as a token verdict reads it; an index row
    /// (it names the record it points to) is not a credential and yields null.</summary>
    private static ApiToken? TokenOf(MeshNode node, JsonSerializerOptions options)
    {
        if (Read<ApiTokenIndex>(node, options) is { TokenPath.Length: > 0 })
            return null;
        return Read<ApiToken>(node, options);
    }

    private static T? Read<T>(MeshNode node, JsonSerializerOptions options) where T : class
    {
        if (node.ContentAs<T>(options) is { } typed)
            return typed;
        if (node.Content is not JsonElement element)
            return null;
        try { return JsonSerializer.Deserialize<T>(element.GetRawText(), options); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// The sweep, as System: every token record in every partition the store enumerates, plus the
    /// Admin partition's service tokens (a declared fan-out never enumerates the system schemas, so
    /// the Admin partition is read on its own anchor). Emits the finished result once.
    /// </summary>
    /// <param name="hub">Any hub of the mesh.</param>
    public static IObservable<TokenPrincipalAudit> Sweep(IMessageHub hub)
    {
        var mesh = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var access = hub.ServiceProvider.GetRequiredService<AccessService>();
        var options = hub.JsonSerializerOptions;
        IObservable<QueryResultChange<MeshNode>> List(string query) =>
            access.RunAsSystem(() => mesh.Query<MeshNode>(MeshQueryRequest.FromQuery(query))
                .Where(change => change.ChangeType == QueryChangeType.Initial)
                .Take(1)
                .Timeout(ReadBudget));

        return List($"nodeType:{TokenRecordType} partitions:all")
            .Zip(List($"namespace:Admin scope:descendants nodeType:{TokenRecordType}"),
                (fanOut, admin) => (fanOut, admin))
            .Select(read =>
            {
                var changes = new[] { read.fanOut, read.admin };
                var nodes = changes.SelectMany(c => c.Items)
                    .Where(n => n.Path is not null)
                    .GroupBy(n => n.Path!, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .ToImmutableList();
                var tokens = nodes.Where(n => TokenOf(n, options) is { TokenHash.Length: > 0 }).ToImmutableList();
                var mismatches = tokens.Select(n => Judge(n, options)).OfType<TokenPrincipalMismatch>()
                    .OrderBy(m => m.Path, StringComparer.Ordinal)
                    .ToImmutableList();
                var partitions = changes.All(c => c.Partitions is null)
                    ? null
                    : changes.SelectMany(c => c.Partitions ?? []).Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(p => p, StringComparer.Ordinal).ToImmutableList();
                var gaps = ImmutableList<string>.Empty;
                foreach (var c in changes)
                {
                    if (c.SilentProviders is { Count: > 0 } silent)
                        gaps = gaps.Add($"provider(s) [{string.Join(", ", silent)}] did not answer");
                    if (c.SnapshotIncomplete)
                        gaps = gaps.Add("a provider dropped part of its read");
                    if (c.PartiallyReadPartitions is { Count: > 0 } partial)
                        gaps = gaps.Add($"partition(s) [{string.Join(", ", partial)}] were read only in part");
                }
                if (partitions is null)
                    gaps = gaps.Add("the store did not report which partitions it read (denominator unknown)");
                return new TokenPrincipalAudit
                {
                    PartitionsRead = partitions,
                    TokensRead = tokens.Count,
                    Mismatches = mismatches,
                    Complete = gaps.IsEmpty,
                    Incomplete = gaps.IsEmpty ? null : string.Join("; ", gaps),
                };
            });
    }

    /// <summary>
    /// The request node's own hub: on activation, a request that has not started is checked (creator
    /// a platform admin, a reason given), marked Running, swept as System and written back — once.
    /// A request that already ran is left exactly as it is.
    /// </summary>
    private static void Execute(IMessageHub hub)
    {
        var path = (hub.Address with { Host = null }).ToString();
        var access = hub.ServiceProvider.GetRequiredService<AccessService>();
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(TokenPrincipalAuditing));
        var options = hub.JsonSerializerOptions;
        var stream = hub.GetWorkspace().GetMeshNodeStream(path);

        IObservable<MeshNode> Write(Func<TokenPrincipalAudit, TokenPrincipalAudit> change) =>
            access.RunAsSystem(() => stream.Update(n => n with
            {
                Content = change(n.ContentAs<TokenPrincipalAudit>(options) ?? new TokenPrincipalAudit()),
            }));

        access.RunAsSystem(() => stream.Where(n => n is not null).Take(1))
            .SelectMany(node =>
            {
                var request = node!.ContentAs<TokenPrincipalAudit>(options) ?? new TokenPrincipalAudit();
                if (request.Status is not null)
                    return Observable.Empty<MeshNode>();
                var creator = node.CreatedBy;
                if (string.IsNullOrWhiteSpace(request.Reason))
                    return Write(r => r with
                    {
                        Status = TokenPrincipalAuditStatus.Refused, RequestedBy = creator, CompletedAt = DateTimeOffset.UtcNow,
                        Error = "a token principal audit needs a reason; nothing was swept",
                    });
                if (string.IsNullOrEmpty(creator))
                    return Write(r => r with
                    {
                        Status = TokenPrincipalAuditStatus.Refused, CompletedAt = DateTimeOffset.UtcNow,
                        Error = "the request carries no creator, so its authority cannot be checked; nothing was swept",
                    });
                // Authorize as the caller, AGAIN, immediately before executing as System.
                return hub.IsGlobalAdmin(creator).Take(1).Timeout(ReadBudget)
                    .SelectMany(isAdmin => !isAdmin
                        ? Write(r => r with
                        {
                            Status = TokenPrincipalAuditStatus.Refused, RequestedBy = creator, CompletedAt = DateTimeOffset.UtcNow,
                            Error = $"'{creator}' is not a platform admin; only a platform admin may run a token principal audit. Nothing was swept.",
                        })
                        : Write(r => r with { Status = TokenPrincipalAuditStatus.Running, RequestedBy = creator, StartedAt = DateTimeOffset.UtcNow })
                            .Take(1)
                            .SelectMany(_ => Sweep(hub))
                            .SelectMany(result => Write(r => r with
                            {
                                Status = TokenPrincipalAuditStatus.Done,
                                CompletedAt = DateTimeOffset.UtcNow,
                                PartitionsRead = result.PartitionsRead,
                                TokensRead = result.TokensRead,
                                Complete = result.Complete,
                                Incomplete = result.Incomplete,
                                Mismatches = result.Mismatches,
                            })));
            })
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex, "[TokenPrincipalAudit] {Path}: the sweep failed; nothing is claimed", path);
                return Write(r => r with
                {
                    Status = TokenPrincipalAuditStatus.Failed, CompletedAt = DateTimeOffset.UtcNow,
                    Error = $"{ex.GetType().Name}: {ex.Message}",
                }).Catch((Exception _) => Observable.Empty<MeshNode>());
            })
            .Subscribe(
                _ => { },
                ex => logger?.LogWarning(ex, "[TokenPrincipalAudit] {Path}: the result could not be written", path));
    }
}
