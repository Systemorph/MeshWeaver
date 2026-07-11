using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Persistence.Parsers;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.GitSync;

/// <summary>Outcome of an AI-content sync-back: how many Agent/Skill files were written, repo-relative.</summary>
public sealed record AiContentSyncResult(int AgentsWritten, int SkillsWritten, IReadOnlyList<string> Files)
{
    /// <summary>Total files written across both partitions.</summary>
    public int TotalWritten => AgentsWritten + SkillsWritten;
}

/// <summary>
/// The <b>sync-back writer</b>: serializes the live <c>Agent</c> and <c>Skill</c> partitions back to
/// the on-disk AI content section (<see cref="AiContentLocator.RepoSectionRoot"/> → <c>content/ai</c>),
/// so an agent or skill edited in the running mesh can be committed to the repo. The inverse of the
/// providers that READ that section (<see cref="BuiltInAgentProvider"/> / <see cref="BuiltInSkillProvider"/>):
/// agents serialize through <see cref="AgentFileParser"/> and skills through <see cref="SkillMarkdown"/>
/// — the SAME converters the read path uses, so a round-trip never drifts (pinned by the round-trip tests).
///
/// <para>🚨 Reactive + pooled — no <c>async</c>/<c>await</c>. Node content is read authoritatively from
/// the owning per-node hub (<see cref="MeshNodeStreamHandle"/>), NOT the eventually-consistent query
/// index, so a just-made edit is never lost (CQRS). Every file write runs on the
/// <see cref="IoPoolNames.FileSystem"/> pool, off the hub. All methods return <b>cold</b> observables —
/// the work runs on <c>Subscribe</c>.</para>
///
/// <para>Only meaningful when running from a source checkout (a deployed container has no working tree —
/// <see cref="AiContentLocator.RepoSectionRoot"/> is null there); callers gate on that.</para>
/// </summary>
public sealed class AiContentDiskWriter(
    IMessageHub hub,
    IMeshService meshService,
    IoPoolRegistry ioPools,
    ILogger<AiContentDiskWriter>? logger = null)
{
    private readonly AgentFileParser agentParser = new();
    private IIoPool FileSystem => ioPools.Get(IoPoolNames.FileSystem);

    /// <summary>
    /// Writes the live Agent + Skill partitions to <c>{sectionRoot}/Agent</c> and
    /// <c>{sectionRoot}/Skill</c> as <c>.md</c> files, creating directories as needed. Emits a single
    /// <see cref="AiContentSyncResult"/> with the counts and repo-relative paths written.
    /// </summary>
    public IObservable<AiContentSyncResult> WriteBack(string sectionRoot) =>
        WritePartition(sectionRoot, AgentPartition, AgentPartition, SerializeAgent).SelectMany(agents =>
            WritePartition(sectionRoot, SkillNodeType.RootNamespace, SkillNodeType.NodeType, SerializeSkill)
                .Select(skills => new AiContentSyncResult(agents.Count, skills.Count, [.. agents, .. skills])));

    private const string AgentPartition = "Agent";

    /// <summary>The Agent partition uses the standard AgentConfiguration content type "Agent".</summary>
    private IObservable<IReadOnlyList<string>> WritePartition(
        string sectionRoot, string partition, string nodeType, Func<MeshNode, string?> serialize) =>
        // Listing a partition's children is a sanctioned query use; content is (re-)read authoritatively
        // per node in WriteOne, so a stale index only affects which nodes exist, never their content.
        meshService
            .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{partition} scope:descendants nodeType:{nodeType}"))
            .Take(1)
            .SelectMany(collection =>
            {
                var paths = collection.Items
                    .Select(n => n.Path)
                    .Where(p => !string.IsNullOrEmpty(p)
                                && !p.Split('/').Skip(1).Any(s => s.StartsWith('_')))   // skip _Policy etc.
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (paths.Length == 0)
                    return Observable.Return((IReadOnlyList<string>)Array.Empty<string>());
                return paths
                    .Select(p => WriteOne(sectionRoot, partition, p, serialize))
                    .Merge(4)
                    .Where(f => f is not null).Select(f => f!)
                    .ToList()
                    .Select(l => (IReadOnlyList<string>)l);
            });

    private IObservable<string?> WriteOne(
        string sectionRoot, string partition, string path, Func<MeshNode, string?> serialize) =>
        hub.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is not null).Take(1).Timeout(TimeSpan.FromSeconds(15))
            .SelectMany(node =>
            {
                var text = serialize(node!);
                if (text is null)
                {
                    logger?.LogWarning("AI content sync-back: skipping {Path} — content not serializable.", path);
                    return Observable.Return<string?>(null);
                }
                // Preserve any nesting under the partition (Agent/Sub/Foo → Agent/Sub/Foo.md), not just the id.
                var rel = path.StartsWith(partition + "/", StringComparison.Ordinal)
                    ? path[(partition.Length + 1)..]
                    : node!.Id;
                var repoRel = Path.Combine(partition, rel.Replace('/', Path.DirectorySeparatorChar) + ".md");
                var full = Path.Combine(sectionRoot, repoRel);
                return FileSystem.InvokeBlocking<string?>(_ =>
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    File.WriteAllText(full, text);
                    return repoRel.Replace(Path.DirectorySeparatorChar, '/');
                });
            });

    // AgentFileParser already handles both typed AgentConfiguration and the JsonElement fallback.
    private string? SerializeAgent(MeshNode node) =>
        agentParser.CanSerialize(node) ? agentParser.Serialize(node) : null;

    // SkillMarkdown.Serialize needs a typed SkillDefinition; normalise a JsonElement fallback first,
    // and refuse (return null → skip, don't clobber) content we can't interpret.
    private string? SerializeSkill(MeshNode node)
    {
        var typed = node.Content switch
        {
            SkillDefinition => node,
            JsonElement je => TryTypeSkill(node, je),
            _ => null,
        };
        return typed is null ? null : SkillMarkdown.Serialize(typed);
    }

    private MeshNode? TryTypeSkill(MeshNode node, JsonElement je)
    {
        try
        {
            var def = JsonSerializer.Deserialize<SkillDefinition>(je.GetRawText(), hub.JsonSerializerOptions);
            return def is null ? null : node with { Content = def };
        }
        catch
        {
            return null;
        }
    }
}
