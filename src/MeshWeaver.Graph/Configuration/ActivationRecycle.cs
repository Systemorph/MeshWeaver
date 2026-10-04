using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// A request that every LIVE activation of one or more NodeTypes be recycled, on every process of
/// the mesh — the durable record at <c>Admin/_Recycle/{id}</c> that IS the broadcast.
///
/// <para><b>Why a node, and not a message.</b> A per-node hub binds its NodeType's build once and is
/// pinned by address until a <see cref="DisposeRequest"/> reaches it
/// (<c>Doc/Architecture/StaleStateUntilRecycle</c>). Which activations exist is a fact of each
/// PROCESS — a silo hosts its own — so the request has to reach every process, and the one channel
/// that already does is a durable write: every replica's <see cref="IMeshInvalidationFeed"/> hears
/// every commit (PostgreSQL LISTEN/NOTIFY in production, the in-process feed on a monolith). No
/// cluster-singleton grain, no index of stored instances: each process walks the hubs its mesh hub
/// HOSTS (<see cref="HostedHubsCollection"/> — the per-node hubs of a monolith, the grains' hubs of a
/// silo), keeps those bound from a requested NodeType (<see cref="NodeTypePathHolder"/>) and
/// disposes those.</para>
///
/// <para>The record is also the audit: who asked, when, why, which types — and, as its children
/// (<see cref="ActivationRecycleReport"/>), what each process actually disposed.</para>
/// </summary>
public record ActivationRecycleRequest
{
    /// <summary>The node type of a request.</summary>
    public const string NodeType = "ActivationRecycle";

    /// <summary>Where requests live: platform infrastructure, in the Admin partition.</summary>
    public const string Namespace = "Admin/_Recycle";

    /// <summary>The NodeTypes whose live activations are recycled (full NodeType paths).</summary>
    public ImmutableList<string> NodeTypes { get; init; } = ImmutableList<string>.Empty;

    /// <summary>Optional: only these exact addresses (each must also be of one of <see cref="NodeTypes"/> when those are given).</summary>
    public ImmutableList<string> Paths { get; init; } = ImmutableList<string>.Empty;

    /// <summary>Optional: only activations at or under this path (e.g. one partition).</summary>
    public string? UnderPath { get; init; }

    /// <summary>WHY — carried onto every <see cref="DisposeRequest"/> and so into each target's [QUIESCE-START]. Required.</summary>
    public string Reason { get; init; } = "";

    /// <summary>Who or what asked (a user id, a job path, a rule name).</summary>
    public string? RequestedBy { get; init; }

    /// <summary>When it was asked.</summary>
    public DateTimeOffset RequestedAt { get; init; }
}

/// <summary>
/// What ONE process did about an <see cref="ActivationRecycleRequest"/> — a child node of the request,
/// at <c>{request}/{process}</c>, written as System once the process has posted its disposals.
/// </summary>
public record ActivationRecycleReport
{
    /// <summary>The node type of a report.</summary>
    public const string NodeType = "ActivationRecycleReport";

    /// <summary>The process (silo identity, or machine and process id on a monolith).</summary>
    public string Process { get; init; } = "";

    /// <summary>How many live activations on this process matched the request.</summary>
    public int Matched { get; init; }

    /// <summary>How many of them were sent a <see cref="DisposeRequest"/> (the rest had already started tearing down).</summary>
    public int Disposed { get; init; }

    /// <summary>The disposed addresses, at most <see cref="ActivationRecycle.MaxReportedPaths"/> of them.</summary>
    public ImmutableList<string> Paths { get; init; } = ImmutableList<string>.Empty;

    /// <summary>When the last disposal was posted.</summary>
    public DateTimeOffset At { get; init; }
}

/// <summary>What <see cref="ActivationRecycle.Request"/> answers: the request's path, or why nothing was written.</summary>
/// <param name="Path">The request node, or null when refused.</param>
/// <param name="Refusal">Why the request was refused (nothing was written), or null.</param>
public sealed record ActivationRecycleTicket(string? Path, string? Refusal)
{
    /// <summary>True when the request was written.</summary>
    public bool Accepted => Path is not null;
}

/// <summary>
/// Type-scoped recycle: request, broadcast, per-process disposal, report. See
/// <see cref="ActivationRecycleRequest"/> for the design; <c>Doc/Architecture/StaleStateUntilRecycle</c>
/// for where it sits.
/// </summary>
public static class ActivationRecycle
{
    /// <summary>Disposals posted per batch on one process.</summary>
    public const int BatchSize = 20;

    /// <summary>The pause between batches — so a type with many live hubs does not tear them all down in one burst.</summary>
    public static readonly TimeSpan BatchInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>The most addresses a report lists (the count is always exact).</summary>
    public const int MaxReportedPaths = 200;

    /// <summary>How long a process waits to read a request it heard about.</summary>
    public static readonly TimeSpan ReadBudget = TimeSpan.FromSeconds(30);

    /// <summary>Registers the request/report node types and the per-process agent.</summary>
    public static TBuilder AddActivationRecycle<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(
            new MeshNode(ActivationRecycleRequest.NodeType)
            {
                Name = "Activation Recycle",
                ExcludeFromContext = new HashSet<string> { "search", "create" },
                HubConfiguration = config => config
                    .AddMeshDataSource(source => source.WithContentType<ActivationRecycleRequest>()),
            },
            new MeshNode(ActivationRecycleReport.NodeType)
            {
                Name = "Activation Recycle Report",
                IsSatelliteType = true,
                ExcludeFromContext = new HashSet<string> { "search", "create", "content" },
                HubConfiguration = config => config
                    .AddMeshDataSource(source => source.WithContentType<ActivationRecycleReport>()),
            });
        builder.ConfigureHub(c => c
            .WithType<ActivationRecycleRequest>(nameof(ActivationRecycleRequest))
            .WithType<ActivationRecycleReport>(nameof(ActivationRecycleReport))
            .WithInitialization(Arm));
        builder.ConfigureDefaultNodeHub(c => c
            .WithType<ActivationRecycleRequest>(nameof(ActivationRecycleRequest))
            .WithType<ActivationRecycleReport>(nameof(ActivationRecycleReport)));
        return builder;
    }

    /// <summary>
    /// The NodeTypes a request may never name: the definition type <c>NodeType</c> (it would recycle
    /// every NodeType definition hub of the mesh) and this feature's own request and report types (it
    /// would tear down its own audit while it is being written).
    /// </summary>
    public static readonly ImmutableHashSet<string> RefusedNodeTypes = ImmutableHashSet.Create(
        StringComparer.OrdinalIgnoreCase,
        MeshNode.NodeTypePath, ActivationRecycleRequest.NodeType, ActivationRecycleReport.NodeType);

    /// <summary>The longest caller-supplied request id; longer ids are refused rather than cut, so two ids never collapse silently.</summary>
    public const int MaxIdLength = 80;

    /// <summary>
    /// Why <paramref name="request"/> must not be written, or null. Pure. Refused: no reason (#3510:
    /// an unstated reason cost six occurrences and four bake seals); no NodeType (a request is always
    /// TYPE-scoped — <see cref="ActivationRecycleRequest.Paths"/> and
    /// <see cref="ActivationRecycleRequest.UnderPath"/> only narrow it); a blank type or address; and
    /// any of <see cref="RefusedNodeTypes"/>. The per-process agent applies the same check to what it
    /// reads, so a request that bypassed <see cref="Request"/> is refused there too.
    /// </summary>
    public static string? Validate(ActivationRecycleRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
            return "a recycle needs a reason — it is carried into every target's [QUIESCE-START] line";
        if (request.NodeTypes.IsEmpty)
            return "a recycle must name at least one NodeType — addresses and a path only narrow it";
        if (request.NodeTypes.Any(t => string.IsNullOrWhiteSpace(t)))
            return "a NodeType in the request is blank";
        if (request.NodeTypes.FirstOrDefault(RefusedNodeTypes.Contains) is { } refused)
            return $"'{refused}' is refused: a recycle of it would tear down every NodeType definition hub or this feature's own audit";
        if (request.Paths.Any(p => string.IsNullOrWhiteSpace(p.Trim('/'))))
            return "an address in the request is blank";
        return null;
    }

    /// <summary>
    /// Writes <paramref name="request"/> as System at <c>Admin/_Recycle/{id}</c> — which is what makes
    /// every process recycle its matching live activations. Refuses (writing nothing) when
    /// <see cref="Validate"/> does, or when a named NodeType does not exist. Cold; emits once.
    /// </summary>
    /// <param name="hub">Any surviving hub of the mesh (never one the request would recycle).</param>
    /// <param name="request">What to recycle and why.</param>
    /// <param name="id">The request's id — a dedupe key: when a request already exists at that id its
    /// path is answered and nothing is written again. Letters, digits and '-' only, at most
    /// <see cref="MaxIdLength"/>; any other id is refused rather than rewritten, so two ids never
    /// collapse onto one request.</param>
    public static IObservable<ActivationRecycleTicket> Request(IMessageHub hub, ActivationRecycleRequest request, string? id = null)
    {
        if (Validate(request) is { } refusal)
            return Observable.Return(new ActivationRecycleTicket(null, refusal));
        if (id is { } given && (given.Length == 0 || given.Length > MaxIdLength || Segment(given) != given))
            return Observable.Return(new ActivationRecycleTicket(null,
                $"the request id '{SanitizeReason(given)}' is refused: use letters, digits and '-' only, at most {MaxIdLength} characters"));
        var mesh = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var access = hub.ServiceProvider.GetRequiredService<AccessService>();
        var nodeId = id ?? Segment($"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}"[..30]);
        var path = $"{ActivationRecycleRequest.Namespace}/{nodeId}";
        IObservable<bool> Exists() =>
            access.RunAsSystem(() => mesh.Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{path}")).Take(1).Timeout(ReadBudget))
                .Select(change => change.Items.Any(n => string.Equals(n.Path, path, StringComparison.OrdinalIgnoreCase)));

        // A NodeType is either a framework built-in (a static node — no NodeType stamp of its own)
        // or a node of type NodeType (a compiled, imported or installed definition).
        IObservable<bool> TypeExists(string type) =>
            hub.ServiceProvider.FindStaticNode(type) is not null
                ? Observable.Return(true)
                : access.RunAsSystem(() => mesh.Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{type}"))
                    .Take(1)
                    .Timeout(ReadBudget))
                .Select(change => change.Items.Any(n => string.Equals(n.Path, type, StringComparison.OrdinalIgnoreCase)
                                                        && string.Equals(n.NodeType, MeshNode.NodeTypePath, StringComparison.OrdinalIgnoreCase)));

        return request.NodeTypes.ToObservable()
            .Select(t => TypeExists(t).Select(ok => (Type: t, Ok: ok)))
            .Concat()
            .ToList()
            .SelectMany(checks =>
            {
                var unknown = checks.Where(c => !c.Ok).Select(c => c.Type).ToList();
                if (unknown.Count > 0)
                    return Observable.Return(new ActivationRecycleTicket(null,
                        $"unknown NodeType(s): {string.Join(", ", unknown)} — nothing was recycled"));
                var node = new MeshNode(nodeId, ActivationRecycleRequest.Namespace)
                {
                    Name = $"Recycle {string.Join(", ", request.NodeTypes.Concat(request.Paths).Take(3))}",
                    NodeType = ActivationRecycleRequest.NodeType,
                    Content = request with { RequestedAt = request.RequestedAt == default ? DateTimeOffset.UtcNow : request.RequestedAt },
                };
                // The id is the dedupe key: an existing request is the answer, never a second write.
                return Exists().SelectMany(there => there
                    ? Observable.Return(new ActivationRecycleTicket(path, null))
                    : access.RunAsSystem(() => mesh.CreateNode(node).Take(1))
                        .Select(_ => new ActivationRecycleTicket(path, null)));
            })
            .Take(1);
    }

    /// <summary>The reports written so far for a request, read as System. Cold; one snapshot.</summary>
    public static IObservable<ImmutableList<ActivationRecycleReport>> Reports(IMessageHub hub, string requestPath)
    {
        var mesh = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var access = hub.ServiceProvider.GetRequiredService<AccessService>();
        return access.RunAsSystem(() => mesh.Query<MeshNode>(
                    MeshQueryRequest.FromQuery($"path:{requestPath} scope:children nodeType:{ActivationRecycleReport.NodeType}"))
                .Take(1)
                .Timeout(ReadBudget))
            .Select(change => change.Items
                .Select(n => n.ContentAs<ActivationRecycleReport>(hub.JsonSerializerOptions))
                .OfType<ActivationRecycleReport>()
                .ToImmutableList());
    }

    /// <summary>
    /// The hubs of <paramref name="live"/> that <paramref name="request"/> selects:
    /// bound from one of its types, at one of its addresses, under its path — and still RUNNING
    /// (a hub already tearing down is not disposed twice). Pure over the snapshot.
    /// </summary>
    public static ImmutableList<IMessageHub> Select(IEnumerable<IMessageHub> live, ActivationRecycleRequest request)
    {
        var types = request.NodeTypes.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        var paths = request.Paths.Select(p => p.Trim('/')).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        var under = request.UnderPath?.Trim('/');
        return live
            .Where(h => h.RunLevel is MessageHubRunLevel.Starting or MessageHubRunLevel.Started)
            .Where(h =>
            {
                var path = PathOf(h);
                // Only per-node hubs carry the type they were bound from; infrastructure hubs
                // (portal/…, client/…, the issuing hubs) never match anything.
                if (BoundNodeType(h) is not { } bound)
                    return false;
                if (!types.IsEmpty && !types.Contains(bound))
                    return false;
                if (!paths.IsEmpty && !paths.Contains(path))
                    return false;
                if (!string.IsNullOrEmpty(under)
                    && !string.Equals(path, under, StringComparison.OrdinalIgnoreCase)
                    && !path.StartsWith(under + "/", StringComparison.OrdinalIgnoreCase))
                    return false;
                return true;
            })
            .GroupBy(PathOf, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(PathOf, StringComparer.Ordinal)
            .ToImmutableList();
    }

    /// <summary>
    /// Recycles this PROCESS's live activations that <paramref name="request"/> selects: one
    /// <see cref="DisposeRequest"/> each, with the request's reason, posted from the mesh's
    /// node-operation issuing hub (a survivor, never the router — #4463), in batches of
    /// <see cref="BatchSize"/> spaced by <see cref="BatchInterval"/>. Each target goes through its
    /// own normal quiesce. Cold; emits the report once the last batch is posted.
    /// </summary>
    public static IObservable<ActivationRecycleReport> RecycleLocal(
        IMessageHub meshHub, ActivationRecycleRequest request, string requestPath, IScheduler? scheduler = null)
    {
        var hosted = meshHub.ServiceProvider.GetService<HostedHubsCollection>();
        var process = ProcessIdentity(meshHub);
        return Observable.Defer(() =>
        {
            var matched = Select(hosted?.Hubs.ToImmutableList() ?? ImmutableList<IMessageHub>.Empty, request);
            var reason = $"ActivationRecycle '{requestPath}' (requested by {SanitizeReason(request.RequestedBy ?? "an unnamed caller")}): "
                         + SanitizeReason(request.Reason);
            var issuing = meshHub.NodeOperationIssuingHub();
            var batches = matched.Select((h, i) => (Hub: h, Batch: i / BatchSize))
                .GroupBy(x => x.Batch)
                .Select(g => g.Select(x => x.Hub).ToImmutableList())
                .ToImmutableList();
            return batches.ToObservable()
                .Select((batch, i) => Observable.Timer(i == 0 ? TimeSpan.Zero : BatchInterval, scheduler ?? Scheduler.Default)
                    .Select(_ => batch
                        // re-checked at post time: a hub that began tearing down since the snapshot is not disposed twice
                        .Where(h => h.RunLevel is MessageHubRunLevel.Starting or MessageHubRunLevel.Started)
                        .Select(h =>
                        {
                            var target = PathOf(h);
                            issuing.Post(new DisposeRequest { Reason = reason }, o => o.WithTarget(new Address(target)));
                            return target;
                        })
                        .ToImmutableList()))
                .Concat()
                .Aggregate(ImmutableList<string>.Empty, (all, posted) => all.AddRange(posted))
                .Select(disposed => new ActivationRecycleReport
                {
                    Process = process,
                    Matched = matched.Count,
                    Disposed = disposed.Count,
                    Paths = disposed.Take(MaxReportedPaths).ToImmutableList(),
                    At = DateTimeOffset.UtcNow,
                });
        });
    }

    /// <summary>The NodeType a hub was BOUND from — the type whose configuration it runs; null for a hub that is not a per-node hub.</summary>
    public static string? BoundNodeType(IMessageHub hub) => hub.Configuration.Get<NodeTypePathHolder>()?.Path;

    /// <summary>A hosted hub's node path (its address without the silo host).</summary>
    public static string PathOf(IMessageHub hub) => (hub.Address with { Host = null }).ToString();

    /// <summary>This process's identity for a report: the silo address, else machine and process id.</summary>
    public static string ProcessIdentity(IMessageHub meshHub) =>
        meshHub.ServiceProvider.GetService<IClusterMembership>()?.LocalIdentity is { Length: > 0 } silo
            ? silo
            : $"{Environment.MachineName}-{Environment.ProcessId}";

    /// <summary>
    /// Flattens a caller's reason onto ONE log line (every control character becomes a space, runs
    /// collapse) and bounds it — the same log-forgery rule MeshOperations.SanitizeReason states: the
    /// reason is rendered into each target's [QUIESCE-START], which the incident filer fingerprints.
    /// </summary>
    public static string SanitizeReason(string reason)
    {
        var flat = new string((reason ?? "").Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        flat = string.Join(' ', flat.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= 300 ? flat : flat[..300] + "… (truncated)";
    }

    /// <summary>A path segment from free text: letters, digits and '-' only.</summary>
    public static string Segment(string raw)
    {
        var chars = (raw ?? "").Trim().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var s = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return s.Length == 0 ? "recycle" : s;
    }

    /// <summary>
    /// The per-process agent, armed on the MESH hub of every process: hears each request's commit on
    /// the invalidation feed, reads it as System, recycles the matching local activations and writes
    /// this process's report. One request at a time per process; each request handled once.
    /// </summary>
    private static IObservable<Unit> Arm(IMessageHub meshHub)
    {
        var logger = meshHub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(ActivationRecycle));
        var feed = meshHub.ServiceProvider.GetService<IMeshInvalidationFeed>();
        var mesh = meshHub.ServiceProvider.GetService<IMeshService>();
        var access = meshHub.ServiceProvider.GetService<AccessService>();
        if (feed is null || mesh is null || access is null)
            return Observable.Return(Unit.Default);
        var heard = new Subject<string>();
        var handled = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        meshHub.RegisterForDisposal(heard);
        meshHub.RegisterForDisposal(feed.Subscribe(change =>
        {
            if (IsRequest(change) && handled.TryAdd(change.Path, 0))
                heard.OnNext(change.Path);
        }));
        meshHub.RegisterForDisposal(heard
            .Select(path => Handle(meshHub, mesh, access, path, logger)
                .Catch((Exception ex) =>
                {
                    logger?.LogError(ex, "[ActivationRecycle] {Path}: this process could not act on the request — its live activations of the named types keep their build", path);
                    return Observable.Empty<Unit>();
                }))
            .Concat()
            .Subscribe(_ => { }, ex => logger?.LogError(ex, "[ActivationRecycle] the agent on {Hub} stopped", meshHub.Address)));
        return Observable.Return(Unit.Default);
    }

    /// <summary>
    /// Whether a change event is the commit of a request (not a report, not a nested node). Created
    /// OR Updated: a storage relay reports a durable write as an update whatever it was (the
    /// in-memory adapter and a NOTIFY payload carry no create/update distinction), and each process
    /// handles a request path once, so a later rewrite cannot replay it.
    /// </summary>
    public static bool IsRequest(MeshChangeEvent change) =>
        change.Kind != MeshChangeKind.Deleted
        && string.Equals(change.NodeType, ActivationRecycleRequest.NodeType, StringComparison.OrdinalIgnoreCase)
        && string.Equals(change.Namespace?.Trim('/'), ActivationRecycleRequest.Namespace, StringComparison.OrdinalIgnoreCase);

    private static IObservable<Unit> Handle(IMessageHub meshHub, IMeshService mesh, AccessService access, string path, ILogger? logger) =>
        access.RunAsSystem(() => mesh.Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{path}")).Take(1).Timeout(ReadBudget))
            .Select(change => change.Items.FirstOrDefault(n => string.Equals(n.Path, path, StringComparison.OrdinalIgnoreCase)))
            .SelectMany(found =>
            {
                // 🚨 The durable write IS the broadcast, so the agent trusts only what Request wrote: a
                // node created by System and valid on its own terms. Anyone else able to write under
                // Admin/_Recycle (a platform admin holds Admin) bypasses Request's checks — and is
                // refused here, with nothing disposed.
                var request = found?.ContentAs<ActivationRecycleRequest>(meshHub.JsonSerializerOptions);
                var why = found is null || request is null ? "unreadable"
                    : !string.Equals(found.CreatedBy, WellKnownUsers.System, StringComparison.OrdinalIgnoreCase)
                        ? $"written by '{found.CreatedBy}', not by System — only ActivationRecycle.Request may issue one"
                        : Validate(request);
                if (request is null || why is not null)
                {
                    logger?.LogWarning("[ActivationRecycle] {Path}: the request is refused on this process ({Why}) — nothing recycled",
                        path, why);
                    return Observable.Empty<Unit>();
                }
                return RecycleLocal(meshHub, request, path)
                    .SelectMany(report =>
                    {
                        logger?.LogInformation(
                            "[ActivationRecycle] {Path}: process {Process} matched {Matched} live activation(s) of {Types} and disposed {Disposed} — reason: {Reason}",
                            path, report.Process, report.Matched, string.Join(", ", request.NodeTypes.Concat(request.Paths)), report.Disposed, request.Reason);
                        var node = new MeshNode(Segment(report.Process), path)
                        {
                            Name = $"{report.Process}: {report.Disposed} disposed",
                            NodeType = ActivationRecycleReport.NodeType,
                            Content = report,
                        };
                        return access.RunAsSystem(() => mesh.CreateNode(node).Take(1))
                            .Select(_ => Unit.Default)
                            .Catch((Exception ex) =>
                            {
                                logger?.LogWarning(ex, "[ActivationRecycle] {Path}: the report of {Process} could not be written (the disposals were posted)", path, report.Process);
                                return Observable.Return(Unit.Default);
                            });
                    });
            });
}
