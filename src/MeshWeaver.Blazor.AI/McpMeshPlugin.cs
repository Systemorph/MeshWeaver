using System.ComponentModel;
using System.Security.Claims;
using MeshWeaver.AI;
using MeshWeaver.Kernel;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
    private readonly ILogger<McpMeshPlugin> logger;
    private readonly string baseUrl;

    public McpMeshPlugin(
        IMessageHub hub,
        IOptions<McpConfiguration>? config = null,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        logger = hub.ServiceProvider.GetRequiredService<ILogger<McpMeshPlugin>>();
        baseUrl = config?.Value.BaseUrl ?? "http://localhost:5000";
        var routingService = hub.ServiceProvider.GetRequiredService<IRoutingService>();

        var sessionHub = ResolveSessionHub(hub, httpContextAccessor?.HttpContext, routingService, logger);
        ops = new MeshOperations(sessionHub);
    }

    private static IMessageHub ResolveSessionHub(
        IMessageHub rootHub, HttpContext? ctx, IRoutingService routingService, ILogger logger)
    {
        var sessionId = ResolveSessionId(ctx);
        if (sessionId is null)
        {
            logger.LogWarning(
                "No MCP session id resolvable from request — falling back to root hub. "
                + "Some routing rules (kernel dispatch, etc.) will not fire.");
            return rootHub;
        }

        // Reuse the existing Portal address type: the RoutingGrain already
        // shortcircuits portal addresses away from Orleans grain resolution
        // (see RoutingGrain.cs:42), so our session hub gets the same
        // "lives outside the grain scope" treatment as a Blazor circuit.
        var address = AddressExtensions.CreatePortalAddress("mcp-" + sessionId);
        logger.LogInformation("Materialising MCP session hub at {Address}", address);

        // Mirrors PortalApplication.DefaultPortalConfig:
        //   1. WithInitialization → RegisterStreamAsync registers the sub-hub
        //      with the routing service so responses (kernel ack,
        //      UpdateNodeResponse, etc.) find their way back.
        //   2. WithRoutes → kernel addresses are resolved as local hosted hubs
        //      rather than bouncing to Orleans grain activation.
        return rootHub.GetHostedHub(
            address,
            sessionConfig => sessionConfig
                .WithInitialization(async (hub, _) =>
                {
                    var registry = await routingService.RegisterStreamAsync(hub);
                    hub.RegisterForDisposal(registry);
                })
                .WithRoutes(routes => routes.RouteAddressToHostedHub(
                    AddressExtensions.KernelType,
                    c => c.AddKernelSubHubHandlers())),
            HostedHubCreation.Always);
    }

    private static string? ResolveSessionId(HttpContext? ctx)
    {
        if (ctx is null) return null;

        // Prefer the standard MCP protocol header. Clients (Claude Desktop,
        // Claude Code, etc.) set this per connection.
        var protocolSession = ctx.Request.Headers["Mcp-Session-Id"].FirstOrDefault();

        // Scope within the authenticated caller so two different users can't
        // collide on a chosen Mcp-Session-Id. Fall through oid → sub → name.
        var callerId = ctx.User?.FindFirst("oid")?.Value
                    ?? ctx.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
                    ?? ctx.User?.FindFirst(ClaimTypes.Email)?.Value
                    ?? ctx.User?.Identity?.Name;

        if (!string.IsNullOrEmpty(callerId) && !string.IsNullOrEmpty(protocolSession))
            return $"{Sanitize(callerId)}-{Sanitize(protocolSession)}";
        if (!string.IsNullOrEmpty(callerId))
            return Sanitize(callerId);
        if (!string.IsNullOrEmpty(protocolSession))
            return $"anon-{Sanitize(protocolSession)}";
        return null;
    }

    private static string Sanitize(string s)
    {
        // Address segments must be safe — strip characters that break hosted-hub
        // grain key lookup. Keep alphanumerics, '-', '_'; replace everything else.
        var chars = s.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '-').ToArray();
        return new string(chars);
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
        => ops.Get(path);

    [McpServerTool]
    [Description("Searches the mesh using GitHub-style query syntax. Returns up to 50 matching nodes.")]
    public Task<string> Search(
        [Description("Query string (e.g., 'nodeType:Agent', 'laptop', 'path:ACME scope:descendants', 'name:*sales*')")] string query,
        [Description("Base path to search from (e.g., @graph). Empty for all.")] string? basePath = null)
        => ops.Search(query, basePath);

    [McpServerTool]
    [Description("Creates a new node in the mesh. Pass a JSON MeshNode object with id, namespace, name, nodeType, and content fields.")]
    public Task<string> Create(
        [Description("JSON MeshNode object to create (e.g., {\"id\": \"NewOrg\", \"namespace\": \"ACME\", \"name\": \"New Org\", \"nodeType\": \"Organization\", \"content\": {}})")] string node)
        => ops.Create(node);

    [McpServerTool]
    [Description("Updates existing nodes in the mesh. Pass a JSON array of complete MeshNode objects. Always Get before Update — the entire node is replaced, not merged.")]
    public Task<string> Update(
        [Description("JSON array of MeshNode objects with all fields (get existing node first, modify, then pass here)")] string nodes)
        => ops.Update(nodes);

    [McpServerTool]
    [Description("Partial update of a single node. Only the keys present in 'fields' are changed; omitted keys preserve existing values. Do NOT include 'content' unless overwriting — never set 'content' to null. Prefer this over Update for small edits like icon/name/category.")]
    public Task<string> Patch(
        [Description("Path to the node (e.g., @User/rbuergi/my-node)")] string path,
        [Description("JSON object with ONLY the fields to change. Examples: {\"icon\": \"<svg>...</svg>\"}, {\"name\": \"New Name\"}.")] string fields)
        => ops.Patch(path, fields);

    [McpServerTool]
    [Description("Deletes one or more nodes from the mesh by path. Recursive: deleting a parent removes all descendants. To remove a subtree, just pass the root path — children do not need to be enumerated.")]
    public Task<string> Delete(
        [Description("JSON array of path strings to delete (e.g., [\"ACME/OldProject\", \"ACME/ArchivedTask\"])")] string paths)
        => ops.Delete(paths);

    [McpServerTool]
    [Description("Moves a node and its descendants to a new path. Equivalent to the Move menu item. Requires Delete on the source namespace and Create on the target. Source and target are full paths (namespace + id), e.g. 'OrgA/Child' -> 'OrgB/Child'.")]
    public Task<string> Move(
        [Description("Current path of the node (e.g., @OrgA/Child)")] string sourcePath,
        [Description("New path for the node (e.g., @OrgB/Child)")] string targetPath)
        => ops.Move(sourcePath, targetPath);

    [McpServerTool]
    [Description("Copies a node and all its descendants to a target namespace. Equivalent to the Copy menu item. Source ids are preserved; paths are rewritten under the target namespace.")]
    public Task<string> Copy(
        [Description("Current path of the node to copy (e.g., @OrgA/Child)")] string sourcePath,
        [Description("Target namespace to copy under (e.g., @OrgB)")] string targetNamespace,
        [Description("Overwrite existing nodes at the target. Default: false.")] bool force = false)
        => ops.Copy(sourcePath, targetNamespace, force);

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
        => ops.GetDiagnostics(path);

    [McpServerTool]
    [Description("Recycles the hub at the given path by posting DisposeRequest. Forces a fresh hub initialization on the next access — use after fixing a broken NodeType, after editing the `sources` list, or whenever a grain is stuck in a cached bad state. Returns {status:'Recycled', path}. Wait ~100ms before the next access so the grain teardown completes.")]
    public Task<string> Recycle(
        [Description("Path to the node (e.g., @Systemorph/SocialMedia/Profile). Use the NodeType path to recycle the whole type; use an instance path to recycle just that instance's hub.")] string path)
        => ops.Recycle(path);

    [McpServerTool]
    [Description("Runs an executable Code node's C# through the kernel (Microsoft.DotNet.Interactive) and returns stdout / return value / errors. The target node must have `CodeConfiguration.IsExecutable == true`. Blocks until the kernel signals completion (side-effects — e.g. mesh.CreateNode calls inside the script — have happened by the time this returns). Use to run import/test scripts from MCP without needing a UI click.")]
    public Task<string> ExecuteScript(
        [Description("Path to an executable Code node (e.g., @Systemorph/FutuRe/EuropeRe/AcmeSubmission2025/Script/ImportLargeClaims). Must be `IsExecutable=true`.")] string path,
        [Description("Timeout in seconds. Default 120.")] int timeoutSeconds = 120)
        => ops.ExecuteScript(path, timeoutSeconds);
}

/// <summary>
/// Configuration options for MCP integration.
/// </summary>
public class McpConfiguration
{
    /// <summary>
    /// Base URL for the MeshWeaver UI. Used for generating NavigateTo URLs.
    /// </summary>
    public string BaseUrl { get; set; } = "http://localhost:5000";
}
