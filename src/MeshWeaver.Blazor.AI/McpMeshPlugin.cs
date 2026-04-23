using System.ComponentModel;
using System.Net;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Security.Claims;
using MeshWeaver.AI;
using MeshWeaver.Kernel;
using MeshWeaver.Mesh.Services;
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

        // Per-MCP-call hosted hub — same pattern as interactive markdown's per-view
        // kernel (MarkdownView.razor.cs uses `AddressExtensions.CreateKernelAddress(guid)`).
        // A fresh kernel-typed address means the session hub's
        // `RouteAddressToHostedHub("kernel", …)` rule materialises a new hosted hub
        // with kernel-sub-hub handlers. Each MCP tool invocation thus runs on its own
        // ActionBlock — no serialisation on the session hub, no bleed of state between
        // concurrent calls. Cleanup happens when the session hub disposes.
        var requestKernelAddress = AddressExtensions.CreateKernelAddress(
            "mcp-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        var requestHub = sessionHub.GetHostedHub(
            requestKernelAddress,
            c => c.AddKernelSubHubHandlers(),
            HostedHubCreation.Always)!;
        logger.LogInformation("Materialising MCP request hub at {Address}", requestHub.Address);

        ops = new MeshOperations(requestHub);
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
        => ops.Get(path).FirstAsync().ToTask();

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
    [Description("Runs an executable Code node's C# through the kernel (Microsoft.DotNet.Interactive) and returns stdout / return value / errors. The target node must have `CodeConfiguration.IsExecutable == true`. Blocks until the kernel signals completion (side-effects — e.g. mesh.CreateNode calls inside the script — have happened by the time this returns). Use to run import/test scripts from MCP without needing a UI click.")]
    public Task<string> ExecuteScript(
        [Description("Path to an executable Code node (e.g., @Systemorph/FutuRe/EuropeRe/AcmeSubmission2025/Script/ImportLargeClaims). Must be `IsExecutable=true`.")] string path,
        [Description("Timeout in seconds. Default 120.")] int timeoutSeconds = 120)
        => ops.ExecuteScript(path, timeoutSeconds).FirstAsync().ToTask();

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
    public string BaseUrl { get; set; } = "http://localhost:5000";
}
