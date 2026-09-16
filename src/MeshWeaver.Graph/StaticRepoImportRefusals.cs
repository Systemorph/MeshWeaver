using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// 🚨 <b>Issue #4469 — the import's answer to "which declared nodes is this partition MISSING?",
/// for the compile pipeline that cannot reference the importer.</b>
///
/// <para>The <see cref="IPartitionImportRefusals"/> implementation over the per-node import manifest
/// (<c>{partition}/_Activity/import-manifest</c>) that <see cref="StaticRepoImporter"/> writes. A
/// node the import evaluated and could not write is recorded there under the refusal sigil
/// (<c>!{token}|{reason}</c>, #4467/#4469), so this is a READ — the whole point of the issue is that
/// the facts already exist and nothing joined them to the symptom.</para>
///
/// <para>🚨 <b>The read keeps the three answers apart.</b> <see cref="MeshNodeStreamExtensions.GetMeshNodeOutcome"/>
/// rather than <c>GetMeshNode</c>, because <c>Absent</c> and <c>Unavailable</c> must NOT collapse
/// here: an absent manifest is a real answer ("this partition's import recorded no refusal"), while
/// a read that timed out, faulted or was refused establishes nothing — and a compile diagnosis that
/// treated the second as the first would be silently unable to report the very state it exists for.
/// Both still produce the same visible outcome (no accusation), and that is deliberate too: a symbol
/// can be missing because it was deleted on purpose, or because the module is not loaded on this
/// replica (MeshWeaver#3583). Only a RECORDED refusal is ever asserted.</para>
///
/// <para>Bounded and total: exactly one emission, never a fault, so a compile write-back can compose
/// it without a way to lose its own terminal write. Observability must never break the thing it
/// observes.</para>
/// </summary>
/// <param name="hub">The hub the read is issued on (see <c>GetMeshNodeOutcome</c>: a root-mesh hub
/// hops to the read seam of its own accord).</param>
/// <param name="logger">Optional; a read that could not answer is logged at Debug — it is an
/// ordinary outcome of asking, not a failure of the compile.</param>
public sealed class StaticRepoImportRefusals(IMessageHub hub, ILogger<StaticRepoImportRefusals>? logger = null)
    : IPartitionImportRefusals
{
    /// <summary>
    /// How long the manifest read may take. Short on purpose: this runs on the tail of a FAILED
    /// compile, whose terminal status write is what un-wedges the NodeType, and a diagnosis is worth
    /// nothing at the price of delaying that. Over budget ⇒ <see langword="null"/> ⇒ the compile
    /// reports exactly what it reported before this existed.
    /// </summary>
    private static readonly TimeSpan ReadBudget = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    public IObservable<ImmutableList<ImportRefusal>?> Refusals(string partition)
    {
        if (string.IsNullOrWhiteSpace(partition))
            return Observable.Return<ImmutableList<ImportRefusal>?>(null);

        var path = StaticRepoImporter.ManifestPath(partition);
        return hub
            // EmitNull, so the budget elapsing arrives as Unavailable (indeterminate) rather than as
            // an OnError — the distinction this seam is built on, kept at the instrument.
            .GetMeshNodeOutcome(path, ReadBudget, ReadTimeoutBehavior.EmitNull)
            .Take(1)
            .Select(outcome => outcome.Status switch
            {
                NodeReadStatus.Present => Parse(outcome.Node),
                // A partition whose import never wrote a manifest — or whose manifest was pruned —
                // has recorded no refusal. That is an ANSWER, and an empty one.
                NodeReadStatus.Absent => ImmutableList<ImportRefusal>.Empty,
                // DeleteInProgress / Unavailable: nothing was established. Never "none".
                _ => Unanswered(partition, outcome.Status.ToString()),
            })
            .Catch((Exception ex) =>
            {
                logger?.LogDebug(ex,
                    "[ImportRefusals] {Partition}: the import manifest could not be read; a compile "
                    + "failure in this partition will report its diagnostics WITHOUT an import "
                    + "verdict rather than guess at one.", partition);
                return Observable.Return<ImmutableList<ImportRefusal>?>(null);
            })
            .DefaultIfEmpty(null);
    }

    private ImmutableList<ImportRefusal>? Unanswered(string partition, string status)
    {
        logger?.LogDebug(
            "[ImportRefusals] {Partition}: the import manifest read answered {Status} — "
            + "indeterminate, so no import verdict is attached to a compile failure here.",
            partition, status);
        return null;
    }

    /// <summary>
    /// The refusal entries of a manifest node, as the seam's vocabulary. Pure apart from the
    /// serializer options it borrows from the hub.
    /// </summary>
    private ImmutableList<ImportRefusal> Parse(MeshNode? manifestNode) =>
        StaticRepoImporter.ParseManifest(manifestNode, hub.JsonSerializerOptions)
            .Where(kvp => StaticRepoImporter.IsRefusal(kvp.Value))
            .Select(kvp => new ImportRefusal(kvp.Key, StaticRepoImporter.RefusalReasonOf(kvp.Value)))
            // Ordered so two readings of an unchanged manifest produce the same sentence — a
            // diagnosis that reshuffles itself between compiles reads as new information.
            .OrderBy(r => r.NodePath, StringComparer.OrdinalIgnoreCase)
            .ToImmutableList();
}
