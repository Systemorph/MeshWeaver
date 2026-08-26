---
Name: MCP endpoint at /api/mcp, and the MCP Server page renders
Category: Fix
Description: The MCP endpoint's primary URL is now /api/mcp (/mcp keeps working for every existing client), and opening /Mcp in the browser now shows the MCP Server page instead of a 404.
Icon: PlugConnected
Order: -20260826
---

# MCP endpoint at /api/mcp, and the MCP Server page renders

The mesh's MCP endpoint now lives at `/api/mcp` as its primary URL. Every existing
client configuration pointing at `/mcp` — Claude Code, Claude Desktop, Copilot,
plugin MCP servers — keeps working unchanged: `/mcp` remains a permanent alias for
the same endpoint, and OAuth discovery answers for both URLs.

Opening `/Mcp` in the browser now renders the MCP Server page (the pre-installed
store plugin with connection instructions) instead of answering "not found". MCP
protocol traffic is unaffected — an agent's JSON calls never see an HTML page, and
a portal whose MCP module is missing still answers an honest 404 rather than a
misleading web page.
