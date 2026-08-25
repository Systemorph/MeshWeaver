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
/// whole lambda (extension methods, layout-area registrations). It is a floor, not a compiler.</para>
///
/// <para><b>An explicit <c>sources</c> list no longer means "skip".</b> It used to, and a skip
/// renders identically to a pass — the exact shape AGENTS.md forbids under "A gate NEVER tests its
/// own inputs", one level in: adding a <c>sources</c> entry silently removed the whole NodeType
/// from this guard's coverage. The two shapes anyone actually authors ARE resolvable from the
/// filesystem — <c>namespace:Source scope:subtree</c> is the node's own folder and
/// <c>[name=]@Some/Node/Path</c> is a file or subtree in the same content tree — so both are
/// resolved. A <c>sources</c> entry that points at nothing therefore FAILS here rather than
/// disappearing, and so does an entry whose SHAPE this cannot resolve: it is reported, asking to
/// be taught, because "skip only the odd entry" is the same silent-coverage-drop with a smaller
/// blast radius. Nothing about a NodeType's <c>sources</c> list can remove it from this guard.</para>
/// </summary>
public class ShippedNodeTypeSourceResolutionTest
{
    /// <summary>Matches <c>WithContentType&lt;Foo&gt;()</c> / <c>WithContentType&lt;Ns.Foo&gt;()</c>.</summary>
    private static readonly Regex ContentTypePattern =
        new(@"WithContentType\s*<\s*([\w.]+)\s*>", RegexOptions.Compiled);

    /// <summary>Matches a C# type declaration well enough to harvest the declared name.</summary>
    private static readonly Regex TypeDeclarationPattern =
        new(@"\b(?:record|class|struct|enum|interface)\s+(\w+)", RegexOptions.Compiled);

    /// <summary><c>CodeQueryResolver.ParseName</c>'s label regex — the <c>name=</c> prefix.</summary>
    private static readonly Regex NameLabelPattern =
        new(@"^[A-Za-z0-9_][A-Za-z0-9_.\-]*$", RegexOptions.Compiled);

    /// <summary>
    /// <c>namespace:&lt;rel&gt;</c> where <c>&lt;rel&gt;</c> carries no <c>/</c> — the relative form
    /// <c>CodeQueryResolver.RebaseRelativeNamespace</c> rebases onto the NodeType's own path.
    /// </summary>
    private static readonly Regex RelativeNamespacePattern =
        new(@"^namespace:([A-Za-z0-9_.\-]+)(\s|$)", RegexOptions.Compiled);

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
            // 🚨 An entry this cannot resolve is REPORTED, not skipped. Skipping is how the guard
            // stopped covering a NodeType silently in the first place, and "skip only the odd
            // case" is the same failure with a smaller blast radius.
            foreach (var entry in nodeType.UnresolvedSources)
                offenders.Add($"  {nodeType.RelativePath}: unresolvable `sources` entry {entry}");

            var own = new HashSet<string>(StringComparer.Ordinal);
            foreach (var sourceRoot in nodeType.SourceRoots)
                own.UnionWith(TypeNamesDeclaredIn(sourceRoot));
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
            + "An `unresolvable sources entry` line means something else: the entry is a shape this "
            + "guard cannot resolve on disk, so it is reported rather than skipped — either author "
            + "it as `namespace:<name> …` or `@<node path>`, or teach ResolveSourceRoots the shape. "
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

        // 🚨 The `sources` path must be EXERCISED, not merely present. Northwind/Product is the
        // shipped NodeType that declares one (its dimension records live in a sibling node's
        // Source/, #1786), so it is the witness that a declared `sources` list still gets checked
        // — if it silently dropped out again, this fails instead of the coverage vanishing.
        var withSources = nodeTypes.SingleOrDefault(
            n => n.RelativePath.EndsWith("Northwind/Product.json", StringComparison.Ordinal));
        Assert.True(withSources.RelativePath is not null,
            "Northwind/Product declares a `sources` list and must still be ENUMERATED — a NodeType "
            + "that declares sources used to be skipped, and a skipped check is indistinguishable "
            + "from a passed one.");
        Assert.True(withSources.SourceRoots.Count >= 2,
            $"expected Northwind/Product's `sources` entries to resolve to its own Source/ folder "
            + $"plus the shared dimension records; resolved {withSources.SourceRoots.Count} root(s)");
        var acrossRoots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in withSources.SourceRoots)
            acrossRoots.UnionWith(TypeNamesDeclaredIn(r));
        Assert.Contains("ProductContent", acrossRoots);   // its own Source/
        Assert.Contains("Supplier", acrossRoots);         // an `@`-shorthand entry, resolved
        Assert.Contains("Category", acrossRoots);

        // A framework content type must resolve …
        Assert.Contains("MarkdownContent", framework);
        // … and no shipped NodeType may carry a `sources` entry this cannot resolve. Stated as its
        // own assertion because the failure MODE differs from a missing type: an unresolvable entry
        // is the guard admitting it does not understand the input, and admitting it out loud is the
        // whole point — the previous version answered that case by dropping the NodeType.
        Assert.All(nodeTypes, n => Assert.Empty(n.UnresolvedSources));
        // … while a type that only ever lived in a sample's Source/ must NOT be mistaken for one,
        // or the invariant above would wave through exactly the #1786 shape.
        Assert.DoesNotContain("LineOfBusiness", framework);
        Assert.DoesNotContain("TransactionMapping", framework);
    }

    // ── the scans ────────────────────────────────────────────────────────────────────────

    private readonly record struct ShippedNodeType(
        string RelativePath,
        IReadOnlyList<string> SourceRoots,
        IReadOnlyCollection<string> ContentTypes,
        IReadOnlyList<string> UnresolvedSources);

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

    /// <summary>
    /// Type names declared at <paramref name="pathOrDirectory"/> — a directory (scanned as a
    /// subtree) or a single <c>.cs</c> file, which is what an <c>@Some/Node/Path</c> source entry
    /// naming one Code node resolves to. A path that exists as neither contributes nothing; the
    /// missing type is then reported by the invariant, which is the correct outcome — a
    /// <c>sources</c> entry pointing at nothing is exactly the #1786 defect.
    /// </summary>
    private static HashSet<string> TypeNamesDeclaredIn(string pathOrDirectory)
    {
        if (Directory.Exists(pathOrDirectory))
            return TypeNamesDeclaredUnder(pathOrDirectory);

        var names = new HashSet<string>(StringComparer.Ordinal);
        var asFile = pathOrDirectory.EndsWith(".cs", StringComparison.Ordinal)
            ? pathOrDirectory
            : pathOrDirectory + ".cs";
        if (File.Exists(asFile))
        {
            foreach (Match m in TypeDeclarationPattern.Matches(File.ReadAllText(asFile)))
                names.Add(m.Groups[1].Value);
        }
        return names;
    }

    /// <summary>
    /// The content-tree root a node file sits in — <c>samples/Graph/Data</c>,
    /// <c>src/MeshWeaver.Documentation/Data</c>, a node repo — derived by walking the node's own
    /// <c>path</c> back off its directory, so an absolute source reference resolves in whichever
    /// tree the node belongs to without this guard hard-coding the list of trees.
    /// </summary>
    private static string? TreeRootOf(string file, JsonElement node)
    {
        if (!node.TryGetProperty("path", out var pathElement)
            || pathElement.ValueKind != JsonValueKind.String)
            return null;
        var nodePath = pathElement.GetString();
        if (string.IsNullOrWhiteSpace(nodePath))
            return null;

        // The directory holding the file corresponds to the node path minus its LAST segment
        // (X.json lives beside its siblings), or the whole path for an index.json root.
        var segments = nodePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var levels = Path.GetFileName(file).Equals("index.json", StringComparison.Ordinal)
            ? segments.Length
            : segments.Length - 1;

        var dir = Path.GetDirectoryName(file);
        for (var i = 0; i < levels && dir is not null; i++)
            dir = Path.GetDirectoryName(dir);
        return dir;
    }

    /// <summary>
    /// Resolves a NodeType's <c>sources</c> entries to filesystem roots, mirroring
    /// <c>CodeQueryResolver.ParseName</c> and <c>CodeQueryResolver.Expand</c> for the two shapes
    /// anyone authors.
    ///
    /// <para>🚨 An entry it cannot decide goes into <paramref name="unresolved"/> and is REPORTED,
    /// never skipped. Returning "skip this NodeType" was the original defect; a narrower version of
    /// it — skip only when one entry is odd — is the same defect with a smaller blast radius, and
    /// it fails in the direction that hides things. Reporting means a `sources` shape this does not
    /// understand fails the build asking to be taught, instead of quietly taking the whole NodeType
    /// out of coverage.</para>
    /// </summary>
    private static IReadOnlyList<string> ResolveSourceRoots(
        JsonElement sources, string nodeFolder, string? treeRoot,
        string selfPath, List<string> unresolved)
    {
        var roots = new List<string>();
        foreach (var entry in sources.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String)
            {
                unresolved.Add($"a non-string entry ({entry.ValueKind}) — `sources` holds query strings");
                continue;
            }
            var raw = (entry.GetString() ?? string.Empty).Trim();

            // ── CodeQueryResolver.ParseName, exactly: TrimEnd the candidate name, and require a
            // non-empty body — so `shared =@X` is a NAMED entry (the space belongs to the label,
            // not the query) and a bare `shared=` is not one. Getting either wrong silently drops
            // an entry the compiler would have honoured (Copilot review, #2278).
            var text = raw;
            var eq = text.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0)
            {
                var candidate = text[..eq].TrimEnd();
                var rest = text[(eq + 1)..].Trim();
                if (rest.Length > 0 && NameLabelPattern.IsMatch(candidate))
                    text = rest;
            }

            // `$self` is the NodeType's own path, substituted before anything else.
            text = text.Replace("$self", selfPath, StringComparison.Ordinal).Trim();

            // `@Node/Path` / `@@Node/Path` — a single Code node or its subtree, absolute in the
            // node's own content tree. `TrimStart('@').TrimStart()` mirrors Expand.
            if (text.StartsWith('@'))
            {
                var target = text.TrimStart('@').TrimStart();
                if (target.Length == 0)
                {
                    unresolved.Add($"'{raw}' — an `@` with nothing after it selects no node");
                    continue;
                }
                if (target.Contains(':', StringComparison.Ordinal))
                {
                    unresolved.Add($"'{raw}' — an `@` carrying a full query, which needs the mesh to answer");
                    continue;
                }
                if (treeRoot is null)
                {
                    unresolved.Add($"'{raw}' — the node file declares no `path`, so its content tree is unknown");
                    continue;
                }
                roots.Add(Path.Combine(treeRoot, target.Replace('/', Path.DirectorySeparatorChar)));
                continue;
            }

            // `namespace:<rel> scope:subtree` — relative (no '/') means the node's own folder.
            var match = RelativeNamespacePattern.Match(text);
            if (match.Success)
            {
                roots.Add(Path.Combine(nodeFolder, match.Groups[1].Value));
                continue;
            }

            unresolved.Add(
                $"'{raw}' — not a relative `namespace:<name> …` and not `@<node path>`, so it is a "
                + $"mesh query only a running mesh can answer");
        }
        return roots;
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

                // 🚨 An explicit `sources` list used to SKIP the node outright — and a skip is
                // indistinguishable from a pass (AGENTS.md → "A gate NEVER tests its own inputs").
                // The shapes actually authored ARE resolvable on disk: `namespace:Source
                // scope:subtree` is the node's own folder, and `[name=]@Some/Node/Path` is a file
                // or directory under the same content tree. Resolve those, and skip ONLY on an
                // entry this cannot decide — so declaring `sources` narrows the coverage to the
                // entry that earned it, instead of dropping the whole NodeType silently.
                IReadOnlyList<string> roots;
                List<string> unresolved = [];
                if (content.TryGetProperty("sources", out var sources)
                    && sources.ValueKind == JsonValueKind.Array
                    && sources.GetArrayLength() > 0)
                {
                    var selfPath = doc.RootElement.TryGetProperty("path", out var p)
                                   && p.ValueKind == JsonValueKind.String
                        ? p.GetString() ?? string.Empty
                        : string.Empty;
                    roots = ResolveSourceRoots(
                        sources, folder, TreeRootOf(file, doc.RootElement), selfPath, unresolved);
                }
                else
                {
                    roots = [Path.Combine(folder, "Source")];
                }

                yield return new ShippedNodeType(
                    Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/'),
                    roots,
                    named,
                    unresolved);
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
