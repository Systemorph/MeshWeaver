using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// The instances a NodeType deletion would STRAND, named — and, since 2026-09-08, the reason the
/// deletion is REFUSED while any exist.
///
/// <para><b>Why this refuses rather than reports.</b> It used to report only: pruning a retired
/// NodeType was intended behaviour (<c>WhatsNew/2026-08-28-retired-node-prune</c>), so the probe
/// raised a ⚠ naming the instances and let the delete proceed. Measured on
/// <c>memex.systemorph.com</c> 2026-09-08 20:29Z, that is exactly the sequence that took a client's
/// record dark: the Crm repository had retired <c>Crm/Mail</c> on 09-06 (its commit message says
/// the one live record is retyped on the portal before the deploy); memex's Crm sync had been
/// <c>Skipped</c> since 08-30, so the retirement reached it two days later, with the instance not
/// yet retyped; the import KNEW the type had an instance, logged the warning, and pruned anyway —
/// <c>PartnerRe/Esl/DueDiligenceMail</c> then had no per-node hub, and the bake gate refused every
/// rollout on the "regression". A warning nobody could act on in time is not a safeguard.</para>
///
/// <para><b>The rule now:</b> a repository-driven prune never deletes a NodeType definition that
/// still has instances. The type is HELD — kept, stamped
/// <see cref="NodeTypeDefinition.PendingRetirement"/>, and reported as drift on the sync activity —
/// until the instances are retyped or deleted, at which point the next sync completes the
/// retirement by itself. A deliberate retirement therefore still lands; a stale sync can no
/// longer strand a record. This is the automated form of step 1 of
/// <c>Doc/Architecture/RetiringANodeType</c> ("establish the instance count is zero"), asked by
/// the machine instead of by hand.</para>
///
/// <para><b>What a stranded instance costs</b>, and why the old warning was not enough: an
/// instance whose type resolves to nothing has no per-node hub. It does not fail — it reads as
/// <c>Unavailable</c> on a timeout, renders empty, and never reaches a verdict. Nothing in the
/// resulting picture names the type that went away.</para>
/// </summary>
public static class NodeTypeInstanceProbe
{
    /// <summary>How many instance paths a report names before it summarises the rest.</summary>
    public const int NamedInstanceLimit = 10;

    /// <summary>
    /// The per-type probe's row cap. The report only needs to establish "not zero" and name enough
    /// paths to act on — it is not an inventory, and a retirement that strands hundreds of nodes is
    /// answered the same way as one that strands twelve.
    /// </summary>
    public const int ProbeLimit = 200;

    /// <summary>
    /// The instances one NodeType path still has at the moment it is about to be deleted.
    /// </summary>
    /// <param name="NodeTypePath">The NodeType definition node the source no longer carries.</param>
    /// <param name="InstancePaths">Up to <see cref="NamedInstanceLimit"/> instance paths, named.</param>
    /// <param name="Count">How many instances the probe saw (capped at <see cref="ProbeLimit"/>).</param>
    /// <param name="Truncated">True when the probe hit <see cref="ProbeLimit"/>, so
    /// <paramref name="Count"/> is a floor rather than a total.</param>
    public sealed record StrandedInstances(
        string NodeTypePath,
        ImmutableList<string> InstancePaths,
        int Count,
        bool Truncated);

    /// <summary>
    /// The NodeType definition paths among <paramref name="nodes"/>. Pure, so the selection is
    /// testable without a mesh.
    ///
    /// <para>Recognition delegates to <see cref="ImportWriteOrder.IsNodeTypeDefinition"/> — the
    /// framework's own answer, deliberately not a second copy of it. Its two-armed test is the
    /// load-bearing part: a definition read back from storage on a hub whose TypeRegistry lacks
    /// <c>NodeTypeDefinition</c> degrades to an untyped <c>JsonElement</c>, so a content pattern
    /// match ALONE silently answers "not a type" for exactly the retired types this is about.</para>
    /// </summary>
    public static IReadOnlyList<string> NodeTypePathsAmong(IEnumerable<MeshNode> nodes) =>
        nodes
            .Where(n => !string.IsNullOrEmpty(n.Path) && ImportWriteOrder.IsNodeTypeDefinition(n))
            .Select(n => n.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>
    /// The prune candidates that survive a hold: everything AT or UNDER a held NodeType's path is
    /// kept — the definition, and its own <c>Source/</c> and <c>Test/</c> subtrees, which are the
    /// type's default sources and would leave it with nothing to compile against if they went
    /// while the definition stayed. Pure — the filter is unit-testable without a mesh.
    ///
    /// <para>Deliberately NOT extended to sources the type draws from elsewhere (a
    /// <c>shared=@Other/Source</c> query): those belong to the partition's other types, the
    /// repository is authoritative for them, and keeping them would compile the retired code into
    /// every consumer for as long as one instance existed — the state #3727 just removed.</para>
    /// </summary>
    /// <param name="candidates">The prune candidates.</param>
    /// <param name="heldNodeTypePaths">The NodeType paths the prune is refusing.</param>
    public static IReadOnlyList<MeshNode> WithoutHeld(
        IEnumerable<MeshNode> candidates, IReadOnlyCollection<string> heldNodeTypePaths)
    {
        if (heldNodeTypePaths.Count == 0)
            return candidates.ToArray();
        return candidates
            .Where(n => !heldNodeTypePaths.Any(held =>
                string.Equals(n.Path, held, StringComparison.OrdinalIgnoreCase)
                || n.Path.StartsWith(held + "/", StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    /// <summary>
    /// The operator-facing line, or <c>null</c> when nothing is held. Pure — the wording is pinned
    /// by test, because a report nobody can act on is the same defect as no report.
    /// </summary>
    public static string? Describe(IReadOnlyCollection<StrandedInstances> held)
    {
        if (held.Count == 0)
            return null;
        return $"⏸ Held {held.Count} NodeType(s) the source no longer carries, because the mesh "
               + "still has instances of them — NOT pruned (deleting the type would leave those "
               + "instances with no per-node hub, reading as Unavailable and rendering empty). "
               + "Retype or delete the instances and the next sync completes the retirement: "
               + DetailOf(held)
               + ".";
    }

    /// <summary>
    /// The text stamped onto a held definition's <see cref="NodeTypeDefinition.PendingRetirement"/>
    /// — who retired it and what keeps it alive. Pure.
    /// </summary>
    /// <param name="held">The probe's answer for this type.</param>
    /// <param name="retiredBy">The source that no longer carries the type (a partition and its
    /// content fingerprint, a package id at a ref).</param>
    /// <param name="at">When the hold was recorded.</param>
    public static string PendingRetirementOf(StrandedInstances held, string retiredBy, DateTimeOffset at)
        => $"Retired by {retiredBy} at {at:O}; held for {InstancesOf(held)}. "
           + "Retype or delete them and the next sync removes the type.";

    /// <summary>
    /// "<c>N instance(s): a, b, … (+k more)</c>", or — for a probe that reached no verdict and was
    /// answered as held (see <see cref="Probe"/>) — a sentence that says so, never "0 instance(s)".
    /// </summary>
    private static string InstancesOf(StrandedInstances held)
    {
        if (held.Count == 0 && held.InstancePaths.Count == 0)
            return "an unknown number of instances (the instance probe could not answer; it is asked again on the next sync)";
        var named = string.Join(", ", held.InstancePaths);
        var rest = held.Count - held.InstancePaths.Count;
        var overflow = rest > 0 ? $", … (+{rest}{(held.Truncated ? " or more" : "")} more)" : "";
        return $"{held.Count}{(held.Truncated ? "+" : "")} instance(s): {named}{overflow}";
    }

    /// <summary>
    /// The per-NodeType enumeration that goes after the colon in <see cref="Describe"/> — paths and
    /// counts, no prose. Split out so the localizable activity line can bind it as ONE argument
    /// (<c>{detail}</c>) while the sentence around it comes from the catalog (#3236).
    /// </summary>
    public static string DetailOf(IReadOnlyCollection<StrandedInstances> held) =>
        string.Join("; ", held.Select(s => $"'{s.NodeTypePath}' — {InstancesOf(s)}"));

    /// <summary>
    /// Probes, for every NodeType definition among <paramref name="pruning"/>, whether the mesh
    /// still holds instances of it.
    ///
    /// <para>Reads as System and mesh-wide on purpose: instances of a package's type live in USER
    /// partitions the importer's own viewer cannot see, and a report that missed them would be
    /// worse than none — it would read as a clean bill of health. Costs one query per NodeType
    /// actually being deleted, which for the overwhelming majority of imports is zero.</para>
    ///
    /// <para>🚨 A probe that FAULTS is answered as "held", never as "no instances" (the direction
    /// changed on 2026-09-08 together with the refusal): the deletion this decides is irreversible
    /// and the instances it would strand are a client's data, so a read that reached no verdict
    /// must fail towards keeping the type. The next sync asks again.</para>
    /// </summary>
    /// <param name="hub">The hub whose <see cref="IMeshService"/> answers the query.</param>
    /// <param name="pruning">The nodes about to be deleted.</param>
    /// <param name="logger">Optional logger for probe failures.</param>
    /// <returns>One entry per NodeType among the candidates that still has instances (or whose
    /// probe could not answer); empty when none do.</returns>
    public static IObservable<ImmutableList<StrandedInstances>> Probe(
        IMessageHub hub, IEnumerable<MeshNode> pruning, ILogger? logger)
    {
        var none = ImmutableList<StrandedInstances>.Empty;
        var types = NodeTypePathsAmong(pruning);
        var meshService = hub.ServiceProvider.GetService<IMeshService>();
        if (types.Count == 0 || meshService is null)
            return Observable.Return(none);

        return types
            .Select(type => meshService
                .Query<MeshNode>(MeshQueryRequest
                    // A stranded instance may sit in any partition — the probe is mesh-wide by
                    // nature and says so (#3202 — fan-out is opt-in).
                    .FromQuery(MeshWideQuery.OfType(type))
                    .AsSystem() with { Limit = ProbeLimit })
                .Take(1)
                .Select(change =>
                {
                    var items = change.Items;
                    if (items.Count == 0)
                        return null;
                    return new StrandedInstances(
                        type,
                        items.Take(NamedInstanceLimit).Select(n => n.Path).ToImmutableList(),
                        items.Count,
                        items.Count >= ProbeLimit);
                })
                .Catch<StrandedInstances?, Exception>(ex =>
                {
                    logger?.LogWarning(ex,
                        "[NodeTypeInstanceProbe] could not check whether NodeType '{NodeType}' still "
                        + "has instances before pruning it; the type is HELD (not pruned) until a "
                        + "probe answers — a deletion must not proceed on a read that reached no verdict.",
                        type);
                    return Observable.Return<StrandedInstances?>(new StrandedInstances(
                        type, ImmutableList<string>.Empty, 0, Truncated: false));
                }))
            .ToObservable()
            .Concat()
            .ToList()
            .Select(results => results
                .Where(r => r is not null)
                .Select(r => r!)
                .ToImmutableList());
    }

    /// <summary>
    /// Stamps each held definition with <see cref="NodeTypeDefinition.PendingRetirement"/> — the
    /// durable trace of the hold, read by the bake gate (a compile failure on a stamped type is
    /// <c>Retired</c>, not a regression) and by anyone opening the node. Written as System like
    /// every other importer bookkeeping write; best-effort, since the hold itself is the decision
    /// NOT to delete and needs no write to take effect. A definition already carrying an equal
    /// stamp is left alone, so a sync that repeats every build does not rewrite the node each time.
    /// </summary>
    /// <param name="hub">The hub performing the import.</param>
    /// <param name="held">The probe's answer.</param>
    /// <param name="retiredBy">Who retired the type — for the stamp's text.</param>
    /// <param name="logger">Optional logger.</param>
    /// <returns>The number of definitions stamped.</returns>
    public static IObservable<int> Hold(
        IMessageHub hub, IReadOnlyCollection<StrandedInstances> held, string retiredBy, ILogger? logger)
    {
        if (held.Count == 0)
            return Observable.Return(0);
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var workspace = hub.GetWorkspace();
        var now = DateTimeOffset.UtcNow;
        return held
            .Select(h => accessService
                .RunAsSystem(() => workspace.GetMeshNodeStream(h.NodeTypePath)
                    .Update(current =>
                    {
                        var def = current.ContentAs<NodeTypeDefinition>(hub.JsonSerializerOptions, logger);
                        if (def is null)
                            return current;
                        var stamp = PendingRetirementOf(h, retiredBy, now);
                        // The instance list and the clock move on every sync; only the FACT of the
                        // hold is worth a write. Same type + same holder ⇒ leave the node alone.
                        if (def.PendingRetirement is { } existing
                            && existing.StartsWith($"Retired by {retiredBy} ", StringComparison.Ordinal))
                            return current;
                        return current with { Content = def with { PendingRetirement = stamp } };
                    }))
                .Take(1)
                .Select(_ => 1)
                .Catch<int, Exception>(ex =>
                {
                    logger?.LogWarning(ex,
                        "[NodeTypeInstanceProbe] could not stamp PendingRetirement on held NodeType "
                        + "'{NodeType}' — it is still held (not pruned); only the trace is missing.",
                        h.NodeTypePath);
                    return Observable.Return(0);
                }))
            .ToObservable()
            .Concat()
            .Sum();
    }

    /// <summary>
    /// The Warning <see cref="LogMessage"/> a caller folds into whatever activity it is already
    /// writing, plus the same text on the process logger (so it is greppable outside the mesh).
    /// <c>null</c> when nothing is held.
    /// </summary>
    /// <param name="held">The probe's answer.</param>
    /// <param name="logger">Optional logger — receives the same text at Warning.</param>
    public static LogMessage? Report(IReadOnlyCollection<StrandedInstances> held, ILogger? logger)
    {
        var text = Describe(held);
        if (text is null)
            return null;
        logger?.LogWarning("[NodeTypeInstanceProbe] {Report}", text);
        return new LogMessage(text, LogLevel.Warning)
            .WithKey("activity.prune.heldNodeTypes",
                ("count", held.Count), ("detail", DetailOf(held)));
    }
}
