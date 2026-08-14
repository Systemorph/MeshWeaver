---
Name: Reconnecting an app replaces its key
Category: Fix
Description: Reconnecting an integration now replaces its access token instead of leaving the old one live for a year.
Icon: Sparkle
Order: -20260814
---

# Reconnecting an app replaces its key

Every time you connected an app — Claude Desktop, an MCP client, any integration — the portal issued
a new access token and left the previous one working. Reinstall the app, switch device, or disconnect
and reconnect, and each round left another live key behind, valid for a year. Your token list grew,
and every entry in it still opened the door.

Reconnecting now **replaces** that app's key rather than adding one: after a fresh connection, the app
has exactly one credential and the earlier ones are gone. The new token is always issued first, so a
reconnection can never leave an app without access.
