---
Name: The MCP server is a module
Category: Feature
Description: The Model Context Protocol server moved into its own MeshWeaver.Mcp module, so a deployment publishes the /mcp surface by listing it rather than by compiling it in.
Icon: PlugConnected
Order: -20260817
---

# The MCP server is a module

The Model Context Protocol server — the mesh tool surface external assistants call (`get`,
`search`, `create`, `update`, `render_area`, the LSP and chunk tools) and the `/mcp` HTTP
transport that carries them — now ships as `MeshWeaver.Mcp` instead of being compiled into every
portal.

Publishing an MCP surface is a deployment decision: it opens the mesh to any client holding an API
token. Listing the DLL is now that decision. A portal that lists it is unchanged — same `/mcp`
route, same Bearer-only `McpAuth` gate, same `Mcp` configuration section, same tools. A portal that
delists it answers `404` on `/mcp` and carries no server at all.

The REST mirror at `/api/mesh/*` is deliberately **not** part of the module and keeps working
either way, together with everything the two surfaces share: the per-caller session hub both route
requests onto, the `Mcp:BaseUrl` used to compose links back into the UI, and the API-token
authentication itself. So the co-hosted Claude Code and Copilot harnesses, `navigate_to` links, and
a server-side renderer reading through `/api/mesh` all keep their behaviour when MCP is delisted.
