// abi-syntax — the public-surface SKELETON of a C# file, from Roslyn's syntax tree (no build, no semantic model).
//
//   abi-syntax <request.json>           request: {"blobs": [{"id": "...", "file": "<path>"}], "strip": true}
//                                       stdout:  {"<id>": {"surface": "<sha256>" | null, "mode": "skeleton" | "tokens",
//                                                          "reason": "<why null / why tokens>"}}
//   abi-syntax --dump <file.cs>         prints the skeleton text (for reading a verdict by hand)
//
// The SURFACE of a file is what a reference assembly is built from: every declaration with its modifiers,
// attributes, type parameters, constraints, base list, parameters and their DEFAULT values, enum member values and
// `const` values — every type and member, private and internal included (a struct's private fields are layout, and
// internals reach the reference assembly under InternalsVisibleTo; keeping more only costs narrowing). What is
// REMOVED is implementation: member bodies (block and expression bodies, of methods, constructors, destructors,
// operators, conversions and accessors), constructor initializers (`: base(…)`), and field/property initializers
// other than `const` — plus all trivia except preprocessor directives and disabled text, which are KEPT verbatim, so
// a `#if` edit (or an edit inside a disabled region) always reads as a surface change and no preprocessor symbol
// has to be known.
//
// A removed body leaves a MARKER, never nothing: `{}` (so a body vs. no body — abstract, extern, partial, an
// auto-accessor `get;` — stays visible), `{ __iterator__; }` when it yields (iterator), `{ __field__; }` when an
// accessor uses the `field` keyword (a compiler-generated backing field — struct layout).
//
// Fail SAFE — no surface (null ⇒ the caller reads "changed"): a parse error; top-level statements (a synthesized
// Program whose signature follows its body). Compared as the FULL token stream instead (any edit is a change): a file
// with a generator input — a partial member declared without a body, or an attribute a source generator is driven by.
// A (non-record) struct with a primary constructor keeps its member bodies: a captured parameter becomes a
// private field there, i.e. layout.
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace MeshWeaver.AbiSyntax;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--dump")
        {
            var r = Surface.Of(File.ReadAllText(args[1]), strip: true);
            Console.WriteLine(r.Text ?? $"<no surface: {r.Reason}>");
            return 0;
        }
        if (args.Length != 1)
        {
            Console.Error.WriteLine("usage: abi-syntax <request.json> | --dump <file.cs>");
            return 2;
        }
        var request = JsonNode.Parse(File.ReadAllText(args[0]))!.AsObject();
        var strip = request["strip"]?.GetValue<bool>() ?? true;
        var output = new JsonObject();
        foreach (var blob in request["blobs"]!.AsArray())
        {
            var id = blob!["id"]!.GetValue<string>();
            var file = blob["file"]!.GetValue<string>();
            Surface r;
            try { r = Surface.Of(File.ReadAllText(file), strip); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            { r = new Surface(null, "error", $"unreadable: {ex.Message}"); }
            output[id] = new JsonObject
            {
                ["surface"] = r.Text is null ? null : Hash(r.Mode + "\n" + r.Text),
                ["mode"] = r.Mode,
                ["reason"] = r.Reason,
            };
        }
        Console.WriteLine(output.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        return 0;
    }

    static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
}

public sealed record Surface(string? Text, string Mode, string Reason)
{
    // Parsed at the newest language version: a superset of every project's LangVersion, so tokens are the same.
    static readonly CSharpParseOptions Options = new(LanguageVersion.Preview, DocumentationMode.None);

    // Attributes a source generator reads (BCL and the libraries this fleet uses). Their files compare by tokens.
    static readonly ImmutableHashSet<string> GeneratorAttributes = ImmutableHashSet.Create(StringComparer.Ordinal,
        "GeneratedRegex", "LoggerMessage", "JsonSerializable", "JsonSourceGenerationOptions", "LibraryImport",
        "GeneratedComInterface", "GeneratedComClass", "JSImport", "JSExport", "GenerateSerializer", "InterceptsLocation",
        "ObservableProperty", "RelayCommand", "OptionsValidator");

    public static Surface Of(string source, bool strip)
    {
        var tree = CSharpSyntaxTree.ParseText(source, Options);
        var root = tree.GetCompilationUnitRoot();
        var error = root.GetDiagnostics().FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error);
        if (error is not null)
            return new Surface(null, "error", $"parse error {error.Id} at line {error.Location.GetLineSpan().StartLinePosition.Line + 1}");
        if (root.Members.OfType<GlobalStatementSyntax>().Any())
            return new Surface(null, "error", "top-level statements (a synthesized Program) — not classified");
        var directives = string.Join("\n", root.DescendantTrivia(descendIntoTrivia: true)
            .Where(t => t.IsDirective || t.IsKind(SyntaxKind.DisabledTextTrivia))
            .Select(t => Normalize(t.ToFullString())));
        if (GeneratorInput(root) is { } why)
            return new Surface(directives + "\n" + Tokens(root), "tokens", why);
        var shape = strip ? new Skeletonizer().Visit(root)! : root;
        return new Surface(directives + "\n" + Tokens(shape), "skeleton", "");
    }

    static string? GeneratorInput(CompilationUnitSyntax root)
    {
        foreach (var node in root.DescendantNodes(n => n is not BlockSyntax and not ArrowExpressionClauseSyntax))
        {
            if (node is AttributeSyntax a)
            {
                var name = a.Name switch
                {
                    QualifiedNameSyntax q => q.Right.Identifier.ValueText,
                    AliasQualifiedNameSyntax q => q.Name.Identifier.ValueText,
                    SimpleNameSyntax s => s.Identifier.ValueText,
                    _ => a.Name.ToString(),
                };
                if (name.EndsWith("Attribute", StringComparison.Ordinal))
                    name = name[..^"Attribute".Length];
                if (GeneratorAttributes.Contains(name))
                    return $"generator input [{name}] — compared token for token";
            }
            if (node is MethodDeclarationSyntax m && m.Modifiers.Any(SyntaxKind.PartialKeyword) && m.Body is null && m.ExpressionBody is null)
                return $"generator input: partial method {m.Identifier.ValueText} declared without a body — compared token for token";
            if (node is BasePropertyDeclarationSyntax p && p.Modifiers.Any(SyntaxKind.PartialKeyword)
                && (p.AccessorList?.Accessors.All(x => x.Body is null && x.ExpressionBody is null) ?? false))
                return "generator input: partial property declared without a body — compared token for token";
        }
        return null;
    }

    static string Tokens(SyntaxNode node) => string.Join(" ", node.DescendantTokens().Select(t => t.Text));

    static string Normalize(string s) => string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

/// <summary>Removes implementation, keeps declarations (see the file header).</summary>
sealed class Skeletonizer : CSharpSyntaxRewriter
{
    static readonly StatementSyntax IteratorMark = SyntaxFactory.ParseStatement("__iterator__;");
    static readonly StatementSyntax FieldMark = SyntaxFactory.ParseStatement("__field__;");

    static BlockSyntax Marker(SyntaxNode? body, bool accessor)
    {
        if (body is null)
            return SyntaxFactory.Block();
        var marks = new List<StatementSyntax>();
        var nodes = body.DescendantNodesAndSelf(n => n is not LocalFunctionStatementSyntax and not AnonymousFunctionExpressionSyntax).ToList();
        if (nodes.OfType<YieldStatementSyntax>().Any())
            marks.Add(IteratorMark);
        if (accessor && body.DescendantTokens().Any(t => t.IsKind(SyntaxKind.FieldKeyword) || t.ValueText == "field"))
            marks.Add(FieldMark);
        return SyntaxFactory.Block(marks);
    }

    static T Strip<T>(T node, bool accessor = false) where T : SyntaxNode
    {
        switch (node)
        {
            case BaseMethodDeclarationSyntax m when m.Body is not null || m.ExpressionBody is not null:
            {
                var stripped = m.WithBody(Marker((SyntaxNode?)m.Body ?? m.ExpressionBody, accessor))
                    .WithExpressionBody(null).WithSemicolonToken(default);
                if (stripped is ConstructorDeclarationSyntax c)
                    stripped = c.WithInitializer(null);
                return (T)(SyntaxNode)stripped;
            }
            case AccessorDeclarationSyntax a when a.Body is not null || a.ExpressionBody is not null:
                return (T)(SyntaxNode)a.WithBody(Marker((SyntaxNode?)a.Body ?? a.ExpressionBody, accessor: true))
                    .WithExpressionBody(null).WithSemicolonToken(default);
            default:
                return node;
        }
    }

    // A plain `using N;` at the COMPILATION-UNIT level cannot change what an existing name in a declaration binds
    // to: lookup tries every enclosing namespace's members first and the global namespace's members before the
    // compilation unit's usings, and two usings of one scope offering the same name are an ambiguity ERROR, never
    // a silent rebinding. So it is not surface (measured: two of the replayed pull requests moved nothing but a
    // `using`). An alias, a `using static`, a `global using` and any using INSIDE a namespace block (an inner scope,
    // searched before the outer one — it CAN shadow) stay surface.
    public override SyntaxNode? VisitCompilationUnit(CompilationUnitSyntax node)
    {
        var cu = (CompilationUnitSyntax)base.VisitCompilationUnit(node)!;
        return cu.WithUsings(SyntaxFactory.List(cu.Usings.Where(u =>
            u.Alias is not null || !u.StaticKeyword.IsKind(SyntaxKind.None) || !u.GlobalKeyword.IsKind(SyntaxKind.None))));
    }

    // A struct with a primary constructor: a parameter captured in a body is a private FIELD (layout) — keep bodies.
    public override SyntaxNode? VisitStructDeclaration(StructDeclarationSyntax node) =>
        node.ParameterList is not null ? node : base.VisitStructDeclaration(node);

    // A RECORD struct's positional parameters are properties: a member body naming one binds to the property, so
    // no parameter is captured into a hidden field — its bodies are stripped like any other.

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node) => Strip(node);
    public override SyntaxNode? VisitConstructorDeclaration(ConstructorDeclarationSyntax node) => Strip(node);
    public override SyntaxNode? VisitDestructorDeclaration(DestructorDeclarationSyntax node) => Strip(node);
    public override SyntaxNode? VisitOperatorDeclaration(OperatorDeclarationSyntax node) => Strip(node);
    public override SyntaxNode? VisitConversionOperatorDeclaration(ConversionOperatorDeclarationSyntax node) => Strip(node);
    public override SyntaxNode? VisitAccessorDeclaration(AccessorDeclarationSyntax node) => Strip(node, accessor: true);

    public override SyntaxNode? VisitPropertyDeclaration(PropertyDeclarationSyntax node)
    {
        var p = (PropertyDeclarationSyntax)base.VisitPropertyDeclaration(node)!;
        if (p.ExpressionBody is { } body)   // `T X => e;` is a getter with a body
            p = p.WithExpressionBody(null).WithAccessorList(GetterWithBody(Marker(body, accessor: true)));
        return p.WithInitializer(null).WithSemicolonToken(default);
    }

    public override SyntaxNode? VisitIndexerDeclaration(IndexerDeclarationSyntax node)
    {
        var i = (IndexerDeclarationSyntax)base.VisitIndexerDeclaration(node)!;
        return i.ExpressionBody is { } body
            ? i.WithExpressionBody(null).WithAccessorList(GetterWithBody(Marker(body, accessor: true))).WithSemicolonToken(default)
            : i;
    }

    static AccessorListSyntax GetterWithBody(BlockSyntax marker) =>
        SyntaxFactory.AccessorList(SyntaxFactory.SingletonList(SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration, marker)));

    public override SyntaxNode? VisitFieldDeclaration(FieldDeclarationSyntax node) =>
        node.Modifiers.Any(SyntaxKind.ConstKeyword) ? node : node.WithDeclaration(WithoutInitializers(node.Declaration));

    public override SyntaxNode? VisitEventFieldDeclaration(EventFieldDeclarationSyntax node) =>
        node.WithDeclaration(WithoutInitializers(node.Declaration));

    static VariableDeclarationSyntax WithoutInitializers(VariableDeclarationSyntax d) =>
        d.WithVariables(SyntaxFactory.SeparatedList(d.Variables.Select(v => v.WithInitializer(null))));
}
