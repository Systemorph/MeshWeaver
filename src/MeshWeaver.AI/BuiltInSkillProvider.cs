using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.AI;

/// <summary>
/// Provides the built-in skill nodes from embedded <c>Data/Skill/*.md</c> resources — the SAME
/// .md-with-YAML authoring model as agents (<see cref="BuiltInAgentProvider"/>). Each skill is a
/// <c>nodeType: Skill</c> markdown file: <b>behaviour</b> skills carry an <c>action:</c> block in the
/// frontmatter (<c>Pick</c> / <c>OpenContent</c> / <c>Connect</c>), <b>instruction</b> skills carry
/// their how-to in the markdown body. The slash word is the file name (<c>agent.md</c> → <c>/agent</c>).
/// Discovered together with per-space / per-user skills via <see cref="SkillNodeType.SkillQueries"/>.
/// </summary>
public class BuiltInSkillProvider : IStaticNodeProvider
{
    private static readonly Lazy<MeshNode[]> LazyNodes = new(LoadEmbeddedNodes);

    /// <inheritdoc />
    public IEnumerable<MeshNode> GetStaticNodes()
    {
        // Read-only, world-readable policy for the Skill namespace — the skill catalog is public, same
        // as Agent/Harness. On the SYNCED path this _Policy MUST be imported (SkillStaticRepoSource),
        // else the partition has no read policy and the skills are unreadable → the chat finds no skills
        // (the Harness wedge, atioz 2026-06-15). The write caps keep the built-in skills unmodifiable.
        yield return new MeshNode("_Policy", SkillNodeType.RootNamespace)
        {
            NodeType = "PartitionAccessPolicy",
            Name = "Access Policy",
            Content = new PartitionAccessPolicy
            {
                PublicRead = true,
                Create = false,
                Update = false,
                Delete = false,
                Comment = false,
                Thread = false,
            }
        };

        foreach (var node in LazyNodes.Value)
            yield return node;
    }

    private static MeshNode[] LoadEmbeddedNodes()
    {
        var assembly = typeof(BuiltInSkillProvider).Assembly;
        // Resource names dot-separate path segments: Data/Skill/agent.md → {asm}.Data.Skill.agent.md
        var skillPrefix = $"{assembly.GetName().Name}.Data.{SkillNodeType.RootNamespace}.";

        var nodes = new List<MeshNode>();

        // Prefer the on-disk AI content section (content/ai/Skill) — editable + syncable back to the
        // repo. Parse is identical to the embedded path; only the byte source moves to disk.
        var root = AiContentLocator.SectionRoot();
        var skillDir = root is null ? null : System.IO.Path.Combine(root, SkillNodeType.RootNamespace);
        if (skillDir is not null && System.IO.Directory.Exists(skillDir))
        {
            foreach (var file in System.IO.Directory
                         .EnumerateFiles(skillDir, "*.md", System.IO.SearchOption.AllDirectories)
                         .OrderBy(f => f, StringComparer.Ordinal))
            {
                var node = ParseSkillNode(System.IO.File.ReadAllText(file),
                    System.IO.Path.GetFileNameWithoutExtension(file));
                if (node != null) nodes.Add(node);
            }
            return nodes.ToArray();
        }

        // Fallback: EMBEDDED resources — the offline default never loses its skills.
        foreach (var resourceName in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith(skillPrefix, StringComparison.Ordinal)
                                 && n.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                     .Order())
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null) continue;
            using var reader = new StreamReader(stream);
            var node = ParseSkillNode(reader.ReadToEnd(), ResourceNameToId(resourceName, skillPrefix));
            if (node != null) nodes.Add(node);
        }
        return nodes.ToArray();
    }

    // The one skill md↔node conversion lives in SkillMarkdown, shared with the sync-back writer
    // (AiContentDiskWriter serializes via SkillMarkdown.Serialize) — so read and write can never drift.
    private static MeshNode? ParseSkillNode(string content, string id) => SkillMarkdown.Parse(content, id);

    private static string ResourceNameToId(string resourceName, string skillPrefix)
    {
        var rest = resourceName[skillPrefix.Length..]; // e.g. "agent.md"
        var lastDot = rest.LastIndexOf('.');           // strip the ".md" extension
        return lastDot > 0 ? rest[..lastDot] : rest;
    }

}
