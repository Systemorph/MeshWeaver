using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MeshWeaver.Plugin.Build;

/// <summary>
/// Discovers a plugin's compilation units in a repo checkout and resolves each one's source
/// closure from the <c>sources</c> queries declared on the owning node — the same closure the
/// portal's compiler assembles at runtime, resolved here against the filesystem instead of the mesh.
/// </summary>
public static partial class PluginUnitResolver
{
    /// <summary>
    /// An include in any of the THREE forms a node's <c>sources</c> may declare:
    /// <list type="bullet">
    ///   <item><description>path — <c>shared=@Store/Coupon/Source</c></description></item>
    ///   <item><description>query — <c>shared=namespace:UWDeepfield/Source scope:subtree</c></description></item>
    ///   <item><description>ALIASED query — <c>client=namespace:UWDeepfield/ReinsuranceClient/Source scope:subtree</c></description></item>
    /// </list>
    ///
    /// <para>🚨 Every one of these is a SILENT under-resolution when unmatched: the include is
    /// skipped, the unit compiles without it, and Roslyn reports a thoroughly convincing CS0246 /
    /// CS0103 on a symbol that does exist. Matching only the path form cost UWDeepfield its
    /// 55-file root across six units; matching only <c>shared=</c> cost IndustryNewsFeed its
    /// <c>client=</c> include. The prefix is an arbitrary author-chosen alias — <c>shared</c>,
    /// <c>news</c>, <c>client</c> — so it must not be part of the match.</para>
    ///
    /// <para>An unresolvable target is dropped deliberately: a bare <c>namespace:Source
    /// scope:subtree</c> IS the unit's own directory, already first in the closure, and it will
    /// not resolve against any repo root.</para>
    /// </summary>
    [GeneratedRegex(@"^(?:[A-Za-z_][A-Za-z0-9_]*=)?(?:@|(?:namespace|path):)(?<target>[A-Za-z0-9/._-]+)")]
    private static partial Regex IncludeForm();

    /// <summary>
    /// Enumerates every compilation unit under <paramref name="pluginDirectory"/>.
    /// </summary>
    /// <param name="pluginDirectory">A plugin root in a repo checkout (the directory holding <c>index.json</c>).</param>
    /// <param name="repoRoots">Checkout roots a <c>shared=</c> mesh path may resolve against — plugins
    /// reference each other across repos, so the search is over all of them, first hit wins.</param>
    /// <returns>The units, ordered by node path for a stable build order.</returns>
    public static ImmutableArray<PluginUnit> Resolve(string pluginDirectory, IReadOnlyList<string> repoRoots)
    {
        var pluginName = Path.GetFileName(pluginDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var units = new List<PluginUnit>();

        foreach (var sourceDir in Directory
                     .EnumerateDirectories(pluginDirectory, "Source", SearchOption.AllDirectories)
                     .Where(d => !d.Contains($"{Path.DirectorySeparatorChar}.worktrees{Path.DirectorySeparatorChar}",
                         StringComparison.Ordinal))
                     .OrderBy(d => d, StringComparer.Ordinal))
        {
            var owner = Path.GetDirectoryName(sourceDir)!;
            var nodePath = owner == pluginDirectory.TrimEnd(Path.DirectorySeparatorChar)
                ? pluginName
                : $"{pluginName}/{Path.GetRelativePath(pluginDirectory, owner).Replace(Path.DirectorySeparatorChar, '/')}";

            var declared = ReadDeclaredQueries(owner, "sources");
            var declaredTests = ReadDeclaredQueries(owner, "tests");
            units.Add(new PluginUnit(
                nodePath,
                sourceDir,
                BuildClosure(sourceDir, declared.AddRange(declaredTests), repoRoots),
                declared));
        }

        return [.. units];
    }

    /// <summary>
    /// Reads a query array (<c>sources</c> or <c>tests</c>) off the node that owns a <c>Source/</c>
    /// directory. The node file is either <c>&lt;Owner&gt;/index.json</c> or <c>&lt;Owner&gt;.json</c>
    /// beside it.
    /// </summary>
    private static ImmutableArray<string> ReadDeclaredQueries(string ownerDirectory, string property)
    {
        foreach (var candidate in new[]
                 {
                     Path.Combine(ownerDirectory, "index.json"),
                     ownerDirectory + ".json",
                 })
        {
            if (!File.Exists(candidate))
                continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(candidate));
                if (doc.RootElement.TryGetProperty("content", out var content)
                    && content.TryGetProperty(property, out var queries)
                    && queries.ValueKind == JsonValueKind.Array)
                {
                    return [.. queries.EnumerateArray()
                        .Select(e => e.GetString())
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Select(s => s!)];
                }
            }
            catch (JsonException)
            {
                // A malformed node file is the author's problem, not a resolution failure — the
                // unit still builds from its own Source and Roslyn reports whatever is missing.
            }
            return [];
        }
        return [];
    }

    /// <summary>
    /// Own <c>Source/</c> plus every resolvable <c>shared=</c> include, de-duplicated, order preserved.
    /// A <c>namespace:Source scope:subtree</c> query needs no resolution — it IS the unit's own
    /// directory, already first in the closure.
    /// </summary>
    private static ImmutableArray<string> BuildClosure(
        string sourceDir, ImmutableArray<string> declared, IReadOnlyList<string> repoRoots)
    {
        var closure = new List<string> { sourceDir };

        // 🚨 Tests are part of the COMPILATION, not a separate project. A NodeType's default
        // `tests` query is `namespace:Test scope:subtree`, and the runtime folds it into the same
        // assembly — the live Store/Plugin node's compiledSources lists `Store/Plugin/Test/*`
        // right beside its Source. Omit it and production code that references a test type (an
        // area rendering its own test results, as IndustryNewsFeed does) fails CS0103 in CI while
        // compiling perfectly in the portal.
        var testDir = Path.Combine(Path.GetDirectoryName(sourceDir)!, "Test");
        if (Directory.Exists(testDir))
            closure.Add(testDir);

        foreach (var query in declared)
        {
            var match = IncludeForm().Match(query);
            if (!match.Success)
                continue;

            var target = match.Groups["target"].Value.TrimEnd('.');
            var resolved = repoRoots
                .Select(root => Path.Combine(root, target.Replace('/', Path.DirectorySeparatorChar)))
                .FirstOrDefault(Directory.Exists);

            if (resolved is not null && !closure.Contains(resolved, StringComparer.Ordinal))
                closure.Add(resolved);
        }

        return [.. closure];
    }
}
