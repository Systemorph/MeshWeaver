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

    [McpServerTool(Title = "Get a node or attached resource", ReadOnly = true, Idempotent = true, OpenWorld = false)]
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

    [McpServerTool(Title = "Upload a file into a content collection", Destructive = false, Idempotent = true, OpenWorld = false)]
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

    [McpServerTool(Title = "Search the mesh", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(@"Searches the mesh using GitHub-style query syntax. Returns {count, limit, truncated, results:[{path,name,nodeType}]} — when 'truncated' is true there are more matches than returned; narrow the query or raise 'limit'.

Query terms (space-separated, all case-insensitive):
  • field filters: nodeType:Agent, name:Acme, name:*sales* (wildcards), -status:Archived (negation), price:>100
  • location: namespace:Doc (immediate children), namespace:Doc scope:descendants (recursive), path:Doc/Architecture (exact)
  • scope values: descendants | ancestors | hierarchy | subtree | ancestorsandself
  • sorting/projection: sort:name, sort:lastModified-desc, select:name,nodeType,icon
  • free text terms ('laptop pricing') run semantic/vector search when available
Full reference: read the 'tools-reference' MCP resource.")]
    public Task<string> Search(
        [Description("Query string (e.g., 'nodeType:Agent', 'laptop', 'path:ACME scope:descendants', 'name:*sales*')")] string query,
        [Description("Base path to search from (e.g., @graph). Empty for all.")] string? basePath = null,
        [Description("Maximum number of results to return. Default 50, max 200.")] int limit = 50)
        => ops.Search(query, basePath, limit).FirstAsync().ToTask();

    [McpServerTool(Title = "Create a node", Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description(@"Creates a new node in the mesh. Pass a JSON MeshNode object. Required fields — validated up-front with a descriptive error before anything is written:
  • id        — the node's own slug, NO slashes (e.g. ""PricingTool""). The parent path goes in 'namespace'; the node's path is derived as {namespace}/{id}.
  • namespace — full parent path (e.g. ""ACME/Projects""). Omit only for partition roots.
  • name      — human-readable display title (shown as the page heading).
  • nodeType  — the type definition that gives the node shape and views (e.g. ""Markdown"", ""Code"", ""Organization""). Discover types with search 'nodeType:NodeType'.
Recommended: 'icon' as an inline SVG starting with <svg, using currentColor. 'content' must match the nodeType's schema — get '@{ns}/schema/' on a sibling to discover it.")]
    public Task<string> Create(
        [Description("JSON MeshNode object to create (e.g., {\"id\": \"NewOrg\", \"namespace\": \"ACME\", \"name\": \"New Org\", \"nodeType\": \"Organization\", \"content\": {}})")] string node)
        => ops.Create(node).FirstAsync().ToTask();

    [McpServerTool(Title = "Replace nodes (full update)", Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Updates existing nodes in the mesh. Pass a JSON array of complete MeshNode objects. Always Get before Update — the entire node is replaced, not merged; a node missing 'nodeType' or 'content' is rejected with a descriptive error before anything is written. For small changes prefer Patch (field-level) or edit_content (text-level).")]
    public Task<string> Update(
        [Description("JSON array of MeshNode objects with all fields (get existing node first, modify, then pass here)")] string nodes)
        => ops.Update(nodes).FirstAsync().ToTask();

    [McpServerTool(Title = "Patch node fields", Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Partial update of a single node. Only the keys present in 'fields' are changed; omitted keys preserve existing values. 'content' deep-merges (RFC 7396): the nested keys you send are updated, the ones you omit are kept, and a null member deletes just that key — so you can change a single content field (e.g. {\"content\":{\"logo\":\"…\"}}) without resending the rest. Setting the whole 'content' to null is rejected. Prefer this over Update for small edits like icon/name/category; for edits inside a long Markdown body or source file prefer edit_content.")]
    public Task<string> Patch(
        [Description("Path to the node (e.g., @User/rbuergi/my-node)")] string path,
        [Description("JSON object with ONLY the fields to change. Examples: {\"icon\": \"<svg>...</svg>\"}, {\"name\": \"New Name\"}, {\"content\":{\"logo\":\"https://…\"}} (deep-merges into existing content).")] string fields)
        => ops.Patch(path, fields).FirstAsync().ToTask();

    [McpServerTool(Title = "Anchored text edit", Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description(@"Anchored text edit on a node's content (Markdown body or Code source). Replaces oldText with newText — pass just the snippet to change plus enough surrounding context to make it unique, instead of re-sending the whole document. Fails with a descriptive error when the text isn't found or isn't unique. Preferred over patch for any edit inside a long document or source file (cheaper, and immune to truncation corrupting the rest of the content).")]
    public Task<string> EditContent(
        [Description("Path to the node (e.g., @User/rbuergi/my-doc or @ACME/Story/Source/Story.cs)")] string path,
        [Description("The exact text to replace — copy it verbatim from get, including whitespace and line breaks. Must match exactly once (or set replaceAll).")] string oldText,
        [Description("The replacement text.")] string newText,
        [Description("Replace every occurrence instead of requiring a unique match. Default: false.")] bool replaceAll = false)
        => ops.EditContent(path, oldText, newText, replaceAll).FirstAsync().ToTask();

    [McpServerTool(Title = "Delete nodes (recursive)", Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Deletes one or more nodes from the mesh by path. Recursive: deleting a parent removes all descendants. To remove a subtree, just pass the root path — children do not need to be enumerated.")]
    public Task<string> Delete(
        [Description("JSON array of path strings to delete (e.g., [\"ACME/OldProject\", \"ACME/ArchivedTask\"])")] string paths)
        => ops.Delete(paths).FirstAsync().ToTask();

    [McpServerTool(Title = "Move a subtree", Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Moves a node and its descendants to a new path. Equivalent to the Move menu item. Requires Delete on the source namespace and Create on the target. Source and target are full paths (namespace + id), e.g. 'OrgA/Child' -> 'OrgB/Child'.")]
    public Task<string> Move(
        [Description("Current path of the node (e.g., @OrgA/Child)")] string sourcePath,
        [Description("New path for the node (e.g., @OrgB/Child)")] string targetPath)
        => ops.Move(sourcePath, targetPath).FirstAsync().ToTask();

    [McpServerTool(Title = "Copy a subtree", Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Copies a node and all its descendants to a target namespace. Equivalent to the Copy menu item. Source ids are preserved; paths are rewritten under the target namespace.")]
    public Task<string> Copy(
        [Description("Current path of the node to copy (e.g., @OrgA/Child)")] string sourcePath,
        [Description("Target namespace to copy under (e.g., @OrgB)")] string targetNamespace,
        [Description("Overwrite existing nodes at the target. Default: false.")] bool force = false)
        => ops.Copy(sourcePath, targetNamespace, force).FirstAsync().ToTask();

    [McpServerTool(Title = "Browser URL for a node", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Returns a URL to view a node in the MeshWeaver UI. The URL shape is `{baseUrl}/{path}` — the mesh path is appended directly to the base URL with no intermediate segment (no `/node/`) and without URL-escaping the path separators. Use this when you want to give a user a link to open in their browser. Call with an empty path to get the base URL on its own.")]
    public string NavigateTo(
        [Description("Path to navigate to (e.g., @Systemorph/FutuRe/EuropeRe). Leading `@` is stripped. Empty returns the base URL.")] string? path = null)
    {
        logger.LogInformation("MCP NavigateTo called with path={Path}", path);

        if (string.IsNullOrWhiteSpace(path))
            return baseUrl.TrimEnd('/');

        var resolvedPath = MeshOperations.ResolvePath(path);
        return $"{baseUrl.TrimEnd('/')}/{resolvedPath.TrimStart('/')}";
    }

    [McpServerTool(Title = "NodeType compile status", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Returns compilation diagnostics for a NodeType (or any instance of one). Status is 'Ok' when the type compiled cleanly, 'Error' with details when it failed, 'Compiling' while a compile is in progress (with elapsedMs), or 'Unknown' when no compile has happened yet. Use after creating/updating a NodeType to verify it actually compiles — a NodeType that doesn't compile is not 'done'.")]
    public Task<string> GetDiagnostics(
        [Description("Path to a NodeType (e.g., @Systemorph/SocialMedia/Profile) or to any instance of one")] string path)
        => ops.GetDiagnostics(path).FirstAsync().ToTask();

    [McpServerTool(Title = "Recycle a node's hub", Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Recycles the hub at the given path by posting DisposeRequest. Forces a fresh hub initialization on the next access — use after fixing a broken NodeType, after editing the `sources` list, or whenever a grain is stuck in a cached bad state. Requires Update permission on the target node. Returns {status:'Recycled', path}. Wait ~100ms before the next access so the grain teardown completes.")]
    public Task<string> Recycle(
        [Description("Path to the node (e.g., @Systemorph/SocialMedia/Profile). Use the NodeType path to recycle the whole type; use an instance path to recycle just that instance's hub.")] string path)
        => ops.Recycle(path).FirstAsync().ToTask();

    [McpServerTool(Title = "Compile a NodeType", Idempotent = true, OpenWorld = false)]
    [Description(@"Compiles a NodeType and waits for the result inline. Flips the NodeType's `compilationStatus` to `Pending` via the canonical remote-stream `Update` (no PatchDataRequest, no Update permission required), then subscribes to the NodeType's MeshNode stream and waits up to 60s for the framework's CompileWatcher to settle the status to `Ok` or `Error`.

Returns a structured result:
  • `{status:'Ok', path, activityPath, message:'Compile SUCCEEDED.'}` — assembly cached, ready to use.
  • `{status:'Error', path, error, activityPath, message:'Compile FAILED ...'}` — `error` carries the Roslyn diagnostics inline.
  • `{status:'Pending', path, message:'... did not settle within deadline'}` — fallback only on timeout; `get @nodeTypePath` to poll.

For the full source-discovery + matched-Code-paths + Roslyn trace, `get @<activityPath>` after the call returns.")]
    public Task<string> Compile(
        [Description("Path to the NodeType (e.g., @User/me/MyType or @Systemorph/SocialMedia/Profile). Must point at a NodeType definition node, not an instance.")] string path)
        => ops.Compile(path).FirstAsync().ToTask();

    [McpServerTool(Title = "Speculative Roslyn check", ReadOnly = true, Idempotent = true, OpenWorld = false)]
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

    [McpServerTool(Title = "Roslyn diagnostics for a NodeType", ReadOnly = true, Idempotent = true, OpenWorld = false)]
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

    // lsp_hover_for_node / lsp_completions_for_node were removed from the MCP surface
    // (2026-06-11 tool-surface compaction): position-based hover/completions are
    // IDE-interaction shapes — an agent driving JSON tool calls reads the source via
    // `get` and runs `lsp_check_node` for the pre-flight loop. IMeshLanguageService
    // keeps both capabilities for first-party UI use.

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

    [McpServerTool(Title = "Execute a Code node", Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Runs an executable Code node's C# through the kernel (Microsoft.DotNet.Interactive) and returns stdout / return value / errors. The target node must have `CodeConfiguration.IsExecutable == true`. Blocks until the kernel signals completion (side-effects — e.g. mesh.CreateNode calls inside the script — have happened by the time this returns). Use to run import/test scripts from MCP without needing a UI click.")]
    public Task<string> ExecuteScript(
        [Description("Path to an executable Code node (e.g., @Systemorph/FutuRe/EuropeRe/AcmeSubmission2025/Script/ImportLargeClaims). Must be `IsExecutable=true`.")] string path,
        [Description("Timeout in seconds. Default 120.")] int timeoutSeconds = 120)
        => ops.ExecuteScript(path, timeoutSeconds).FirstAsync().ToTask();

    [McpServerTool(Title = "Start an agent thread", Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description(@"Starts a new agent conversation thread and submits the first message — the server-side agent executes it asynchronously. This is the ONLY way to launch a thread from MCP: do NOT hand-assemble Thread nodes with `create`/`patch` — the submission protocol (pending-message draining, response-cell allocation) is owned by the thread hub, and bypassing it leaves a wedged thread.

Returns `{status:'Started', threadPath}` as soon as the thread node exists; the agent keeps working after this returns. Observe progress and the result with `get` on the threadPath: `content.status` is 'Idle' when the round finished and `content.summary` carries the result digest. The full transcript is queryable via `search 'path:{threadPath} scope:descendants nodeType:ThreadMessage select:text,role,timestamp'`.")]
    public Task<string> StartThread(
        [Description("Namespace to create the thread under (e.g. 'rbuergi' or 'ACME/Projects'). The thread lives at {namespace}/_Thread/{id}.")] string namespacePath,
        [Description("The first user message — the task for the agent. Write it self-contained: the agent has not seen this conversation.")] string message,
        [Description("Agent to run the thread (e.g. 'Assistant', 'Coder', 'Researcher'). Default: the platform's default agent.")] string? agentName = null,
        [Description("Optional node path the agent should treat as its working context (relative @-paths in the conversation resolve against it).")] string? contextPath = null)
    {
        if (string.IsNullOrWhiteSpace(namespacePath))
            return Task.FromResult("Error: namespacePath is required — the thread is created under it.");
        if (string.IsNullOrWhiteSpace(message))
            return Task.FromResult("Error: message is required — it is the task the agent executes.");

        // MCP boundary adapter: bridge the extension's one-shot callbacks to the Task the
        // MCP surface requires (same pattern as InboxTool). Bounded so a lost callback
        // can't hang the MCP call forever.
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        cts.Token.Register(() => tcs.TrySetResult(
            "Error: thread creation did not confirm within 30s. Verify the namespace exists and check with search 'nodeType:Thread namespace:" +
            MeshOperations.ResolvePath(namespacePath) + "/_Thread'."));

        sessionHub.StartThread(
            MeshOperations.ResolvePath(namespacePath),
            message,
            agentName: agentName,
            contextPath: contextPath != null ? MeshOperations.ResolvePath(contextPath) : null,
            onCreated: node => tcs.TrySetResult(JsonSerializer.Serialize(
                new
                {
                    status = "Started",
                    threadPath = node.Path,
                    agentName,
                    hint = "The agent executes asynchronously. get the threadPath to observe: content.status == 'Idle' means the round finished; content.summary is the result digest."
                },
                sessionHub.JsonSerializerOptions)),
            onError: err => tcs.TrySetResult($"Error starting thread: {err}"));

        return tcs.Task.ContinueWith(t => { cts.Dispose(); return t.Result; });
    }

    [McpServerTool(Title = "Send a message to a thread", Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description(@"Queues a follow-up user message on an existing agent thread. If the thread is idle, the submission watcher dispatches a new round immediately; if the agent is mid-round, the message is delivered the next time it checks its inbox. Use `start_thread` to create a new thread; use `get` on the thread path to read status and results.")]
    public Task<string> SubmitMessage(
        [Description("Path of the thread (e.g. 'rbuergi/_Thread/fix-login-bug-3f9a') — as returned by start_thread or found via search 'nodeType:Thread'.")] string threadPath,
        [Description("The user message text.")] string message,
        [Description("Optional agent override for this round. Default: the thread's current agent.")] string? agentName = null)
    {
        if (string.IsNullOrWhiteSpace(threadPath))
            return Task.FromResult("Error: threadPath is required. Use start_thread to create a new thread.");
        if (string.IsNullOrWhiteSpace(message))
            return Task.FromResult("Error: message is required.");

        var resolvedPath = MeshOperations.ResolvePath(threadPath);
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        sessionHub.SubmitMessage(
            resolvedPath,
            message,
            agentName: agentName,
            onError: err => tcs.TrySetResult($"Error submitting to {resolvedPath}: {err}"));

        // SubmitMessage is fire-and-forget with an error-only callback; give a short grace
        // window for a synchronous failure (bad path, unreadable thread) to surface, then
        // report queued. The write itself is confirmed via the thread node's stream.
        return Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(2)))
            .ContinueWith(_ => tcs.Task.IsCompleted
                ? tcs.Task.Result
                : JsonSerializer.Serialize(
                    new
                    {
                        status = "Submitted",
                        threadPath = resolvedPath,
                        hint = "Message queued. get the threadPath to observe: content.status == 'Idle' means the round finished."
                    },
                    sessionHub.JsonSerializerOptions));
    }

    [McpServerTool(Title = "Mirror a subtree to/from a remote portal", Destructive = true, Idempotent = true)]
    [Description(@"Mirror a subtree between THIS instance and a remote MeshWeaver portal. `direction='push'` copies local → remote (promote dev content to prod, stage a snapshot for review); `direction='pull'` copies remote → local (seed dev with prod data).

Authentication — prefer a named remote profile configured server-side (`Mirror:Remotes:{name}` with `BaseUrl` + `Token` in the host configuration) and pass the profile name as `remote`: the token then never enters the model context or transcripts. Passing a base URL as `remote` also works when a profile with a matching BaseUrl is configured. Supplying `remoteToken` inline is a discouraged fallback for ad-hoc one-offs.

Returns a JSON summary: `{status, direction, sourcePath, targetPath, nodesImported, nodesSkipped, nodesRemoved, partitionsImported, elapsedMs}`. With `dryRun=true` returns `{status:'DryRun', nodesScanned, paths:[...]}` so you can preview before writing.

Network: this instance must have outbound HTTPS reach to the remote. Prod can't reach localhost — for prod→local you need a tunnel (Cloudflare / ngrok).")]
    public Task<string> Mirror(
        [Description("'push' (local → remote) or 'pull' (remote → local).")] string direction,
        [Description("Named remote profile (configured under Mirror:Remotes:{name}) — preferred — or a base URL like 'https://memex.meshweaver.cloud'.")] string remote,
        [Description("Path whose subtree to mirror: a local path for push (e.g. 'rbuergi/Story'), a remote path for pull (e.g. 'Doc/Architecture').")] string sourcePath,
        [Description("Optional destination path to write under. Defaults to sourcePath.")] string? targetPath = null,
        [Description("If true, delete destination nodes that don't exist at the source (DESTRUCTIVE).")] bool removeMissing = false,
        [Description("If true, only enumerate what would be touched without writing.")] bool dryRun = false,
        [Description("ApiToken for the remote (mw_…). Discouraged — prefer a configured remote profile so the secret stays server-side.")] string? remoteToken = null)
    {
        var dir = direction?.Trim().ToLowerInvariant() switch
        {
            "push" => "Push",
            "pull" => "Pull",
            _ => null
        };
        if (dir is null)
            return Task.FromResult("Error: direction must be 'push' (local → remote) or 'pull' (remote → local).");

        var (resolvedUrl, resolvedToken, error) = ResolveRemote(remote, remoteToken);
        if (error != null)
            return Task.FromResult(error);

        return PostMirror(new MirrorRequest
        {
            RemoteBaseUrl = resolvedUrl!,
            RemoteToken = resolvedToken!,
            SourcePath = sourcePath,
            TargetPath = targetPath,
            Direction = dir,
            RemoveMissing = removeMissing,
            DryRun = dryRun,
        });
    }

    /// <summary>
    /// Resolves the mirror destination + credential. Profiles live in host configuration under
    /// <c>Mirror:Remotes:{name}</c> (<c>BaseUrl</c> + <c>Token</c>) so the ApiToken stays
    /// server-side — tool arguments flow through the model context and transcripts, which is
    /// no place for a credential. An explicitly passed token always wins (ad-hoc escape hatch).
    /// </summary>
    private (string? BaseUrl, string? Token, string? Error) ResolveRemote(string remote, string? explicitToken)
    {
        if (string.IsNullOrWhiteSpace(remote))
            return (null, null,
                "Error: 'remote' is required — a profile name configured under Mirror:Remotes, or a base URL.");

        var config = rootHub.ServiceProvider.GetService<Microsoft.Extensions.Configuration.IConfiguration>();
        var isUrl = remote.StartsWith("http", StringComparison.OrdinalIgnoreCase);

        if (!isUrl)
        {
            var section = config?.GetSection($"Mirror:Remotes:{remote}");
            var url = section?["BaseUrl"];
            if (string.IsNullOrEmpty(url))
                return (null, null,
                    $"Error: no remote profile '{remote}' is configured (expected Mirror:Remotes:{remote}:BaseUrl " +
                    "in the host configuration). Configure the profile server-side, or pass the base URL directly.");
            var token = !string.IsNullOrEmpty(explicitToken) ? explicitToken : section?["Token"];
            if (string.IsNullOrEmpty(token))
                return (null, null,
                    $"Error: remote profile '{remote}' has no Token configured (Mirror:Remotes:{remote}:Token).");
            return (url.TrimEnd('/'), token, null);
        }

        // URL given: still prefer a configured profile whose BaseUrl matches, so the token stays server-side.
        var trimmed = remote.TrimEnd('/');
        var match = config?.GetSection("Mirror:Remotes").GetChildren()
            .FirstOrDefault(c => string.Equals(c["BaseUrl"]?.TrimEnd('/'), trimmed, StringComparison.OrdinalIgnoreCase));
        var resolvedToken = !string.IsNullOrEmpty(explicitToken) ? explicitToken : match?["Token"];
        if (string.IsNullOrEmpty(resolvedToken))
            return (null, null,
                $"Error: no token available for '{remote}'. Configure a profile under Mirror:Remotes with this " +
                "BaseUrl + Token (preferred — the secret never enters the model context), or pass remoteToken explicitly.");
        return (trimmed, resolvedToken, null);
    }

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

    [McpServerTool(Title = "Render a layout area (MCP-UI)", ReadOnly = true, Idempotent = true, OpenWorld = false)]
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
