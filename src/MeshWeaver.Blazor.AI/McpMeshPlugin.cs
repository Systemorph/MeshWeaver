using System.ComponentModel;
using System.Net;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using MeshWeaver.AI;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Services.LanguageServer;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace MeshWeaver.Blazor.AI;

/// <summary>
/// MCP wrapper exposing mesh operations as MCP tools.
/// Thin wrapper over <see cref="MeshOperations"/> with MCP attributes and
/// URL-based NavigateTo.
///
/// <para>
/// <b>Session hub</b>: on construction, the plugin materialises a session-scoped
/// hub at <c>portal/mcp-{callerId}-{mcpSessionId}</c> — exactly mirroring the
/// Blazor <c>PortalApplication</c> pattern. Portal-typed addresses are skipped
/// from Orleans grain resolution (<c>RoutingGrain</c>) and the sub-hub is
/// registered with the routing service so responses (e.g. kernel
/// <c>SubmitCodeRequest</c> ack) route back correctly. Inlines the same
/// <c>RouteAddressToHostedHub("kernel", ...)</c> rule so in-session kernel
/// execution stays local.
/// </para>
///
/// <para>Each authenticated caller × MCP session id gets its own hub; idle
/// hubs dispose when the MCP connection ends.</para>
/// </summary>
[McpServerToolType]
public class McpMeshPlugin
{
    private readonly MeshOperations ops;
    private readonly IMessageHub rootHub;
    private readonly IMessageHub sessionHub;
    private readonly ILogger<McpMeshPlugin> logger;
    private readonly string baseUrl;

    public McpMeshPlugin(
        IMessageHub hub,
        IOptions<McpConfiguration>? config = null,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        logger = hub.ServiceProvider.GetRequiredService<ILogger<McpMeshPlugin>>();
        // Resolve the UI base URL in priority order. No hard-coded URLs:
        //   1. Configured McpConfiguration.BaseUrl — Aspire AppHost passes the
        //      portal's external HTTPS endpoint via env var (Mcp__BaseUrl) so
        //      the deployment topology owns this; no per-environment patching
        //      of source. Same mechanism in prod / test / local.
        //   2. Current HTTP request's scheme + host — the live answer when MCP
        //      is invoked over the same portal that serves the UI; correct for
        //      any port Aspire allocates dynamically without any config.
        //   3. Empty (signals "no base URL resolvable") — surfaces in the URL
        //      string returned by GetBaseUrl / NavigateTo so the caller sees
        //      a clearly-broken value instead of a quietly-wrong localhost
        //      one. Better to fail loud than to ship the wrong URL.
        var requestUrl = httpContextAccessor?.HttpContext?.Request is { } req
            ? $"{req.Scheme}://{req.Host.Value}".TrimEnd('/')
            : null;
        baseUrl = config?.Value.BaseUrl
            ?? requestUrl
            ?? string.Empty;
        rootHub = hub;

        // The session hub IS the MCP-side actor: registered with the routing
        // service so responses route back via the standard portal/* path.
        // No per-call kernel hub — execute_script flows through the Code hub,
        // which creates an Activity MeshNode whose hub hosts the kernel
        // (see ActivityNodeType.HubConfiguration + AddKernelSubHubHandlers).
        // Replies route through the standard MeshNode chain to portal/mcp-…
        // — same routing every other MCP tool already uses (Get, Search, …).
        // SessionHubResolver is shared with the REST endpoint module so both
        // transports get identical routing semantics.
        sessionHub = SessionHubResolver.ResolveSessionHub(hub, httpContextAccessor?.HttpContext, "mcp", logger);

        ops = new MeshOperations(sessionHub);
    }

    [McpServerTool]
    [Description(@"Retrieves a node or a resource attached to a node by path. Returns JSON for nodes/data/schemas, or raw file bytes (JSON-escaped) for content-collection files.

Path shapes:
  • `@Node/Path`               — the MeshNode itself (metadata + Content)
  • `@Node/Path/*`             — immediate children of the node
  • `@Node/Path/data/`         — node Content as structured JSON (whole model)
  • `@Node/Path/data/Type/id`  — one entity from the node's data collection
  • `@Node/Path/schema/`       — JSON Schema of the node's Content type
  • `@Node/Path/schema/Type`   — schema for a specific type
  • `@Node/Path/model/`        — full data model with all registered types
  • `@Node/Path/layoutAreas/`  — list of layout areas on the node
  • `@Node/Path/area/Name`     — that layout area's rendered payload
  • `@Node/Path/content/file.ext`            — file from the 'content' collection
  • `@Node/Path/content/subfolder/file.ext`  — file from a nested path
  • `@Node/Path/{collection}/file.ext`       — file from a NAMED collection (e.g. 'Files/', 'assets/')
  • `@Node/Path/collection/`                 — list of collection configs on the node
  • `@Node/Path/collection/name1,name2`      — specific collection configs
Legacy colon form `path/prefix:value` still works for backward compatibility.")]
    public Task<string> Get(
        [Description(@"Path to data. Examples:
  @graph/org1                                   (node)
  @Agent/*                                      (children)
  @Systemorph/FutuRe/EuropeRe/content/LargeClaims.xlsx  (file from 'content' collection)
  @Doc/Architecture/content/icon.svg            (file)
  @Cornerstone/schema/TypeName                  (schema)
  @Cornerstone/model/                           (full model)")] string path)
        => ops.Get(path).FirstAsync().ToTask();

    [McpServerTool]
    [Description(@"Uploads raw file bytes into a node's content collection — the write-side mirror of `Get` for content-collection files. Use this to attach images, documents, or any binary asset to a node (e.g. an organisation logo or an Excel input file for a script).

Path shapes (must include a collection segment + filename):
  • `@Node/Path/content/file.ext`            — write into the default 'content' collection
  • `@Node/Path/content/subfolder/file.ext`  — nested path within the collection
  • `@Node/Path/{collection}/file.ext`       — write into a named collection (e.g. 'Files/', 'assets/')

The target collection must exist on the node and be editable (`IsEditable=true`). Returns JSON like
`{""status"":""Uploaded"",""path"":""Systemorph/content/logo.png"",""bytes"":4958}` on success, or an `Error: …` string otherwise.")]
    public Task<string> Upload(
        [Description(@"Target path including collection + filename, e.g. '@Systemorph/content/logo.png' or '@Doc/Architecture/content/diagrams/flow.svg'. The path is parsed as {nodePath}/{collection}/{filePath}.")] string path,
        [Description("File content as base64-encoded bytes (no data:URI prefix; just the raw base64 payload).")] string base64Content)
    {
        if (string.IsNullOrEmpty(base64Content))
            return Task.FromResult("Error: base64Content is required.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64Content); }
        catch (FormatException ex) { return Task.FromResult($"Error: invalid base64 content: {ex.Message}"); }
        return ops.Upload(path, bytes).FirstAsync().ToTask();
    }

    [McpServerTool]
    [Description("Searches the mesh using GitHub-style query syntax. Returns up to 50 matching nodes.")]
    public Task<string> Search(
        [Description("Query string (e.g., 'nodeType:Agent', 'laptop', 'path:ACME scope:descendants', 'name:*sales*')")] string query,
        [Description("Base path to search from (e.g., @graph). Empty for all.")] string? basePath = null)
        => ops.Search(query, basePath).FirstAsync().ToTask();

    [McpServerTool]
    [Description("Creates a new node in the mesh. Pass a JSON MeshNode object with id, namespace, name, nodeType, and content fields.")]
    public Task<string> Create(
        [Description("JSON MeshNode object to create (e.g., {\"id\": \"NewOrg\", \"namespace\": \"ACME\", \"name\": \"New Org\", \"nodeType\": \"Organization\", \"content\": {}})")] string node)
        => ops.Create(node).FirstAsync().ToTask();

    [McpServerTool]
    [Description("Updates existing nodes in the mesh. Pass a JSON array of complete MeshNode objects. Always Get before Update — the entire node is replaced, not merged.")]
    public Task<string> Update(
        [Description("JSON array of MeshNode objects with all fields (get existing node first, modify, then pass here)")] string nodes)
        => ops.Update(nodes).FirstAsync().ToTask();

    [McpServerTool]
    [Description("Partial update of a single node. Only the keys present in 'fields' are changed; omitted keys preserve existing values. Do NOT include 'content' unless overwriting — never set 'content' to null. Prefer this over Update for small edits like icon/name/category.")]
    public Task<string> Patch(
        [Description("Path to the node (e.g., @User/rbuergi/my-node)")] string path,
        [Description("JSON object with ONLY the fields to change. Examples: {\"icon\": \"<svg>...</svg>\"}, {\"name\": \"New Name\"}.")] string fields)
        => ops.Patch(path, fields).FirstAsync().ToTask();

    [McpServerTool]
    [Description("Deletes one or more nodes from the mesh by path. Recursive: deleting a parent removes all descendants. To remove a subtree, just pass the root path — children do not need to be enumerated.")]
    public Task<string> Delete(
        [Description("JSON array of path strings to delete (e.g., [\"ACME/OldProject\", \"ACME/ArchivedTask\"])")] string paths)
        => ops.Delete(paths).FirstAsync().ToTask();

    [McpServerTool]
    [Description("Moves a node and its descendants to a new path. Equivalent to the Move menu item. Requires Delete on the source namespace and Create on the target. Source and target are full paths (namespace + id), e.g. 'OrgA/Child' -> 'OrgB/Child'.")]
    public Task<string> Move(
        [Description("Current path of the node (e.g., @OrgA/Child)")] string sourcePath,
        [Description("New path for the node (e.g., @OrgB/Child)")] string targetPath)
        => ops.Move(sourcePath, targetPath).FirstAsync().ToTask();

    [McpServerTool]
    [Description("Copies a node and all its descendants to a target namespace. Equivalent to the Copy menu item. Source ids are preserved; paths are rewritten under the target namespace.")]
    public Task<string> Copy(
        [Description("Current path of the node to copy (e.g., @OrgA/Child)")] string sourcePath,
        [Description("Target namespace to copy under (e.g., @OrgB)")] string targetNamespace,
        [Description("Overwrite existing nodes at the target. Default: false.")] bool force = false)
        => ops.Copy(sourcePath, targetNamespace, force).FirstAsync().ToTask();

    [McpServerTool]
    [Description("Returns a URL to view a node in the MeshWeaver UI. The URL shape is `{baseUrl}/{path}` — the mesh path is appended directly to the base URL with no intermediate segment (no `/node/`) and without URL-escaping the path separators. Use this when you want to give a user a link to open in their browser. For the base URL on its own, use `GetBaseUrl`.")]
    public string NavigateTo(
        [Description("Path to navigate to (e.g., @Systemorph/FutuRe/EuropeRe). Leading `@` is stripped.")] string path)
    {
        logger.LogInformation("MCP NavigateTo called with path={Path}", path);

        var resolvedPath = MeshOperations.ResolvePath(path);
        return $"{baseUrl.TrimEnd('/')}/{resolvedPath.TrimStart('/')}";
    }

    [McpServerTool]
    [Description("Returns the MeshWeaver UI base URL configured for this MCP server (e.g. `https://memex.meshweaver.cloud` in prod, `http://localhost:5000` in dev). Every node's browser URL is just `{baseUrl}/{meshpath}` — no `/node/` segment, no URL-escaping of path separators.")]
    public string GetBaseUrl() => baseUrl.TrimEnd('/');

    [McpServerTool]
    [Description("Returns compilation diagnostics for a NodeType (or any instance of one). Status is 'Ok' when the type compiled cleanly, 'Error' with details when it failed, 'Compiling' while a compile is in progress (with elapsedMs), or 'Unknown' when no compile has happened yet. Use after creating/updating a NodeType to verify it actually compiles — a NodeType that doesn't compile is not 'done'.")]
    public Task<string> GetDiagnostics(
        [Description("Path to a NodeType (e.g., @Systemorph/SocialMedia/Profile) or to any instance of one")] string path)
        => ops.GetDiagnostics(path).FirstAsync().ToTask();

    [McpServerTool]
    [Description("Recycles the hub at the given path by posting DisposeRequest. Forces a fresh hub initialization on the next access — use after fixing a broken NodeType, after editing the `sources` list, or whenever a grain is stuck in a cached bad state. Returns {status:'Recycled', path}. Wait ~100ms before the next access so the grain teardown completes.")]
    public Task<string> Recycle(
        [Description("Path to the node (e.g., @Systemorph/SocialMedia/Profile). Use the NodeType path to recycle the whole type; use an instance path to recycle just that instance's hub.")] string path)
        => ops.Recycle(path).FirstAsync().ToTask();

    [McpServerTool]
    [Description(@"Compiles a NodeType and waits for the result inline. Flips the NodeType's `compilationStatus` to `Pending` via the canonical remote-stream `Update` (no PatchDataRequest, no Update permission required), then subscribes to the NodeType's MeshNode stream and waits up to 60s for the framework's CompileWatcher to settle the status to `Ok` or `Error`.

Returns a structured result:
  • `{status:'Ok', path, activityPath, message:'Compile SUCCEEDED.'}` — assembly cached, ready to use.
  • `{status:'Error', path, error, activityPath, message:'Compile FAILED ...'}` — `error` carries the Roslyn diagnostics inline.
  • `{status:'Pending', path, message:'... did not settle within deadline'}` — fallback only on timeout; `get @nodeTypePath` to poll.

For the full source-discovery + matched-Code-paths + Roslyn trace, `get @<activityPath>` after the call returns.")]
    public Task<string> Compile(
        [Description("Path to the NodeType (e.g., @User/me/MyType or @Systemorph/SocialMedia/Profile). Must point at a NodeType definition node, not an instance.")] string path)
        => ops.Compile(path).FirstAsync().ToTask();

    [McpServerTool]
    [Description(@"PRE-FLIGHT CHECK before committing a source change to a NodeType. Runs Roslyn against the NodeType's current source set with ONE source file substituted by `proposedCode`, returns all diagnostics (errors + warnings). No emit, no Recycle, no side effects — purely speculative.

Use this in the Coder edit loop: edit a Source/*.cs file in your head → `lsp_check_node` → if diagnostics, fix → repeat → only then `patch` + `compile`. Eliminates the costly blind-patch / Compile / fix cycle.

Returns `{ok: true, diagnostics: []}` when the substituted source compiles cleanly, or `{ok: false, diagnostics: [{id, severity, message, sourcePath?, line?, character?}, ...]}` when it doesn't. Severity is one of `Hidden|Info|Warning|Error`. Positions are 0-based.")]
    public Task<string> LspCheckNode(
        [Description("Path to the NodeType (e.g., @ACME/Story).")] string nodeTypePath,
        [Description("Path of the Source Code node being edited (e.g., @ACME/Story/Source/StoryTypes.cs). If not in the current source set, the proposed code is added as a new file.")] string sourcePath,
        [Description("The proposed full source text for that file.")] string proposedCode)
    {
        var lang = rootHub.ServiceProvider.GetRequiredService<IMeshLanguageService>();
        return lang.CheckSpeculative(
                MeshOperations.ResolvePath(nodeTypePath),
                MeshOperations.ResolvePath(sourcePath),
                proposedCode ?? string.Empty)
            .Select(diagnostics => FormatDiagnosticsJson(diagnostics, sessionHub.JsonSerializerOptions))
            .FirstAsync().ToTask();
    }

    [McpServerTool]
    [Description(@"Returns Roslyn diagnostics from the NodeType's CURRENT cached compilation — distinct from `GetDiagnostics` which only reports compile status (Ok/Error/Compiling). This enumerates every diagnostic in the compilation (errors + warnings + info) with source location, so you can see exactly what's wrong without re-compiling.

Returns `{ok: true|false, diagnostics: [...]}` — same shape as `lsp_check_node`. Empty `diagnostics` plus `ok:true` means clean.")]
    public Task<string> LspDiagnosticsForNode(
        [Description("Path to the NodeType (e.g., @ACME/Story).")] string nodeTypePath)
    {
        var lang = rootHub.ServiceProvider.GetRequiredService<IMeshLanguageService>();
        return lang.GetDiagnostics(MeshOperations.ResolvePath(nodeTypePath))
            .Select(diagnostics => FormatDiagnosticsJson(diagnostics, sessionHub.JsonSerializerOptions))
            .FirstAsync().ToTask();
    }

    [McpServerTool]
    [Description(@"Roslyn QuickInfo (hover tooltip) at a position in a Source Code file. Returns the symbol's signature and XML doc summary as markdown, ready to display.

Returns `{markdown: ""..."" }` when a symbol resolves at the position, or `{}` when nothing is there. Positions are 0-based (LSP convention) — line 0 is the first line.")]
    public Task<string> LspHoverForNode(
        [Description("Path to the NodeType (e.g., @ACME/Story).")] string nodeTypePath,
        [Description("Path of the Source Code node (e.g., @ACME/Story/Source/StoryTypes.cs).")] string sourcePath,
        [Description("0-based line number.")] int line,
        [Description("0-based character offset within the line.")] int character)
    {
        var lang = rootHub.ServiceProvider.GetRequiredService<IMeshLanguageService>();
        return lang.GetHover(
                MeshOperations.ResolvePath(nodeTypePath),
                MeshOperations.ResolvePath(sourcePath),
                new SourcePosition(line, character))
            .Select(hover => JsonSerializer.Serialize(
                hover is null ? new { } : (object)new { markdown = hover.ContentMarkdown },
                sessionHub.JsonSerializerOptions))
            .FirstAsync().ToTask();
    }

    [McpServerTool]
    [Description(@"Roslyn code completions at a position in a Source Code file. Returns up to `max` suggestions with kind / insert text / detail / sort key, sorted by Roslyn's relevance.

Returns `{items: [{label, kind, insertText, detail?, sortText?}, ...]}`. `kind` is the LSP completion-item kind name (`Method`, `Class`, `Field`, etc.). Empty `items` means no completions at that position. Positions are 0-based.")]
    public Task<string> LspCompletionsForNode(
        [Description("Path to the NodeType (e.g., @ACME/Story).")] string nodeTypePath,
        [Description("Path of the Source Code node (e.g., @ACME/Story/Source/StoryTypes.cs).")] string sourcePath,
        [Description("0-based line number.")] int line,
        [Description("0-based character offset within the line.")] int character,
        [Description("Maximum number of completions to return. Default 20.")] int max = 20)
    {
        var lang = rootHub.ServiceProvider.GetRequiredService<IMeshLanguageService>();
        return lang.GetCompletions(
                MeshOperations.ResolvePath(nodeTypePath),
                MeshOperations.ResolvePath(sourcePath),
                new SourcePosition(line, character),
                max)
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
                sessionHub.JsonSerializerOptions))
            .FirstAsync().ToTask();
    }

    /// <summary>
    /// Shared diagnostic JSON shape for the lsp_check_node + lsp_diagnostics_for_node tools.
    /// <c>ok</c> is true when the diagnostic list has no Error-severity entries — warnings
    /// alone don't fail the check (mirrors how a regular compile succeeds with warnings).
    /// </summary>
    private static string FormatDiagnosticsJson(IReadOnlyList<DiagnosticInfo> diagnostics, JsonSerializerOptions options)
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
            options);
    }

    [McpServerTool]
    [Description("Runs an executable Code node's C# through the kernel (Microsoft.DotNet.Interactive) and returns stdout / return value / errors. The target node must have `CodeConfiguration.IsExecutable == true`. Blocks until the kernel signals completion (side-effects — e.g. mesh.CreateNode calls inside the script — have happened by the time this returns). Use to run import/test scripts from MCP without needing a UI click.")]
    public Task<string> ExecuteScript(
        [Description("Path to an executable Code node (e.g., @Systemorph/FutuRe/EuropeRe/AcmeSubmission2025/Script/ImportLargeClaims). Must be `IsExecutable=true`.")] string path,
        [Description("Timeout in seconds. Default 120.")] int timeoutSeconds = 120)
        => ops.ExecuteScript(path, timeoutSeconds).FirstAsync().ToTask();

    [McpServerTool]
    [Description(@"Mirror a subtree from THIS instance up to a remote MeshWeaver portal. Recursively reads every node under `sourcePath` (along with partition data) from the local persistence and creates/updates them on the remote. Authentication is the remote's standard ApiToken Bearer flow — issue a token on the destination once, paste it here.

Use cases:
  • Promote local development content to prod (`remoteBaseUrl=https://memex.meshweaver.cloud`).
  • Stage a snapshot to a peer instance for review.

Returns a JSON summary: `{status, direction:'Push', sourcePath, targetPath, nodesImported, nodesSkipped, nodesRemoved, partitionsImported, elapsedMs}`. With `dryRun=true` returns `{status:'DryRun', nodesScanned, paths:[...]}` so you can preview before writing.

Network: this instance must have outbound HTTPS reach to `remoteBaseUrl`. Prod can't reach localhost — for the reverse direction (prod→local) you need a tunnel (Cloudflare / ngrok).")]
    public Task<string> MirrorToRemote(
        [Description("Remote portal base URL, e.g. 'https://memex.meshweaver.cloud'.")] string remoteBaseUrl,
        [Description("ApiToken issued on the REMOTE portal — pastes as `mw_…`.")] string remoteToken,
        [Description("Local path whose subtree to push (e.g. 'rbuergi/Story').")] string sourcePath,
        [Description("Optional remote path to write under. Defaults to sourcePath.")] string? targetPath = null,
        [Description("If true, delete remote nodes that don't exist locally (DESTRUCTIVE).")] bool removeMissing = false,
        [Description("If true, only enumerate what would be touched without writing.")] bool dryRun = false)
        => PostMirror(new MirrorRequest
        {
            RemoteBaseUrl = remoteBaseUrl,
            RemoteToken = remoteToken,
            SourcePath = sourcePath,
            TargetPath = targetPath,
            Direction = "Push",
            RemoveMissing = removeMissing,
            DryRun = dryRun,
        });

    [McpServerTool]
    [Description(@"Mirror a subtree from a remote MeshWeaver portal DOWN to THIS instance. The local instance pulls from `remoteBaseUrl` over HTTPS — the remote must be reachable from here. Use to seed dev with prod data, or to mirror Doc/Architecture markdown into a partition you can edit.

Returns the same JSON summary shape as MirrorToRemote, with direction='Pull'. Same dry-run support.")]
    public Task<string> PullFromRemote(
        [Description("Remote portal base URL, e.g. 'https://memex.meshweaver.cloud'.")] string remoteBaseUrl,
        [Description("ApiToken issued on the REMOTE portal — pastes as `mw_…`.")] string remoteToken,
        [Description("Remote path whose subtree to pull (e.g. 'Doc/Architecture').")] string sourcePath,
        [Description("Optional local path to write under. Defaults to sourcePath.")] string? targetPath = null,
        [Description("If true, delete LOCAL nodes that don't exist on the remote (DESTRUCTIVE).")] bool removeMissing = false,
        [Description("If true, only enumerate what would be touched without writing.")] bool dryRun = false)
        => PostMirror(new MirrorRequest
        {
            RemoteBaseUrl = remoteBaseUrl,
            RemoteToken = remoteToken,
            SourcePath = sourcePath,
            TargetPath = targetPath,
            Direction = "Pull",
            RemoveMissing = removeMissing,
            DryRun = dryRun,
        });

    /// <summary>
    /// Shared MCP-tool body: posts the mirror request at the mesh hub via
    /// the standard request/response pattern (`hub.Observe`) and serialises
    /// the response. The handler is registered by
    /// <c>MirrorHubExtensions.AddMirrorHandler</c> on the mesh hub
    /// (wired into every <c>AddPersistence</c>-enabled host).
    /// </summary>
    private Task<string> PostMirror(MirrorRequest request) =>
        sessionHub.Observe<MirrorResult>(request, o => o.WithTarget(new Address("mesh")))
            .Catch((Exception ex) =>
            {
                logger.LogError(ex, "Mirror failed for {Source} {Direction} {Url}",
                    request.SourcePath, request.Direction, request.RemoteBaseUrl);
                return Observable.Return((IMessageDelivery<MirrorResult>)null!);
            })
            .Select(d => d?.Message ?? new MirrorResult
            {
                Status = "Error",
                Direction = request.Direction,
                SourcePath = request.SourcePath,
                TargetPath = request.TargetPath ?? request.SourcePath,
                Error = "No response from mirror handler — is the mesh hub reachable and AddPersistence configured?",
            })
            .Select(r => JsonSerializer.Serialize(r, sessionHub.JsonSerializerOptions))
            .FirstAsync().ToTask();

    [McpServerTool]
    [Description(@"Returns an interactive rendering of a layout area as an MCP-UI embedded resource. Hosts that support MCP-UI (Claude.ai web/desktop, ChatGPT Apps) render this inline as an iframe widget; text-only hosts see the URL as a fallback.

Use this when the user would benefit from seeing the live view — charts, grids, dashboards, triangles — rather than a JSON dump. For plain data inspection keep using `Get`.

Examples:
  RenderArea('@Systemorph/FutuRe/EuropeRe/AcmeSubmission2025', 'Triangle')
  RenderArea('Northwind', 'SalesByCategory')")]
    public CallToolResult RenderArea(
        [Description("Path to the node hosting the layout area (e.g., @Systemorph/FutuRe/EuropeRe/AcmeSubmission2025). Leading `@` is stripped.")] string path,
        [Description("Layout area name on that node (e.g., 'Triangle', 'Overview', 'Dashboard').")] string areaName)
    {
        if (string.IsNullOrWhiteSpace(path))
            return ErrorResult("Error: path is required.");
        if (string.IsNullOrWhiteSpace(areaName))
            return ErrorResult("Error: areaName is required.");

        var resolvedPath = MeshOperations.ResolvePath(path).TrimStart('/');
        var areaUrl = $"{baseUrl.TrimEnd('/')}/{resolvedPath}/{Uri.EscapeDataString(areaName).Replace("%2F", "/")}";
        var resourceUri = $"ui://mesh/{resolvedPath}/{areaName}";

        logger.LogInformation("MCP RenderArea path={Path} areaName={Area} url={Url}", resolvedPath, areaName, areaUrl);

        var iframeHtml = BuildIframeHtml(areaUrl, areaName);

        return new CallToolResult
        {
            Content =
            [
                new EmbeddedResourceBlock
                {
                    Resource = new TextResourceContents
                    {
                        Uri = resourceUri,
                        MimeType = "text/html",
                        Text = iframeHtml,
                    },
                },
                new ResourceLinkBlock
                {
                    Uri = areaUrl,
                    Name = areaName,
                    Title = $"{areaName} — {resolvedPath}",
                    MimeType = "text/html",
                },
                new TextContentBlock
                {
                    Text = $"Open in browser: {areaUrl}",
                },
            ],
        };
    }

    private static CallToolResult ErrorResult(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }],
    };

    private static string BuildIframeHtml(string areaUrl, string areaName)
    {
        var src = WebUtility.HtmlEncode(areaUrl);
        var title = WebUtility.HtmlEncode(areaName);
        return $$"""
            <!doctype html>
            <html>
            <head>
              <meta charset="utf-8">
              <title>{{title}}</title>
              <style>html,body{margin:0;padding:0;height:100%;width:100%}iframe{border:0;width:100%;height:100%;min-height:600px;display:block}</style>
            </head>
            <body>
              <iframe src="{{src}}" title="{{title}}" allow="clipboard-read; clipboard-write"></iframe>
            </body>
            </html>
            """;
    }
}

/// <summary>
/// Configuration options for MCP integration.
/// </summary>
public class McpConfiguration
{
    /// <summary>
    /// Base URL for the MeshWeaver UI. Used for generating NavigateTo URLs.
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;
}
