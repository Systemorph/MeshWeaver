using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using MeshWeaver.PluginTester.Extensibility;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace MeshWeaver.PluginTester;

/// <summary>
/// The fourth fail-closed check on <c>--superseded-image-assembly</c>: <b>is the entry still
/// needed?</b> (MeshWeaver#3223.)
///
/// <para><b>The hole it closes.</b> The other three refusals — an empty name, a name the image does
/// not carry, a name the run already builds from source — all fire on the SHAPE of the input. None
/// of them fires on an entry that has simply outlived its reason. A superseded entry exists for one
/// wave: a type moved out of an image-shipped assembly into a module in the same repository, and the
/// pinned image is still a wave behind. The moment the pin moves onto an image built AFTER the move,
/// that image's copy no longer defines the moved types — but the <i>assembly still exists</i>, so
/// "the container carries no such assembly" never fires. The entry then keeps a real image assembly
/// out of the reference set forever, and the day something legitimately needs a DIFFERENT type from
/// it the build dies <c>CS0246</c> pointing at the consuming code rather than at the declaration
/// that caused it. An allow-shaped entry that outlives its reason with nothing to retire it is a
/// recurring defect class here, not a hypothesis.</para>
///
/// <para><b>The question, stated exactly.</b> The entry is stale when the image's copy of the named
/// assembly defines <i>none</i> of the type names this repository's source declares. That is the
/// precise form of "there is nothing left to supersede": a collision needs one type name defined on
/// both sides, so an empty intersection means no collision is possible and the drop can only
/// subtract.</para>
///
/// <para>🚨 <b>The comparison is against the REPOSITORY, never against the run's selection.</b> The
/// pack lane narrows what it compiles — a PR-scoped diff, and a build ledger that hands back reused
/// bundles — so on most runs the module that owns the moved type is not in the graph at all. Reading
/// "this run's compilations" as the source side would therefore turn the entry RED on ordinary
/// narrowed PRs during exactly the one wave the entry exists to serve. The source side is the whole
/// tree under <c>Graph.SourceRoot</c>, which is what the lane mounts and is independent of the
/// selection.</para>
///
/// <para><b>Every ambiguity is resolved towards QUIET.</b> A false red here blocks a pipeline; a
/// missed staleness costs what the status quo already costs. So the image side counts type
/// FORWARDERS as definitions (a forwarded type is just as visible to the compiler), the source side
/// counts every <c>.cs</c> under the root — in-mesh <c>Source/*.cs</c> nodes included, they are this
/// repository's source too — and every file a loaded BUILD EXTENSION claims contributes whatever the
/// extension reads off it (the Razor extension: a component's declared namespace, and as a fallback
/// its simple name with its directory suffix, since the <c>RootNamespace</c> prefix is the SDK's to
/// compute and a re-derivation that got it wrong would produce precisely the false red this check
/// must never produce).</para>
///
/// <para>An assembly whose metadata cannot be READ is a refusal of its own, never a staleness
/// verdict — naming the wrong entry to delete is worse than saying nothing.</para>
/// </summary>
internal sealed class SupersededEntryStaleness
{
    /// <summary>Build output and tool caches under the source root declare nothing a human wrote.</summary>
    private static readonly ImmutableHashSet<string> SkippedDirectories = ImmutableHashSet.Create(
        StringComparer.OrdinalIgnoreCase, "bin", "obj", ".git", ".vs", ".idea", "node_modules", "packages");

    private readonly ImmutableHashSet<string> declaredTypes;
    private readonly ImmutableDictionary<string, ImmutableHashSet<string>> suffixDeclared;

    private SupersededEntryStaleness(
        string sourceRoot,
        int filesScanned,
        ImmutableHashSet<string> declaredTypes,
        ImmutableDictionary<string, ImmutableHashSet<string>> suffixDeclared)
    {
        SourceRoot = sourceRoot;
        FilesScanned = filesScanned;
        this.declaredTypes = declaredTypes;
        this.suffixDeclared = suffixDeclared;
    }

    /// <summary>The tree the source side was read from.</summary>
    public string SourceRoot { get; }

    /// <summary>How many <c>.cs</c> files and extension-claimed files contributed.</summary>
    public int FilesScanned { get; }

    /// <summary>Top-level type names the tree declares in C# source.</summary>
    public int DeclaredTypeCount => declaredTypes.Count;

    /// <summary>
    /// Indexes every top-level type name the tree under <paramref name="sourceRoot"/> declares.
    /// Syntax only — no compilation, no semantic model, nothing loaded: the question is which NAMES
    /// exist, and a parse answers it for a repository in a couple of seconds. Paid only on the runs
    /// that actually pass <c>--superseded-image-assembly</c>, which is the rare wave after a move.
    /// </summary>
    /// <param name="sourceRoot">The repository's source root.</param>
    /// <param name="extensions">The loaded build extensions; each indexes the files it claims.</param>
    public static SupersededEntryStaleness ForSourceRoot(
        string sourceRoot, IReadOnlyList<IBuildExtension>? extensions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        var root = Path.GetFullPath(sourceRoot);
        if (!Directory.Exists(root))
            throw new InvalidOperationException(
                $"the source root '{root}' does not exist, so whether a --superseded-image-assembly "
                + "entry is still needed cannot be answered. Refusing rather than guessing.");

        var scans = new Dictionary<string, IDeclaredTypeScan>(StringComparer.OrdinalIgnoreCase);
        foreach (var extension in extensions ?? [])
        {
            var scan = extension.BeginDeclaredTypeScan(root);
            foreach (var suffix in extension.ClaimedSourceSuffixes)
                scans.TryAdd(suffix, scan);
        }

        var sink = new Sink();
        var files = 0;
        foreach (var path in SourceFiles(root, scans.Keys))
        {
            files++;
            var extension = Path.GetExtension(path);
            if (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase))
                CollectCSharpTypes(path, sink.Exact);
            else if (scans.TryGetValue(extension, out var scan))
                scan.Collect(path, sink);
        }

        return new SupersededEntryStaleness(
            root,
            files,
            sink.Exact.ToImmutable(),
            sink.BySuffix.ToImmutableDictionary(e => e.Key, e => e.Value.ToImmutable(), StringComparer.Ordinal));
    }

    /// <summary>Collects what the C# parse and the extensions declare.</summary>
    private sealed class Sink : IDeclaredTypeSink
    {
        internal ImmutableHashSet<string>.Builder Exact { get; } = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);

        internal Dictionary<string, ImmutableHashSet<string>.Builder> BySuffix { get; } = new(StringComparer.Ordinal);

        public void Declare(string qualifiedName) => Exact.Add(qualifiedName);

        public void DeclareBySuffix(string simpleName, string dottedDirectory)
        {
            if (!BySuffix.TryGetValue(simpleName, out var directories))
                BySuffix[simpleName] = directories = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
            directories.Add(dottedDirectory);
        }
    }

    /// <summary>
    /// Does this repository's source declare a top-level type of this exact name? The C# side is an
    /// exact namespace-qualified match; a name an extension declared by suffix matches its simple name
    /// and requires the namespace to END with the recorded directory path (an empty path matches any
    /// namespace — the root namespace is the SDK's to compute, not this index's).
    /// </summary>
    public bool Declares(string @namespace, string name)
    {
        ArgumentNullException.ThrowIfNull(@namespace);
        ArgumentException.ThrowIfNullOrEmpty(name);
        var qualified = @namespace.Length == 0 ? name : @namespace + "." + name;
        if (declaredTypes.Contains(qualified))
            return true;
        if (!suffixDeclared.TryGetValue(name, out var directories))
            return false;
        foreach (var directory in directories)
            if (directory.Length == 0
                || @namespace.Equals(directory, StringComparison.Ordinal)
                || @namespace.EndsWith("." + directory, StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>
    /// The type names this repository's source ALSO declares out of the image assembly's own — the
    /// evidence that the entry is still doing something. Empty ⇒ the entry is stale.
    /// </summary>
    /// <param name="entryName">The <c>--superseded-image-assembly</c> value, for the message.</param>
    /// <param name="assemblyPath">The image's copy of it.</param>
    public Overlap Measure(string entryName, string assemblyPath)
    {
        var imageTypes = ImageTypes(entryName, assemblyPath);
        var shared = imageTypes
            .Where(t => Declares(t.Namespace, t.Name))
            .Select(t => t.Namespace.Length == 0 ? t.Name : t.Namespace + "." + t.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToImmutableArray();
        return new Overlap(imageTypes.Length, shared);
    }

    /// <summary>What the image's copy and this repository's source have in common.</summary>
    /// <param name="ImageTypeCount">Top-level types (definitions + forwarders) the image's copy carries.</param>
    /// <param name="StillDefinedInSource">Those the repository's source declares too, in name order.</param>
    public sealed record Overlap(int ImageTypeCount, ImmutableArray<string> StillDefinedInSource)
    {
        /// <summary>Nothing left to supersede: the entry has done its job and must be removed.</summary>
        public bool IsStale => StillDefinedInSource.IsEmpty;
    }

    // ── the image side ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Top-level types the image's copy DEFINES or FORWARDS. Forwarders count: a forwarded type is
    /// as visible through this assembly as a defined one, so treating one as absent could retire an
    /// entry that is still load-bearing.
    /// </summary>
    private static ImmutableArray<(string Namespace, string Name)> ImageTypes(
        string entryName, string assemblyPath)
    {
        var types = ImmutableArray.CreateBuilder<(string, string)>();
        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
                throw Unreadable(entryName, assemblyPath, "the file carries no CLI metadata");
            var md = pe.GetMetadataReader();
            foreach (var handle in md.TypeDefinitions)
            {
                var definition = md.GetTypeDefinition(handle);
                if (!definition.GetDeclaringType().IsNil)
                    continue;
                if (Simplify(md.GetString(definition.Name)) is { } name)
                    types.Add((md.GetString(definition.Namespace), name));
            }
            foreach (var handle in md.ExportedTypes)
            {
                var exported = md.GetExportedType(handle);
                if (exported.Implementation.Kind == HandleKind.ExportedType)
                    continue; // a nested forward — its declaring type is already counted
                if (Simplify(md.GetString(exported.Name)) is { } name)
                    types.Add((md.GetString(exported.Namespace), name));
            }
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or UnauthorizedAccessException)
        {
            throw Unreadable(entryName, assemblyPath, $"{ex.GetType().Name}: {ex.Message}");
        }
        return types.ToImmutable();
    }

    /// <summary>An unreadable image copy is its OWN refusal — never a staleness verdict.</summary>
    private static InvalidOperationException Unreadable(string entryName, string path, string reason) =>
        new($"--superseded-image-assembly '{entryName}': the image's copy at {path} could not be read "
            + $"as managed metadata ({reason}), so whether the entry is still needed cannot be "
            + "answered. Refusing rather than guessing — a staleness verdict read off an assembly "
            + "nobody could open would name the wrong entry to delete.");

    /// <summary>The metadata name a source declaration would carry: no arity suffix, no
    /// compiler-synthesized <c>&lt;…&gt;</c> names (no source declares one, so they can only
    /// mislead).</summary>
    private static string? Simplify(string metadataName)
    {
        if (metadataName.Length == 0 || metadataName[0] == '<')
            return null;
        var tick = metadataName.IndexOf('`');
        return tick > 0 ? metadataName[..tick] : metadataName;
    }

    // ── the source side ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Walks the tree explicitly rather than with <c>RecurseSubdirectories</c>, so a skipped
    /// directory is never DESCENDED INTO — the lane's source root is a git checkout, and walking
    /// <c>.git</c> to discard every file in it costs more than the whole rest of the scan. A
    /// symlinked directory is not followed: a link out of the tree is not this repository's source,
    /// and a link back into it is a cycle.
    /// </summary>
    private static IEnumerable<string> SourceFiles(string root, IEnumerable<string> claimedSuffixes)
    {
        var suffixes = claimedSuffixes.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            var files = new List<string>();
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    var extension = Path.GetExtension(file);
                    if (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase)
                        || suffixes.Contains(extension))
                        files.Add(file);
                }
                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    if (SkippedDirectories.Contains(Path.GetFileName(child)))
                        continue;
                    if (new DirectoryInfo(child).LinkTarget is not null)
                        continue;
                    pending.Push(child);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An unreadable directory contributes nothing. It cannot make the check FIRE
                // either: the verdict is "stale" only when the whole tree shares nothing with the
                // image copy, and a build whose sources are unreadable is red long before here.
            }
            foreach (var file in files)
                yield return file;
        }
    }

    private static void CollectCSharpTypes(string path, ImmutableHashSet<string>.Builder declared)
    {
        SourceText text;
        try
        {
            using var stream = File.OpenRead(path);
            text = SourceText.From(stream);
        }
        catch (IOException)
        {
            // A file that cannot be read contributes no names. It cannot make the check fire
            // either — the verdict is only ever "stale" when the ENTIRE tree shares nothing with
            // the image copy, and a build whose sources are unreadable is red long before here.
            return;
        }
        var root = CSharpSyntaxTree.ParseText(text).GetCompilationUnitRoot();
        CollectFrom(root.Members, string.Empty, declared);
    }

    private static void CollectFrom(
        IEnumerable<MemberDeclarationSyntax> members, string @namespace, ImmutableHashSet<string>.Builder declared)
    {
        foreach (var member in members)
            switch (member)
            {
                case BaseNamespaceDeclarationSyntax nested:
                    var inner = nested.Name.ToString();
                    CollectFrom(
                        nested.Members,
                        @namespace.Length == 0 ? inner : @namespace + "." + inner,
                        declared);
                    break;
                // class / struct / record / interface / enum — the whole BaseTypeDeclaration family.
                case BaseTypeDeclarationSyntax type:
                    declared.Add(Qualify(@namespace, type.Identifier.ValueText));
                    break;
                case DelegateDeclarationSyntax @delegate:
                    declared.Add(Qualify(@namespace, @delegate.Identifier.ValueText));
                    break;
            }
    }

    private static string Qualify(string @namespace, string name) =>
        @namespace.Length == 0 ? name : @namespace + "." + name;
}
