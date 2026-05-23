using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Services.LanguageServer;
using MeshWeaver.Messaging;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.QuickInfo;
using Microsoft.CodeAnalysis.Tags;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using RoslynDiagnosticSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;
using LspDiagnosticSeverity = MeshWeaver.Mesh.Services.LanguageServer.DiagnosticSeverity;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// In-process Roslyn language services over a NodeType's live <c>CSharpCompilation</c>.
/// Wraps Roslyn's <see cref="CompletionService"/> / <see cref="QuickInfoService"/> in an
/// <see cref="AdhocWorkspace"/> per NodeType — cached, keyed by source-versions hash.
/// <para>
/// Stage 1 of LSP integration. Consumed by the <c>lsp_*_for_node</c> MCP tools so the
/// Coder agent can hover / complete / diagnose without a full <c>Compile</c> round-trip.
/// </para>
/// </summary>
internal sealed class MeshNodeLanguageService(
    MeshNodeCompilationService compilationService,
    SpeculativeCompilation speculativeCompilation,
    IMessageHub hub,
    ILogger<MeshNodeLanguageService> logger)
    : IMeshLanguageService
{
    // Path used as the SyntaxTree.FilePath for the generated skeleton tree.
    // Distinct from any user MeshNode path so callers can never address it accidentally.
    private const string SkeletonDocumentPath = "__skeleton__.cs";

    // Per-NodeType cached workspace, invalidated when source versions change.
    // Concurrent because hub-message handlers may invoke language-service queries
    // for the same NodeType from multiple threads.
    private readonly ConcurrentDictionary<string, CachedWorkspace> _cache =
        new(StringComparer.Ordinal);

    public IObservable<IReadOnlyList<DiagnosticInfo>> GetDiagnostics(string nodeTypePath)
        => GetOrBuildWorkspace(nodeTypePath)
            .SelectMany(cached => cached is null
                ? Observable.Return<IReadOnlyList<DiagnosticInfo>>(Array.Empty<DiagnosticInfo>())
                : Observable.FromAsync(ct => GetDiagnosticsAsync(cached, ct)));

    public IObservable<HoverInfo?> GetHover(string nodeTypePath, string sourcePath, SourcePosition position)
        => GetOrBuildWorkspace(nodeTypePath)
            .SelectMany(cached =>
            {
                if (cached is null || !cached.DocumentsByPath.TryGetValue(sourcePath, out var docId))
                    return Observable.Return<HoverInfo?>(null);
                return Observable.FromAsync(ct => GetHoverAsync(cached, docId, position, ct));
            });

    public IObservable<IReadOnlyList<CompletionEntry>> GetCompletions(
        string nodeTypePath, string sourcePath, SourcePosition position, int maxResults = 20)
        => GetOrBuildWorkspace(nodeTypePath)
            .SelectMany(cached =>
            {
                if (cached is null || !cached.DocumentsByPath.TryGetValue(sourcePath, out var docId))
                    return Observable.Return<IReadOnlyList<CompletionEntry>>(Array.Empty<CompletionEntry>());
                return Observable.FromAsync(ct => GetCompletionsAsync(cached, docId, position, maxResults, ct));
            });

    public IObservable<IReadOnlyList<DiagnosticInfo>> CheckSpeculative(
        string nodeTypePath, string sourcePath, string proposedCode)
        => ResolveNode(nodeTypePath)
            .SelectMany(node => node is null
                ? Observable.Return<IReadOnlyList<DiagnosticInfo>>(Array.Empty<DiagnosticInfo>())
                : compilationService.GetCompilationInputsAsync(node)
                    .SelectMany(inputs => inputs is null
                        ? Observable.Return<IReadOnlyList<DiagnosticInfo>>(Array.Empty<DiagnosticInfo>())
                        : Observable.FromAsync(ct =>
                            speculativeCompilation.GetDiagnosticsAsync(inputs, sourcePath, proposedCode, ct))));

    /// <summary>
    /// Resolves the NodeType MeshNode, fetches <see cref="CompilationInputs"/>, and builds
    /// (or reuses) an <see cref="AdhocWorkspace"/> whose Documents mirror the input sources.
    /// Returns <c>null</c> when the node or its compilation cannot be resolved.
    /// </summary>
    private IObservable<CachedWorkspace?> GetOrBuildWorkspace(string nodeTypePath)
        => ResolveNode(nodeTypePath)
            .SelectMany(node => node is null
                ? Observable.Return<CachedWorkspace?>(null)
                : compilationService.GetCompilationInputsAsync(node)
                    .Select(inputs => inputs is null ? null : BuildOrReuseWorkspace(nodeTypePath, inputs)));

    private IObservable<MeshNode?> ResolveNode(string nodeTypePath)
        => hub.GetMeshNode(nodeTypePath, TimeSpan.FromSeconds(15));

    private CachedWorkspace BuildOrReuseWorkspace(string nodeTypePath, CompilationInputs inputs)
    {
        if (_cache.TryGetValue(nodeTypePath, out var existing)
            && VersionsEqual(existing.SourceVersions, inputs.SourceVersions))
        {
            return existing;
        }

        // Dispose the old workspace before replacing — Roslyn AdhocWorkspace
        // holds onto syntax trees / compilation state that we no longer need.
        existing?.Workspace.Dispose();

        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var projectInfo = ProjectInfo
            .Create(
                projectId,
                VersionStamp.Create(),
                name: inputs.AssemblyName,
                assemblyName: inputs.AssemblyName,
                language: LanguageNames.CSharp)
            .WithMetadataReferences(inputs.References)
            .WithCompilationOptions(inputs.CompilationOptions)
            .WithParseOptions(inputs.ParseOptions);

        workspace.AddProject(projectInfo);

        var pathToDocId = ImmutableDictionary.CreateBuilder<string, DocumentId>(StringComparer.OrdinalIgnoreCase);

        // Skeleton document — the assembly attribute + generated provider class. Carries the
        // common framework usings so user code resolves framework types.
        var skeletonDocId = DocumentId.CreateNewId(projectId);
        workspace.AddDocument(DocumentInfo.Create(
            skeletonDocId,
            name: SkeletonDocumentPath,
            filePath: SkeletonDocumentPath,
            loader: TextLoader.From(TextAndVersion.Create(
                SourceText.From(inputs.SkeletonSource), VersionStamp.Create()))));

        // One Document per user source, with the MeshNode Path as FilePath so language-service
        // queries from Monaco / MCP tools can address each file independently.
        foreach (var (path, code) in inputs.Sources)
        {
            var docId = DocumentId.CreateNewId(projectId);
            workspace.AddDocument(DocumentInfo.Create(
                docId,
                name: path,
                filePath: path,
                loader: TextLoader.From(TextAndVersion.Create(
                    SourceText.From(code), VersionStamp.Create()))));
            pathToDocId[path] = docId;
        }

        var cached = new CachedWorkspace(
            Workspace: workspace,
            ProjectId: projectId,
            SourceVersions: inputs.SourceVersions,
            DocumentsByPath: pathToDocId.ToImmutable(),
            SkeletonDocumentId: skeletonDocId);

        _cache[nodeTypePath] = cached;
        logger.LogDebug("Built AdhocWorkspace for {NodeTypePath} with {DocCount} user documents",
            nodeTypePath, inputs.Sources.Length);
        return cached;
    }

    private static bool VersionsEqual(
        ImmutableDictionary<string, long> a,
        ImmutableDictionary<string, long> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var kv in a)
        {
            if (!b.TryGetValue(kv.Key, out var v) || v != kv.Value) return false;
        }
        return true;
    }

    private static async Task<IReadOnlyList<DiagnosticInfo>> GetDiagnosticsAsync(
        CachedWorkspace cached, CancellationToken ct)
    {
        var project = cached.Workspace.CurrentSolution.GetProject(cached.ProjectId);
        if (project is null) return Array.Empty<DiagnosticInfo>();

        var compilation = await project.GetCompilationAsync(ct);
        if (compilation is null) return Array.Empty<DiagnosticInfo>();

        var diags = compilation.GetDiagnostics(ct);
        if (diags.IsDefaultOrEmpty) return Array.Empty<DiagnosticInfo>();

        var result = new List<DiagnosticInfo>(diags.Length);
        foreach (var d in diags)
        {
            // Skip diagnostics that fall inside the skeleton tree — they're artifacts of
            // the generated code, not actionable for the user editing source files.
            var path = d.Location.SourceTree?.FilePath;
            if (path == SkeletonDocumentPath) continue;

            result.Add(ToDiagnosticInfo(d));
        }
        return result;
    }

    private static async Task<HoverInfo?> GetHoverAsync(
        CachedWorkspace cached, DocumentId docId, SourcePosition position, CancellationToken ct)
    {
        var document = cached.Workspace.CurrentSolution.GetDocument(docId);
        if (document is null) return null;

        var text = await document.GetTextAsync(ct);
        var offset = TryGetOffset(text, position);
        if (offset is null) return null;

        var service = QuickInfoService.GetService(document);
        if (service is null) return null;

        var info = await service.GetQuickInfoAsync(document, offset.Value, ct);
        if (info is null) return null;

        var markdown = RenderQuickInfo(info);
        var range = SpanToRange(text, info.Span);
        return new HoverInfo(markdown, range);
    }

    private static async Task<IReadOnlyList<CompletionEntry>> GetCompletionsAsync(
        CachedWorkspace cached, DocumentId docId, SourcePosition position, int maxResults, CancellationToken ct)
    {
        var document = cached.Workspace.CurrentSolution.GetDocument(docId);
        if (document is null) return Array.Empty<CompletionEntry>();

        var text = await document.GetTextAsync(ct);
        var offset = TryGetOffset(text, position);
        if (offset is null) return Array.Empty<CompletionEntry>();

        var service = CompletionService.GetService(document);
        if (service is null) return Array.Empty<CompletionEntry>();

        var list = await service.GetCompletionsAsync(document, offset.Value, cancellationToken: ct);
        if (list is null || list.ItemsList.Count == 0) return Array.Empty<CompletionEntry>();

        var take = Math.Min(maxResults, list.ItemsList.Count);
        var result = new List<CompletionEntry>(take);
        for (var i = 0; i < take; i++)
        {
            var item = list.ItemsList[i];
            result.Add(new CompletionEntry(
                Label: item.DisplayText,
                Kind: MapTagsToKind(item.Tags),
                InsertText: item.DisplayText,
                Detail: item.InlineDescription is { Length: > 0 } ? item.InlineDescription : null,
                Documentation: null,
                SortText: item.SortText));
        }
        return result;
    }

    private static DiagnosticInfo ToDiagnosticInfo(Diagnostic d)
    {
        SourceLocation? location = null;
        if (d.Location.IsInSource && d.Location.SourceTree?.FilePath is { Length: > 0 } path)
        {
            var span = d.Location.GetLineSpan();
            location = new SourceLocation(
                path,
                new SourceRange(
                    new SourcePosition(span.StartLinePosition.Line, span.StartLinePosition.Character),
                    new SourcePosition(span.EndLinePosition.Line, span.EndLinePosition.Character)));
        }
        return new DiagnosticInfo(
            Id: d.Id,
            Severity: MapSeverity(d.Severity),
            Message: d.GetMessage(),
            Location: location);
    }

    private static LspDiagnosticSeverity MapSeverity(RoslynDiagnosticSeverity s) => s switch
    {
        RoslynDiagnosticSeverity.Hidden => LspDiagnosticSeverity.Hidden,
        RoslynDiagnosticSeverity.Info => LspDiagnosticSeverity.Info,
        RoslynDiagnosticSeverity.Warning => LspDiagnosticSeverity.Warning,
        RoslynDiagnosticSeverity.Error => LspDiagnosticSeverity.Error,
        _ => LspDiagnosticSeverity.Info,
    };

    private static int? TryGetOffset(SourceText text, SourcePosition position)
    {
        if (position.Line < 0 || position.Line >= text.Lines.Count) return null;
        var line = text.Lines[position.Line];
        var character = Math.Max(0, position.Character);
        if (character > line.End - line.Start) return null;
        return line.Start + character;
    }

    private static SourceRange? SpanToRange(SourceText text, TextSpan span)
    {
        if (span.IsEmpty && span.Start == 0) return null;
        var line = text.Lines.GetLinePositionSpan(span);
        return new SourceRange(
            new SourcePosition(line.Start.Line, line.Start.Character),
            new SourcePosition(line.End.Line, line.End.Character));
    }

    private static string RenderQuickInfo(QuickInfoItem info)
    {
        // Concatenate sections as markdown — Description as a C# code block, Documentation as plain markdown.
        var sb = new System.Text.StringBuilder();
        foreach (var section in info.Sections)
        {
            var text = section.Text;
            if (string.IsNullOrEmpty(text)) continue;

            if (section.Kind == QuickInfoSectionKinds.Description)
            {
                sb.AppendLine("```csharp");
                sb.AppendLine(text);
                sb.AppendLine("```");
            }
            else
            {
                sb.AppendLine(text);
            }
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    private static CompletionKind MapTagsToKind(ImmutableArray<string> tags)
    {
        if (tags.IsDefaultOrEmpty) return CompletionKind.Text;
        foreach (var tag in tags)
        {
            switch (tag)
            {
                case WellKnownTags.Class: return CompletionKind.Class;
                case WellKnownTags.Constant: return CompletionKind.Constant;
                case WellKnownTags.Delegate: return CompletionKind.Function;
                case WellKnownTags.Enum: return CompletionKind.Enum;
                case WellKnownTags.EnumMember: return CompletionKind.EnumMember;
                case WellKnownTags.Event: return CompletionKind.Event;
                case WellKnownTags.ExtensionMethod: return CompletionKind.Method;
                case WellKnownTags.Field: return CompletionKind.Field;
                case WellKnownTags.Interface: return CompletionKind.Interface;
                case WellKnownTags.Keyword: return CompletionKind.Keyword;
                case WellKnownTags.Local: return CompletionKind.Variable;
                case WellKnownTags.Method: return CompletionKind.Method;
                case WellKnownTags.Module: return CompletionKind.Module;
                case WellKnownTags.Namespace: return CompletionKind.Module;
                case WellKnownTags.Operator: return CompletionKind.Operator;
                case WellKnownTags.Parameter: return CompletionKind.Variable;
                case WellKnownTags.Property: return CompletionKind.Property;
                case WellKnownTags.Snippet: return CompletionKind.Snippet;
                case WellKnownTags.Structure: return CompletionKind.Struct;
                case WellKnownTags.TypeParameter: return CompletionKind.TypeParameter;
            }
        }
        return CompletionKind.Text;
    }

    private sealed record CachedWorkspace(
        AdhocWorkspace Workspace,
        ProjectId ProjectId,
        ImmutableDictionary<string, long> SourceVersions,
        ImmutableDictionary<string, DocumentId> DocumentsByPath,
        DocumentId SkeletonDocumentId);
}
