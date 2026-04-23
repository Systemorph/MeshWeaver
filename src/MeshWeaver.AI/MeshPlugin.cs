using System.ComponentModel;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Layout;
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

    [Description("Retrieves a node or content from the mesh by path. Paths are relative to current context; use @/ prefix for absolute paths. Supports Unified Path prefixes: content/, data/, schema/, model/, collection/, area/.")]
    public Task<string> Get(
        [Description("Path to data. Relative: @content/file.docx, @MyChild/*. Absolute: @/OrgA/Doc, @/OrgA/content/file.docx. For spaces: \"@content/My File.docx\"")] string path)
    {
        RestoreAccessContext();
        return ops.Get(ResolveContextPath(path)).FirstAsync().ToTask();
    }

    [Description("Searches the mesh using GitHub-style query syntax.")]
    public Task<string> Search(
        [Description("Query string (e.g., 'nodeType:Agent', 'path:ACME scope:descendants', 'name:*sales*')")] string query,
        [Description("Base path to search from (e.g., @graph). Empty for all.")] string? basePath = null)
    {
        RestoreAccessContext();
        return ops.Search(query, basePath != null ? ResolveContextPath(basePath) : null).FirstAsync().ToTask();
    }

    [Description("Creates a new node in the mesh. ALWAYS set the 'name' property to a human-readable display name.")]
    public Task<string> Create(
        [Description("JSON MeshNode with required: id, name, nodeType, namespace. Example: {\"id\":\"my-page\",\"namespace\":\"MyOrg\",\"name\":\"My Page\",\"nodeType\":\"Markdown\"}")] string node)
    {
        RestoreAccessContext();
        return ops.Create(node).FirstAsync().ToTask();
    }

    [Description("Full replacement update of existing nodes. ALWAYS Get the node first, modify the returned object, then send it back here unchanged-except-for-edits. The 'content' field MUST be present and non-null — null content is rejected and the response will include the expected schema. Prefer Patch for small changes.")]
    public Task<string> Update(
        [Description("JSON array of complete MeshNode objects fetched via Get and then modified")] string nodes)
    {
        RestoreAccessContext();
        return ops.Update(nodes).FirstAsync().ToTask();
    }

    [Description("Partial update of a single node. Only the keys present in 'fields' are changed; omitted keys preserve existing values. Do NOT include 'content' unless you intend to overwrite it — and never set 'content' to null (will be rejected with the schema). Prefer this over Update for small edits like icon/name/category.")]
    public Task<string> Patch(
        [Description("Path to the node (e.g., @User/rbuergi/my-node)")] string path,
        [Description("JSON object with ONLY the fields to change. Examples: {\"icon\": \"<svg>...</svg>\"}, {\"name\": \"New Name\"}. Include 'content' only if overwriting — and never as null.")] string fields)
    {
        RestoreAccessContext();
        return ops.Patch(ResolveContextPath(path), fields).FirstAsync().ToTask();
    }

    [Description("Deletes nodes from the mesh by path. Recursive: deleting a parent removes all descendants — pass the subtree root, no need to enumerate children.")]
    public Task<string> Delete(
        [Description("JSON array of path strings to delete")] string paths)
    {
        RestoreAccessContext();
        return ops.Delete(paths).FirstAsync().ToTask();
    }

    [Description("Returns compilation diagnostics for a NodeType or an instance of one. Status is 'Ok' when the type compiled cleanly, 'Error' with a detailed message when it failed, or 'Unknown' when no compile has happened yet. Use this after creating/updating a NodeType to verify it actually compiles — a NodeType that doesn't compile is not 'done'.")]
    public Task<string> GetDiagnostics(
        [Description("Path to a NodeType (e.g., @Systemorph/SocialMedia/Profile) or to any instance of one")] string path)
    {
        RestoreAccessContext();
        return ops.GetDiagnostics(ResolveContextPath(path)).FirstAsync().ToTask();
    }

    [Description("Recycles the hub at the given path by posting DisposeRequest. Forces a fresh hub initialization on the next access — use this after fixing a broken NodeType, after editing the `sources` list, or whenever a grain is stuck. Returns {status:'Recycled', path}. Wait ~100ms before the next access so the grain teardown completes.")]
    public Task<string> Recycle(
        [Description("Path to the node (e.g., @Systemorph/SocialMedia/Profile). Use the NodeType path to recycle the whole type; use an instance path to recycle just that instance's hub.")] string path)
    {
        RestoreAccessContext();
        return ops.Recycle(ResolveContextPath(path)).FirstAsync().ToTask();
    }

    [Description("Moves a node and its descendants to a new path. Equivalent to the Move menu item. Requires Delete on the source namespace and Create on the target. Source and target are full paths (namespace + id), e.g. 'OrgA/Child' -> 'OrgB/Child'.")]
    public Task<string> Move(
        [Description("Current path of the node (e.g., @OrgA/Child)")] string sourcePath,
        [Description("New path for the node (e.g., @OrgB/Child)")] string targetPath)
    {
        RestoreAccessContext();
        return ops.Move(ResolveContextPath(sourcePath), ResolveContextPath(targetPath)).FirstAsync().ToTask();
    }

    [Description("Copies a node and all its descendants to a target namespace. Equivalent to the Copy menu item. Source ids are preserved; paths are rewritten under the target namespace.")]
    public Task<string> Copy(
        [Description("Current path of the node to copy (e.g., @OrgA/Child)")] string sourcePath,
        [Description("Target namespace to copy under (e.g., @OrgB)")] string targetNamespace,
        [Description("Overwrite existing nodes at the target. Default: false (skip if any target path already exists).")] bool force = false)
    {
        RestoreAccessContext();
        return ops.Copy(ResolveContextPath(sourcePath), ResolveContextPath(targetNamespace), force).FirstAsync().ToTask();
    }

    /// <summary>
    /// Restores the user's AccessContext from <see cref="IAgentChat.ExecutionContext"/>.
    /// AsyncLocal doesn't flow reliably through the AI framework's streaming + tool
    /// invocation pipeline, so every plugin entry point must explicitly re-seed the
    /// context before it hits downstream hub-backed operations. Idempotent when the
    /// AccessContextAIFunction wrapper has already run.
    /// </summary>
    private void RestoreAccessContext()
    {
        var userCtx = chat.ExecutionContext?.UserAccessContext;
        if (userCtx != null)
            accessService?.SetContext(userCtx);
    }

    [Description("Displays a node's visual layout in the chat UI.")]
    public string NavigateTo(
        [Description("Path to navigate to (e.g., @graph/org1)")] string path)
    {
        logger.LogInformation("NavigateTo called with path={Path}", path);

        var resolvedPath = MeshOperations.ResolvePath(ResolveContextPath(path));
        var address = new Address(resolvedPath);
        var layoutControl = Controls.LayoutArea(address, string.Empty);

        chat.DisplayLayoutArea(layoutControl);
        return $"Navigating to: {resolvedPath}";
    }

    [Description("Runs xUnit tests via `dotnet test` on the given test project path (repo-relative, e.g. 'test/MeshWeaver.Acme.Test'). Optional filter uses the xunit `--filter` syntax: 'FullyQualifiedName~TodoViewsTest' to narrow by class, or '...Test.MethodName' for a single method. Returns the condensed test runner output (stdout + pass/fail summary). Dev-only — intended for the Monolith portal, not production.")]
    public async Task<string> RunTests(
        [Description("Repo-relative path to the test project or its directory (e.g. 'test/MeshWeaver.Acme.Test')")] string projectPath,
        [Description("Optional xunit filter expression (e.g. 'FullyQualifiedName~TodoViewsTest')")] string? filter = null)
    {
        logger.LogInformation("RunTests called project={Project} filter={Filter}", projectPath, filter ?? "<none>");

        var repoRoot = FindRepoRoot(AppContext.BaseDirectory);
        if (repoRoot is null)
            return "{\"status\":\"Error\",\"message\":\"Could not locate repo root (no MeshWeaver.slnx upstream from executable).\"}";

        var fullPath = Path.GetFullPath(Path.Combine(repoRoot, projectPath));
        if (!fullPath.StartsWith(repoRoot, StringComparison.OrdinalIgnoreCase))
            return "{\"status\":\"Error\",\"message\":\"projectPath must stay inside the repo root.\"}";
        if (!Directory.Exists(fullPath) && !File.Exists(fullPath))
            return $"{{\"status\":\"Error\",\"message\":\"Path not found: {projectPath}\"}}";

        var args = new List<string> { "test", fullPath, "--no-restore", "--nologo" };
        if (!string.IsNullOrWhiteSpace(filter))
        {
            args.Add("--filter");
            args.Add(filter);
        }

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

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return "{\"status\":\"Timeout\",\"message\":\"Test run exceeded 5 minutes.\"}";
        }

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

    private string ResolveContextPath(string path) => MeshOperations.ResolveContextPath(chat, path);

    /// <summary>
    /// Creates the standard tools for this plugin (read-only operations).
    /// </summary>
    public IList<AITool> CreateTools()
    {
        return
        [
            AIFunctionFactory.Create(Get),
            AIFunctionFactory.Create(Search),
            AIFunctionFactory.Create(NavigateTo),
            AIFunctionFactory.Create(GetDiagnostics),
            AIFunctionFactory.Create(RunTests),
        ];
    }

    /// <summary>
    /// Creates all tools including write operations (Create, Update, Delete).
    /// </summary>
    public IList<AITool> CreateAllTools()
    {
        return
        [
            AIFunctionFactory.Create(Get),
            AIFunctionFactory.Create(Search),
            AIFunctionFactory.Create(NavigateTo),
            AIFunctionFactory.Create(Create),
            AIFunctionFactory.Create(Update),
            AIFunctionFactory.Create(Patch),
            AIFunctionFactory.Create(Delete),
            AIFunctionFactory.Create(Move),
            AIFunctionFactory.Create(Copy),
            AIFunctionFactory.Create(GetDiagnostics),
            AIFunctionFactory.Create(Recycle),
            AIFunctionFactory.Create(RunTests),
        ];
    }
}
