using System.Collections.Immutable;

namespace MeshWeaver.Compiler;

/// <summary>
/// 🚨 <b>Which of a NodeType's DECLARED source queries matched NOTHING</b> — the granularity
/// <see cref="SourceSnapshot"/> is missing, and the reason a NodeType whose own <c>Source/</c>
/// subtree is absent reports itself as broken C# instead of as missing content (issue #3903).
///
/// <para><b>The defect.</b> <see cref="SourceSnapshot"/> already establishes that a SHORT source
/// set must never reach Roslyn as if it were the whole one: handed a set that is missing files,
/// Roslyn emits a completely genuine-looking <c>CS0246</c>/<c>CS1061</c> about the author's code
/// (#1218). But that mechanism measures emptiness on the MERGED set — the union of every expanded
/// source and test query — and a NodeType that also draws on a shared library keeps a non-empty
/// union even when the query for its OWN sources matches nothing at all. Measured on
/// memex.meshweaver.cloud, 2026-09-10:</para>
///
/// <code>
/// rbuergi/OperationRequest      compilationStatus: Error   since 2026-09-06, retried every boot
///   sources: [ "namespace:Source scope:subtree",        ← matched 0 nodes
///              "shared=@Store/Core/Source",             ← matched 41 between them
///              "shared=@Store/Licensing/Source", … ]
///   CS0246 'OperationRequestContent' could not be found
///   CS1061 'MessageHubConfiguration' has no 'AddOperationRequestControlPlane'
///   CS1061 'LayoutDefinition'        has no 'AddOperationRequestLayoutAreas'
/// </code>
///
/// <para>All three unresolved symbols are the type's own <c>Source/*</c> nodes, which do not exist
/// in that partition (<c>search 'namespace:rbuergi/OperationRequest scope:subtree'</c> → 0, against
/// 21 under the package copy). The union was 41 nodes, so <c>PreWarmStatus.NoSources</c> could not
/// fire and nothing anywhere said "one of your source queries matched nothing" — the reader was
/// left hunting three symbols through module surfaces that never carried them.</para>
///
/// <para><b>The unit is the DECLARED ENTRY, not the expanded query</b>, and that is load-bearing.
/// <see cref="CodeQueryResolver.Expand"/> turns one <c>@X</c> shorthand into TWO queries — a
/// <c>path:X</c> exact match and a <c>namespace:X scope:subtree</c> folder match — of which the
/// exact one is EXPECTED to match nothing whenever the folder node is not itself a Code node. Per
/// QUERY, every <c>shared=@Lib/Source</c> entry in a healthy mesh would be reported as half
/// missing. Per ENTRY — "did anything this line asks for exist?" — the answer is the one the author
/// can act on.</para>
///
/// <para><b>🚨 Three answers, never two</b> (the <c>NodeDiagnosticsOutcome</c> rule: a status that
/// says "nothing was checked" must not be readable as "checked and clean"):</para>
/// <list type="bullet">
///   <item><c>null</c> — NOT DETERMINED. No source snapshot to measure against, no NodeType path to
///     expand <c>$self</c> against, or not one declared entry could be evaluated offline. Says
///     nothing about the type's sources.</item>
///   <item>EMPTY — determined, and every evaluable declared entry matched at least one node.</item>
///   <item>NON-EMPTY — these declared entries answered and matched nothing.</item>
/// </list>
///
/// <para><b>Why an entry may be inevaluable, and why that is not a failure.</b> The offline
/// evaluator (<see cref="NodeSetQuery"/>) speaks the closed four-selector grammar
/// <see cref="CodeQueryResolver.Expand"/> emits and REFUSES everything else rather than
/// approximating it — free text routes to a vector index a pure function cannot reproduce. An entry
/// it cannot parse simply does not contribute, in either direction: it is neither reported as
/// missing nor counted as satisfied. Reporting one would be an accusation from evidence nobody has.
/// </para>
/// </summary>
public static class SourceCoverage
{
    /// <summary>
    /// The declared SOURCE entries of <paramref name="nodeTypePath"/> that answered and matched
    /// none of <paramref name="matchedPaths"/>.
    ///
    /// <para>🚨 <b>Source entries only — never Tests.</b> The default test query
    /// (<c>namespace:{path}/Test scope:subtree</c>) matches nothing for most NodeTypes in a real
    /// mesh, by design: a type without tests is normal, not broken. Folding tests in would make
    /// this fire on nearly the whole population and the signal would be worth nothing.</para>
    /// </summary>
    /// <param name="declaredSources">The NodeType's <c>Sources</c> as authored. Null/empty means
    /// it uses <see cref="CodeQueryResolver.DefaultSources"/> — which is how very nearly every
    /// NodeType is written, and exactly the population #1391 taught us not to exclude.</param>
    /// <param name="nodeTypePath">The NodeType's mesh path — the <c>$self</c> expansion and the
    /// rebase root for a bare <c>namespace:Source</c>.</param>
    /// <param name="matchedPaths">The paths the compile's source snapshot actually resolved
    /// (<c>NodeTypeDefinition.CurrentSourceVersions</c> / <c>CompiledSources</c> keys). A
    /// <c>null</c> snapshot is "not established yet", NOT "nothing matched" — the same distinction
    /// <c>NodeTypeCompilationHelpers.SourceToken</c> keeps.</param>
    /// <returns>Null when nothing could be determined; otherwise the unmatched entries as
    /// authored, in declaration order.</returns>
    public static ImmutableList<string>? UnmatchedSourceQueries(
        IReadOnlyList<string>? declaredSources,
        string? nodeTypePath,
        IReadOnlyCollection<string>? matchedPaths)
    {
        if (string.IsNullOrEmpty(nodeTypePath) || matchedPaths is null)
            return null;

        var entries = declaredSources is { Count: > 0 }
            ? declaredSources
            : CodeQueryResolver.DefaultSources;

        var determined = 0;
        var unmatched = ImmutableList.CreateBuilder<string>();

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry))
                continue;

            // One authored entry, every query it expands to. `evaluable` stays false until at
            // least one expansion parses — an entry the offline evaluator cannot read contributes
            // to neither side of the answer.
            var evaluable = false;
            var matched = false;
            foreach (var query in CodeQueryResolver.Expand(entry, nodeTypePath))
            {
                if (!NodeSetQuery.TryParse(query, out var predicate, out _))
                    continue;
                evaluable = true;
                if (matchedPaths.Any(predicate.MatchesPath))
                {
                    matched = true;
                    break;
                }
            }

            if (!evaluable)
                continue;
            determined++;
            if (!matched)
                unmatched.Add(entry);
        }

        // 🚨 Not one entry could be evaluated ⇒ NOT DETERMINED, never "all clean". An empty list
        // returned here would be indistinguishable from "every declared query matched", which is
        // the exact shape a probe is forbidden to have.
        return determined == 0 ? null : unmatched.ToImmutable();
    }

    /// <summary>
    /// One line naming what <see cref="UnmatchedSourceQueries"/> found, for the compile's recorded
    /// error and the operator log — or <c>null</c> when there is nothing to report.
    ///
    /// <para>Deliberately says what the reader should do with the diagnostics that follow it: a
    /// missing type or extension method on a SHORT source set is far more likely to be an absent
    /// source NODE than an absent module, and sending the reader to the module surfaces is the
    /// cost this whole classification exists to avoid.</para>
    /// </summary>
    /// <param name="nodeTypePath">The NodeType the finding is about.</param>
    /// <param name="unmatched">The unmatched declared entries.</param>
    /// <param name="declaredCount">How many source entries the type declares in total.</param>
    public static string? Describe(
        string nodeTypePath, IReadOnlyList<string>? unmatched, int declaredCount) =>
        unmatched is not { Count: > 0 }
            ? null
            : $"MISSING SOURCES: {unmatched.Count} of {declaredCount} declared source quer"
              + $"{(declaredCount == 1 ? "y" : "ies")} for '{nodeTypePath}' matched NO nodes on "
              + $"this mesh ({string.Join(", ", unmatched.Select(q => $"'{q}'"))}). The compile ran "
              + "against a source set SHORT of what this NodeType declares, so an unresolved type "
              + "or extension method below is far more likely to be an absent source NODE than an "
              + "absent module. No framework or module change can supply it — restore the source "
              + "nodes, or correct the query.";
}
