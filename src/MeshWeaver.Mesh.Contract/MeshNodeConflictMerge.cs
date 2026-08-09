using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Data.Serialization;

namespace MeshWeaver.Mesh;

/// <summary>
/// The outcome of <see cref="MeshNodeConflictMerge.Merge"/>: the node to persist plus exactly which
/// members were reconciled and which had the losing writer's value dropped.
/// </summary>
/// <param name="Node">The merged node — always carries the LATEST row's identity and bookkeeping.</param>
/// <param name="MergedMembers">
/// Members where BOTH writers' content survived (a string the stale writer extended, an array element
/// only it carried). Reported, not warned: nothing was lost.
/// </param>
/// <param name="OverwrittenMembers">
/// Members that were NOT auto-resolvable — two divergent values with no common ancestor to rebase
/// against. Latest won and the stale writer's value was DROPPED. This is the set that must reach the
/// activity log: dropping a value silently is the whole defect this merge exists to stop.
/// </param>
public sealed record MeshNodeConflictResolution(
    MeshNode Node,
    ImmutableList<string> MergedMembers,
    ImmutableList<string> OverwrittenMembers)
{
    /// <summary>
    /// True when the stale writer contributed nothing that survived — the durable row already IS the
    /// answer, so the resolution needs no write of its own.
    /// </summary>
    public bool IsLatestUnchanged => MergedMembers.IsEmpty;
}

/// <summary>
/// Maps a <see cref="MeshNode"/> onto the base-less conflict policy implemented by
/// <see cref="MeshNodePatchMerge.ApplyTwoWay"/> — the resolution for a write that lost a version race
/// AT THE STORE (issue #971).
///
/// <para><b>Where the conflict comes from.</b> The write-integrity chain sees an incoming node whose
/// <see cref="MeshNode.Version"/> is strictly BELOW the durable row's — either this process's
/// high-water mark said so and the verification read confirmed it, or the store itself refused the
/// write with a version-conditional upsert and handed back the row it kept. Both are the same event:
/// a writer holding an older snapshot.</para>
///
/// <para><b>This class owns only the MEMBER MAPPING.</b> Every merge decision — the string rule, the
/// array-identity union, the recursion, what counts as unresolvable — lives in
/// <see cref="MeshNodePatchMerge"/>, alongside the three-way merge that cross-hub patches use, so the
/// two can never drift into disagreeing about the same JSON. What is specific here is which
/// <see cref="MeshNode"/> members are user data (merge them), which are the store's own bookkeeping
/// (latest always wins, never reported), and how <see cref="MeshNode.Content"/> is projected to JSON
/// and materialised back into its CLR type.</para>
///
/// <para><b>Which members take the string merge.</b> Only <see cref="MeshNode.Name"/> and
/// <see cref="MeshNode.Description"/> — the prose. <see cref="MeshNode.NodeType"/>,
/// <see cref="MeshNode.Category"/>, <see cref="MeshNode.Icon"/> and <see cref="MeshNode.DesiredId"/>
/// are wire identifiers, not text: splicing them could retype a node (<c>"Code"</c> + <c>"CodeCell"</c>),
/// which drives routing and hub activation. <see cref="MeshNode.PreRenderedHtml"/> is derived from
/// <see cref="MeshNode.Content"/>, so it follows the merged content rather than being spliced. All of
/// those take latest-wins-and-REPORT, so the policy's fallback still covers them and nothing is
/// dropped silently. The user data that actually benefits from merging lives in
/// <see cref="MeshNode.Content"/>, which is merged leaf by leaf.</para>
///
/// <para>🚨 <b>Identity and bookkeeping always come from LATEST, never reported.</b>
/// <see cref="MeshNode.Id"/>, <see cref="MeshNode.Namespace"/>, <see cref="MeshNode.MainNode"/>,
/// <see cref="MeshNode.Version"/>, <see cref="MeshNode.LastModified"/>,
/// <see cref="MeshNode.LastModifiedBy"/>, <see cref="MeshNode.CreatedBy"/> and
/// <see cref="MeshNode.CreatedDate"/> are the store's clock and authorship, not user data. They differ
/// on essentially every conflict, so folding them into the report would bury the members that actually
/// matter. The merged node keeps the latest row's version specifically so re-persisting it cannot
/// itself be read as a rollback.</para>
///
/// <para>🚨 <b><see cref="MeshNode.HubConfiguration"/> and
/// <see cref="MeshNode.GlobalServiceConfigurations"/> are delegates</b> — they cannot round-trip
/// through JSON, which is why the node as a whole is never serialized here. Only
/// <see cref="MeshNode.Content"/> takes that path, and it is materialised back into the LATEST
/// content's own type so a merge can never degrade typed content into a bare
/// <see cref="JsonElement"/>.</para>
/// </summary>
public static class MeshNodeConflictMerge
{
    /// <summary>
    /// Reconciles <paramref name="stale"/> (the write that lost the version race) into
    /// <paramref name="latest"/> (the durable row) under the policy documented on
    /// <see cref="MeshNodePatchMerge.ApplyTwoWay"/>.
    /// </summary>
    /// <param name="latest">The durable row — the higher <see cref="MeshNode.Version"/>. Wins every tie.</param>
    /// <param name="stale">The refused write.</param>
    /// <param name="options">Serializer options used to project <see cref="MeshNode.Content"/> to JSON and back.</param>
    /// <returns>The merged node plus the merged / overwritten member reports.</returns>
    public static MeshNodeConflictResolution Merge(
        MeshNode latest, MeshNode stale, JsonSerializerOptions options)
    {
        var merged = new List<string>();
        var overwritten = new List<string>();

        // PROSE members take the string merge. Only these two: they are text a human wrote, which is
        // what a splice-level merge is meaningful on.
        var result = latest with
        {
            Name = MergeText(nameof(MeshNode.Name), latest.Name, stale.Name, merged, overwritten),
            Description = MergeText(nameof(MeshNode.Description), latest.Description, stale.Description, merged, overwritten),
        };

        // 🚨 IDENTIFIER-shaped strings are NOT prose and are deliberately excluded from the string
        // rule: NodeType, Category, Icon and DesiredId are wire discriminators that drive routing and
        // per-node hub activation. The superset rule would happily splice "Code" and "CodeCell" into
        // one of them and silently retype the node — an outsized consequence next to a merged
        // sentence. They take the same latest-wins-and-REPORT path as the true non-strings, so the
        // policy's fallback still applies and nothing is dropped silently. PreRenderedHtml joins them
        // because it is DERIVED from Content: the correct value follows from the merged content, and
        // splicing two renderings could only produce broken markup.
        ReportIfDifferent(nameof(MeshNode.NodeType), latest.NodeType, stale.NodeType, overwritten);
        ReportIfDifferent(nameof(MeshNode.Category), latest.Category, stale.Category, overwritten);
        ReportIfDifferent(nameof(MeshNode.Icon), latest.Icon, stale.Icon, overwritten);
        ReportIfDifferent(nameof(MeshNode.DesiredId), latest.DesiredId, stale.DesiredId, overwritten);
        ReportIfDifferent(nameof(MeshNode.PreRenderedHtml), latest.PreRenderedHtml, stale.PreRenderedHtml, overwritten);

        // Non-string members: latest wins, reported when the stale writer held something else.
        ReportIfDifferent(nameof(MeshNode.Order), latest.Order, stale.Order, overwritten);
        ReportIfDifferent(nameof(MeshNode.State), latest.State, stale.State, overwritten);
        ReportIfDifferent(nameof(MeshNode.SyncBehavior), latest.SyncBehavior, stale.SyncBehavior, overwritten);
        ReportIfDifferent(nameof(MeshNode.IsDefinitionOnly), latest.IsDefinitionOnly, stale.IsDefinitionOnly, overwritten);
        ReportIfDifferent(nameof(MeshNode.IsSatelliteType), latest.IsSatelliteType, stale.IsSatelliteType, overwritten);
        if (!SameExclusions(latest.ExcludeFromContext, stale.ExcludeFromContext))
            overwritten.Add(nameof(MeshNode.ExcludeFromContext));

        result = result with { Content = MergeContent(latest.Content, stale.Content, options, merged, overwritten) };

        return new MeshNodeConflictResolution(result, [.. merged], [.. overwritten]);
    }

    /// <summary>
    /// A MeshNode-level string member, resolved by the SAME rule
    /// (<see cref="MeshNodePatchMerge.TryMergeTwoWay"/>) that <see cref="MeshNodePatchMerge.ApplyTwoWay"/>
    /// applies to string leaves inside <see cref="MeshNode.Content"/>.
    /// </summary>
    private static string? MergeText(
        string member, string? latest, string? stale, List<string> merged, List<string> overwritten)
    {
        if (MeshNodePatchMerge.TryMergeTwoWay(latest, stale, out var resolved))
        {
            if (!string.Equals(resolved, latest, StringComparison.Ordinal))
                merged.Add(member);
            return resolved;
        }
        overwritten.Add(member);
        return latest;
    }

    private static void ReportIfDifferent<T>(string member, T latest, T stale, List<string> overwritten)
    {
        if (!EqualityComparer<T>.Default.Equals(latest, stale))
            overwritten.Add(member);
    }

    private static bool SameExclusions(IReadOnlyCollection<string>? latest, IReadOnlyCollection<string>? stale)
    {
        if (latest is null || latest.Count == 0)
            return stale is null || stale.Count == 0;
        if (stale is null || stale.Count != latest.Count)
            return false;
        return latest.SequenceEqual(stale, StringComparer.Ordinal);
    }

    /// <summary>
    /// Projects both contents to JSON, merges through <see cref="MeshNodePatchMerge.ApplyTwoWay"/>, and
    /// materialises the result back into the LATEST content's CLR type. A content whose two sides are
    /// not both JSON objects (a shape change, a scalar content) is not mergeable: latest wins and the
    /// member is reported rather than silently dropped.
    /// </summary>
    private static object? MergeContent(
        object? latest, object? stale, JsonSerializerOptions options,
        List<string> merged, List<string> overwritten)
    {
        if (stale is null)
            return latest;
        if (latest is null)
        {
            merged.Add(nameof(MeshNode.Content));
            return stale;
        }

        JsonObject? latestJson, staleJson;
        try
        {
            latestJson = JsonSerializer.SerializeToNode(latest, latest.GetType(), options) as JsonObject;
            staleJson = JsonSerializer.SerializeToNode(stale, stale.GetType(), options) as JsonObject;
        }
        catch (Exception ex) when (IsSerializationFailure(ex))
        {
            // Not hidden: content this serializer cannot project is REPORTED as an overwritten
            // member, so the activity-log entry names it and the newer row is kept intact.
            overwritten.Add(nameof(MeshNode.Content));
            return latest;
        }

        if (latestJson is null || staleJson is null)
        {
            overwritten.Add(nameof(MeshNode.Content));
            return latest;
        }

        // Only entries added to `merged` correspond to an actual mutation of `latestJson`; an
        // overwritten member leaves the live value in place by definition. So an unchanged `merged`
        // means there is nothing to re-materialise — keep the live instance rather than paying a
        // round-trip through JSON for no gain.
        var mutationsBefore = merged.Count;
        MeshNodePatchMerge.ApplyTwoWay(
            latestJson, staleJson, nameof(MeshNode.Content), merged.Add, overwritten.Add);
        if (merged.Count == mutationsBefore)
            return latest;

        try
        {
            return latestJson.Deserialize(latest.GetType(), options) ?? latest;
        }
        catch (Exception ex) when (IsSerializationFailure(ex))
        {
            overwritten.Add(nameof(MeshNode.Content));
            return latest;
        }
    }

    /// <summary>
    /// The serialization failures that are an EXPECTED input condition here — content whose CLR type
    /// this serializer cannot project to JSON and back. <see cref="JsonSerializer"/> documents both:
    /// <see cref="NotSupportedException"/> when no compatible converter exists for the type or one of
    /// its members, and <see cref="JsonException"/> for malformed/cyclic JSON. Both mean the same
    /// thing for a merge — the content is not mergeable — so both are reported as an overwritten
    /// member and the newer row is kept.
    ///
    /// <para>🚨 Deliberately NOT a blanket catch. Anything else — a custom converter throwing an
    /// arbitrary exception — is a genuine fault, not an unmergeable input, and it must propagate.
    /// Swallowing it would hide a defect behind a plausible-looking "latest wins", which is the exact
    /// shape this whole change exists to eliminate. A loud failure is an acceptable outcome; a silent
    /// wrong one is not.</para>
    /// </summary>
    private static bool IsSerializationFailure(Exception ex)
        => ex is JsonException or NotSupportedException;
}
