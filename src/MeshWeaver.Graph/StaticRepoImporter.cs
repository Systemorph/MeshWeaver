using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>Outcome of a <see cref="StaticRepoImporter.Import"/> run.</summary>
public sealed record StaticRepoImportResult(string Partition, string Fingerprint, string Outcome, int Count = 0);

/// <summary>
/// Materializes an <see cref="IStaticRepoSource"/> into its partition through the canonical
/// create pipeline — content + prerender — tracked as a content-addressed <c>Activity</c> and
/// idempotent via the source fingerprint. See <c>Doc/Architecture/StaticRepoImport.md</c>.
///
/// <para>Single-execution: the activity at <c>{Partition}/_Activity/import-{fingerprint}</c> is the
/// lock — <see cref="IMeshService.CreateNode"/> makes the first caller win and concurrent replicas
/// get "already exists". A <see cref="ActivityStatus.Succeeded"/> activity for the fingerprint is
/// the durable "already imported" record (the short-circuit). Reactive end-to-end — no await.</para>
/// </summary>
public static class StaticRepoImporter
{
    private const int BatchSize = 16;

    public static IObservable<StaticRepoImportResult> Import(
        IMessageHub hub, IStaticRepoSource source, ILogger? logger = null)
    {
        logger ??= hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.Graph.StaticRepoImporter");
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();

        var nodes = source.EnumerateSourceNodes();
        var fingerprint = PartitionSourceFingerprint.Compute(nodes, source.Versioned, hub.JsonSerializerOptions);
        var activityId = $"import-{fingerprint}";
        var activityNamespace = $"{source.Partition}/_Activity";
        var activityPath = $"{activityNamespace}/{activityId}";

        // Short-circuit: a Succeeded import activity for this fingerprint = already imported.
        // (Existence check via query — eventually consistent, but the CreateNode lock below is the
        // authoritative guard; a stale miss just attempts the create and loses the race.)
        return meshService.Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{activityPath}"))
            .Take(1)
            .SelectMany(change =>
            {
                if (change.Items.FirstOrDefault()?.Content is ActivityLog { Status: ActivityStatus.Succeeded })
                {
                    logger?.LogInformation(
                        "[StaticRepoImport] {Partition} already at {Fingerprint} — skipping.",
                        source.Partition, fingerprint);
                    return Observable.Return(new StaticRepoImportResult(source.Partition, fingerprint, "Skipped"));
                }

                var activityNode = new MeshNode(activityId, activityNamespace)
                {
                    Name = $"Import {source.Partition} ({nodes.Count} nodes)",
                    NodeType = ActivityNodeType.NodeType,
                    MainNode = source.Partition,
                    State = MeshNodeState.Active,
                    Content = new ActivityLog(ActivityCategory.Import)
                    {
                        Id = activityId,
                        HubPath = source.Partition,
                        Status = ActivityStatus.Running
                    }
                };

                // CreateNode is the lock: first instance wins; concurrent replicas fault here.
                return meshService.CreateNode(activityNode)
                    .SelectMany(_ => Run(hub, source, nodes, activityPath, fingerprint, logger))
                    .Catch<StaticRepoImportResult, Exception>(ex =>
                    {
                        logger?.LogInformation(
                            "[StaticRepoImport] {Partition} ({Fingerprint}) lock held / create faulted: {Message}",
                            source.Partition, fingerprint, ex.Message);
                        return Observable.Return(
                            new StaticRepoImportResult(source.Partition, fingerprint, "AlreadyRunning"));
                    });
            });
    }

    private static IObservable<StaticRepoImportResult> Run(
        IMessageHub hub, IStaticRepoSource source, IReadOnlyList<MeshNode> nodes,
        string activityPath, string fingerprint, ILogger? logger)
    {
        NodeTypeCompilationActivity.AppendLog(
            hub, activityPath, $"Importing {nodes.Count} node(s) into {source.Partition}…", logger!);

        var upserted = nodes
            .Select(n => UpsertOne(hub, Materialize(n)))
            .ToObservable()
            .Merge(BatchSize)
            .Sum();

        return upserted
            .SelectMany(count =>
            {
                NodeTypeCompilationActivity.AppendLog(hub, activityPath, $"Imported {count} node(s).", logger!);
                NodeTypeCompilationActivity.MarkSucceeded(hub, activityPath, logger!);
                logger?.LogInformation(
                    "[StaticRepoImport] {Partition}: imported {Count} node(s) at {Fingerprint}.",
                    source.Partition, count, fingerprint);
                return Observable.Return(new StaticRepoImportResult(source.Partition, fingerprint, "Imported", count));
            })
            .Catch<StaticRepoImportResult, Exception>(ex =>
            {
                NodeTypeCompilationActivity.MarkFailed(hub, activityPath, ex.Message, logger!);
                logger?.LogWarning(ex, "[StaticRepoImport] {Partition} import failed.", source.Partition);
                return Observable.Return(new StaticRepoImportResult(source.Partition, fingerprint, "Failed"));
            });
    }

    /// <summary>
    /// Computes prerendered HTML for markdown nodes via the shared <see cref="MarkdownContent.Parse"/>
    /// (the exact call the runtime uses) so the materialized partition serves fully from the DB.
    /// Non-markdown content passes through unchanged.
    /// </summary>
    private static MeshNode Materialize(MeshNode node)
    {
        if (node.Content is MarkdownContent { Content.Length: > 0 } md)
        {
            var html = md.PrerenderedHtml ?? MarkdownContent.Parse(md.Content, node.Path, node.Path).PrerenderedHtml;
            return node with
            {
                State = MeshNodeState.Active,
                PreRenderedHtml = html,
                Content = md with { PrerenderedHtml = html }
            };
        }
        return node with { State = MeshNodeState.Active };
    }

    private static IObservable<int> UpsertOne(IMessageHub hub, MeshNode node) =>
        hub.Observe<CreateOrUpdateNodeResponse>(new CreateOrUpdateNodeRequest(node))
            .FirstAsync()
            .Select(d => d.Message)
            .SelectMany(resp => resp.Success
                ? Observable.Return(1)
                : Observable.Throw<int>(
                    new InvalidOperationException($"Upsert of '{node.Path}' failed: {resp.Error}")));
}
