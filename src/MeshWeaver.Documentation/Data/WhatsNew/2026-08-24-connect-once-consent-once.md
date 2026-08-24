---
Name: Connect once, consent once — and sign-in no longer wedges
Category: Fix
Description: MCP connections no longer ask for consent on every reconnect, and abandoned sign-ins no longer pile up cookies until login breaks with a 502.
Icon: Sparkle
Order: -20260824
---

# Connect once, consent once — and sign-in no longer wedges

Two sign-in repairs from the same day of production observation.

Connecting an MCP client (claude.ai Connectors, Claude Desktop) used to ask you to approve the
connection **every single time**. The portal handed out a brand-new client identity on every
reconnect, so nothing a previous approval was recorded against ever matched. The identity is now
derived from the client itself — reconnecting with the same client presents the same identity, and
your earlier consent counts. As a side effect, a re-connection now replaces the client's previous
access credential instead of leaving another one behind.

Separately, an **abandoned** sign-in attempt (a closed tab, an error, a retry) used to leave a small
handshake cookie behind forever. Enough of them and the browser's requests grew past what the server
accepts — sign-in then failed with an error that retrying only made worse, because every retry added
another cookie. These cookies now expire on their own after fifteen minutes, so the state heals
itself.
