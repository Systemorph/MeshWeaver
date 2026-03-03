---
NodeType: "Doc/Article"
Title: "MCP Authentication"
Abstract: "How to generate API tokens and configure MCP clients like Claude Code to securely access the MeshWeaver MCP endpoint with bearer token authentication."
Icon: "/static/DocContent/AI/McpAuthentication/icon.svg"
Published: "2025-06-15"
Thumbnail: "images/agenticai.svg"
Authors:
  - "Roland Buergi"
Tags:
  - "AI"
  - "MCP"
  - "Authentication"
  - "Security"
  - "API Tokens"
---

## Overview

The MeshWeaver MCP endpoint (`/mcp`) requires bearer token authentication. Every MCP client (Claude Code, Cursor, custom integrations) must include a valid API token in the `Authorization` header. Without a token, requests receive a `401 Unauthorized` response.

Tokens are personal — each token is tied to a specific user identity, so all actions performed through MCP run with that user's permissions (RLS is enforced).

## Generating a Token

### Via the Web UI

1. Log in to the Memex portal
2. Navigate to **Settings** (gear icon) on any node
3. Under the **Security** group, click **API Tokens**
4. Enter a label (e.g. "Claude Code") and optionally set an expiry in days
5. Click **Generate Token**
6. **Copy the token immediately** — it is shown only once and cannot be retrieved later

The token starts with `mw_` followed by a base64url-encoded random string. Example:

```
mw_dGhpcyBpcyBhIHNhbXBsZSB0b2tlbg
```

### Via the REST API

If you prefer automation, use the token management API (requires cookie authentication from a logged-in session):

```bash
# Create a token
curl -X POST https://your-portal.com/api/tokens \
  -H "Content-Type: application/json" \
  -b cookies.txt \
  -d '{"label": "Claude Code", "expiresInDays": 90}'
```

The response includes the raw token:

```json
{
  "rawToken": "mw_dGhpcyBpcyBhIHNhbXBsZSB0b2tlbg",
  "nodePath": "ApiToken/a1b2c3d4e5f6",
  "label": "Claude Code",
  "createdAt": "2025-06-15T10:00:00Z",
  "expiresAt": "2025-09-13T10:00:00Z"
}
```

## Configuring Claude Code

Add the MCP server to your Claude Code configuration (`.claude/settings.json` or project-level `claude_code_config.json`):

```json
{
  "mcpServers": {
    "meshweaver": {
      "type": "sse",
      "url": "https://your-portal.com/mcp",
      "headers": {
        "Authorization": "Bearer mw_dGhpcyBpcyBhIHNhbXBsZSB0b2tlbg"
      }
    }
  }
}
```

Once configured, Claude Code can use MeshWeaver tools (Get, Search, Create, Update, Delete) with your identity.

## Configuring Other MCP Clients

Any MCP client that supports HTTP/SSE transport can connect. The key requirement is sending the `Authorization` header:

```
Authorization: Bearer mw_<your-token>
```

### Example with curl

```bash
# Test the MCP endpoint
curl -X POST https://your-portal.com/mcp \
  -H "Authorization: Bearer mw_dGhpcyBpcyBhIHNhbXBsZSB0b2tlbg" \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","method":"initialize","params":{},"id":1}'
```

A successful response means authentication is working. A `401` response means the token is missing, invalid, expired, or revoked.

## Managing Tokens

### Listing Tokens

View all your tokens in the **API Tokens** settings page. Each token shows:

- **Label** — the name you gave it
- **Token ID** — first 8 characters of the hash (for identification)
- **Created** — when the token was generated
- **Expires** — expiration date, or "Never"
- **Last Used** — last time the token was used for authentication
- **Status** — Active, Expired, or Revoked

### Revoking Tokens

Click **Revoke** next to any active token to immediately invalidate it. Revoked tokens cannot be used for authentication. This is useful when:

- A token may have been compromised
- A team member leaves the project
- You want to rotate credentials

### Via the REST API

```bash
# List tokens
curl https://your-portal.com/api/tokens -b cookies.txt

# Revoke a token
curl -X DELETE https://your-portal.com/api/tokens/ApiToken/a1b2c3d4e5f6 -b cookies.txt
```

## Security Best Practices

- **One token per client** — create separate tokens for each tool or integration. This makes revocation granular.
- **Set expiration dates** — for shared environments, set tokens to expire (e.g. 90 days). Regenerate as needed.
- **Revoke unused tokens** — regularly review your token list and revoke any tokens you no longer need.
- **Never commit tokens to source control** — store them in environment variables or secure secret managers.
- **Use HTTPS** — tokens are sent in HTTP headers. Always use HTTPS in production to prevent interception.

## How It Works

```mermaid
%%{init: {'theme':'neutral', 'themeVariables': {'fontSize':'14px'}}}%%
sequenceDiagram
    participant Client as MCP Client
    participant Auth as Auth Handler
    participant VUM as Virtual User MW
    participant UCM as User Context MW
    participant MCP as MCP Handler

    Client->>Auth: POST /mcp + Bearer token
    Auth->>Auth: SHA-256 hash token
    Auth->>Auth: Lookup ApiToken node by hash
    Auth->>Auth: Verify not expired/revoked
    Auth-->>UCM: ClaimsPrincipal (userId, name, email)
    Note over VUM: Skipped for /mcp
    UCM->>UCM: Extract AccessContext from claims
    UCM->>MCP: Request with user identity
    MCP-->>Client: MCP response (RLS enforced)
```

When a request arrives at `/mcp`:

1. The **ApiTokenAuthenticationHandler** extracts the Bearer token from the `Authorization` header
2. It hashes the token with SHA-256 and looks up the corresponding `ApiToken` node
3. If found and valid (not expired, not revoked), it builds a `ClaimsPrincipal` with the token owner's identity
4. The **UserContextMiddleware** reads these claims and sets the `AccessContext` on the request
5. MCP tools execute with the token owner's identity — row-level security (RLS) is enforced naturally

Unauthenticated requests never reach the MCP handler — they receive `401 Unauthorized` immediately.
