using System.Collections.Immutable;
using System.Text.Json;
using MeshWeaver.GitSync;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Persistence.Parsers;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging.Serialization;
using MeshWeaver.PluginCatalog;

namespace MeshWeaver.PluginTester;

/// <summary>
/// Turns a node-repo CHECKOUT into the in-memory <see cref="MeshNode"/> set a compile resolves its
/// sources from — the file→node half of the compiler-driven bake (#1763). No mesh, no hub, no
/// import: the same files the installer would write, materialised in a list.
///
/// <para><b>The path rule is not re-implemented.</b> Which files are nodes, and what path each one
/// takes, comes from <see cref="PackageInstaller.NodePathForFile"/> — the installer's own public
/// mapping (<c>NodeFileMapper.FromRelativePath</c> plus the README / <c>manifest.lock</c> /
/// <c>content/**</c> exclusions). A private copy of that rule here is exactly how a build-process
/// bake would start resolving sources under paths the runtime never uses, and the failure would be
/// silent: the source query simply matches less.</para>
///
/// <para><b>Content typing is DELIBERATELY narrow.</b> A hub supplies
/// <c>JsonSerializerOptions</c> carrying the mesh's TypeRegistry, which is what lets a
/// <c>.json</c> node's <c>$type</c> discriminator materialise. A build process has no hub, and
/// fabricating a half-populated registry would silently degrade unknown content to
/// <c>JsonElement</c> — the trap-door in AGENTS.md's "never cast an object payload". So only the
/// two content types a COMPILE reads are materialised, by explicit discriminator:
/// <see cref="NodeTypeDefinition"/> (the type being compiled: its <c>configuration</c> lambda,
/// <c>sources</c>/<c>tests</c> queries and content collections) and <see cref="CodeConfiguration"/>
/// (the sources themselves). Everything else keeps a null content — a compile never reads it, and
/// a null is honest where a mistyped object would not be. <c>.cs</c> and <c>.md</c> files go
/// through the real <see cref="FileFormatParserRegistry"/>, which needs no serializer options.</para>
/// </summary>
public static class TreeNodeLoader
{
    /// <summary>
    /// Web defaults = camelCase, the shape authored node files use; unknown properties (including
    /// the <c>$type</c> discriminator itself) are skipped by default.
    ///
    /// <para>🚨 The three non-default settings are NOT decoration — each one was a silently dropped
    /// NodeType when it was missing, caught by running this loader over
    /// <c>samples/Graph/Data</c>:</para>
    /// <list type="bullet">
    ///   <item><see cref="EnumMemberJsonStringEnumConverter"/> — the FRAMEWORK'S OWN converter, not
    ///     a stock <c>JsonStringEnumConverter</c>. Enums persist as strings
    ///     (<c>"compilationStatus": "Ok"</c>) and web defaults do not convert them, so every
    ///     NodeType carrying a compile stamp failed to deserialize: 28 of them across ACME,
    ///     FutuRe and Northwind.</item>
    ///   <item><c>ReadCommentHandling</c> / <c>AllowTrailingCommas</c> — copied off the hub's own
    ///     options for the same reason <c>JsonFileParser</c> copies them: a node file that parses
    ///     under a hub must parse here, or the bake resolves a smaller tree than the runtime.</item>
    /// </list>
    /// </summary>
    private static readonly JsonSerializerOptions ContentJson = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new EnumMemberJsonStringEnumConverter() },
    };

    /// <summary>Document-parse tolerances matching <see cref="ContentJson"/> — the same copy
    /// <c>JsonFileParser.Parse</c> makes, and for the same reason.</summary>
    private static readonly JsonDocumentOptions ContentJsonDocument = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>A node the tree produced, together with the package it came from.</summary>
    /// <param name="Node">The materialised node.</param>
    /// <param name="Package">The package id (top-level folder) that shipped it.</param>
    /// <param name="RelativePath">The repo-relative file it was parsed from.</param>
    public sealed record TreeNode(MeshNode Node, string Package, string RelativePath);

    /// <summary>
    /// Materialises every node of every package in <paramref name="snapshot"/>.
    ///
    /// <para>🚨 The result spans ALL packages, on purpose. A <c>shared=@Other/Lib/Source</c> query
    /// reaches across package boundaries, and the runtime resolves it against the whole mesh; a
    /// per-package node set would resolve it to nothing and compile a short set that produces a
    /// completely genuine-looking CS0246 (#1218).</para>
    /// </summary>
    /// <param name="snapshot">The swept checkout.</param>
    /// <param name="packages">The discovered packages.</param>
    /// <param name="onSkipped">Invoked with (relativePath, reason) for a file that has a node path
    /// but could not be materialised — never swallowed, because a dropped source file is invisible
    /// in the output and fatal in production.</param>
    public static ImmutableArray<TreeNode> Load(
        RepoSnapshot snapshot,
        IReadOnlyList<PackageManifest> packages,
        Action<string, string>? onSkipped = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(packages);

        // No serializer options: the JSON parser is replaced by the explicit typed reader below.
        var parsers = new FileFormatParserRegistry();
        var result = ImmutableArray.CreateBuilder<TreeNode>();

        foreach (var package in packages.OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            var prefix = (package.SourceFolder ?? package.Id) + "/";
            foreach (var file in snapshot.Files)
            {
                if (!file.Path.StartsWith(prefix, StringComparison.Ordinal))
                    continue;
                // The installer's own rule: null = this file is not a node (README, manifest.lock,
                // content/** asset). NodeRepo packages keep the FULL repo-relative path — the
                // package folder IS the partition — so no rebasing happens here either.
                if (PackageInstaller.NodePathForFile(file.Path) is not { Length: > 0 } nodePath)
                    continue;

                var node = Materialise(parsers, file, nodePath, out var reason);
                if (node is null)
                {
                    if (reason is not null)
                        onSkipped?.Invoke(file.Path, reason);
                    continue;
                }
                result.Add(new TreeNode(node, package.Id, file.Path));
            }
        }
        return result.ToImmutable();
    }

    private static MeshNode? Materialise(
        FileFormatParserRegistry parsers, RepoFile file, string nodePath, out string? reason)
    {
        reason = null;
        var lastSlash = nodePath.LastIndexOf('/');
        var id = lastSlash < 0 ? nodePath : nodePath[(lastSlash + 1)..];
        var ns = lastSlash < 0 ? string.Empty : nodePath[..lastSlash];
        var extension = Path.GetExtension(file.Path);

        if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
            return MaterialiseJson(file, id, ns, out reason);

        // .cs and .md go through the framework's own parsers — no serializer options needed, and
        // CSharpFileParser is what turns a `// NodeType: Scope` header into a Scope-typed node
        // (which `nodeType:Code` then does NOT match, exactly as at runtime).
        string? parseError = null;
        var parsed = parsers.TryParse(
            extension, file.Path, file.Content, file.Path,
            (_, ex) => parseError = $"{ex.GetType().Name}: {ex.Message}");
        reason = parseError;
        if (parsed is null)
            // No parser for this extension is the normal case for the non-node files a repo
            // carries (.yml, .png, .csproj) — silent, exactly as the installer treats them.
            return null;
        return parsed with { Id = id, Namespace = ns, State = MeshNodeState.Active };
    }

    private static MeshNode? MaterialiseJson(RepoFile file, string id, string ns, out string? reason)
    {
        reason = null;
        JsonDocument document;
        try
        {
            // 🚨 The BOM strip here is REQUIRED, and the requirement inverted on 2026-08-17.
            // This line used to carry the opposite instruction — "do NOT strip a UTF-8 BOM, the
            // RUNTIME drops those nodes and a bake that compiled them would be an equivalence
            // break" — which was correct while the runtime skipped them. #1767 fixed the runtime
            // (FileFormatParserRegistry.WithoutBom), so NOT stripping here is now the equivalence
            // break, in the other direction: the bake would resolve a SMALLER tree than the mesh
            // imports and silently ship no bundle for content the portal then compiles at runtime.
            // Whichever way the runtime goes, this path follows it — that is the invariant, not
            // the strip itself, and BakeEquivalenceTest is what holds the two together.
            document = JsonDocument.Parse(FileFormatParserRegistry.WithoutBom(file.Content), ContentJsonDocument);
        }
        catch (JsonException ex)
        {
            // Matches JsonFileParser: a document that will not parse yields NO node, and the
            // installer logs "No parser for …; skipped".
            reason = $"malformed JSON: {ex.Message}";
            return null;
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            // The installer's own "is this a node?" gate (JsonFileParser.LooksLikeMeshNode):
            // ordinary JSON a repo happens to carry is not a node and is not an error. `$type` is
            // matched EXACTLY (it is the serializer's discriminator, not an authored name);
            // `id`/`nodeType` case-insensitively.
            if (!LooksLikeMeshNode(root))
                return null;

            var nodeType = TryGetString(root, "nodeType");
            var name = TryGetString(root, "name");

            object? content = null;
            if (TryGetElement(root, "content", out var contentElement)
                && contentElement.ValueKind == JsonValueKind.Object
                && contentElement.TryGetProperty("$type", out var discriminator)
                && discriminator.ValueKind == JsonValueKind.String)
            {
                var typeName = discriminator.GetString();
                try
                {
                    content = typeName switch
                    {
                        nameof(NodeTypeDefinition) =>
                            contentElement.Deserialize<NodeTypeDefinition>(ContentJson),
                        nameof(CodeConfiguration) =>
                            contentElement.Deserialize<CodeConfiguration>(ContentJson),
                        // Anything else is content a COMPILE never reads. Left null rather than
                        // guessed — see the class remarks.
                        _ => null,
                    };
                }
                catch (JsonException ex)
                {
                    // A NodeType or Code node whose content will not materialise MUST be loud: its
                    // sources would silently vanish from the compile.
                    reason = $"content ${typeName} did not deserialize: {ex.Message}";
                    return null;
                }
            }

            return new MeshNode(id, ns)
            {
                NodeType = nodeType,
                Name = name ?? id,
                State = MeshNodeState.Active,
                Content = content,
            };
        }
    }

    /// <summary>Verbatim mirror of <c>JsonFileParser.LooksLikeMeshNode</c>.</summary>
    private static bool LooksLikeMeshNode(JsonElement root)
    {
        foreach (var property in root.EnumerateObject())
            if (property.NameEquals("$type")
                || string.Equals(property.Name, "id", StringComparison.OrdinalIgnoreCase)
                || string.Equals(property.Name, "nodeType", StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>Case-insensitive property lookup — the web-defaults behaviour the deserializer
    /// applies to the same document.</summary>
    private static bool TryGetElement(JsonElement root, string name, out JsonElement value)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                continue;
            value = property.Value;
            return true;
        }
        value = default;
        return false;
    }

    private static string? TryGetString(JsonElement root, string name)
        => TryGetElement(root, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
