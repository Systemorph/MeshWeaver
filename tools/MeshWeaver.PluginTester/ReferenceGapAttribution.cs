using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;

namespace MeshWeaver.PluginTester;

/// <summary>
/// Names the REFERENCE-SET gap behind a NodeType's CS0234 / CS0246 — so a bake that could not see
/// an assembly the platform ships stops reading as a content defect.
///
/// <para><b>The failure this exists for.</b> Roslyn reports a missing reference as
/// <c>CS0234: The type or namespace name 'Maps' does not exist in the namespace 'MeshWeaver'</c>
/// and then one <c>CS0246 … 'MapControl' could not be found</c> per use. Every line names the
/// CONTENT. On 2026-09-02 that is what the platform release wave died on: four NodeTypes RED in
/// the Plugins bake, the source unchanged for weeks, the cause a reference set (the tester's
/// <c>/app</c>) that lacked <c>MeshWeaver.Maps.dll</c> while every portal carried it — and nothing
/// in the verdict said so (#3022). The same shape had already cost the fleet a day when
/// <c>MeshWeaver.AI</c> became a module (#2563).</para>
///
/// <para><b>What it says, and what it refuses to say.</b> A missing namespace is looked up in
/// two places: the reference set itself (a namespace some reference DOES declare is a genuine
/// content error — the type is what is missing — and this adds nothing), and the platform host's
/// <c>modules/</c> tree — assemblies the image SHIPS but which are not in <c>/app</c> and therefore
/// not in any reference set unless composed with <c>--module</c>. A hit there is named as exactly
/// that: <c>reference set lacks &lt;assembly&gt; (portal-shipped, not composed: modules/…)</c>. A
/// <c>MeshWeaver.*</c> namespace found nowhere is named too, with the two causes it can have
/// (a registry module that was not composed; a bake that did not compile against the platform's
/// <c>/app</c>). Nothing is guessed from a bare type name alone: a CS0246 contributes an
/// attribution only when a shipped-but-not-composed assembly declares that exact type.</para>
///
/// <para>Built lazily, once per bake, only when the first such diagnostic appears — metadata
/// reads over the reference set's files and the host's <c>modules/**</c>, nothing loaded.</para>
/// </summary>
internal sealed partial class ReferenceGapAttribution
{
    /// <summary>An assembly the platform host ships outside <c>/app</c>, i.e. one no reference set holds
    /// unless it is composed.</summary>
    /// <param name="Name">The assembly simple name.</param>
    /// <param name="RelativePath">Its path relative to the host's application directory.</param>
    internal sealed record ShippedAssembly(string Name, string RelativePath);

    private readonly string appDirectory;
    private readonly ImmutableHashSet<string> referencedNamespaces;
    private readonly ImmutableDictionary<string, ShippedAssembly> shippedByNamespace;
    private readonly ImmutableDictionary<string, ShippedAssembly> shippedByType;

    private ReferenceGapAttribution(
        string appDirectory,
        ImmutableHashSet<string> referencedNamespaces,
        ImmutableDictionary<string, ShippedAssembly> shippedByNamespace,
        ImmutableDictionary<string, ShippedAssembly> shippedByType)
    {
        this.appDirectory = appDirectory;
        this.referencedNamespaces = referencedNamespaces;
        this.shippedByNamespace = shippedByNamespace;
        this.shippedByType = shippedByType;
    }

    /// <summary>
    /// Indexes the reference set's declared namespaces and the platform host's
    /// shipped-but-not-composed assemblies (<c>&lt;app&gt;/modules/**/*.dll</c>).
    /// </summary>
    /// <param name="references">The metadata references the bake compiled against.</param>
    /// <param name="appDirectory">The platform host's application directory.</param>
    public static ReferenceGapAttribution Create(
        IEnumerable<MetadataReference> references, string appDirectory)
    {
        ArgumentNullException.ThrowIfNull(references);
        ArgumentException.ThrowIfNullOrWhiteSpace(appDirectory);
        var app = Path.GetFullPath(appDirectory);

        var referencePaths = references
            .OfType<PortableExecutableReference>()
            .Select(r => r.FilePath)
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => Path.GetFullPath(p!))
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

        var referenced = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        foreach (var path in referencePaths)
            foreach (var (ns, _) in DeclaredTypes(path))
                AddWithPrefixes(referenced, ns);

        var byNamespace = ImmutableDictionary.CreateBuilder<string, ShippedAssembly>(StringComparer.Ordinal);
        var byType = ImmutableDictionary.CreateBuilder<string, ShippedAssembly>(StringComparer.Ordinal);
        var modulesRoot = Path.Combine(app, "modules");
        if (Directory.Exists(modulesRoot))
        {
            foreach (var dll in Directory.EnumerateFiles(modulesRoot, "*.dll", SearchOption.AllDirectories)
                         .OrderBy(p => p, StringComparer.Ordinal))
            {
                if (referencePaths.Contains(Path.GetFullPath(dll)))
                    continue;
                var shipped = new ShippedAssembly(
                    Path.GetFileNameWithoutExtension(dll), Path.GetRelativePath(app, dll));
                foreach (var (ns, type) in DeclaredTypes(dll))
                {
                    if (ns.Length > 0)
                        byNamespace.TryAdd(ns, shipped);
                    byType.TryAdd(type, shipped);
                }
            }
        }

        return new ReferenceGapAttribution(
            app, referenced.ToImmutable(), byNamespace.ToImmutable(), byType.ToImmutable());
    }

    /// <summary>Whether a failure text carries a diagnostic this index could explain — the cheap
    /// pre-check that keeps the index from being built for a failure that is not reference-shaped.</summary>
    /// <param name="compileError">The failure message.</param>
    public static bool MayExplain(string compileError) =>
        compileError.Contains("CS0234", StringComparison.Ordinal)
        || compileError.Contains("CS0246", StringComparison.Ordinal);

    /// <summary>
    /// The attribution lines for one compile failure's text, joined for appending to the verdict,
    /// or null when the diagnostics name nothing this index can account for.
    /// </summary>
    /// <param name="compileError">The failure message (the Roslyn diagnostics, one per line).</param>
    public string? Explain(string compileError)
    {
        ArgumentNullException.ThrowIfNull(compileError);
        var lines = new List<string>();
        var named = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match match in MissingNamespace().Matches(compileError))
        {
            var ns = $"{match.Groups["parent"].Value}.{match.Groups["name"].Value}";
            // A namespace the reference set declares is not a gap: the TYPE is what the content got
            // wrong, and the compiler's own line already says so.
            if (referencedNamespaces.Contains(ns))
                continue;
            if (FindShippedByNamespace(ns) is { } shipped)
            {
                if (named.Add(shipped.Name))
                    lines.Add(LacksShipped(shipped, $"it declares namespace '{ns}'"));
            }
            else if (named.Add("namespace:" + ns))
            {
                lines.Add(
                    $"no assembly in the reference set declares namespace '{ns}' — it is neither in "
                    + $"'{appDirectory}' nor under its modules/. If '{ns}' is a MODULE, compose it "
                    + "(--module / registry-modules); if the platform ships it, this bake did not "
                    + "compile against the platform's /app (--app).");
            }
        }

        foreach (Match match in MissingType().Matches(compileError))
        {
            var type = match.Groups["name"].Value;
            if (shippedByType.TryGetValue(type, out var shipped) && named.Add(shipped.Name))
                lines.Add(LacksShipped(shipped, $"it declares type '{type}'"));
        }

        return lines.Count == 0 ? null : string.Join("\n   ", lines);
    }

    /// <summary>The one phrasing a reader greps for, whichever diagnostic led here.</summary>
    private static string LacksShipped(ShippedAssembly shipped, string evidence) =>
        $"reference set lacks {shipped.Name} (portal-shipped, not composed: {shipped.RelativePath}) — "
        + $"{evidence}; compose it with --module";

    private ShippedAssembly? FindShippedByNamespace(string ns)
    {
        if (shippedByNamespace.TryGetValue(ns, out var exact))
            return exact;
        // A CS0234 for 'A.B' is also raised when only 'A.B.C' exists nowhere in the set — the
        // shipped assembly declaring the deeper namespace is still the one the content wanted.
        var prefix = ns + ".";
        return shippedByNamespace
            .Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Value)
            .FirstOrDefault();
    }

    private static void AddWithPrefixes(ImmutableHashSet<string>.Builder set, string ns)
    {
        while (ns.Length > 0)
        {
            if (!set.Add(ns))
                return;
            var dot = ns.LastIndexOf('.');
            if (dot <= 0)
                return;
            ns = ns[..dot];
        }
    }

    /// <summary>Top-level (namespace, type) pairs an assembly file declares — metadata only.
    /// Unreadable or non-managed files contribute nothing rather than faulting the attribution,
    /// which runs on an already-failing path and must never replace the diagnostic it explains.</summary>
    private static IReadOnlyList<(string Namespace, string Type)> DeclaredTypes(string path)
    {
        var result = new List<(string, string)>();
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
                return result;
            var md = pe.GetMetadataReader();
            foreach (var handle in md.TypeDefinitions)
            {
                var definition = md.GetTypeDefinition(handle);
                if (!definition.GetDeclaringType().IsNil)
                    continue;
                var name = md.GetString(definition.Name);
                if (name.StartsWith('<'))
                    continue;
                var tick = name.IndexOf('`');
                if (tick > 0)
                    name = name[..tick];
                result.Add((md.GetString(definition.Namespace), name));
            }
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or InvalidOperationException)
        {
            // Not a readable managed assembly — nothing to index from it.
        }
        return result;
    }

    [GeneratedRegex(@"CS0234\b[^\n]*?name '(?<name>[^']+)' does not exist in the namespace '(?<parent>[^']+)'")]
    private static partial Regex MissingNamespace();

    // A generic type name reads 'Foo<T>' in the diagnostic; the identifier is what the index keys on.
    [GeneratedRegex(@"CS0246\b[^\n]*?name '(?<name>[^'<]+)(?:<[^']*)?' could not be found")]
    private static partial Regex MissingType();
}
