---
Name: Reliable MCP sign-in on scaled deployments
Category: What's New
Description: Connecting an MCP client (Claude Code, Copilot, IDEs) now works reliably on portals running multiple replicas.
Icon: Sparkle
---

# Reliable MCP sign-in on scaled deployments

Connecting an MCP client to the portal could fail with an authorization error when the portal was running more than one replica — the sign-in would only succeed if two consecutive requests happened to reach the same server instance. Authorization codes are now stored in the mesh itself, so any replica can complete a sign-in started on any other. Codes remain single-use and short-lived, and interrupted sign-ins now fail fast with a clear error instead of hanging.
