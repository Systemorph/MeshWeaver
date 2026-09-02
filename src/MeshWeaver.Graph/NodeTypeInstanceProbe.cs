using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// The instances a NodeType deletion would STRAND, named — the reporting half of issue #2993's
/// hole B.
///
/// <para><b>Why this reports rather than refuses.</b> Pruning a retired NodeType is intended
/// behaviour, shipped and documented (<c>WhatsNew/2026-08-28-retired-node-prune</c>): whatever a
/// repo no longer carries is removed from the installed partition, and a definition left standing
/// with no source to compile against is itself a defect
/// (<c>Doc/Architecture/RetiringANodeType</c>). So refusing the prune would contradict the shipped
/// contract and would strand the DEFINITION instead of its instances. What was missing is not a
/// veto — it is the answer to the question <c>Doc/Architecture/RetiringANodeType</c> already tells
/// an operator to ask by hand as step 1 of every retirement: <i>"Establish the instance count is
/// zero (<c>search nodeType:{Type}</c>)"</i>. The detector existed only as a UI query
/// (<c>MeshNodeLayoutAreas</c>' Search area) and the policy only as prose plus four hand-written
/// per-incident SQL migrations (<c>V34</c>, <c>V48</c>, <c>V52</c>, <c>V53</c>). This wires the two
/// together so the automated deletion asks the same question the manual one does.</para>
///
/// <para><b>What a stranded instance costs</b>, and why a count is not enough to bury in a debug
/// line: an instance whose type resolves to nothing has no per-node hub. It does not fail — it
/// reads as <c>Unavailable</c> on a timeout, renders empty, and never reaches a verdict. Nothing
/// in the resulting picture names the type that went away, which is exactly why the live example
/// (<c>rbuergi/_Draft/PartnerRe_EslProposalQA</c>, <c>nodeType: EmailDraft</c>) sat unexplained.
/// So the report names the TYPE and the PATHS, at Warning, on the surface an operator already
/// watches.</para>
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
    /// <param name="NodeTypePath">The NodeType definition node being pruned.</param>
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
    /// The operator-facing line, or <c>null</c> when nothing would be stranded. Pure — the wording
    /// is pinned by test, because a report nobody can act on is the same defect as no report.
    /// </summary>
    public static string? Describe(IReadOnlyCollection<StrandedInstances> stranded)
    {
        if (stranded.Count == 0)
            return null;
        var parts = stranded.Select(s =>
        {
            var named = string.Join(", ", s.InstancePaths);
            var rest = s.Count - s.InstancePaths.Count;
            var overflow = rest > 0 ? $", … (+{rest}{(s.Truncated ? " or more" : "")} more)" : "";
            return $"'{s.NodeTypePath}' ({s.Count}{(s.Truncated ? "+" : "")}): {named}{overflow}";
        });
        return $"⚠ Pruned {stranded.Count} NodeType(s) that still have instances — those instances "
               + "are now STRANDED: they have no per-node hub, so they read as Unavailable and "
               + "render empty. Retype or delete them, or restore the NodeType: "
               + string.Join("; ", parts)
               + ".";
    }

    /// <summary>
    /// Probes, for every NodeType definition among <paramref name="pruning"/>, whether the mesh
    /// still holds instances of it.
    ///
    /// <para>Reads as System and mesh-wide on purpose: instances of a package's type live in USER
    /// partitions the importer's own viewer cannot see, and a report that missed them would be
    /// worse than none — it would read as a clean bill of health. Costs one query per NodeType
    /// actually being deleted, which for the overwhelming majority of imports is zero.</para>
    ///
    /// <para>A faulted probe is reported and skipped, never fatal: the prune is the operation, the
    /// report is the diagnosis, and a diagnosis that fails must not take the operation down with
    /// it.</para>
    /// </summary>
    /// <param name="hub">The hub whose <see cref="IMeshService"/> answers the query.</param>
    /// <param name="pruning">The nodes about to be deleted.</param>
    /// <param name="logger">Optional logger for probe failures.</param>
    /// <returns>One entry per pruned NodeType that still has instances; empty when none do.</returns>
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
                    .FromQuery($"nodeType:{type}")
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
                        + "has instances before pruning it; the prune proceeds unreported for this type.",
                        type);
                    return Observable.Return<StrandedInstances?>(null);
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
    /// <see cref="Probe"/> plus the two deliveries every caller wants: the process logger (so it is
    /// greppable outside the mesh) and a Warning <see cref="LogMessage"/> the caller can fold into
    /// whatever activity it is already writing.
    /// </summary>
    /// <param name="hub">The hub whose <see cref="IMeshService"/> answers the query.</param>
    /// <param name="pruning">The nodes about to be deleted.</param>
    /// <param name="logger">Optional logger — receives the same text at Warning.</param>
    /// <returns>The activity line, or <c>null</c> when nothing would be stranded.</returns>
    public static IObservable<LogMessage?> ProbeAndReport(
        IMessageHub hub, IEnumerable<MeshNode> pruning, ILogger? logger) =>
        Probe(hub, pruning, logger)
            .Select(stranded =>
            {
                var text = Describe(stranded);
                if (text is null)
                    return null;
                logger?.LogWarning("[NodeTypeInstanceProbe] {Report}", text);
                return new LogMessage(text, LogLevel.Warning);
            });
}
