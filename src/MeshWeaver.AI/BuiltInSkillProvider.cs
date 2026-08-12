using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.AI;

/// <summary>
/// Provides the built-in skill nodes from embedded <c>Data/Skill/*.md</c> resources — the SAME
/// .md-with-YAML authoring model as agents (<see cref="BuiltInAgentProvider"/>). Each skill is a
/// <c>nodeType: Skill</c> markdown file: <b>behaviour</b> skills carry an <c>action:</c> block in the
/// frontmatter (any <see cref="SkillActionKind"/> — <c>Pick</c>, <c>OpenContent</c>, <c>Navigate</c>,
/// <c>Connect</c>, <c>Disconnect</c>, <c>NewThread</c>), <b>instruction</b> skills carry
/// their how-to in the markdown body. The slash word is the file name (<c>agent.md</c> → <c>/agent</c>).
/// Discovered together with per-space / per-user skills via <see cref="SkillNodeType.SkillQueries"/>.
/// </summary>
public class BuiltInSkillProvider : IStaticNodeProvider
{
    private static readonly Lazy<SkillCatalog> LazyCatalog = new(LoadCatalog);

    /// <summary>
    /// The loaded built-in skill catalog: the nodes that parsed, and — critically — every file that did
    /// NOT. A file that fails to parse is skipped (a throw here would fail mesh startup), so
    /// <see cref="SkillCatalog.Failures"/> is the ONLY place a dropped skill is visible.
    /// <c>internal</c> so the catalog guard test can fail RED naming the offending file
    /// (<c>BuiltInSkillCatalogTest</c>).
    /// </summary>
    /// <param name="Nodes">The skills that loaded.</param>
    /// <param name="Failures">The files that were skipped, each with an author-actionable reason.</param>
    internal sealed record SkillCatalog(MeshNode[] Nodes, IReadOnlyList<AiContentLoadFailure> Failures);

    /// <summary>The built-in skill catalog — loaded once, nodes plus the files that failed to load.</summary>
    internal static SkillCatalog Catalog => LazyCatalog.Value;

    /// <inheritdoc />
    public IEnumerable<MeshNode> GetStaticNodes()
    {
        // Read-only, world-readable policy for the Skill namespace — the skill catalog is public, same
        // as Agent/Harness. On the SYNCED path this _Policy MUST be imported (SkillStaticRepoSource),
        // else the partition has no read policy and the skills are unreadable → the chat finds no skills
        // (the Harness wedge, prod 2026-06-15). The write caps keep the built-in skills unmodifiable.
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

        foreach (var node in LazyCatalog.Value.Nodes)
            yield return node;
    }

    private static SkillCatalog LoadCatalog()
    {
        var assembly = typeof(BuiltInSkillProvider).Assembly;
        // Resource names dot-separate path segments: Data/Skill/agent.md → {asm}.Data.Skill.agent.md
        var skillPrefix = $"{assembly.GetName().Name}.Data.{SkillNodeType.RootNamespace}.";

        var nodes = new List<MeshNode>();
        var failures = new List<AiContentLoadFailure>();

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
                    System.IO.Path.GetFileNameWithoutExtension(file),
                    // The RELATIVE path, so a file in a subdirectory is still findable from the message.
                    $"{SkillNodeType.RootNamespace}/"
                    + System.IO.Path.GetRelativePath(skillDir, file).Replace('\\', '/'), failures);
                if (node != null) nodes.Add(node);
            }
            return new SkillCatalog(nodes.ToArray(), failures);
        }

        // Fallback: EMBEDDED resources — the offline default never loses its skills.
        foreach (var resourceName in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith(skillPrefix, StringComparison.Ordinal)
                                 && n.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                     .Order())
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                failures.Add(new AiContentLoadFailure(resourceName, "embedded resource has no stream"));
                continue;
            }
            using var reader = new StreamReader(stream);
            var node = ParseSkillNode(reader.ReadToEnd(), ResourceNameToId(resourceName, skillPrefix),
                resourceName, failures);
            if (node != null) nodes.Add(node);
        }
        return new SkillCatalog(nodes.ToArray(), failures);
    }

    // The one skill md↔node conversion lives in SkillMarkdown, shared with the sync-back writer
    // (AiContentDiskWriter serializes via SkillMarkdown.Serialize) — so read and write can never drift.
    //
    // 🚨 A file that does not parse is SKIPPED — throwing here would fail mesh startup, and one bad
    // skill must never stop the host. But skipping SILENTLY is the defect this records against: the
    // catalog quietly loses an entry, every hardcoded-expectation test stays green, and the author sees
    // only "my skill doesn't appear". So the failure is recorded (the guard test fails RED naming the
    // file) AND written to stderr — the sink that reaches pod logs from this static startup path,
    // exactly as BuiltInAgentProvider does for the agent half of the same section.
    // <c>internal</c> so the guard test can prove the RECORDING works. Asserting only that the shipped
    // catalog has no failures is one level of the same blind spot: if this method stopped recording,
    // the failure list would be permanently empty and that assertion would pass forever.
    internal static MeshNode? ParseSkillNode(string content, string id, string file,
        List<AiContentLoadFailure> failures)
    {
        var node = SkillMarkdown.TryParse(content, id, out var error);
        if (node != null) return node;

        var failure = new AiContentLoadFailure(file, error ?? "could not be parsed as a skill");
        failures.Add(failure);
        Console.Error.WriteLine($"[BuiltInSkillProvider] Skipping '{failure.File}': {failure.Reason}");
        return null;
    }

    private static string ResourceNameToId(string resourceName, string skillPrefix)
    {
        var rest = resourceName[skillPrefix.Length..]; // e.g. "agent.md"
        var lastDot = rest.LastIndexOf('.');           // strip the ".md" extension
        return lastDot > 0 ? rest[..lastDot] : rest;
    }

}
