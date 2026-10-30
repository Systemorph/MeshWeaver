using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// The data half of Systemorph/MeshWeaver#6639 — a detection-driven repair for stored
/// <c>MarkdownContent</c> whose body sits under a key the record does not declare
/// (<c>body</c>, <c>markdown</c>, <c>text</c>) instead of <c>content</c>.
///
/// <para><b>Why the rows exist and why the code cannot absorb them.</b> Writers that hand-built the
/// JSON (an installer writing <c>["body"] = …</c>, agents authoring through a raw <c>create</c>)
/// stored the document under the wrong key. <see cref="MarkdownContent"/> deliberately refuses to
/// bind such a payload as an empty document (#4600, #5736): an empty document over a stored body
/// would render the "No content yet" invitation whose first save overwrites the text. So every read
/// seam degrades the node to raw JSON, the overview marks it Unreadable, and the defect stays in the
/// DATA until the body is moved under <c>content</c>. That move is what this pass does.</para>
///
/// <para><b>Narrow on purpose.</b> A candidate is a node whose content is raw JSON typed
/// <c>MarkdownContent</c> (or a <c>Markdown</c> node with untyped object content), with NO
/// <c>content</c> member, and exactly ONE undeclared member — a string under one of
/// <see cref="LegacyBodyKeys"/>. Anything else that fails to bind (two stray members, a non-string
/// value, an unknown key name) is reported as <see cref="MarkdownBodyKeyOutcome.NeedsReview"/> and
/// never written: which member is the body is then a judgement about content, not a mechanical
/// rename.</para>
///
/// <para><b>The write.</b> Through <c>GetMeshNodeStream(path).Update(…)</c>, the one mutation API,
/// under <c>RunAsSystem</c> (a platform repair of rows whose owners may be anyone). The lambda
/// re-derives the rename from the node as the owning hub holds it, so a node that was fixed between
/// detection and write is returned unchanged. The body text is moved byte-for-byte; every declared
/// member (tags, authors, abstract, …) is kept.</para>
///
/// <para><b>🚨 Nothing self-arms.</b> Like <see cref="StaleMainNodeRepair"/>, this is a static
/// one-shot pipeline that no composition root calls: deploying it repairs nothing. <see cref="Detect"/>
/// reads only and is safe on production; running <see cref="Repair"/> against a live portal is a
/// separate, deliberate operator decision. See <c>Doc/Architecture/MarkdownBodyKeyRepair</c>.</para>
/// </summary>
public static class MarkdownBodyKeyRepair
{
    /// <summary>
    /// The undeclared member names a body has been measured under (#6639: <c>body</c> from an
    /// installer and hand-written nodes, <c>markdown</c> and <c>text</c> from agent-authored nodes).
    /// Immutable; only these are ever renamed.
    /// </summary>
    public static readonly ImmutableHashSet<string> LegacyBodyKeys =
        ImmutableHashSet.Create(StringComparer.Ordinal, "body", "markdown", "text");

    private const int ReadBatchSize = 250;
    private const string MarkdownNodeType = "Markdown";
    private const string ContentMember = "content";

    /// <summary>
    /// Finds every stored Markdown body under a legacy key and reports it WITHOUT writing.
    /// Safe to run against a live portal.
    /// </summary>
    /// <param name="hub">Hub supplying the storage adapter, serializer options and logger.</param>
    /// <param name="roots">Partitions / subtrees to sweep. Null or empty sweeps the whole mesh.</param>
    /// <returns>A cold observable emitting one report and completing. <b>Subscribe to run it.</b></returns>
    public static IObservable<MarkdownBodyKeyRepairReport> Detect(
        IMessageHub hub, IReadOnlyCollection<string>? roots = null)
        => Sweep(hub, roots, write: false);

    /// <summary>
    /// Finds every stored Markdown body under a legacy key and moves it under <c>content</c>.
    /// </summary>
    /// <param name="hub">Hub supplying the storage adapter, access service, serializer options and logger.</param>
    /// <param name="roots">Partitions / subtrees to sweep. Null or empty sweeps the whole mesh.</param>
    /// <returns>A cold observable emitting one report and completing. <b>Subscribe to run it.</b></returns>
    public static IObservable<MarkdownBodyKeyRepairReport> Repair(
        IMessageHub hub, IReadOnlyCollection<string>? roots = null)
        => Sweep(hub, roots, write: true);

    /// <summary>
    /// Classifies one node's content. <see cref="MarkdownBodyKeyOutcome.Clean"/> for everything
    /// that is not a refused Markdown payload; otherwise the legacy key the body sits under, or
    /// <see cref="MarkdownBodyKeyOutcome.NeedsReview"/> with the stray member names.
    /// </summary>
    /// <param name="node">The node as storage returned it.</param>
    /// <returns>The outcome, and the legacy key or the stray member names.</returns>
    public static (MarkdownBodyKeyOutcome Outcome, string Detail) Classify(MeshNode node)
    {
        if (node.Content is not JsonElement { ValueKind: JsonValueKind.Object } json)
            return (MarkdownBodyKeyOutcome.Clean, string.Empty);

        var typeName = json.TryGetProperty("$type", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString()
            : null;
        var isMarkdown = typeName is not null
            ? typeName == nameof(MarkdownContent) || typeName == typeof(MarkdownContent).FullName
            : string.Equals(node.NodeType, MarkdownNodeType, StringComparison.Ordinal);
        if (!isMarkdown || json.TryGetProperty(ContentMember, out _))
            return (MarkdownBodyKeyOutcome.Clean, string.Empty);

        var stray = json.EnumerateObject()
            .Where(p => !p.Name.StartsWith('$') && !DeclaredMembers.Contains(p.Name))
            .ToArray();
        if (stray.Length == 1
            && LegacyBodyKeys.Contains(stray[0].Name)
            && stray[0].Value.ValueKind == JsonValueKind.String)
            return (MarkdownBodyKeyOutcome.Repairable, stray[0].Name);

        return stray.Length == 0
            // No stray member: it binds (or is a missing source over a rendering) — not this defect.
            ? (MarkdownBodyKeyOutcome.Clean, string.Empty)
            : (MarkdownBodyKeyOutcome.NeedsReview,
                string.Join(", ", stray.Select(p => $"{p.Name}:{p.Value.ValueKind}")));
    }

    /// <summary>
    /// Rebuilds the content with the body under <c>content</c>, or returns null when the node is not
    /// (or no longer) repairable. Pure: the same function the sweep classifies with and the write
    /// applies inside the owning hub's update.
    /// </summary>
    /// <param name="node">The node to rewrite.</param>
    /// <param name="options">Serializer options of the hub that owns the type.</param>
    /// <returns>The typed content, or null.</returns>
    public static MarkdownContent? Rewrite(MeshNode node, JsonSerializerOptions options)
    {
        var (outcome, key) = Classify(node);
        if (outcome != MarkdownBodyKeyOutcome.Repairable)
            return null;
        var obj = JsonNode.Parse(((JsonElement)node.Content!).GetRawText())!.AsObject();
        var body = obj[key]!.GetValue<string>();
        obj.Remove(key);
        // `$`-metadata is the serializer's business; binding the record directly must not see it,
        // or it would land in the extension buffer and be written twice.
        foreach (var meta in obj.Select(p => p.Key).Where(k => k.StartsWith('$')).ToArray())
            obj.Remove(meta);
        obj[ContentMember] = body;
        var typed = obj.Deserialize<MarkdownContent>(options);
        // 🚨 The rename must be the ONLY change. A declared member the typed shape cannot carry
        // unchanged (e.g. `tags` stored as a string, which the list converter reads as an empty
        // list) would be rewritten by the write: data loss beyond the move. Such a row is not
        // mechanical and is left for review.
        return typed is null || LossyMembers(obj, typed, options).Count > 0 ? null : typed;
    }

    /// <summary>
    /// The declared members of <paramref name="source"/> whose value does not survive binding to
    /// <paramref name="typed"/> and serialising back unchanged. Empty when the round-trip is lossless.
    /// </summary>
    private static ImmutableList<string> LossyMembers(
        JsonObject source, MarkdownContent typed, JsonSerializerOptions options)
    {
        var written = JsonSerializer.SerializeToNode(typed, options) as JsonObject;
        return source
            .Where(p => !p.Key.StartsWith('$') && p.Key != ContentMember)
            .Where(p => !(written is not null && written.TryGetPropertyValue(p.Key, out var after)
                ? JsonNode.DeepEquals(p.Value, after)
                : p.Value is null))
            .Select(p => p.Key)
            .ToImmutableList();
    }

    /// <summary>
    /// <see cref="Classify"/>, plus the round-trip check only the hub's serializer options can make:
    /// a Repairable row whose rename would change a declared member is reported as
    /// <see cref="MarkdownBodyKeyOutcome.NeedsReview"/> by Detect and Repair alike.
    /// </summary>
    private static (MarkdownBodyKeyOutcome Outcome, string Detail) ClassifyForRepair(
        MeshNode node, JsonSerializerOptions options)
    {
        var result = Classify(node);
        return result.Outcome == MarkdownBodyKeyOutcome.Repairable && Rewrite(node, options) is null
            ? (MarkdownBodyKeyOutcome.NeedsReview, $"{result.Detail}: the rename would change a declared member")
            : result;
    }

    private static readonly ImmutableHashSet<string> DeclaredMembers =
        typeof(MarkdownContent)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(p => p.Name != nameof(MarkdownContent.Content)
                        && !p.IsDefined(typeof(System.Text.Json.Serialization.JsonExtensionDataAttribute), true)
                        && !p.IsDefined(typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), true))
            .Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name))
            .ToImmutableHashSet(StringComparer.Ordinal);

    private static IObservable<MarkdownBodyKeyRepairReport> Sweep(
        IMessageHub hub, IReadOnlyCollection<string>? roots, bool write)
        => Observable.Defer(() =>
        {
            var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
                ?.CreateLogger(typeof(MarkdownBodyKeyRepair));
            var storage = hub.ServiceProvider.GetService<IStorageAdapter>();
            if (storage is null)
            {
                logger?.LogInformation("[MarkdownBodyKeyRepair] no storage adapter on this hub — nothing to sweep");
                return Observable.Return(MarkdownBodyKeyRepairReport.Empty(write));
            }

            var options = hub.JsonSerializerOptions;
            return StorageTreeWalk.EnumeratePaths(storage, roots, logger, nameof(MarkdownBodyKeyRepair))
                .SelectMany(paths => ReadFindings(storage, paths, options)
                    .SelectMany(read => (write
                            ? read.Findings
                                .Select(f => f.Outcome == MarkdownBodyKeyOutcome.Repairable
                                    ? RepairOne(hub, f, logger)
                                    : Observable.Return(f))
                                .Concat()
                                .Aggregate(ImmutableList<MarkdownBodyKeyFinding>.Empty, (acc, f) => acc.Add(f))
                            : Observable.Return(read.Findings))
                        .Select(findings => new MarkdownBodyKeyRepairReport(
                            findings, paths.Count, read.NodesRead, write))))
                .Do(report => logger?.LogInformation(
                    "[MarkdownBodyKeyRepair] sweep completed ({Mode}): {Scanned} path(s) enumerated, "
                    + "{Read} node(s) read, {Found} finding(s), {Repaired} repaired, {Review} need review, "
                    + "{Failed} failed [{Paths}]",
                    write ? "repair" : "detect-only",
                    report.PathsScanned, report.NodesRead, report.Findings.Count,
                    report.RepairedCount, report.NeedsReviewCount, report.FailedCount,
                    string.Join(", ", report.Findings.Select(f => $"{f.Path}({f.Outcome}:{f.Detail})"))));
        });

    private static IObservable<(ImmutableList<MarkdownBodyKeyFinding> Findings, int NodesRead)> ReadFindings(
        IStorageAdapter storage, ImmutableList<string> paths, JsonSerializerOptions options)
        => paths.Count == 0
            ? Observable.Return((ImmutableList<MarkdownBodyKeyFinding>.Empty, 0))
            : paths.Chunk(ReadBatchSize)
                .Select(batch => storage.ReadMany(batch, options).ToList())
                .Concat()
                .Aggregate(
                    (Findings: ImmutableList<MarkdownBodyKeyFinding>.Empty, NodesRead: 0),
                    (acc, batch) => (
                        acc.Findings.AddRange(batch
                            .Select(n => (Node: n, Result: ClassifyForRepair(n, options)))
                            .Where(x => x.Result.Outcome != MarkdownBodyKeyOutcome.Clean)
                            .Select(x => new MarkdownBodyKeyFinding(
                                x.Node.Path, x.Result.Outcome, x.Result.Detail, Error: null))),
                        acc.NodesRead + batch.Count))
                // ReadMany's order is not guaranteed; the report is evidence and must be diffable.
                .Select(acc => (
                    acc.Findings.Sort((l, r) => string.Compare(l.Path, r.Path, StringComparison.OrdinalIgnoreCase)),
                    acc.NodesRead));

    private static IObservable<MarkdownBodyKeyFinding> RepairOne(
        IMessageHub hub, MarkdownBodyKeyFinding finding, ILogger? logger)
    {
        var path = finding.Path;
        var options = hub.JsonSerializerOptions;
        var access = hub.ServiceProvider.GetRequiredService<AccessService>();
        // RunAsSystem, never Observable.Using(access.ImpersonateAsSystem, …) (#1790).
        return access.RunAsSystem(() => hub.GetMeshNodeStream(path)
                .Update(current => Rewrite(current, options) is { } typed
                    ? current with { Content = typed }
                    : current))
            .Take(1)
            .Select(updated =>
            {
                // `Update` emits the node even when the lambda returned it unchanged (the row was
                // fixed, or became ambiguous or lossy, between the scan and the write). Only a node
                // that no longer classifies was repaired; anything else is reported for review.
                var after = Classify(updated);
                if (after.Outcome != MarkdownBodyKeyOutcome.Clean)
                {
                    logger?.LogWarning(
                        "[MarkdownBodyKeyRepair] '{Path}' was not rewritten: at write time it classified as {Outcome} ({Detail})",
                        path, after.Outcome, after.Detail);
                    return finding with
                    {
                        Outcome = MarkdownBodyKeyOutcome.NeedsReview,
                        Detail = $"{finding.Detail}: not rewritten, {after.Outcome} at write time ({after.Detail})",
                    };
                }
                logger?.LogInformation(
                    "[MarkdownBodyKeyRepair] repaired '{Path}': body moved from '{Key}' to 'content' (#6639)",
                    path, finding.Detail);
                return finding with { Outcome = MarkdownBodyKeyOutcome.Repaired };
            })
            // Per-node tolerance: one unwritable node must not stop the rest, and its failure comes
            // back as EVIDENCE naming the node. Re-running is safe — an unrepaired node still matches.
            .Catch<MarkdownBodyKeyFinding, Exception>(ex =>
            {
                logger?.LogWarning(ex,
                    "[MarkdownBodyKeyRepair] repairing '{Path}' failed; its body stays under '{Key}'",
                    path, finding.Detail);
                return Observable.Return(finding with { Error = ex.Message });
            });
    }
}

/// <summary>What the sweep found for one node.</summary>
public enum MarkdownBodyKeyOutcome
{
    /// <summary>Not this defect. Never part of a report.</summary>
    Clean,

    /// <summary>The body sits under a legacy key and can be moved mechanically.</summary>
    Repairable,

    /// <summary>The body was moved under <c>content</c> by this run.</summary>
    Repaired,

    /// <summary>The payload does not bind, but which member is the body is not mechanical. Never written.</summary>
    NeedsReview,
}

/// <summary>One node the sweep reported.</summary>
/// <param name="Path">The node's path.</param>
/// <param name="Outcome">What was found, or done.</param>
/// <param name="Detail">The legacy key, or the stray member names and kinds for a review finding.</param>
/// <param name="Error">Why the write failed, when it did; null otherwise.</param>
public sealed record MarkdownBodyKeyFinding(
    string Path,
    MarkdownBodyKeyOutcome Outcome,
    string Detail,
    string? Error);

/// <summary>The outcome of one sweep — per node, so a live run produces evidence rather than a count.</summary>
/// <param name="Findings">Every reported node, in path order.</param>
/// <param name="PathsScanned">How many paths the storage-tree enumeration produced.</param>
/// <param name="NodesRead">How many rows storage returned — the sweep's proof of work.</param>
/// <param name="Wrote">Whether this was a repair pass.</param>
public sealed record MarkdownBodyKeyRepairReport(
    ImmutableList<MarkdownBodyKeyFinding> Findings,
    int PathsScanned,
    int NodesRead,
    bool Wrote)
{
    /// <summary>A sweep that had nothing to look at.</summary>
    /// <param name="wrote">Whether the caller asked for a repair pass.</param>
    public static MarkdownBodyKeyRepairReport Empty(bool wrote) =>
        new(ImmutableList<MarkdownBodyKeyFinding>.Empty, PathsScanned: 0, NodesRead: 0, wrote);

    /// <summary>How many findings this run moved under <c>content</c>.</summary>
    public int RepairedCount => Findings.Count(f => f.Outcome == MarkdownBodyKeyOutcome.Repaired);

    /// <summary>How many findings were left for a person to judge.</summary>
    public int NeedsReviewCount => Findings.Count(f => f.Outcome == MarkdownBodyKeyOutcome.NeedsReview);

    /// <summary>How many repairs were attempted and failed.</summary>
    public int FailedCount => Findings.Count(f => f.Error is not null);
}
