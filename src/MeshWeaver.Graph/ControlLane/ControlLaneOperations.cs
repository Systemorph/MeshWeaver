using System.Reactive.Linq;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.ControlLane;

/// <summary>Text helpers every lane operation shares. Pure.</summary>
public static class ControlLaneText
{
    /// <summary>Longest requester reason carried onto a log line.</summary>
    public const int MaxReasonLength = 300;

    /// <summary>
    /// Flattens a reason to ONE line: every control character and the Unicode line/paragraph
    /// separators become a space, runs collapse, length is bounded — an embedded newline would forge
    /// a second log entry for the incident filer. Pure.
    /// </summary>
    public static string Sanitize(string? reason)
    {
        var flattened = new string((reason ?? "").Select(c =>
            char.IsControl(c)
            || char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.LineSeparator
                or System.Globalization.UnicodeCategory.ParagraphSeparator
                ? ' ' : c).ToArray());
        flattened = string.Join(' ', flattened.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return flattened.Length <= MaxReasonLength ? flattened : flattened[..MaxReasonLength] + "… (truncated)";
    }

    /// <summary>The first 12 hex characters of a digest. Pure.</summary>
    public static string Short(string? digest)
    {
        var d = (digest ?? "").Trim();
        var hex = d.StartsWith("sha256:", StringComparison.Ordinal) ? d[7..] : d;
        return hex.Length <= 12 ? hex : hex[..12];
    }

    /// <summary>Who asked and who approved, through which control action, and why — one line. Pure.</summary>
    public static string ReasonLine(ControlLaneRequest request) =>
        $"control lane {request.Operation} of '{request.Target}' on '{request.Deployment}' (control action "
        + $"/{request.Action}, request {request.RequestId}), requested by {Or(request.RequestedBy, "(unattributed)")}, "
        + $"approved by {Or(request.ApprovedBy, "(no approval — dry run)")}, run as system: {Sanitize(request.Reason)}";

    /// <summary>An identity from the request, flattened to one line (<see cref="Sanitize"/>), or the fallback. Pure.</summary>
    public static string Or(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : Sanitize(value);
}

/// <summary>
/// <see cref="ControlLaneOperation.DeleteSpace"/> on the target — the SAME engine as the in-process
/// <c>DeleteSpace</c> instance action (<see cref="SpaceDeletion"/>): inventory as system, the plan,
/// the framework's deletes and the platform's teardown, verified afterwards.
/// </summary>
public sealed class DeleteSpaceOperation : IControlLaneOperation
{
    /// <inheritdoc />
    public string Operation => ControlLaneOperation.DeleteSpace;

    /// <inheritdoc />
    public string? ShapeRefusal(ControlLaneRequest request)
    {
        if (request.Target.Contains('/'))
            return $"a space is one path segment (the partition root), and '{request.Target}' is not";
        return string.Equals(request.Confirmation, request.Target, StringComparison.Ordinal)
            ? null
            : "the confirmation does not repeat the space exactly — a space deletion names its target twice";
    }

    /// <inheritdoc />
    public IObservable<ControlLanePreparation> Prepare(IMessageHub hub, ControlLaneRequest request)
    {
        var space = request.Target;
        return SpaceDeletion.Inventory(hub, space).Select(inventory =>
        {
            if (SpaceDeletion.Preflight(hub, ControlLaneAdmission.LedgerPath(request.RequestId), inventory) is { } refused)
                throw new InvalidOperationException(refused);
            var (steps, notes) = SpaceDeletion.PlanSteps(inventory);
            var plan = ControlLanePlan.OfSteps(Operation, request.Deployment, steps, notes);
            return new ControlLanePreparation(plan, () =>
                SpaceDeletion.Run(hub, inventory, ControlLaneText.ReasonLine(request))
                    .Select(step => step.After is null ? step.Line : step.Line + " — " + AuditLine(request, inventory, step.After, plan)));
        });
    }

    /// <summary>The audit line of a verified deletion — who asked, who approved, what was removed. Pure.</summary>
    public static string AuditLine(
        ControlLaneRequest request, SpaceDeletionInventory before, SpaceDeletionInventory after, ControlLanePlan plan) =>
        $"[DeleteSpace] '{before.Space}' on '{request.Deployment}' DELETED as system through the control lane — "
        + $"requested by {ControlLaneText.Or(request.RequestedBy, "(unattributed)")}, approved by {ControlLaneText.Or(request.ApprovedBy, "(none)")}"
        + (request.ApprovedAt is { } at ? $" at {at.UtcDateTime:yyyy-MM-dd'T'HH:mm:ss'Z'}" : "")
        + $"; {before.TotalRows} row(s) ("
        + string.Join(", ", before.Tables.Select(t => $"{t.Table} {t.Rows}"))
        + $"), {before.Grants.Count} grant(s), {before.GitSync.Count} GitSync node(s), {before.NodeTypes.Count} NodeType(s); "
        + $"store '{before.Schema}' {(after.StoreExists is null ? "not reported" : "dropped")}; plan {ControlLaneText.Short(plan.Digest())}; "
        + $"request {request.RequestId}; reason: {ControlLaneText.Sanitize(request.Reason)}";
}

/// <summary>
/// <see cref="ControlLaneOperation.Recycle"/> on the target — the ONE framework surface,
/// <c>hub.RecycleNode</c>, issued as system from the lane's off-router execution hub (which outlives
/// every target), after the target was resolved through a complete reading and — for a NodeType —
/// its dependency network derived, both recorded in the plan.
/// </summary>
public sealed class RecycleOperation : IControlLaneOperation
{
    /// <summary>Target label key: the one address a recycle tears down.</summary>
    public const string TargetAddress = "address";

    /// <summary>How long the recycled address may take to answer again (a NodeType may compile on re-activation).</summary>
    public static readonly TimeSpan RecycleBudget = TimeSpan.FromMinutes(2);

    /// <summary>How long deriving the dependency network may take.</summary>
    public static readonly TimeSpan NetworkBudget = TimeSpan.FromMinutes(4);

    /// <inheritdoc />
    public string Operation => ControlLaneOperation.Recycle;

    /// <inheritdoc />
    public string? ShapeRefusal(ControlLaneRequest request) =>
        request.Target.StartsWith(ControlLaneAdmission.LedgerNamespace, StringComparison.OrdinalIgnoreCase)
            ? "the lane's own ledger is never a recycle target"
            : null;

    /// <inheritdoc />
    public IObservable<ControlLanePreparation> Prepare(IMessageHub hub, ControlLaneRequest request)
    {
        var target = request.Target;
        var mesh = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var query = SpaceDeletion.ParentListingQuery(target);
        return SpaceDeletion.AsSystem(hub, () => MeshReading.Read(mesh, query))
            .SelectMany(reading =>
            {
                if (!reading.IsAnswer)
                    return Observable.Throw<ControlLanePreparation>(new InvalidOperationException(
                        $"could NOT ESTABLISH whether '{target}' exists — {reading.WhyNotAnAnswer}. Nothing was recycled, "
                        + "and this is NOT a statement that the target is absent"));
                var listedNode = reading.Rows.FirstOrDefault(r => string.Equals(r.Path, target, StringComparison.Ordinal));
                if (listedNode is null)
                    return Observable.Throw<ControlLanePreparation>(new InvalidOperationException(
                        $"there is no node at '{target}' — the index was asked ({query}) and answered without listing it. "
                        + "Nothing was recycled: a dispose to an address with no node is a no-op, and a no-op is never reported as a recycle"));
                return SpaceDeletion.ReadCurrentNode(hub, target).SelectMany(current =>
                {
                    if (current.Node is null)
                        return Observable.Throw<ControlLanePreparation>(new InvalidOperationException(
                            $"could NOT ESTABLISH the current content of '{target}' after its path was listed — {current.Error}. "
                            + "Nothing was recycled"));
                    var node = current.Node;
                    var isNodeType = string.Equals(node.NodeType, MeshNode.NodeTypePath, StringComparison.Ordinal);
                    var network = isNodeType
                        ? Network(hub, node.Path).Select(n => (DependencyNetworkResult?)n)
                        : Observable.Return<DependencyNetworkResult?>(null);
                    return network.Select(result =>
                        result is { IsComplete: false }
                            // 🚨 Refused BEFORE anything is disposed: an incomplete network means an unknown
                            // number of live hubs, and no plan can bind a blast radius nobody could read.
                            ? throw new InvalidOperationException(
                                $"the dependency network of '{node.Path}' is INCOMPLETE — {result.Incomplete.Count} enumeration leg(s) "
                                + $"could not be read ({string.Join(" | ", result.Incomplete)}). Nothing is planned and nothing was "
                                + "recycled; re-request once the index answers")
                            : Preparation(hub, request, node, result));
                });
            });
    }

    /// <summary>The dependency network of a NodeType, derived as system — the function the cascade itself runs. Cold.</summary>
    private static IObservable<DependencyNetworkResult> Network(IMessageHub hub, string nodeType) =>
        SpaceDeletion.AsSystem(hub, () => NodeTypeRecycleCascade.DependencyNetwork(hub, nodeType))
            .Take(1).Timeout(NetworkBudget);

    /// <summary>
    /// The EXACT address set a NodeType recycle reaches, as the digest the plan binds — so a network
    /// that changed between plan and run (same count, other addresses) is another plan. Pure.
    /// </summary>
    public static string NetworkDigest(IEnumerable<string> addresses) =>
        "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            string.Join("\n", addresses.Distinct(StringComparer.Ordinal).OrderBy(a => a, StringComparer.Ordinal))))).ToLowerInvariant();

    private ControlLanePreparation Preparation(
        IMessageHub hub, ControlLaneRequest request, MeshNode node, DependencyNetworkResult? network)
    {
        var isNodeType = network is not null;
        var bound = network is null ? null : NetworkDigest(network.Addresses);
        // The address is a SET of one, stated as a query; a NodeType's dependency network is not a
        // query, so it is a COUNT and the DIGEST of its exact address set in the command — never a
        // listing of the addresses it reaches.
        var steps = new[]
        {
            new ControlLanePlanStep
            {
                Name = "Recycle",
                Command = $"hub.RecycleNode(\"{node.Path}\", reason) as system — "
                    + (isNodeType
                        ? $"a NodeType: the dispose cascades to {network!.Addresses.Count} address(es) in its dependency network "
                          + $"(address set {bound}); the run re-derives the network and refuses if it is not this set"
                        : $"a node address (nodeType {node.NodeType ?? "none"}): only this address, no cascade"),
                Targets = [new ControlLanePlanTarget { Label = TargetAddress, Query = $"path:{node.Path}", Count = 1 }],
            },
        };
        var notes = Array.Empty<string>();
        var plan = ControlLanePlan.OfSteps(Operation, request.Deployment, steps, notes);
        // 🚨 The cascade recomputes the network when the dispose lands, so the run derives it once more
        // right before the dispose and refuses unless it is the bound set.
        var verified = isNodeType
            ? Network(hub, node.Path).Select(now => now.IsComplete && NetworkDigest(now.Addresses) == bound
                ? now
                : throw new InvalidOperationException(
                    $"the dependency network of '{node.Path}' is no longer the approved one ({(now.IsComplete ? $"{now.Addresses.Count} address(es), set {NetworkDigest(now.Addresses)}" : "incomplete")} "
                    + $"vs approved set {bound}) — nothing was recycled; re-request to plan again"))
            : Observable.Return<DependencyNetworkResult>(null!);
        return new ControlLanePreparation(plan, () =>
            verified.SelectMany(_ => SpaceDeletion.AsSystem(hub, () => hub.RecycleNode(node.Path, RecycleBudget, ControlLaneText.ReasonLine(request)))
                .Take(1)
                .DefaultIfEmpty(null)
                .Select(back => back is null
                    ? throw new InvalidOperationException(
                        $"the dispose of '{node.Path}' was posted, but the address answered again WITHOUT the node — this is not reported as a successful recycle")
                    : $"[Recycle] '{node.Path}' on '{request.Deployment}' recycled as system through the control lane: the dispose "
                      + $"was posted and a FRESH activation answered a read (version {back.Version})"
                      + (isNodeType ? $"; the cascade targeted the approved network of {network!.Addresses.Count} address(es)" : "")
                      + $" — requested by {ControlLaneText.Or(request.RequestedBy, "(unattributed)")}, approved by {ControlLaneText.Or(request.ApprovedBy, "(none)")}; "
                      + $"plan {ControlLaneText.Short(plan.Digest())}; request {request.RequestId}; reason: {ControlLaneText.Sanitize(request.Reason)}")));
    }
}
