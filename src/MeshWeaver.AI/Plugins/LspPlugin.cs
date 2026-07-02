using System.ComponentModel;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services.LanguageServer;
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.AI.Plugins;

/// <summary>
/// Stage-1 LSP language-services plugin for code-authoring agents (the <c>/code</c> skill's
/// pre-flight loop). Exposes Roslyn-backed pre-flight diagnostics, hover, and completions over a
/// NodeType's live <c>CSharpCompilation</c> — the same surface as the <c>lsp_*</c> MCP tools,
/// mounted only on agents that opt into <c>plugins: - Lsp</c> so agents that never author code
/// (e.g. Researcher) don't see the noise.
///
/// <para>
/// Method names mirror the MCP surface (<c>McpMeshPlugin.LspCheckNode</c> etc.) so docs
/// and the <c>/code</c> skill's instructions can reference one set of tool names regardless of
/// transport. Path arguments go through <see cref="MeshOperations.ResolveContextPath"/>
/// so relative paths (<c>@MyChild/...</c>) resolve against the current chat context,
/// matching <see cref="MeshPlugin"/>'s conventions.
/// </para>
/// </summary>
public class LspPlugin(IMessageHub hub, IAgentChat chat)
{
    private readonly ILogger<LspPlugin> logger = hub.ServiceProvider.GetRequiredService<ILogger<LspPlugin>>();
    // Optional: IMeshLanguageService is registered by AddGraph(). On an AI-only mesh the
    // plugin mounts no tools (CreateTools returns empty) instead of failing agent creation —
    // the built-in agents (Assistant, Worker, Email Router) declare `plugins: - Lsp` for the
    // /code skill's pre-flight loop, and must still build where Graph isn't configured.
    private readonly IMeshLanguageService? languageService =
        hub.ServiceProvider.GetService<IMeshLanguageService>();
    private readonly AccessService? accessService = hub.ServiceProvider.GetService<AccessService>();

    /// <summary>
    /// Agent tool: speculative pre-flight Roslyn check. Compiles the NodeType's current source set
    /// with one file substituted by <paramref name="proposedCode"/> and returns all diagnostics, with
    /// no emit, recycle, or side effects.
    /// </summary>
    /// <param name="nodeTypePath">Path to the NodeType (e.g. <c>@ACME/Story</c>).</param>
    /// <param name="sourcePath">Path of the Source Code node being edited; added as a new file if not in the current source set.</param>
    /// <param name="proposedCode">The proposed full source text for that file.</param>
    /// <returns>A JSON string <c>{ok, diagnostics:[...]}</c>; <c>ok</c> is true when the substituted source compiles without errors.</returns>
    [Description(@"PRE-FLIGHT CHECK before committing a source change to a NodeType. Runs Roslyn against the NodeType's current source set with ONE source file substituted by `proposedCode`, returns all diagnostics (errors + warnings). No emit, no Recycle, no side effects — purely speculative.

Use this in the code edit loop: edit a Source/*.cs file in your head → `LspCheckNode` → if diagnostics, fix → repeat → only then `Patch` + `Compile`. Eliminates the costly blind-patch / Compile / fix cycle.

Returns `{ok: true, diagnostics: []}` when the substituted source compiles cleanly, or `{ok: false, diagnostics: [{id, severity, message, sourcePath?, line?, character?}, ...]}` when it doesn't. Severity is one of `Hidden|Info|Warning|Error`. Positions are 0-based.")]
    public Task<string> LspCheckNode(
        [Description("Path to the NodeType (e.g., @ACME/Story).")] string nodeTypePath,
        [Description("Path of the Source Code node being edited (e.g., @ACME/Story/Source/StoryTypes.cs). If not in the current source set, the proposed code is added as a new file.")] string sourcePath,
        [Description("The proposed full source text for that file.")] string proposedCode)
        => WithContext(() => languageService!.CheckSpeculative(
                MeshOperations.ResolvePath(MeshOperations.ResolveContextPath(chat, nodeTypePath)),
                MeshOperations.ResolvePath(MeshOperations.ResolveContextPath(chat, sourcePath)),
                proposedCode ?? string.Empty))
            .Select(diagnostics => FormatDiagnosticsJson(diagnostics))
            .FirstAsync().ToTask();

    /// <summary>
    /// Agent tool: enumerates every Roslyn diagnostic (errors, warnings, info) from the NodeType's
    /// current cached compilation, with source locations — without re-compiling.
    /// </summary>
    /// <param name="nodeTypePath">Path to the NodeType (e.g. <c>@ACME/Story</c>).</param>
    /// <returns>A JSON string <c>{ok, diagnostics:[...]}</c>; an empty list with <c>ok:true</c> means clean.</returns>
    [Description(@"Returns Roslyn diagnostics from the NodeType's CURRENT cached compilation — distinct from `GetDiagnostics` which only reports compile status (Ok/Error/Compiling). This enumerates every diagnostic in the compilation (errors + warnings + info) with source location, so you can see exactly what's wrong without re-compiling.

Returns `{ok: true|false, diagnostics: [...]}` — same shape as `LspCheckNode`. Empty `diagnostics` plus `ok:true` means clean.")]
    public Task<string> LspDiagnosticsForNode(
        [Description("Path to the NodeType (e.g., @ACME/Story).")] string nodeTypePath)
        => WithContext(() => languageService!.GetDiagnostics(
                MeshOperations.ResolvePath(MeshOperations.ResolveContextPath(chat, nodeTypePath))))
            .Select(diagnostics => FormatDiagnosticsJson(diagnostics))
            .FirstAsync().ToTask();

    /// <summary>
    /// Agent tool: Roslyn QuickInfo (hover) at a position in a Source Code file, returning the
    /// symbol's signature and XML doc summary as markdown.
    /// </summary>
    /// <param name="nodeTypePath">Path to the NodeType (e.g. <c>@ACME/Story</c>).</param>
    /// <param name="sourcePath">Path of the Source Code node.</param>
    /// <param name="line">0-based line number.</param>
    /// <param name="character">0-based character offset within the line.</param>
    /// <returns>A JSON string <c>{markdown:"..."}</c> when a symbol resolves, or <c>{}</c> when nothing is at the position.</returns>
    [Description(@"Roslyn QuickInfo (hover tooltip) at a position in a Source Code file. Returns the symbol's signature and XML doc summary as markdown.

Returns `{markdown: ""..."" }` when a symbol resolves at the position, or `{}` when nothing is there. Positions are 0-based (LSP convention) — line 0 is the first line.")]
    public Task<string> LspHoverForNode(
        [Description("Path to the NodeType (e.g., @ACME/Story).")] string nodeTypePath,
        [Description("Path of the Source Code node (e.g., @ACME/Story/Source/StoryTypes.cs).")] string sourcePath,
        [Description("0-based line number.")] int line,
        [Description("0-based character offset within the line.")] int character)
        => WithContext(() => languageService!.GetHover(
                MeshOperations.ResolvePath(MeshOperations.ResolveContextPath(chat, nodeTypePath)),
                MeshOperations.ResolvePath(MeshOperations.ResolveContextPath(chat, sourcePath)),
                new SourcePosition(line, character)))
            .Select(hover => JsonSerializer.Serialize(
                hover is null ? new { } : (object)new { markdown = hover.ContentMarkdown },
                hub.JsonSerializerOptions))
            .FirstAsync().ToTask();

    /// <summary>
    /// Agent tool: Roslyn code completions at a position in a Source Code file, returning up to
    /// <paramref name="max"/> suggestions sorted by Roslyn's relevance.
    /// </summary>
    /// <param name="nodeTypePath">Path to the NodeType (e.g. <c>@ACME/Story</c>).</param>
    /// <param name="sourcePath">Path of the Source Code node.</param>
    /// <param name="line">0-based line number.</param>
    /// <param name="character">0-based character offset within the line.</param>
    /// <param name="max">Maximum number of completions to return; default 20.</param>
    /// <returns>A JSON string <c>{items:[{label, kind, insertText, detail?, sortText?}, ...]}</c>; empty <c>items</c> means no completions.</returns>
    [Description(@"Roslyn code completions at a position in a Source Code file. Returns up to `max` suggestions with kind / insert text / detail / sort key, sorted by Roslyn's relevance.

Returns `{items: [{label, kind, insertText, detail?, sortText?}, ...]}`. `kind` is the LSP completion-item kind name (`Method`, `Class`, `Field`, etc.). Empty `items` means no completions at that position. Positions are 0-based.")]
    public Task<string> LspCompletionsForNode(
        [Description("Path to the NodeType (e.g., @ACME/Story).")] string nodeTypePath,
        [Description("Path of the Source Code node (e.g., @ACME/Story/Source/StoryTypes.cs).")] string sourcePath,
        [Description("0-based line number.")] int line,
        [Description("0-based character offset within the line.")] int character,
        [Description("Maximum number of completions to return. Default 20.")] int max = 20)
        => WithContext(() => languageService!.GetCompletions(
                MeshOperations.ResolvePath(MeshOperations.ResolveContextPath(chat, nodeTypePath)),
                MeshOperations.ResolvePath(MeshOperations.ResolveContextPath(chat, sourcePath)),
                new SourcePosition(line, character),
                max))
            .Select(items => JsonSerializer.Serialize(
                new
                {
                    items = items.Select(i => new
                    {
                        label = i.Label,
                        kind = i.Kind.ToString(),
                        insertText = i.InsertText,
                        detail = i.Detail,
                        sortText = i.SortText,
                    }).ToArray()
                },
                hub.JsonSerializerOptions))
            .FirstAsync().ToTask();

    /// <summary>
    /// AccessContext re-seed on Subscribe — mirrors <see cref="MeshPlugin.WithContext{T}"/>.
    /// AsyncLocal doesn't flow through the agent framework's streaming + tool-invocation
    /// pipeline, so every plugin entry point that touches hub state must re-seed.
    /// </summary>
    private IObservable<T> WithContext<T>(Func<IObservable<T>> work) =>
        Observable.Defer(() =>
        {
            var userCtx = chat.ExecutionContext?.UserAccessContext;
            if (userCtx != null)
                accessService?.SetContext(userCtx);
            return work();
        });

    private string FormatDiagnosticsJson(IReadOnlyList<DiagnosticInfo> diagnostics)
    {
        var anyErrors = diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
        return JsonSerializer.Serialize(
            new
            {
                ok = !anyErrors,
                diagnostics = diagnostics.Select(d => new
                {
                    id = d.Id,
                    severity = d.Severity.ToString(),
                    message = d.Message,
                    sourcePath = d.Location?.SourcePath,
                    line = d.Location?.Range.Start.Line,
                    character = d.Location?.Range.Start.Character,
                }).ToArray()
            },
            hub.JsonSerializerOptions);
    }

    /// <summary>
    /// Builds the LSP agent tools: speculative check, diagnostics, hover, and completions.
    /// Empty when <see cref="IMeshLanguageService"/> is not registered on this mesh
    /// (language services ship with <c>AddGraph()</c>).
    /// </summary>
    /// <returns>The plugin's <see cref="AITool"/> instances.</returns>
    public IList<AITool> CreateTools()
    {
        if (languageService is null)
        {
            logger.LogDebug(
                "IMeshLanguageService is not registered on this mesh — the Lsp plugin mounts no tools.");
            return [];
        }

        return
        [
            AIFunctionFactory.Create(LspCheckNode),
            AIFunctionFactory.Create(LspDiagnosticsForNode),
            AIFunctionFactory.Create(LspHoverForNode),
            AIFunctionFactory.Create(LspCompletionsForNode),
        ];
    }
}
