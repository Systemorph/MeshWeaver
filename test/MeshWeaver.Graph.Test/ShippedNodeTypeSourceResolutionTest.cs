using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using MeshWeaver.Graph.Configuration;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 A SHIPPED NodeType MUST NOT NAME A CONTENT TYPE ITS OWN SOURCES CANNOT PROVIDE — the
/// configuration lambda and the <c>Source/</c> folder are authored in two different places, and
/// nothing but a live compile has ever checked that the second one satisfies the first.
///
/// <para><b>What went wrong (#1786, part two).</b> <see cref="ShippedNodeTypeStateTest"/> removed
/// the imported compile state that made these types <em>unrecoverable</em>. That let them compile
/// for real on each deployment — and six of them then failed for real, with the compiler saying so
/// in as many words:</para>
/// <code>
/// CS0246 The type or namespace name 'LineOfBusiness' could not be found
/// --- Source discovery ---
/// Executed source queries (2):
///   - namespace:…/AmericasIns/LineOfBusiness/Source scope:subtree nodeType:Code
/// Matched Code nodes (0):
///   (none) — the configuration lambda cannot reference types because no source files were included.
/// </code>
/// <para>The six <c>FutuRe/{AmericasIns,EuropeRe,AsiaRe}/{LineOfBusiness,TransactionMapping}</c>
/// NodeTypes were copy-pasted from their parents — the same <c>WithContentType&lt;LineOfBusiness&gt;()</c>
/// lambda, but none of the parent's <c>Source/*.cs</c>. They were measured parked at
/// <c>CompilationStatus.Error</c> on memex.meshweaver.cloud, and not one node anywhere was typed by
/// them, so they were pure dead weight that could only ever park. They are now ordinary Markdown
/// nodes. Three more (<c>samples/Graph/Data/Doc/**</c>) were source-less DUPLICATES of nodes the
/// documentation tree already authors correctly, and could only clobber the good copies.</para>
///
/// <para><b>Why a static check and not "the compile will catch it".</b> The compile only runs on a
/// deployment that imports the tree, and a NodeType that fails there does not fail loudly: the
/// watcher PARKS it and serves the cached error without retrying, so every page backed by it is
/// broken while CI stays green. This runs in milliseconds and fails the PR that introduces one.</para>
///
/// <para><b>Scope, stated honestly.</b> This checks the one reference a NodeType always makes
/// explicitly — the content type in <c>WithContentType&lt;T&gt;()</c>. It does NOT type-check the
/// whole lambda (extension methods, layout-area registrations), and it skips a NodeType that
/// declares an explicit <c>sources</c> list, whose mesh queries cannot be resolved from the
/// filesystem alone. It is a floor, not a compiler.</para>
/// </summary>
public class ShippedNodeTypeSourceResolutionTest
{
    /// <summary>Matches <c>WithContentType&lt;Foo&gt;()</c> / <c>WithContentType&lt;Ns.Foo&gt;()</c>.</summary>
    private static readonly Regex ContentTypePattern =
        new(@"WithContentType\s*<\s*([\w.]+)\s*>", RegexOptions.Compiled);

    /// <summary>Matches a C# type declaration well enough to harvest the declared name.</summary>
    private static readonly Regex TypeDeclarationPattern =
        new(@"\b(?:record|class|struct|enum|interface)\s+(\w+)", RegexOptions.Compiled);

    /// <summary>
    /// 🚨 THE INVARIANT. Every content type a shipped NodeType names must be declared either in
    /// that NodeType's own <c>Source/</c> folder or by the framework itself. Fails on the tree as
    /// it stood before the fix, naming all nine offenders.
    /// </summary>
    [Fact]
    public void NoShippedNodeTypeNamesAContentTypeItsSourcesCannotProvide()
    {
        var root = FindRepoRoot();
        var framework = FrameworkTypeNames(root);

        var offenders = new List<string>();
        foreach (var nodeType in EnumerateShippedNodeTypes(root))
        {
            var own = TypeNamesDeclaredUnder(Path.Combine(nodeType.SourceFolder, "Source"));
            var missing = nodeType.ContentTypes
                .Where(n => !own.Contains(n) && !framework.Contains(n))
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
            if (missing.Count > 0)
                offenders.Add($"  {nodeType.RelativePath}: {string.Join(", ", missing)}");
        }

        Assert.True(offenders.Count == 0,
            "A shipped NodeType names a content type that neither its own Source/ folder nor the "
            + "framework declares, so its configuration lambda CANNOT compile on any deployment "
            + "that imports it. The compile does not fail loudly — CompileWatcher parks the type "
            + "and serves the cached error without retrying, so every page backed by it is broken "
            + "while CI stays green (#1786). Either give the NodeType a Source/ folder declaring "
            + "the type, or — if nothing is actually typed by it — stop shipping it as a NodeType. "
            + "Offending files:\n"
            + string.Join("\n", offenders));
    }

    /// <summary>
    /// The guard is only as good as its two scans: if either silently matched nothing, the check
    /// above would pass on an empty set. Pins both, plus the discrimination that does the work —
    /// a framework content type resolves, a sample-tree one does not.
    /// </summary>
    [Fact]
    public void TheScansAreNotVacuous()
    {
        var root = FindRepoRoot();
        var framework = FrameworkTypeNames(root);
        var nodeTypes = EnumerateShippedNodeTypes(root).ToList();

        Assert.True(framework.Count > 500,
            $"expected the framework type scan to find the src/ tree's types; found {framework.Count}");
        Assert.True(nodeTypes.Count > 10,
            $"expected to find the shipped NodeType files; found {nodeTypes.Count}");
        Assert.Contains(nodeTypes, n => n.ContentTypes.Count > 0);

        // A framework content type must resolve …
        Assert.Contains("MarkdownContent", framework);
        // … while a type that only ever lived in a sample's Source/ must NOT be mistaken for one,
        // or the invariant above would wave through exactly the #1786 shape.
        Assert.DoesNotContain("LineOfBusiness", framework);
        Assert.DoesNotContain("TransactionMapping", framework);
    }

    // ── the scans ────────────────────────────────────────────────────────────────────────

    private readonly record struct ShippedNodeType(
        string RelativePath, string SourceFolder, IReadOnlyCollection<string> ContentTypes);

    /// <summary>
    /// Type names the dynamic compile can see without any Source node — the framework assemblies,
    /// harvested from <c>src/</c>. <c>src/MeshWeaver.Documentation/Data</c> is EXCLUDED: it is a
    /// node repo that happens to live under src/, and its per-NodeType Source folders would
    /// otherwise leak into the framework set and vouch for types no framework assembly exports.
    /// </summary>
    private static IReadOnlySet<string> FrameworkTypeNames(string root)
    {
        var names = TypeNamesDeclaredUnder(Path.Combine(root, "src"));
        names.ExceptWith(
            TypeNamesDeclaredUnder(Path.Combine(root, "src", "MeshWeaver.Documentation", "Data")));
        return names;
    }

    private static HashSet<string> TypeNamesDeclaredUnder(string directory)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (!Directory.Exists(directory))
            return names;
        foreach (var file in Walk(directory, "*.cs"))
        {
            foreach (Match m in TypeDeclarationPattern.Matches(File.ReadAllText(file)))
                names.Add(m.Groups[1].Value);
        }
        return names;
    }

    private static IEnumerable<ShippedNodeType> EnumerateShippedNodeTypes(string root)
    {
        foreach (var file in Walk(root, "*.json"))
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(File.ReadAllText(file));
            }
            catch (JsonException)
            {
                continue;   // not a node file — not this guard's business
            }

            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object
                    || !doc.RootElement.TryGetProperty("content", out var content)
                    || content.ValueKind != JsonValueKind.Object
                    || !content.TryGetProperty("$type", out var type)
                    || type.ValueKind != JsonValueKind.String
                    || !string.Equals(type.GetString(), nameof(NodeTypeDefinition), StringComparison.Ordinal))
                    continue;

                // An explicit sources list is a set of MESH queries — not resolvable from the
                // filesystem, so this guard has nothing trustworthy to say about it.
                if (content.TryGetProperty("sources", out var sources)
                    && sources.ValueKind == JsonValueKind.Array
                    && sources.GetArrayLength() > 0)
                    continue;

                if (!content.TryGetProperty("configuration", out var configuration)
                    || configuration.ValueKind != JsonValueKind.String)
                    continue;

                var named = ContentTypePattern.Matches(configuration.GetString() ?? string.Empty)
                    .Select(m => m.Groups[1].Value.Split('.')[^1])
                    .ToHashSet(StringComparer.Ordinal);
                if (named.Count == 0)
                    continue;

                // X.json's sources live in the sibling folder X/; a root authored as index.json
                // owns the directory it sits in.
                var directory = Path.GetDirectoryName(file)!;
                var folder = Path.GetFileName(file).Equals("index.json", StringComparison.Ordinal)
                    ? directory
                    : Path.Combine(directory, Path.GetFileNameWithoutExtension(file));

                yield return new ShippedNodeType(
                    Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/'),
                    folder,
                    named);
            }
        }
    }

    /// <summary>
    /// Pruning walk — mirrors <see cref="ShippedNodeTypeStateTest"/>: never descends into build
    /// output, node_modules, or the sibling agent worktrees under <c>.claude/worktrees/</c>, each
    /// of which is a full checkout on somebody else's branch.
    /// </summary>
    private static IEnumerable<string> Walk(string root, string pattern)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            foreach (var file in Directory.EnumerateFiles(dir, pattern).OrderBy(f => f, StringComparer.Ordinal))
                yield return file;
            foreach (var child in Directory.EnumerateDirectories(dir))
            {
                if (!IsPruned(Path.GetFileName(child)))
                    stack.Push(child);
            }
        }
    }

    private static bool IsPruned(string directoryName) =>
        directoryName.StartsWith('.')
        || directoryName.Equals("bin", StringComparison.OrdinalIgnoreCase)
        || directoryName.Equals("obj", StringComparison.OrdinalIgnoreCase)
        || directoryName.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
        || directoryName.Equals("artifacts", StringComparison.OrdinalIgnoreCase);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        return dir?.FullName
               ?? throw new InvalidOperationException(
                   "Could not locate the repo root (MeshWeaver.slnx) from " + AppContext.BaseDirectory);
    }
}
