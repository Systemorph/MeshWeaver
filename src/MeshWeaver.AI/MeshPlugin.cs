using MeshWeaver.Mesh;
using System.ComponentModel;
using System.Reactive.Linq;
using MeshWeaver.AI.Navigation;
using MeshWeaver.AI.Plugins;
using MeshWeaver.Layout;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.AI;

/// <summary>
/// Plugin providing mesh operations for AI agents.
/// Thin wrapper over MeshOperations with AITool factory and layout-based NavigateTo.
/// Resolves relative paths against the current chat context.
/// </summary>
public class MeshPlugin(IMessageHub hub, IAgentChat chat)
{
    private readonly MeshOperations ops = new(hub) { OnNodeChange = change => chat.ForwardNodeChange?.Invoke(change) };
    private readonly ILogger<MeshPlugin> logger = hub.ServiceProvider.GetRequiredService<ILogger<MeshPlugin>>();
    private readonly AccessService? accessService = hub.ServiceProvider.GetService<AccessService>();

    // Bounded "Process" pool (Wave 3 of Controlled I/O Pooling). RunTests spawns a
    // `dotnet test` child process and BLOCKS a thread for the whole run (up to 5
    // minutes) — that thread-holding wait must run OFF the hub/grain scheduler and
    // be bounded so a fan-out of test runs can't trigger ThreadPool thread-injection
    // that starves Orleans' grain schedulers. Resolve the mesh-scoped pool from DI;
    // fall back to the stateless IoPool.Unbounded when no registry is wired (still
    // offloads to the ThreadPool — never worse than the inline call). No static state.
    private readonly IIoPool _processPool =
        hub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.Process)
            ?? IoPool.Unbounded;

    /// <summary>MCP/agent tool: reads a node (or its children) from the mesh by path, resolving relative paths against the current chat context.</summary>
    /// <param name="path">The node path; relative to context or <c>@/</c>-prefixed for absolute.</param>
    /// <returns>A task with the node JSON or a descriptive error string.</returns>
    [Description("Retrieves a node or content from the mesh by path. Paths are relative to current context; use @/ prefix for absolute paths. Supports Unified Path prefixes: content/, data/, schema/, model/, collection/, area/.")]
    public Task<string> Get(
        [Description("Path to data. Relative: @content/file.docx, @MyChild/*. Absolute: @/OrgA/Doc, @/OrgA/content/file.docx. For spaces: \"@content/My File.docx\"")] string path,
        CancellationToken cancellationToken = default)
        => Tool("get", path, cancellationToken, () => ops.Get(ResolveContextPath(path)));

    /// <summary>MCP/agent tool: searches the mesh with GitHub-style query syntax, scoping to an optional base path.</summary>
    /// <param name="query">The query string (e.g. <c>nodeType:Agent</c>).</param>
    /// <param name="basePath">Optional base path to search from; <c>null</c> for all.</param>
    /// <param name="limit">Maximum results to return (default 50, max 200).</param>
    /// <returns>A task with the JSON results envelope or a descriptive error string.</returns>
    [Description("Searches the mesh using GitHub-style query syntax. Returns {count, limit, truncated, results:[{path,name,nodeType}]} — when 'truncated' is true there are more matches than returned; narrow the query or raise 'limit'.")]
    public Task<string> Search(
        [Description("Query string (e.g., 'nodeType:Agent', 'path:ACME scope:descendants', 'name:*sales*')")] string query,
        [Description("Base path to search from (e.g., @graph). Empty for all.")] string? basePath = null,
        [Description("Maximum number of results to return. Default 50, max 200.")] int limit = 50,
        CancellationToken cancellationToken = default)
        => Tool("search", query, cancellationToken,
            () => ops.Search(query, basePath != null ? ResolveContextPath(basePath) : null, limit));

    /// <summary>MCP/agent tool: creates a new node in the mesh from a JSON MeshNode.</summary>
    /// <param name="node">The JSON MeshNode (requires id, name, nodeType, namespace).</param>
    /// <returns>A task with <c>"Created: {path}"</c> or a descriptive error string.</returns>
    [Description("Creates a new node in the mesh. ALWAYS set the 'name' property to a human-readable display name.")]
    public Task<string> Create(
        [Description("JSON MeshNode with required: id, name, nodeType, namespace. Example: {\"id\":\"my-page\",\"namespace\":\"MyOrg\",\"name\":\"My Page\",\"nodeType\":\"Markdown\"}")] string node,
        CancellationToken cancellationToken = default)
        => Tool("create", "the supplied node", cancellationToken, () => ops.Create(node));

    /// <summary>MCP/agent tool: full-replacement update of existing nodes from a JSON array of complete MeshNodes.</summary>
    /// <param name="nodes">A JSON array of complete MeshNode objects fetched via Get and modified.</param>
    /// <returns>A task with a per-node result/error summary.</returns>
    [Description("Full replacement update of existing nodes. ALWAYS Get the node first, modify the returned object, then send it back here unchanged-except-for-edits. The 'content' field MUST be present and non-null — null content is rejected and the response will include the expected schema. Prefer Patch for small changes.")]
    public Task<string> Update(
        [Description("JSON array of complete MeshNode objects fetched via Get and then modified")] string nodes,
        CancellationToken cancellationToken = default)
        => Tool("update", "the supplied nodes", cancellationToken, () => ops.Update(nodes));

    /// <summary>MCP/agent tool: partial update of a single node — only the supplied fields change, with <c>content</c> deep-merged (RFC 7396).</summary>
    /// <param name="path">The path of the node to patch (resolved against context).</param>
    /// <param name="fields">A JSON object with only the fields to change.</param>
    /// <returns>A task with <c>"Patched: {path}"</c> or a descriptive error string.</returns>
    [Description("Partial update of a single node. Only the keys present in 'fields' are changed; omitted keys preserve existing values. 'content' deep-merges (RFC 7396): nested keys you send are updated, omitted keys are kept, a null member deletes that one key — so you can change a single content field without resending the rest. Setting the whole 'content' to null is rejected (with the schema). Prefer this over Update for small edits like icon/name/category.")]
    public Task<string> Patch(
        [Description("Path to the node (e.g., @User/rbuergi/my-node)")] string path,
        [Description("JSON object with ONLY the fields to change. Examples: {\"icon\": \"<svg>...</svg>\"}, {\"name\": \"New Name\"}, {\"content\":{\"logo\":\"https://…\"}} (deep-merges into existing content). Never set 'content' to null.")] string fields,
        CancellationToken cancellationToken = default)
        => Tool("patch", path, cancellationToken, () => ops.Patch(ResolveContextPath(path), fields));

    /// <summary>MCP/agent tool: anchored text edit on a node's Markdown body or Code source — replaces an exact snippet.</summary>
    /// <param name="path">The path of the node to edit (resolved against context).</param>
    /// <param name="oldText">The exact text to replace, copied verbatim including whitespace.</param>
    /// <param name="newText">The replacement text.</param>
    /// <param name="replaceAll">When <c>true</c>, replaces every occurrence instead of requiring a unique match.</param>
    /// <returns>A task with <c>"Edited: {path} (N replacements)"</c> or a descriptive error string.</returns>
    [Description("Anchored text edit on a node's content (Markdown body or Code source). Replaces oldText with newText — pass just the snippet to change plus enough surrounding context to make it unique, instead of re-sending the whole document through Patch. Fails with a descriptive error when the text isn't found or isn't unique. Preferred over Patch for any edit inside a long document or source file.")]
    public Task<string> EditContent(
        [Description("Path to the node (e.g., @User/rbuergi/my-doc or @ACME/Story/Source/Story.cs)")] string path,
        [Description("The exact text to replace — copy it verbatim from Get, including whitespace and line breaks. Must match exactly once (or set replaceAll).")] string oldText,
        [Description("The replacement text.")] string newText,
        [Description("Replace every occurrence instead of requiring a unique match. Default: false.")] bool replaceAll = false,
        CancellationToken cancellationToken = default)
        => Tool("edit_content", path, cancellationToken,
            () => ops.EditContent(ResolveContextPath(path), oldText, newText, replaceAll));

    /// <summary>MCP/agent tool: deletes nodes (and their descendants) from the mesh by path.</summary>
    /// <param name="paths">A JSON array of path strings to delete.</param>
    /// <returns>A task with the deletion result or a descriptive error string.</returns>
    [Description("Deletes nodes from the mesh by path. Recursive: deleting a parent removes all descendants — pass the subtree root, no need to enumerate children. This operation can be BLOCKED by permissions: the current identity often holds no Delete on shared or system-synced spaces, and a refusal is not an error to retry. The result then names the refused path with its Delete page link (/{path}/Delete) — present that URL to the user so they review and confirm the deletion under their OWN identity in the GUI. A whole SET of nodes can be offered the same way via /{anchorPath}/Delete?q=<url-encoded mesh query> (multiple queries newline-separated inside the one encoded parameter).")]
    public Task<string> Delete(
        [Description("JSON array of path strings to delete")] string paths,
        CancellationToken cancellationToken = default)
        => Tool("delete", paths, cancellationToken, () => ops.Delete(paths));

    /// <summary>MCP/agent tool: returns compilation diagnostics (Ok/Error/Unknown) for a NodeType or an instance of one.</summary>
    /// <param name="path">The path of a NodeType, or of any instance of one.</param>
    /// <returns>A task with the diagnostics JSON (status and any error message).</returns>
    [Description("Returns compilation diagnostics for a NodeType or an instance of one. Status is 'Ok' when the type compiled cleanly, 'Error' with a detailed message when it failed, or 'Unknown' when no compile has happened yet. Use this after creating/updating a NodeType to verify it actually compiles — a NodeType that doesn't compile is not 'done'.")]
    public Task<string> GetDiagnostics(
        [Description("Path to a NodeType (e.g., @Systemorph/SocialMedia/Profile) or to any instance of one")] string path,
        CancellationToken cancellationToken = default)
        => Tool("get_diagnostics", path, cancellationToken, () => ops.GetDiagnostics(ResolveContextPath(path)));

    /// <summary>MCP/agent tool: disposes the hub at a path so it re-initialises fresh on next access — used after fixing a broken NodeType or a stuck grain.</summary>
    /// <param name="path">The path whose hub should be recycled (NodeType path for the whole type, or an instance path).</param>
    /// <returns>A task with <c>{status:'Recycled', path}</c> or a descriptive error string.</returns>
    [Description("Recycles the hub at the given path by posting DisposeRequest. Forces a fresh hub initialization on the next access — use this after fixing a broken NodeType, after editing the `sources` list, or whenever a grain is stuck. Returns {status:'Recycled', path}. Wait ~100ms before the next access so the grain teardown completes.")]
    public Task<string> Recycle(
        [Description("Path to the node (e.g., @Systemorph/SocialMedia/Profile). Use the NodeType path to recycle the whole type; use an instance path to recycle just that instance's hub.")] string path,
        CancellationToken cancellationToken = default)
        => Tool("recycle", path, cancellationToken, () => ops.Recycle(ResolveContextPath(path)));

    /// <summary>MCP/agent tool: moves a node and its descendants to a new path.</summary>
    /// <param name="sourcePath">The current full path of the node.</param>
    /// <param name="targetPath">The new full path for the node.</param>
    /// <returns>A task with the move result or a descriptive error string.</returns>
    [Description("Moves a node and its descendants to a new path. Equivalent to the Move menu item. Requires Delete on the source namespace and Create on the target. Source and target are full paths (namespace + id), e.g. 'OrgA/Child' -> 'OrgB/Child'.")]
    public Task<string> Move(
        [Description("Current path of the node (e.g., @OrgA/Child)")] string sourcePath,
        [Description("New path for the node (e.g., @OrgB/Child)")] string targetPath,
        CancellationToken cancellationToken = default)
        => Tool("move", sourcePath, cancellationToken,
            () => ops.Move(ResolveContextPath(sourcePath), ResolveContextPath(targetPath)));

    /// <summary>MCP/agent tool: copies a node and all its descendants under a target namespace, preserving source ids.</summary>
    /// <param name="sourcePath">The current path of the node to copy.</param>
    /// <param name="targetNamespace">The namespace to copy the subtree under.</param>
    /// <param name="force">When <c>true</c>, overwrites existing target nodes instead of skipping.</param>
    /// <returns>A task with the copy result or a descriptive error string.</returns>
    [Description("Copies a node and all its descendants to a target namespace. Equivalent to the Copy menu item. Source ids are preserved; paths are rewritten under the target namespace.")]
    public Task<string> Copy(
        [Description("Current path of the node to copy (e.g., @OrgA/Child)")] string sourcePath,
        [Description("Target namespace to copy under (e.g., @OrgB)")] string targetNamespace,
        [Description("Overwrite existing nodes at the target. Default: false (skip if any target path already exists).")] bool force = false,
        CancellationToken cancellationToken = default)
        => Tool("copy", sourcePath, cancellationToken,
            () => ops.Copy(ResolveContextPath(sourcePath), ResolveContextPath(targetNamespace), force));

    // MCP adapter helper: re-seeds the user's AccessContext on Subscribe, then runs the
    // observable. AsyncLocal doesn't flow reliably through the AI framework's streaming +
    // tool invocation pipeline, so each plugin entry point must explicitly re-seed before
    // hitting hub-backed ops. Defer ensures the seed runs on Subscribe (which ToolTask.Bridge
    // performs), keeping each public method a strict one-line MCP adapter per AsynchronousCalls.md.
    //
    // CAPTURE THE EFFECTIVE IDENTITY SYNCHRONOUSLY, on the calling thread, where it is reliable:
    // the agent's execution context wins; else the request-scoped Context an active delivery set;
    // else the circuit/persistent context the Blazor session or test established. Re-reading it
    // *inside* Defer is the concurrency bug — under N parallel tool calls (Task.WhenAll) the
    // ambient AsyncLocal can be lost past a thread hop for one operation, so its hub-post stamps an
    // empty AccessContext → "Access denied". Capturing once, here, and re-seeding the request-scoped
    // Context on Subscribe makes every operation carry the caller's identity regardless of flow.
    /// <summary>
    /// The ONE boundary adapter every mesh tool goes through: re-seed the caller's identity, then
    /// hand the reactive operation to <see cref="ToolTask.Bridge{T}"/>.
    ///
    /// <para>🚨 <b>Why not <c>.FirstAsync().ToTask()</c>, which is what all thirteen of these used
    /// to be.</b> That bridge takes no <see cref="CancellationToken"/>, and none of these tools
    /// declared one — so the returned <c>Task</c> could not be cancelled by anything. The whole
    /// round runs as a leaf on the bounded <c>IoPoolNames.Ai</c> pool and holds one gate permit for
    /// its entire duration; <c>IoPool.Drain()</c> — the join every teardown performs before
    /// disposing the service scope and unloading collectible node ALCs — cancels the pool token and
    /// re-acquires every permit. A round parked in <c>get</c> or <c>search</c> against a hub that
    /// never answers therefore held its permit through the full drain budget and teardown proceeded
    /// over live code, and the Stop button did nothing. This is the #1956 defect class, fixed for
    /// <c>VersionPlugin</c>/<c>SkillTool</c>/<c>PlanStorageTool</c>/<c>CollaborationPlugin</c> and
    /// left standing here; <see cref="ToolTask.Bridge{T}"/> is the shared cure.</para>
    ///
    /// <para>It also supplies the two terminals the old bridge got wrong: an EMPTY completion
    /// (which <c>FirstAsync</c> turned into an <c>InvalidOperationException</c> about a sequence
    /// containing no elements — a stack trace where the model needed an answer) and a fault, both
    /// formatted as the <c>"Error: …"</c> string the rest of this surface already speaks. Callers
    /// like <c>AgentChatClient.LoadToolDocumentationAsync</c> already branch on that prefix.</para>
    /// </summary>
    /// <param name="operation">Tool name, for the failure answers.</param>
    /// <param name="subject">What the call was about (a path or query), for the failure answers.</param>
    /// <param name="cancellationToken">The round's token, bound by <c>AIFunctionFactory</c>.</param>
    /// <param name="work">The reactive operation. Runs under the caller's re-seeded identity.</param>
    private Task<string> Tool(
        string operation, string subject, CancellationToken cancellationToken, Func<IObservable<string>> work)
        => ToolTask.Bridge(
            WithContext(work),
            cancellationToken,
            answer => answer,
            ex => $"Error: {operation} on '{subject}' failed — {ex.Message}",
            () => $"Error: {operation} on '{subject}' produced no answer.");

    private IObservable<T> WithContext<T>(Func<IObservable<T>> work)
    {
        var captured = chat.ExecutionContext?.UserAccessContext
            ?? accessService?.Context
            ?? accessService?.CircuitContext;
        return Observable.Defer(() =>
        {
            if (captured != null)
                accessService?.SetContext(captured);
            return work();
        });
    }

    /// <summary>
    /// MCP/agent tool: takes the user to a place in the UI. Resolution is RESILIENT — a single path-shaped
    /// argument is tried as a direct path first (with URL correction and a search fallback), free text is
    /// interpreted (an intent→skill match, else the best-matching node) — and HONEST: it never reports
    /// success for a place that does not exist. The resolved target renders in the chat's content view.
    /// </summary>
    /// <param name="path">A path (<c>@/Doc/AI/ModelProviderSettings</c>) or plain words describing where to go.</param>
    /// <returns>A task with an honest confirmation of where it navigated, or that nothing matched.</returns>
    [Description("Takes the user to a node, doc, or page in the UI. Resilient (resolves the best match even when the path is imperfect) and honest (NEVER claims to open a place that doesn't exist — if nothing matches it says so and lists the closest candidates). Pass a path (@/Doc/AI/ModelProviderSettings) or plain words describing where to go (\"model settings\").")]
    public Task<string> NavigateTo(
        [Description("A path (@/Doc/AI/ModelProviderSettings), or what the user is looking for in words.")] string path,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("NavigateTo called with path={Path}", path);
        return Tool("navigate_to", path, cancellationToken,
            () => new NavigationResolver(hub).Resolve(path, chat.Context?.Context)
                .Select(resolution => NavigateToResult(path, resolution)));
    }

    // Renders the resolved target inline and returns an honest result string. A page ROUTE can't render as
    // a node view, so it's reported only. The pane-aware open is applied on the client (ThreadChatView).
    private string NavigateToResult(string requested, NavigationResolution resolution)
    {
        if (resolution.Kind == NavigationTargetKind.Unresolved || string.IsNullOrEmpty(resolution.Target))
        {
            var alts = resolution.Alternatives.Count > 0
                ? " Closest matches: " + string.Join(", ", resolution.Alternatives) + "."
                : string.Empty;
            return (resolution.Message ?? $"Couldn't find anywhere matching \"{requested}\".") + alts;
        }

        if (resolution.Kind != NavigationTargetKind.Route)
            chat.DisplayLayoutArea(Controls.LayoutArea(new Address(resolution.Target.TrimStart('/')), string.Empty));

        var note = resolution.WasCorrected && !string.IsNullOrEmpty(resolution.Message)
            ? " " + resolution.Message
            : string.Empty;
        return $"Navigating to: {resolution.Target}.{note}";
    }

    /// <summary>MCP/agent tool (dev-only): runs xUnit tests via <c>dotnet test</c> on a repo-relative test project, optionally filtered.</summary>
    /// <param name="projectPath">Repo-relative path to the test project or its directory.</param>
    /// <param name="filter">Optional xUnit <c>--filter</c> expression; <c>null</c> runs all tests.</param>
    /// <returns>A task with the condensed runner output and pass/fail summary.</returns>
    [Description("Runs xUnit tests via `dotnet test` on the given test project path (repo-relative, e.g. 'test/MeshWeaver.Acme.Test'). Optional filter uses the xunit `--filter` syntax: 'FullyQualifiedName~TodoViewsTest' to narrow by class, or '...Test.MethodName' for a single method. Returns the condensed test runner output (stdout + pass/fail summary). Dev-only — intended for the Monolith portal, not production.")]
    public Task<string> RunTests(
        [Description("Repo-relative path to the test project or its directory (e.g. 'test/MeshWeaver.Acme.Test')")] string projectPath,
        [Description("Optional xunit filter expression (e.g. 'FullyQualifiedName~TodoViewsTest')")] string? filter = null,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("RunTests called project={Project} filter={Filter}", projectPath, filter ?? "<none>");

        var repoRoot = FindRepoRoot(AppContext.BaseDirectory);
        if (repoRoot is null)
            return Task.FromResult("{\"status\":\"Error\",\"message\":\"Could not locate repo root (no MeshWeaver.slnx upstream from executable).\"}");

        var fullPath = Path.GetFullPath(Path.Combine(repoRoot, projectPath));
        if (!fullPath.StartsWith(repoRoot, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult("{\"status\":\"Error\",\"message\":\"projectPath must stay inside the repo root.\"}");
        if (!Directory.Exists(fullPath) && !File.Exists(fullPath))
            return Task.FromResult($"{{\"status\":\"Error\",\"message\":\"Path not found: {projectPath}\"}}");

        var args = new List<string> { "test", fullPath, "--no-restore", "--nologo" };
        if (!string.IsNullOrWhiteSpace(filter))
        {
            args.Add("--filter");
            args.Add(filter);
        }

        // Route the thread-holding `dotnet test` wait onto the bounded Process pool
        // (off the hub/grain scheduler). InvokeBlocking dispatches on the pool's
        // LimitedConcurrencyLevelTaskScheduler; ToolTask.Bridge is the only Task
        // bridge (the AIFunctionFactory surface requires a Task<string>), and it is
        // what makes a five-minute test run observe the round's cancellation.
        return ToolTask.Bridge(
            _processPool.InvokeBlocking(ct => RunTestsCore(repoRoot, projectPath, filter, args, ct)),
            cancellationToken,
            answer => answer,
            // Serialized, not interpolated: an exception message is arbitrary text and a single
            // quote or backslash in it would emit invalid JSON to a tool whose contract is a JSON
            // object. (The hand-written literals above are constant or a repo-relative path.)
            ex => RunTestsError(ex.Message),
            () => RunTestsError("The test runner produced no result."));
    }

    private static string RunTestsError(string message) =>
        System.Text.Json.JsonSerializer.Serialize(new { status = "Error", message });

    private static string RunTestsCore(
        string repoRoot, string projectPath, string? filter,
        IReadOnlyList<string> args, CancellationToken ct)
    {
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };
        foreach (var a in args) process.StartInfo.ArgumentList.Add(a);

        var stdout = new System.Text.StringBuilder();
        var stderr = new System.Text.StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Block this pooled thread on the child process. A pool unsubscribe / dispose
        // (ct) tears the process down; a 5-minute wall clock cap bounds the run. Both
        // resolve to the same Timeout result the inline await produced before.
        using var killOnCancel = ct.Register(() => { try { process.Kill(entireProcessTree: true); } catch { } });
        var exited = process.WaitForExit((int)TimeSpan.FromMinutes(5).TotalMilliseconds);
        if (!exited)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return "{\"status\":\"Timeout\",\"message\":\"Test run exceeded 5 minutes.\"}";
        }
        // WaitForExit(timeout) can return before the async output handlers have
        // flushed the final lines; the parameterless overload waits for that flush.
        process.WaitForExit();

        var combined = stdout.ToString();
        if (stderr.Length > 0) combined += "\n--- stderr ---\n" + stderr;
        // Trim to last ~4 KB so a noisy build log doesn't blow up the tool result.
        const int MaxLen = 4000;
        if (combined.Length > MaxLen)
            combined = "…\n" + combined[^MaxLen..];

        return System.Text.Json.JsonSerializer.Serialize(new
        {
            status = process.ExitCode == 0 ? "Passed" : "Failed",
            exitCode = process.ExitCode,
            projectPath,
            filter,
            output = combined,
        });
    }

    private static string? FindRepoRoot(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    private string ResolveContextPath(string path) => AgentChatPaths.ResolveContextPath(chat, path);

    /// <summary>
    /// RunTests only exists where the source repo does: it shells out to `dotnet test`
    /// against a repo-relative project path, which requires MeshWeaver.slnx upstream of
    /// the executable. Dev/test machines have it; deployed containers don't — so the
    /// tool is simply absent from the agent's tool list in production instead of being
    /// a permanently-erroring trap.
    /// </summary>
    private static bool RunTestsAvailable => FindRepoRoot(AppContext.BaseDirectory) is not null;

    /// <summary>
    /// Creates the standard tools for this plugin (read-only operations).
    /// </summary>
    public IList<AITool> CreateTools()
    {
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(Get),
            AIFunctionFactory.Create(Search),
            AIFunctionFactory.Create(NavigateTo),
            AIFunctionFactory.Create(GetDiagnostics),
        };
        if (RunTestsAvailable)
            tools.Add(AIFunctionFactory.Create(RunTests));
        return tools;
    }

    /// <summary>
    /// Creates all tools including write operations (Create, Update, Delete).
    /// </summary>
    public IList<AITool> CreateAllTools()
    {
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(Get),
            AIFunctionFactory.Create(Search),
            AIFunctionFactory.Create(NavigateTo),
            AIFunctionFactory.Create(Create),
            AIFunctionFactory.Create(Update),
            AIFunctionFactory.Create(Patch),
            AIFunctionFactory.Create(EditContent),
            AIFunctionFactory.Create(Delete),
            AIFunctionFactory.Create(Move),
            AIFunctionFactory.Create(Copy),
            AIFunctionFactory.Create(GetDiagnostics),
            AIFunctionFactory.Create(Recycle),
        };
        if (RunTestsAvailable)
            tools.Add(AIFunctionFactory.Create(RunTests));
        return tools;
    }
}
