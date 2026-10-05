using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.Configuration;

/// <summary>Where a <see cref="PackageUninstallRequest"/> has got to — open string constants.</summary>
public static class PackageUninstallStatus
{
    /// <summary>Written, not yet picked up.</summary>
    public const string Requested = "Requested";

    /// <summary>Phase 1 is running: refusals checked, the preview measured, the module retired,
    /// the package's hubs closed and its install record removed.</summary>
    public const string Uninstalling = "Uninstalling";

    /// <summary>Phase 1 is done: the package is UNINSTALLED and its data RETAINED. The preview says
    /// exactly what phase 2 would destroy; phase 2 runs only once the requester sends
    /// <see cref="PackageUninstallRequest.ConfirmationRequired"/> back.</summary>
    public const string AwaitingConfirmation = "AwaitingConfirmation";

    /// <summary>Confirmed: the partition storage is being dropped.</summary>
    public const string TearingDown = "TearingDown";

    /// <summary>Uninstalled and its partitions torn down.</summary>
    public const string Done = "Done";

    /// <summary>Red, with <see cref="PackageUninstallRequest.Failure"/> naming why.</summary>
    public const string Failed = "Failed";

    /// <summary>Terminal — <see cref="Done"/> or <see cref="Failed"/>. Unknown values are not.</summary>
    public static bool IsTerminal(string? status) =>
        string.Equals(status, Done, StringComparison.Ordinal)
        || string.Equals(status, Failed, StringComparison.Ordinal);
}

/// <summary>
/// "Uninstall package P on this instance" — ONE durable request at
/// <c>Admin/_PackageUninstall/{id}</c> (<c>Doc/Architecture/PackageUninstall</c>), executed in two
/// phases by the instance's executor. Phase 1 (no confirmation) refuses a shared partition or one
/// holding user data, retires the module, closes the package's hubs, removes its install record
/// and blocks every unattended re-install — then measures and records exactly what phase 2 WOULD
/// destroy. Phase 2 — the irreversible drop of the partition storage and its registry record —
/// runs only after the requester sends <see cref="ConfirmationRequired"/> back as
/// <see cref="Confirmation"/>; who confirmed and when is recorded.
/// </summary>
public record PackageUninstallRequest
{
    /// <summary>The node type of a request.</summary>
    public const string NodeType = "PackageUninstall";

    /// <summary>Where requests live.</summary>
    public const string Namespace = "Admin/_PackageUninstall";

    /// <summary>The package id (<c>Plugins/{Package}</c>). Required.</summary>
    public string Package { get; init; } = "";

    /// <summary>Why. Required.</summary>
    public string Reason { get; init; } = "";

    /// <summary>Who asked — the only principal whose confirmation phase 2 accepts.</summary>
    public string? RequestedBy { get; init; }

    /// <summary>When it was asked.</summary>
    public DateTimeOffset RequestedAt { get; init; }

    /// <summary><see cref="PackageUninstallStatus"/>. Executor-owned.</summary>
    public string Status { get; init; } = PackageUninstallStatus.Requested;

    /// <summary>The module the package declared, when it declared one. Executor-owned.</summary>
    public string? Module { get; init; }

    /// <summary>How the module was retired — live, by a restart, or why not. Executor-owned.</summary>
    public string? ModuleOutcome { get; init; }

    /// <summary>When the ONE restart was requested, if the module needed one. Executor-owned.</summary>
    public DateTimeOffset? RestartRequestedAt { get; init; }

    /// <summary>Exactly what phase 2 destroys, per partition. Executor-owned.</summary>
    public ImmutableList<PackageUninstallPartition> Partitions { get; init; } = ImmutableList<PackageUninstallPartition>.Empty;

    /// <summary>The exact string the requester must send back as <see cref="Confirmation"/> to run
    /// phase 2 — the partition name(s), comma-separated. Executor-owned.</summary>
    public string? ConfirmationRequired { get; init; }

    /// <summary>The requester's confirmation. Written through <see cref="PackageUninstall.Confirm"/>.</summary>
    public string? Confirmation { get; init; }

    /// <summary>Who confirmed. Written with <see cref="Confirmation"/>.</summary>
    public string? ConfirmedBy { get; init; }

    /// <summary>When it was confirmed.</summary>
    public DateTimeOffset? ConfirmedAt { get; init; }

    /// <summary>When phase 1 reached <see cref="PackageUninstallStatus.AwaitingConfirmation"/> — the
    /// preview a confirmation answers. A confirmation recorded before it is refused by phase 2.</summary>
    public DateTimeOffset? AwaitingConfirmationAt { get; init; }

    /// <summary>Why a confirmation was refused (a mismatch), by name. Executor-owned.</summary>
    public string? ConfirmationRefusal { get; init; }

    /// <summary>Why the request is red, by name. Executor-owned.</summary>
    public string? Failure { get; init; }

    /// <summary>When it reached a terminal status. Executor-owned.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>Every step, in order. Executor-owned.</summary>
    public ImmutableList<string> Log { get; init; } = ImmutableList<string>.Empty;
}

/// <summary>One partition an uninstall would tear down — measured before anything is destroyed.</summary>
public record PackageUninstallPartition
{
    /// <summary>The partition (first path segment).</summary>
    public string Partition { get; init; } = "";

    /// <summary>Whether a storage provider reports a per-partition store (Postgres: the schema)
    /// for it — null when no provider could say.</summary>
    public bool? StoreExists { get; init; }

    /// <summary>Nodes per table: <c>mesh_nodes</c> for main nodes, the satellite segment
    /// (<c>_Access</c>, <c>_Thread</c>, …) for satellite rows.</summary>
    public ImmutableDictionary<string, int> RowsByTable { get; init; } = ImmutableDictionary<string, int>.Empty;

    /// <summary>Whether the partition carries a sync configuration (<c>{partition}/_GitSync</c>).</summary>
    public bool SyncConfig { get; init; }

    /// <summary>What phase 2 removes that this measurement cannot count, named.</summary>
    public ImmutableList<string> Unmeasured { get; init; } = ImmutableList<string>.Empty;

    /// <summary>Whether phase 2 dropped it. Executor-owned.</summary>
    public bool TornDown { get; init; }

    /// <summary>The teardown's own sentence (providers asked, registry record deleted). Executor-owned.</summary>
    public string? TeardownOutcome { get; init; }
}

/// <summary>What <see cref="PackageUninstall.Request"/> / <see cref="PackageUninstall.Confirm"/> answer.</summary>
/// <param name="Path">The request node, or null when refused.</param>
/// <param name="Refusal">Why nothing was written, or null.</param>
public sealed record PackageUninstallTicket(string? Path, string? Refusal)
{
    /// <summary>True when written.</summary>
    public bool Accepted => Path is not null;
}

/// <summary>The writers of a <see cref="PackageUninstallRequest"/>. Callers authorise; these write as System.</summary>
public static class PackageUninstall
{
    /// <summary>Why the request must not be written, or null. Pure.</summary>
    public static string? Validate(PackageUninstallRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Package))
            return "an uninstall needs a package id";
        if (request.Package.Trim().Any(c => char.IsWhiteSpace(c) || c is '/' or '\\' or ':' or '@'))
            return $"'{ActivationRecycle.SanitizeReason(request.Package)}' is not a package id";
        if (string.IsNullOrWhiteSpace(request.Reason))
            return "an uninstall needs a reason";
        return null;
    }

    /// <summary>Writes the request as System at <c>Admin/_PackageUninstall/{id}</c> and reads it once
    /// (which activates its executor). Refuses, writing nothing, when invalid or when this mesh has
    /// no executor. Cold; emits once.</summary>
    /// <param name="hub">Any surviving hub of the mesh.</param>
    /// <param name="request">What to uninstall and why.</param>
    public static IObservable<PackageUninstallTicket> Request(IMessageHub hub, PackageUninstallRequest request)
    {
        if (Validate(request) is { } refusal)
            return Observable.Return(new PackageUninstallTicket(null, refusal));
        if (hub.ServiceProvider.FindStaticNode(PackageUninstallRequest.NodeType) is null)
            return Observable.Return(new PackageUninstallTicket(null,
                "this mesh registers no package uninstall executor (the plugin catalog is not installed) — nothing was written"));
        var mesh = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var access = hub.ServiceProvider.GetRequiredService<AccessService>();
        var id = ActivationRecycle.Segment($"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{request.Package.Trim()}-{Guid.NewGuid().ToString("N")[..8]}");
        var path = $"{PackageUninstallRequest.Namespace}/{id}";
        var node = new MeshNode(id, PackageUninstallRequest.Namespace)
        {
            Name = $"Uninstall {request.Package.Trim()}",
            NodeType = PackageUninstallRequest.NodeType,
            Content = request with
            {
                Package = request.Package.Trim(),
                Reason = ActivationRecycle.SanitizeReason(request.Reason),
                RequestedAt = request.RequestedAt == default ? DateTimeOffset.UtcNow : request.RequestedAt,
                Status = PackageUninstallStatus.Requested,
                Confirmation = null,
                ConfirmedBy = null,
                ConfirmedAt = null,
            },
        };
        var ticket = new PackageUninstallTicket(path, null);
        return access.RunAsSystem(() => mesh.CreateNode(node).Take(1))
            .SelectMany(_ => access.RunAsSystem(() => hub.GetMeshNodeStream(path).Take(1).Timeout(ActivationRecycle.ReadBudget))
                .Select(_ => ticket)
                .Catch((Exception _) => Observable.Return(ticket)))
            .Take(1);
    }

    /// <summary>
    /// Records a confirmation for phase 2, as System, with who and when — only while the request
    /// AWAITS one (a confirmation sent before the preview exists is refused here, writing nothing).
    /// The CALLER checks that <paramref name="confirmedBy"/> is a platform admin. The EXECUTOR is
    /// the authority on everything else and refuses by name: a string that is not
    /// <see cref="PackageUninstallRequest.ConfirmationRequired"/>, a confirmer who is not the
    /// request's <see cref="PackageUninstallRequest.RequestedBy"/> (or a request that names none),
    /// and a confirmation recorded before <see cref="PackageUninstallRequest.AwaitingConfirmationAt"/>.
    /// Cold; emits once.
    /// </summary>
    /// <param name="hub">Any surviving hub of the mesh.</param>
    /// <param name="path">The request node.</param>
    /// <param name="confirmation">What the requester typed.</param>
    /// <param name="confirmedBy">Who confirmed.</param>
    public static IObservable<PackageUninstallTicket> Confirm(IMessageHub hub, string path, string confirmation, string? confirmedBy)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith(PackageUninstallRequest.Namespace + "/", StringComparison.OrdinalIgnoreCase))
            return Observable.Return(new PackageUninstallTicket(null, $"'{path}' is not a package uninstall request"));
        var access = hub.ServiceProvider.GetRequiredService<AccessService>();
        return access.RunAsSystem(() => hub.GetMeshNodeStream(path).Take(1).Timeout(ActivationRecycle.ReadBudget))
            .Select(node => node.ContentAs<PackageUninstallRequest>(hub.JsonSerializerOptions))
            .SelectMany(current => current is { Status: PackageUninstallStatus.AwaitingConfirmation }
                ? access.RunAsSystem(() => hub.GetMeshNodeStream(path)
                        .Update<PackageUninstallRequest>(latest => latest with
                        {
                            Confirmation = confirmation ?? "",
                            ConfirmedBy = confirmedBy,
                            ConfirmedAt = DateTimeOffset.UtcNow,
                            ConfirmationRefusal = null,
                        }))
                    .Take(1)
                    .Select(_ => new PackageUninstallTicket(path, null))
                : Observable.Return(new PackageUninstallTicket(null,
                    $"'{path}' does not await a confirmation (status '{current?.Status ?? "unreadable"}') — confirm only after phase 1 "
                    + "has answered its preview; nothing was recorded")))
            .Catch((Exception ex) => Observable.Return(new PackageUninstallTicket(null, $"the confirmation could not be recorded: {ex.Message}")));
    }
}
