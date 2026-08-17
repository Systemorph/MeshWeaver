namespace MeshWeaver.Hosting.AspNetCore;

/// <summary>
/// The portal's own externally-reachable base URL, bound from the <c>Mcp</c> configuration
/// section (<c>Mcp__BaseUrl</c> — the Aspire AppHost wires the portal's external endpoint into it
/// at deployment time). Used to compose absolute links back into the MeshWeaver UI.
///
/// <para>
/// 🚨 It lives PLATFORM-side, not in <c>MeshWeaver.Mcp</c>, because three surfaces read it and
/// only one of them is the MCP module: the REST mirror <c>/api/mesh/navigate_to</c> +
/// <c>/api/mesh/base_url</c>, the co-hosted CLI back-connection (which composes
/// <c>{BaseUrl}/mcp</c>), and the MCP <c>navigate_to</c> tool itself. Delisting
/// <c>MeshWeaver.Mcp</c> from <c>Modules:Assemblies</c> must not take the other two with it.
/// The section name stays <c>Mcp</c> so no deployment's configuration changes.
/// </para>
/// </summary>
public class McpConfiguration
{
    /// <summary>
    /// Base URL for the MeshWeaver UI. Used for generating NavigateTo URLs.
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;
}
